using System.Text;
using Aiursoft.Canon;
using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.AlertCenter.Configuration;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.WebTools;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Aiursoft.AlertCenter.Services.BackgroundJobs;

/// <summary>
/// Generates embedding vectors for incidents using the configured Ollama embedding model.
/// 告警标题和内容创建后不可变，且没有本地化，只生成一次向量。
/// </summary>
public class GenerateEmbeddingsJob(
    TemplateDbContext db,
    GlobalSettingsService settingsService,
    IHttpClientFactory httpClientFactory,
    RetryEngine retryEngine,
    ILogger<GenerateEmbeddingsJob> logger) : IBackgroundJob
{
    internal const int MaxDocumentsPerRun = 50;
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    public string Name => "Generate Incident Embeddings";

    public string Description =>
        "Generates 1024-dimension embedding vectors for incidents using the configured Ollama embedding model.";

    public async Task ExecuteAsync()
    {
        if (!await RunLock.WaitAsync(0))
        {
            logger.LogInformation("GenerateEmbeddingsJob: previous run is still active. Skipping.");
            return;
        }

        try
        {
            await ExecuteCoreAsync();
        }
        finally
        {
            RunLock.Release();
        }
    }

    private async Task ExecuteCoreAsync()
    {
        if (!await settingsService.IsAiSearchEnabledAsync())
        {
            logger.LogInformation("GenerateEmbeddingsJob: Embedding endpoint not configured. Skipping.");
            return;
        }

        var useAiSearch = await settingsService.GetBoolSettingAsync(SettingsMap.EnableEmbeddingBasedSearch);
        if (!useAiSearch)
        {
            logger.LogInformation("GenerateEmbeddingsJob: EnableEmbeddingBasedSearch is disabled. Skipping.");
            return;
        }

        var model = await settingsService.GetSettingValueAsync(SettingsMap.EmbeddingModel);
        if (string.IsNullOrWhiteSpace(model))
        {
            logger.LogInformation("GenerateEmbeddingsJob: EmbeddingModel not configured. Skipping.");
            return;
        }

        var instance = await settingsService.GetEmbeddingEndpointAsync();
        var token = await settingsService.GetEmbeddingTokenAsync();

        var attempted = 0;
        var succeeded = 0;

        // 告警内容不可变，只处理尚未生成向量的条目，无本地化 Phase 2。
        var lastId = Guid.Empty;
        while (true)
        {
            if (attempted >= MaxDocumentsPerRun)
            {
                logger.LogInformation(
                    "GenerateEmbeddingsJob: attempted {Count} documents, stopping until next run.",
                    attempted);
                break;
            }

            var currentLastId = lastId;
            var take = Math.Min(10, MaxDocumentsPerRun - attempted);
            var pendingIncidents = await db.Incidents
                .Where(r => r.Id.CompareTo(currentLastId) > 0 && (r.Embedding == null || r.LastEmbeddedAt == DateTime.MinValue))
                .OrderBy(r => r.Id)
                .Take(take)
                .ToListAsync();

            if (pendingIncidents.Count == 0) break;

            foreach (var incident in pendingIncidents)
            {
                attempted++;
                try
                {
                    var embedding = await retryEngine.RunWithRetry(async _ =>
                    {
                        var text = BuildIncidentText(incident);
                        return await CallEmbedApiAsync(instance, model, token, text, incident.Title);
                    });

                    var serialized = EmbeddingHelper.Serialize(embedding);
                    if (await TrySaveEmbeddingIfIncidentUnchangedAsync(db, incident, serialized))
                    {
                        succeeded++;
                    }
                    else
                    {
                        logger.LogInformation(
                            "GenerateEmbeddingsJob: incident '{Name}' (id={Id}) changed while embedding was running. Skipping stale result.",
                            incident.Title, incident.Id);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "GenerateEmbeddingsJob: Failed to generate embedding for incident '{Name}'.", incident.Title);
                }
            }

            lastId = pendingIncidents[^1].Id;
        }

        logger.LogInformation(
            "GenerateEmbeddingsJob: done. {Succeeded}/{Attempted} documents processed.",
            succeeded, attempted);
    }

    internal async Task<float[]> CallEmbedApiAsync(string instance, string model, string token, string text, string logName)
    {
        var http = httpClientFactory.CreateClient();

        var baseUri = new Uri(instance);
        var embedEndpoint = $"{baseUri.Scheme}://{baseUri.Authority}/api/embed?keep_alive=-1";

        // bge-m3 has an 8192-token context window. Characters map to tokens at different
        // rates per language (CJK ≈ 1:1, English ≈ 1:4). Start with 8000 chars (safe for
        // all languages) and use binary-search fallback if Ollama still reports the input
        // is too long.
        var maxChars = 8000;
        while (true)
        {
            var input = TruncateForEmbedding(text, maxChars);

            var requestBody = new { model, input };
            var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, embedEndpoint) { Content = content };
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var response = await http.SendAsync(request, timeoutCts.Token);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(timeoutCts.Token);
                if (result?.Embeddings == null || result.Embeddings.Length == 0)
                {
                    throw new InvalidOperationException($"Ollama returned no embeddings for '{logName}'.");
                }

                var vector = result.Embeddings[0];
                EmbeddingHelper.Normalize(vector);
                return vector;
            }

            // If the input is too long, halve the limit and retry. Otherwise fail.
            var errorBody = await response.Content.ReadAsStringAsync();
            var isContextError = EmbeddingHelper.IsContextLengthError(response.StatusCode, errorBody);
            if (!isContextError || maxChars <= 500)
            {
                throw new HttpRequestException(
                    $"Ollama embedding request failed for '{logName}' (HTTP {(int)response.StatusCode}): {errorBody}");
            }

            var prev = maxChars;
            maxChars /= 2;
            logger.LogWarning(
                "Embedding input for '{Name}' still too long at {Prev} chars, retrying with {Current} chars (binary fallback).",
                logName, prev, maxChars);
        }
    }

    /// <summary>
    /// Truncates text to fit within bge-m3's 8192-token context window.
    /// Uses head+tail preservation: keeps the first 75% and last ~25% of the budget
    /// so both the introduction and conclusion contribute to the embedding.
    /// </summary>
    internal static string TruncateForEmbedding(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;

        var head = (int)(maxChars * 0.75);
        var tail = maxChars - head - 5; // 5 for "\n...\n" separator
        if (tail <= 0) return text[..maxChars];

        return string.Concat(text.AsSpan(0, head), "\n...\n", text.AsSpan(text.Length - tail));
    }

    private static string BuildIncidentText(Incident incident)
    {
        // 只嵌入不可变的标题和内容，应用改名不应触发向量重建。
        var sb = new StringBuilder();
        sb.AppendLine(incident.Title);
        if (!string.IsNullOrWhiteSpace(incident.Content))
            sb.AppendLine(incident.Content);
        return sb.ToString();
    }

    internal static async Task<bool> TrySaveEmbeddingIfIncidentUnchangedAsync(
        TemplateDbContext db,
        Incident incident,
        byte[]? embedding)
    {
        // 标题和内容不可变，保留 unchanged 保存结构，仅按 Id 判断告警仍存在。
        var embeddedAt = DateTime.UtcNow;
        if (db.Database.IsRelational())
        {
            var updated = await db.Incidents
                .Where(r => r.Id == incident.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Embedding, embedding)
                    .SetProperty(r => r.LastEmbeddedAt, embeddedAt));
            return updated == 1;
        }

        await db.Entry(incident).ReloadAsync();
        if (db.Entry(incident).State == EntityState.Detached)
        {
            return false;
        }

        incident.Embedding = embedding;
        incident.LastEmbeddedAt = embeddedAt;
        await db.SaveChangesAsync();
        return true;
    }

    private class OllamaEmbedResponse
    {
        [JsonProperty("embeddings")]
        public float[][]? Embeddings { get; set; }
    }
}

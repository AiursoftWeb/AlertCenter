using System.Security.Cryptography;
using System.Text;
using Aiursoft.AlertCenter.Configuration;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.WebTools;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Aiursoft.AlertCenter.Services;

/// <summary>
/// Semantic vector search for incidents using an Ollama-hosted embedding model (bge-m3).
/// Computes cosine similarity against an in-memory cache of pre-computed incident embeddings.
/// Caches query embeddings in the database (circular buffer of 2000 entries) to avoid
/// redundant calls to the embedding model.
/// Falls back to classic keyword search when AI search is unavailable or times out.
/// </summary>
public class IncidentVectorSearchService(
    TemplateDbContext db,
    IncidentEmbeddingCache cache,
    GlobalSettingsService settingsService,
    IHttpClientFactory httpClientFactory,
    ILogger<IncidentVectorSearchService> logger)
{
    private const int EmbedTimeoutSeconds = 10;

    /// <summary>
    /// Only update LastAccessedAt when the previous update was at least this long ago.
    /// Avoids a write on every cache-hit search while still preserving approximate LRU order.
    /// </summary>
    internal static readonly TimeSpan AccessThrottle = TimeSpan.FromHours(1);

    public async Task<(bool UsedAi, List<Incident> Results, int TotalCount)> SearchAsync(
        IQueryable<Incident> baseQuery,
        string query,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Check all three preconditions for AI vector search.
        if (!await ShouldAttemptVectorSearchAsync())
        {
            return (false, [], 0);
        }

        var snapshot = cache.Snapshot();
        if (snapshot.Count == 0)
        {
            return (false, [], 0);
        }

        float[]? queryVector;
        try
        {
            var expectedDimension = snapshot.Values.First().Length;
            queryVector = await EmbedQueryAsync(query, expectedDimension, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (false, [], 0);
        }

        if (queryVector == null)
        {
            return (false, [], 0);
        }

        // 告警权限影响总数和分页，必须先取得可见 Id，再对缓存打分。
        var visibleIds = (await baseQuery.Select(r => r.Id).ToListAsync(ct)).ToHashSet();
        var scored = new List<(Guid IncidentId, float Score)>();
        var skippedDimensionMismatch = 0;
        foreach (var kv in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            if (!visibleIds.Contains(kv.Key)) continue;
            if (kv.Value.Length != queryVector.Length)
            {
                skippedDimensionMismatch++;
                continue;
            }
            var score = EmbeddingHelper.CosineSimilarity(queryVector, kv.Value);
            if (score > 0)
            {
                scored.Add((kv.Key, score));
            }
        }

        if (scored.Count == 0 && skippedDimensionMismatch > 0)
        {
            logger.LogWarning(
                "Vector search skipped {Count} incident embeddings because their dimensions did not match the query vector.",
                skippedDimensionMismatch);
            return (false, [], 0);
        }

        scored = scored
            .OrderByDescending(x => x.Score)
            .ToList();

        var total = scored.Count;
        var topIds = scored
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => x.IncidentId)
            .ToList();

        if (topIds.Count == 0)
        {
            return (true, [], total);
        }

        // Load incidents for this page, preserving vector-score order.
        var incidents = await baseQuery
            .Where(r => topIds.Contains(r.Id))
            .ToListAsync(ct);

        var incidentMap = incidents.ToDictionary(r => r.Id);
        var ordered = topIds
            .Select(id => incidentMap.GetValueOrDefault(id))
            .Where(r => r != null)
            .Cast<Incident>()
            .ToList();

        return (true, ordered, total);
    }

    // 没有相似告警页面，因此不提供菜谱的相似条目方法。

    private static string ComputeQueryCacheKey(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var sb = new StringBuilder(40);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
            if (sb.Length >= 40) break;
        }

        return sb.ToString();
    }

    private async Task<bool> ShouldAttemptVectorSearchAsync()
    {
        var enabled = await settingsService.GetBoolSettingAsync(SettingsMap.EnableEmbeddingBasedSearch);
        if (!enabled) return false;

        var endpoint = await settingsService.GetEmbeddingEndpointAsync();
        if (string.IsNullOrWhiteSpace(endpoint)) return false;

        var model = await settingsService.GetSettingValueAsync(SettingsMap.EmbeddingModel);
        return !string.IsNullOrWhiteSpace(model);
    }

    private async Task<float[]?> EmbedQueryAsync(string text, int expectedDimension, CancellationToken ct)
    {
        // Hash the full query text for the cache key. The QueryText column is capped at 40 chars with a
        // unique index, so we keep the first 40 hex chars of the SHA-256 digest. Hashing the full text
        // (instead of truncating the raw text) avoids collisions between queries that share a long common
        // prefix but differ later — those used to return each other's cached embedding.
        var cacheKey = ComputeQueryCacheKey(text);

        // Check database cache first.
        var cached = await db.SearchEmbeddings
            .FirstOrDefaultAsync(e => e.QueryText == cacheKey, ct);

        if (cached != null)
        {
            var vector = EmbeddingHelper.Deserialize(cached.Embedding);
            if (vector != null && vector.Length == expectedDimension)
            {
                // Throttled LRU bump: only touch the timestamp every AccessThrottle.
                var now = DateTime.UtcNow;
                if (now - cached.LastAccessedAt >= AccessThrottle)
                {
                    cached.LastAccessedAt = now;
                    await db.SaveChangesAsync(ct);
                }

                return vector;
            }

            db.SearchEmbeddings.Remove(cached);
            await db.SaveChangesAsync(ct);
        }

        // Compute embedding via Ollama.
        var instance = await settingsService.GetEmbeddingEndpointAsync();
        var model = await settingsService.GetSettingValueAsync(SettingsMap.EmbeddingModel);
        var token = await settingsService.GetEmbeddingTokenAsync();

        // Truncate query text to fit bge-m3's 8192-token context window.
        // Queries are typically short, but a user might paste a very long document.
        var maxChars = 8000;

        var http = httpClientFactory.CreateClient();
        var baseUri = new Uri(instance);
        var embedEndpoint = $"{baseUri.Scheme}://{baseUri.Authority}/api/embed?keep_alive=-1";
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(EmbedTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        float[] embedding;
        while (true)
        {
            var input = text.Length > maxChars ? text[..maxChars] : text;
            var requestBody = new { model, input };
            using var request = new HttpRequestMessage(HttpMethod.Post, embedEndpoint);
            request.Content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await http.SendAsync(request, linkedCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(linkedCts.Token);
                if (maxChars > 500 && EmbeddingHelper.IsContextLengthError(response.StatusCode, error))
                {
                    maxChars /= 2;
                    continue;
                }
                response.EnsureSuccessStatusCode();
            }

            var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(linkedCts.Token);
            if (result?.Embeddings == null || result.Embeddings.Length == 0)
            {
                return null;
            }

            embedding = result.Embeddings[0];
            break;
        }

        EmbeddingHelper.Normalize(embedding);

        // Cache the result in the database.
        var serialized = EmbeddingHelper.Serialize(embedding);
        try
        {
            var now = DateTime.UtcNow;
            db.SearchEmbeddings.Add(new SearchEmbedding
            {
                QueryText = cacheKey,
                Embedding = serialized,
                CreatedAt = now,
                LastAccessedAt = now
            });
            await db.SaveChangesAsync(ct);

            // Trim to MaxCachedQueries entries (circular buffer).
            await TrimCacheAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Race: another request already cached this query. Ignore.
            logger.LogWarning(ex, "Failed to cache query embedding for '{Query}'. Likely a concurrent duplicate.", text);
        }

        return embedding;
    }

    /// <summary>Remove least-recently-accessed entries if the cache exceeds the configured limit.</summary>
    private async Task TrimCacheAsync(CancellationToken ct)
    {
        var limit = await settingsService.GetIntSettingAsync(SettingsMap.EmbeddingQueryCacheLimit);
        if (limit <= 0) limit = 2000;

        var count = await db.SearchEmbeddings.CountAsync(ct);
        if (count <= limit) return;

        var toDelete = await db.SearchEmbeddings
            .OrderBy(e => e.LastAccessedAt)
            .Take(count - limit)
            .ToListAsync(ct);

        if (toDelete.Count > 0)
        {
            db.SearchEmbeddings.RemoveRange(toDelete);
            await db.SaveChangesAsync(ct);
        }
    }

    private class OllamaEmbedResponse
    {
        [JsonProperty("embeddings")]
        public float[][]? Embeddings { get; set; }
    }
}

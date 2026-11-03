using Aiursoft.AlertCenter.Entities;
using Aiursoft.Canon.BackgroundJobs;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services.BackgroundJobs;

public sealed class GenerateIncidentEmbeddingsJob(TemplateDbContext db, EmbeddingClient client,
    IncidentEmbeddingJobGate gate, ILogger<GenerateIncidentEmbeddingsJob> logger) : IBackgroundJob
{
    public string Name => "Generate incident embeddings";
    public string Description => "Incrementally indexes incident titles, content and application names for semantic search. " +
        "Scans up to 1000 incidents and processes up to 50 per run; changing the model or server rebuilds the index automatically.";

    public async Task ExecuteAsync()
    {
        if (!await gate.Semaphore.WaitAsync(0)) return;
        try
        {
            var configuration = await client.GetConfigurationAsync();
            if (configuration == null) return;
            var pending = new List<EmbeddingSource>();
            var scanned = 0;
            var cursor = gate.LastScannedId;
            var scanEnd = cursor;
            var wrapped = false;
            while (pending.Count < 50 && scanned < 1000)
            {
                var source = db.Incidents.AsNoTracking();
                if (cursor is { } afterId) source = source.Where(incident => incident.Id.CompareTo(afterId) > 0);
                if (wrapped && scanEnd.HasValue) source = source.Where(incident => incident.Id.CompareTo(scanEnd.Value) <= 0);
                var size = Math.Min(200, 1000 - scanned);
                var batch = await source.OrderBy(incident => incident.Id).Take(size)
                    .Select(incident => new
                    {
                        incident.Id,
                        incident.Title,
                        incident.Content,
                        ApplicationName = incident.Application.Name,
                        ModelKey = incident.SearchEmbedding == null ? null : incident.SearchEmbedding.ModelKey,
                        ContentHash = incident.SearchEmbedding == null ? null : incident.SearchEmbedding.ContentHash
                    }).ToListAsync();
                if (batch.Count == 0)
                {
                    if (wrapped || !scanEnd.HasValue) break;
                    cursor = null;
                    wrapped = true;
                    continue;
                }
                foreach (var item in batch)
                {
                    scanned++;
                    cursor = item.Id;
                    var text = IncidentEmbeddingText.Build(item.ApplicationName, item.Title, item.Content);
                    var hash = IncidentEmbeddingText.Hash(text);
                    if (item.ModelKey != configuration.ModelKey || item.ContentHash != hash)
                        pending.Add(new EmbeddingSource(item.Id, text, hash));
                    if (pending.Count == 50) break;
                }
            }
            // 跨轮次推进扫描位置，避免持续失败的记录一直占满候选窗口。
            gate.LastScannedId = cursor;
            var failures = new List<Exception>();
            foreach (var batch in pending.Chunk(10))
            {
                float[][] vectors;
                try
                {
                    vectors = await client.GenerateAsync(configuration, batch.Select(item => item.Text).ToArray(),
                        TimeSpan.FromSeconds(120), CancellationToken.None);
                }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
                {
                    failures.Add(exception);
                    logger.LogWarning("Incident embedding batch failed ({ErrorType}); continuing with remaining batches.", exception.GetType().Name);
                    continue;
                }
                var ids = batch.Select(item => item.Id).ToArray();
                var current = await db.Incidents.AsNoTracking().Where(incident => ids.Contains(incident.Id))
                    .Select(incident => new { incident.Id, incident.Title, incident.Content, ApplicationName = incident.Application.Name })
                    .ToDictionaryAsync(incident => incident.Id);
                var existing = await db.IncidentEmbeddings.Where(item => ids.Contains(item.IncidentId)).ToDictionaryAsync(item => item.IncidentId);
                for (var index = 0; index < batch.Length; index++)
                {
                    var source = batch[index];
                    if (!current.TryGetValue(source.Id, out var incident) || source.Hash != IncidentEmbeddingText.Hash(
                            IncidentEmbeddingText.Build(incident.ApplicationName, incident.Title, incident.Content))) continue;
                    if (!existing.TryGetValue(source.Id, out var embedding))
                    {
                        embedding = new IncidentEmbedding { IncidentId = source.Id };
                        db.IncidentEmbeddings.Add(embedding);
                    }
                    embedding.ModelKey = configuration.ModelKey;
                    embedding.ContentHash = source.Hash;
                    embedding.Vector = EmbeddingVector.Serialize(vectors[index]);
                    embedding.GeneratedAtUtc = DateTime.UtcNow;
                }
                await db.SaveChangesAsync();
            }
            if (failures.Count > 0)
                throw new AggregateException("Some incident embedding batches failed; remaining batches were processed.", failures);
            logger.LogInformation("Incident embedding indexing completed; processed {Count} candidates.", pending.Count);
        }
        finally { gate.Semaphore.Release(); }
    }

    private sealed record EmbeddingSource(Guid Id, string Text, string Hash);
}

public sealed class IncidentEmbeddingJobGate : IDisposable
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
    internal Guid? LastScannedId { get; set; }
    public void Dispose() => Semaphore.Dispose();
}

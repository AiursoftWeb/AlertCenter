using Aiursoft.AlertCenter.Entities;
using Aiursoft.Canon.BackgroundJobs;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services.BackgroundJobs;

public sealed class GenerateIncidentEmbeddingsJob(TemplateDbContext db, EmbeddingClient client,
    IncidentEmbeddingJobGate gate, ILogger<GenerateIncidentEmbeddingsJob> logger) : IBackgroundJob
{
    public string Name => "Generate incident embeddings";
    public string Description => "Incrementally indexes incident titles, content and application names for semantic search. " +
        "Processes up to 50 incidents per run; changing the model or server rebuilds the index automatically.";

    public async Task ExecuteAsync()
    {
        if (!await gate.Semaphore.WaitAsync(0)) return;
        try
        {
            var configuration = await client.GetConfigurationAsync();
            if (configuration == null) return;
            var pending = new List<EmbeddingSource>();
            var offset = 0;
            while (pending.Count < 50)
            {
                var batch = await db.Incidents.AsNoTracking().OrderBy(incident => incident.Id).Skip(offset).Take(200)
                    .Select(incident => new
                    {
                        incident.Id,
                        incident.Title,
                        incident.Content,
                        ApplicationName = incident.Application.Name,
                        ModelKey = incident.SearchEmbedding == null ? null : incident.SearchEmbedding.ModelKey,
                        ContentHash = incident.SearchEmbedding == null ? null : incident.SearchEmbedding.ContentHash
                    }).ToListAsync();
                foreach (var item in batch)
                {
                    var text = IncidentEmbeddingText.Build(item.ApplicationName, item.Title, item.Content);
                    var hash = IncidentEmbeddingText.Hash(text);
                    if (item.ModelKey != configuration.ModelKey || item.ContentHash != hash)
                        pending.Add(new EmbeddingSource(item.Id, text, hash));
                    if (pending.Count == 50) break;
                }
                if (batch.Count < 200) break;
                offset += batch.Count;
            }
            foreach (var batch in pending.Chunk(10))
            {
                var vectors = await client.GenerateAsync(configuration, batch.Select(item => item.Text).ToArray(),
                    TimeSpan.FromSeconds(120), CancellationToken.None);
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
            logger.LogInformation("Incident embedding indexing completed; processed {Count} candidates.", pending.Count);
        }
        finally { gate.Semaphore.Release(); }
    }

    private sealed record EmbeddingSource(Guid Id, string Text, string Hash);
}

public sealed class IncidentEmbeddingJobGate : IDisposable
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
    public void Dispose() => Semaphore.Dispose();
}

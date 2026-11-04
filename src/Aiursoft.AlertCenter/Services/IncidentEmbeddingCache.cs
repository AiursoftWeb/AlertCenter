using System.Diagnostics.CodeAnalysis;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.WebTools;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

/// <summary>
/// In-memory cache of incident embedding vectors for fast cosine-similarity search.
/// 每个告警只有一个向量，不需要菜谱的多语言向量列表。
/// Loaded at startup and refreshed periodically via RefreshEmbeddingCacheJob.
/// </summary>
[ExcludeFromCodeCoverage]
public class IncidentEmbeddingCache(ILogger<IncidentEmbeddingCache> logger)
{
    private Dictionary<Guid, float[]> _cache = [];
    private readonly Lock _lock = new();
    private const int MaxEntries = 10_000;

    public int Count
    {
        get { lock (_lock) return _cache.Count; }
    }

    /// <summary>Returns a snapshot of the current cache for search.</summary>
    public Dictionary<Guid, float[]> Snapshot()
    {
        lock (_lock)
        {
            return new Dictionary<Guid, float[]>(_cache);
        }
    }

    public async Task LoadAsync(TemplateDbContext db)
    {
        var newCache = new Dictionary<Guid, float[]>();

        // 告警持续产生，在 SQL 层仅加载最新的 MaxEntries 条向量。
        var incidentEmbeddings = await db.Incidents
            .AsNoTracking()
            .Where(r => r.Embedding != null)
            .OrderByDescending(r => r.ReceivedAtUtc)
            .Take(MaxEntries)
            .Select(r => new { r.Id, r.Embedding })
            .ToListAsync();

        foreach (var item in incidentEmbeddings)
        {
            var vector = EmbeddingHelper.Deserialize(item.Embedding!);
            if (vector != null)
            {
                newCache[item.Id] = vector;
            }
            else
            {
                logger.LogWarning(
                    "Failed to deserialize embedding for incident {IncidentId}: byte length {Length} is not a multiple of 4.",
                    item.Id, item.Embedding!.Length);
            }
        }

        lock (_lock)
        {
            _cache = newCache;
        }
    }
}

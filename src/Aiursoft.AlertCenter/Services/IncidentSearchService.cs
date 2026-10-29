using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public sealed class IncidentSearchService(TemplateDbContext db, IApplicationAccessService access,
    EmbeddingClient embeddings, ILogger<IncidentSearchService> logger)
{
    public async Task<IncidentSearchViewModel> SearchAsync(ClaimsPrincipal user, string query, int page,
        CancellationToken cancellationToken)
    {
        var applicationIds = await access.GetAccessibleApplicationIdsAsync(user, cancellationToken: cancellationToken);
        var policy = await new SeverityPolicyStore(db).LoadAsync(cancellationToken);
        var source = db.Incidents.AsNoTracking().Where(incident => applicationIds.Contains(incident.ApplicationId))
            .Where(policy.VisibleIncidents);
        var model = new IncidentSearchViewModel { Query = query, Page = page };
        if (applicationIds.Count == 0) return model;
        try
        {
            var configuration = await embeddings.GetConfigurationAsync();
            if (configuration != null && await SearchVectorsAsync(source, configuration, model, cancellationToken)) return model;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            logger.LogWarning("Semantic search unavailable ({ErrorType}); using keyword search.", exception.GetType().Name);
        }

        var keyword = query.ToLowerInvariant();
        var matches = source.Where(incident => incident.Title.ToLower().Contains(keyword) ||
            (incident.Content != null && incident.Content.ToLower().Contains(keyword)) || incident.Application.Name.ToLower().Contains(keyword));
        model.TotalCount = await matches.CountAsync(cancellationToken);
        model.Incidents = await matches.Include(incident => incident.Application)
            .OrderByDescending(incident => incident.Title.ToLower() == keyword)
            .ThenByDescending(incident => incident.Title.ToLower().StartsWith(keyword))
            .ThenByDescending(incident => incident.Title.ToLower().Contains(keyword))
            .ThenByDescending(incident => incident.ReceivedAtUtc).ThenBy(incident => incident.Id)
            .Skip((page - 1) * IncidentSearchViewModel.PageSize).Take(IncidentSearchViewModel.PageSize).ToListAsync(cancellationToken);
        return model;
    }

    private async Task<bool> SearchVectorsAsync(IQueryable<Incident> source, EmbeddingConfiguration configuration,
        IncidentSearchViewModel model, CancellationToken cancellationToken)
    {
        var candidates = await source.Where(incident => incident.SearchEmbedding != null && incident.SearchEmbedding.ModelKey == configuration.ModelKey)
            .Select(incident => new
            {
                incident.Id,
                incident.Title,
                incident.Content,
                ApplicationName = incident.Application.Name,
                incident.SearchEmbedding!.ContentHash,
                incident.SearchEmbedding.Vector
            }).ToListAsync(cancellationToken);
        var valid = candidates.Where(candidate => candidate.ContentHash == IncidentEmbeddingText.Hash(
                IncidentEmbeddingText.Build(candidate.ApplicationName, candidate.Title, candidate.Content)))
            .Select(candidate => new { candidate.Id, Vector = EmbeddingVector.Deserialize(candidate.Vector) })
            .Where(candidate => candidate.Vector != null).ToList();
        if (valid.Count == 0) return false;
        var queryVector = await GetQueryVectorAsync(configuration, model.Query, cancellationToken);
        var compatible = valid.Where(candidate => candidate.Vector!.Length == queryVector.Length).ToList();
        if (compatible.Count == 0) return false;
        var ranked = compatible.Select(candidate => new { candidate.Id, Score = EmbeddingVector.Similarity(queryVector, candidate.Vector!) })
            .Where(candidate => candidate.Score > 0).OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Id).ToList();
        var ids = ranked.Skip((model.Page - 1) * IncidentSearchViewModel.PageSize).Take(IncidentSearchViewModel.PageSize)
            .Select(candidate => candidate.Id).ToList();
        var incidents = await source.Include(incident => incident.Application).Where(incident => ids.Contains(incident.Id))
            .ToDictionaryAsync(incident => incident.Id, cancellationToken);
        model.Incidents = ids.Where(incidents.ContainsKey).Select(id => incidents[id]).ToList();
        model.TotalCount = ranked.Count;
        model.UsedAiSearch = true;
        return true;
    }

    private async Task<float[]> GetQueryVectorAsync(EmbeddingConfiguration configuration, string query, CancellationToken cancellationToken)
    {
        var key = IncidentEmbeddingText.Hash($"{configuration.ModelKey}\n{query}");
        var cached = await db.SearchEmbeddings.SingleOrDefaultAsync(item => item.Id == key, cancellationToken);
        var vector = cached == null ? null : EmbeddingVector.Deserialize(cached.Vector);
        if (vector == null)
        {
            vector = (await embeddings.GenerateAsync(configuration, [query], TimeSpan.FromSeconds(10), cancellationToken))[0];
            if (cached == null)
            {
                cached = new SearchEmbedding { Id = key };
                db.SearchEmbeddings.Add(cached);
            }
            cached.Vector = EmbeddingVector.Serialize(vector);
        }
        if (cached!.LastAccessedAtUtc < DateTime.UtcNow.AddHours(-1)) cached.LastAccessedAtUtc = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            var expired = await db.SearchEmbeddings.OrderByDescending(item => item.LastAccessedAtUtc).ThenBy(item => item.Id)
                .Skip(configuration.QueryCacheLimit).ToListAsync(cancellationToken);
            db.SearchEmbeddings.RemoveRange(expired);
            if (expired.Count > 0) await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // 缓存写入可能与同一查询的并发请求冲突，不影响已经取得的查询向量。
            logger.LogWarning("Could not persist the query embedding cache.");
            db.ChangeTracker.Clear();
        }
        return vector;
    }
}

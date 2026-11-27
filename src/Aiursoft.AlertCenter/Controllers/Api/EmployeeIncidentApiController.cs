using System.Security.Claims;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AiurProtocol.Server;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.SDK.Models;
using Aiursoft.AlertCenter.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Controllers.Api;

public abstract class EmployeeIncidentApiController(
    TemplateDbContext db, IApplicationAccessService access, IStringLocalizerFactory localizers) : ControllerBase
{
    protected TemplateDbContext Db { get; } = db;
    protected IApplicationAccessService Access { get; } = access;
    protected IStringLocalizerFactory Localizers { get; } = localizers;

    protected async Task<IActionResult> ListIncidents(MobileIncidentQuery filter, CancellationToken cancellationToken,
        bool manageableOnly = false, bool unresolvedOnly = false, bool applyVisibilityPolicy = true)
    {
        var ids = await Access.GetAccessibleApplicationIdsAsync(User,
            manageableOnly ? ApplicationPermission.UpdateIncidents : ApplicationPermission.ViewIncidents, cancellationToken);
        if (filter.ApplicationId.HasValue && !ids.Contains(filter.ApplicationId.Value))
            return this.Protocol(Code.Unauthorized, "You cannot view this application's incidents.");
        var query = Db.Incidents.AsNoTracking().Include(item => item.Application).Include(item => item.Owner)
            .Where(item => ids.Contains(item.ApplicationId));
        if (filter.ApplicationId.HasValue) query = query.Where(item => item.ApplicationId == filter.ApplicationId.Value);
        if (filter.OnlyMine)
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
            query = query.Where(item => item.OwnerId == id);
        }
        if (unresolvedOnly) query = query.Where(item => item.Status != Incident.Resolved);
        if (filter.Status != "all") query = query.Where(item => item.Status == filter.Status);
        if (filter.Acknowledged.HasValue)
            query = query.Where(item => (item.AcknowledgedAtUtc != null) == filter.Acknowledged.Value);
        if (!string.IsNullOrWhiteSpace(filter.Keyword))
        {
            var keyword = filter.Keyword.Trim().ToLowerInvariant();
            query = query.Where(item => item.Title.ToLower().Contains(keyword) ||
                (item.Content != null && item.Content.ToLower().Contains(keyword)) || item.Application.Name.ToLower().Contains(keyword));
        }
        if (filter.Level == null && applyVisibilityPolicy)
        {
            var policy = await new SeverityPolicyStore(Db).LoadAsync(cancellationToken);
            query = query.Where(policy.VisibleIncidents);
        }
        else if (filter.Level != null)
        {
            var levels = filter.Level.StartsWith("lte-", StringComparison.Ordinal)
                ? new[] { "sev0", "sev1", "sev2", "sev3", "sev4" }.Take(filter.Level[^1] - '0' + 1).ToArray()
                : [filter.Level];
            query = query.Where(item => levels.Contains(item.Level));
        }
        var incidents = await query.OrderBy(item => item.Status == Incident.Resolved)
            .ThenByDescending(item => item.ReceivedAtUtc).ThenByDescending(item => item.Id)
            .Skip((filter.Page - 1) * 20).Take(21).ToListAsync(cancellationToken);
        return this.Protocol(new MobileIncidentsResponse
        {
            Code = Code.ResultShown, Message = "Incidents retrieved.", Page = filter.Page,
            HasNextPage = incidents.Count > 20, Incidents = incidents.Take(20).Select(item => ToDto(item)).ToList()
        });
    }

    protected async Task<IActionResult> GetIncident(Guid id, int page, CancellationToken cancellationToken,
        bool allowUpdates = true)
    {
        var incident = await Db.Incidents.AsNoTracking().Include(item => item.Application).Include(item => item.Owner)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (incident == null) return this.Protocol(Code.NotFound, "Incident not found.");
        if (!await Access.CanAccessAsync(User, incident.ApplicationId, cancellationToken: cancellationToken))
            return this.Protocol(Code.Unauthorized, "You cannot view this incident.");
        page = Math.Clamp(page, 1, 1000000);
        var keys = await Db.IncidentTimelineEntries.AsNoTracking().Where(item => item.IncidentId == id)
            .Select(item => new { item.Id, item.CreatedAtUtc, IsComment = false })
            .Concat(Db.IncidentComments.AsNoTracking().Where(item => item.IncidentId == id)
                .Select(item => new { item.Id, item.CreatedAtUtc, IsComment = true }))
            .OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.IsComment).ThenByDescending(item => item.Id)
            .Skip((page - 1) * 20).Take(21).ToListAsync(cancellationToken);
        var commentIds = keys.Take(20).Where(item => item.IsComment).Select(item => item.Id).ToArray();
        var entryIds = keys.Take(20).Where(item => !item.IsComment).Select(item => item.Id).ToArray();
        var comments = await Db.IncidentComments.AsNoTracking().Where(item => commentIds.Contains(item.Id)).ToListAsync(cancellationToken);
        var entries = await Db.IncidentTimelineEntries.AsNoTracking().Include(item => item.Actor)
            .Where(item => entryIds.Contains(item.Id)).ToListAsync(cancellationToken);
        var original = await OnCallEmailQueue.GetOriginalSnapshotAsync(Db, id, cancellationToken);
        var oce = incident.Application.OnCallGroupId.HasValue
            ? await new OnCallOceService(Db, new StringLocalizer<OnCallOceService>(Localizers))
                .QueryAsync(incident.Application.OnCallGroupId.Value, DateTime.UtcNow, cancellationToken) : null;
        var activities = new List<MobileActivity>();
        foreach (var key in keys.Take(20))
        {
            if (key.IsComment)
            {
                var comment = comments.Single(item => item.Id == key.Id);
                activities.Add(new MobileActivity
                {
                    Id = key.Id, Type = "comment", ActorName = comment.AuthorName,
                    Note = comment.DeletedAtUtc == null ? comment.Markdown : "Comment deleted.", CreatedAtUtc = Utc(key.CreatedAtUtc)
                });
            }
            else
            {
                var entry = entries.Single(item => item.Id == key.Id);
                activities.Add(new MobileActivity
                {
                    Id = key.Id, Type = entry.Type, ActorName = entry.ActorDisplayName, Note = entry.Note,
                    PreviousStatus = entry.PreviousStatus, NewStatus = entry.NewStatus,
                    PreviousLevel = entry.PreviousLevel, NewLevel = entry.NewLevel,
                    NewOwnerName = entry.NewOwnerName, CreatedAtUtc = Utc(key.CreatedAtUtc)
                });
            }
        }
        var dto = ToDto(incident);
        dto.CanManage = allowUpdates && await Access.CanAccessAsync(User, incident.ApplicationId, ApplicationPermission.UpdateIncidents, cancellationToken);
        return this.Protocol(new MobileIncidentResponse
        {
            Code = Code.ResultShown, Message = "Incident retrieved.", Incident = dto, Activities = activities,
            Page = page, HasNextPage = keys.Count > 20,
            OriginalOces = original?.Recipients.OrderBy(item => item.DisplayName, StringComparer.CurrentCulture).Select(item => item.DisplayName).ToList() ?? [],
            CurrentOces = oce?.Engineers.Select(item => item.DisplayName).ToList() ?? [], CurrentOceError = oce?.Error
        });
    }

    protected async Task<IActionResult> ExecuteIncident(Guid id, string operation, MobileIncidentAction request,
        CancellationToken cancellationToken)
    {
        var type = operation switch
        {
            "acknowledge" => IncidentTimelineEntry.Acked,
            "take-ownership" => IncidentTimelineEntry.OwnershipChanged,
            "mitigate" => IncidentTimelineEntry.Mitigated,
            "resolve" => IncidentTimelineEntry.Resolved,
            "reopen" => IncidentTimelineEntry.Reopened,
            "severity" => IncidentTimelineEntry.LevelChanged,
            _ => null
        };
        if (type == null) return this.Protocol(Code.NotFound, "Unknown incident action.");
        IncidentNoteRequest model = type == IncidentTimelineEntry.LevelChanged
            ? new IncidentLevelRequest { Level = request.Level ?? string.Empty } : new IncidentNoteRequest();
        model.IncidentId = id;
        model.Version = request.Version;
        model.Note = request.Note;
        var result = await new IncidentWorkflowService(Db, Access, new StringLocalizer<IncidentWorkflowService>(Localizers), Localizers)
            .ExecuteAsync(User, model, type, cancellationToken);
        return ActionResult(result.StatusCode, result.Error);
    }

    protected IActionResult ActionResult(int status, string? error) => this.Protocol(status switch
    {
        200 => Code.JobDone, 403 => Code.Unauthorized, 404 => Code.NotFound, 409 => Code.Conflict, _ => Code.InvalidInput
    }, error ?? (status == 200 ? "Incident updated." : "The incident could not be updated."));

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static DateTimeOffset? Utc(DateTime? value) => value.HasValue ? Utc(value.Value) : null;
    private static MobileIncident ToDto(Incident item) => new()
    {
        Id = item.Id, Version = item.Version, ShortId = item.ShortId,
        ApplicationId = item.ApplicationId, ApplicationName = item.Application.Name,
        Title = item.Title, Content = item.Content, ContextJson = item.ContextJson, MetadataJson = item.MetadataJson,
        Level = item.Level, Status = item.Status, OwnerName = item.Owner?.DisplayName ?? item.OwnerDisplayName,
        InvestigateUrl = item.InvestigateUrl,
        OccurredAtUtc = Utc(item.OccurredAtUtc), ReceivedAtUtc = Utc(item.ReceivedAtUtc),
        AcknowledgedAtUtc = Utc(item.AcknowledgedAtUtc), MitigatedAtUtc = Utc(item.MitigatedAtUtc), ResolvedAtUtc = Utc(item.ResolvedAtUtc)
    };
}

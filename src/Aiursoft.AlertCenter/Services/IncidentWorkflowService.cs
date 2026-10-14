using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public sealed record IncidentActionResult(int StatusCode, string? Error = null);

public class IncidentWorkflowService(TemplateDbContext db, IApplicationAccessService applicationAccess)
{
    public async Task<IncidentActionResult> ExecuteAsync(ClaimsPrincipal user, IncidentNoteRequest request, string action,
        CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.Include(item => item.Owner)
            .SingleOrDefaultAsync(item => item.Id == request.IncidentId, cancellationToken);
        if (incident == null) return new(404);
        var now = DateTime.UtcNow;
        if (!await new IncidentAccessService(db, applicationAccess).CanManageAsync(user, incident, now, cancellationToken)) return new(403);
        if (request.Version == Guid.Empty) return new(400, "事故版本不能为空，请刷新页面。");
        if (request.Version != incident.Version) return new(409, "事故已被其他工程师修改，请刷新后重试。");
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note?.Length > 4000) return new(400, "理由或处理进展不能超过 4000 个字符。");
        if ((action is IncidentTimelineEntry.Mitigated or IncidentTimelineEntry.Resolved or IncidentTimelineEntry.LevelChanged or IncidentTimelineEntry.Updated) && note == null)
            return new(400, "此操作必须填写理由或处理进展。");
        var allowed = action switch
        {
            IncidentTimelineEntry.Acked => incident.Status == Incident.New,
            IncidentTimelineEntry.OwnershipChanged or IncidentTimelineEntry.Mitigated => incident.Status == Incident.Acked,
            IncidentTimelineEntry.Resolved => incident.Status == Incident.Mitigated,
            IncidentTimelineEntry.Reopened => incident.Status is Incident.Mitigated or Incident.Resolved,
            IncidentTimelineEntry.LevelChanged or IncidentTimelineEntry.Updated => incident.Status is Incident.New or Incident.Acked or Incident.Mitigated,
            _ => false
        };
        if (!allowed) return new(400, "当前事故状态不允许此操作。");
        var actorId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == actorId, cancellationToken);
        if (actor == null) return new(403);
        var level = (request as IncidentLevelRequest)?.Level;
        if (action == IncidentTimelineEntry.LevelChanged && level is not ("sev0" or "sev1" or "sev2" or "sev3" or "sev4"))
            return new(400, "请选择 sev0 至 sev4 的事故级别。");
        if (action == IncidentTimelineEntry.LevelChanged && level == incident.AlertLevel)
            return new(400, "事故已处于所选级别。");
        if (action == IncidentTimelineEntry.OwnershipChanged && incident.OwnerId == actorId)
            return new(400, "你已经是当前负责人。");
        var entry = new IncidentTimelineEntry
        {
            Incident = incident, Type = action, ActorId = actorId, Note = note, CreatedAtUtc = now,
            PreviousStatus = incident.Status, PreviousOwnerId = incident.OwnerId,
            PreviousOwnerName = incident.Owner?.DisplayName ?? incident.OwnerDisplayName, PreviousLevel = incident.AlertLevel
        };
        switch (action)
        {
            case IncidentTimelineEntry.Acked:
                incident.Status = Incident.Acked;
                incident.AcknowledgedAtUtc = now;
                incident.OwnerId = actorId;
                incident.OwnerDisplayName = actor.DisplayName;
                break;
            case IncidentTimelineEntry.OwnershipChanged:
                incident.OwnerId = actorId;
                incident.OwnerDisplayName = actor.DisplayName;
                break;
            case IncidentTimelineEntry.Mitigated:
                incident.Status = Incident.Mitigated;
                incident.MitigatedAtUtc = now;
                break;
            case IncidentTimelineEntry.Resolved:
                incident.Status = Incident.Resolved;
                incident.ResolvedAtUtc = now;
                incident.ActiveMatchKey = null;
                break;
            case IncidentTimelineEntry.Reopened:
                var matchKey = string.IsNullOrWhiteSpace(incident.IncidentKey)
                    ? $"alert:{incident.AlertTitle}\u001f{incident.MatchLevel}" : $"key:{incident.IncidentKey}";
                if (await db.Incidents.AsNoTracking().AnyAsync(item => item.Id != incident.Id && item.ApplicationId == incident.ApplicationId &&
                    item.Status != Incident.Resolved && (item.ActiveMatchKey == matchKey ||
                        (item.ActiveMatchKey == null && (incident.IncidentKey != null
                            ? item.IncidentKey == incident.IncidentKey
                            : item.IncidentKey == null && item.AlertTitle == incident.AlertTitle && item.MatchLevel == incident.MatchLevel))), cancellationToken))
                    return new(409, "已有相同关联规则的活动事故，请先处置该事故，不能合并或重复重开。");
                incident.Status = Incident.New;
                incident.ActiveMatchKey = matchKey;
                incident.OwnerId = null;
                incident.OwnerDisplayName = null;
                incident.AcknowledgedAtUtc = null;
                incident.MitigatedAtUtc = null;
                incident.ResolvedAtUtc = null;
                break;
            case IncidentTimelineEntry.LevelChanged:
                incident.AlertLevel = level!;
                break;
        }
        entry.NewStatus = incident.Status;
        entry.NewOwnerId = incident.OwnerId;
        entry.NewOwnerName = incident.OwnerId == entry.PreviousOwnerId ? entry.PreviousOwnerName : incident.OwnerDisplayName;
        entry.NewLevel = incident.AlertLevel;
        incident.Version = Guid.NewGuid();
        db.IncidentTimelineEntries.Add(entry);
        if (action == IncidentTimelineEntry.Reopened)
            await OnCallEmailQueue.CreateReopenAsync(db, entry, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new(409, "事故已被其他工程师修改，请刷新后重试。");
        }
        catch (DbUpdateException) when (action == IncidentTimelineEntry.Reopened)
        {
            db.ChangeTracker.Clear();
            var conflicting = await db.Incidents.AsNoTracking().AnyAsync(item => item.Id != request.IncidentId &&
                item.ApplicationId == incident.ApplicationId && item.ActiveMatchKey == incident.ActiveMatchKey, cancellationToken);
            if (!conflicting) throw;
            return new(409, "关联规则已被另一活动事故占用，请刷新后重试。");
        }
        return new(200);
    }
}

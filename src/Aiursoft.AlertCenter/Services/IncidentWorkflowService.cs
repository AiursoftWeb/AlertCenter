using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public sealed record IncidentActionResult(int StatusCode, string? Error = null);

public class IncidentWorkflowService(TemplateDbContext db, IApplicationAccessService applicationAccess, IStringLocalizer<IncidentWorkflowService> localizer, IStringLocalizerFactory localizerFactory)
{
    public async Task<IncidentActionResult> ExecuteAsync(ClaimsPrincipal user, IncidentNoteRequest request, string action,
        CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.Include(item => item.Owner)
            .SingleOrDefaultAsync(item => item.Id == request.IncidentId, cancellationToken);
        if (incident == null) return new(404);
        var now = DateTime.UtcNow;
        if (!await new IncidentAccessService(db, applicationAccess, localizerFactory).CanManageAsync(user, incident, now, cancellationToken)) return new(403);
        if (request.Version == Guid.Empty) return new(400, localizer["The incident version cannot be empty. Please refresh the page."].Value);
        if (request.Version != incident.Version) return new(409, localizer["The incident has been modified by another engineer. Please refresh and try again."].Value);
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note?.Length > 4000) return new(400, localizer["Reason or progress cannot exceed 4000 characters."].Value);
        if ((action is IncidentTimelineEntry.Mitigated or IncidentTimelineEntry.Resolved or IncidentTimelineEntry.LevelChanged or IncidentTimelineEntry.Updated) && note == null)
            return new(400, localizer["A reason or progress update is required for this action."].Value);
        var allowed = action switch
        {
            IncidentTimelineEntry.Acked => incident.Status == Incident.New,
            IncidentTimelineEntry.OwnershipChanged or IncidentTimelineEntry.Mitigated => incident.Status == Incident.Acked,
            IncidentTimelineEntry.Resolved => incident.Status == Incident.Mitigated,
            IncidentTimelineEntry.Reopened => incident.Status is Incident.Mitigated or Incident.Resolved,
            IncidentTimelineEntry.LevelChanged or IncidentTimelineEntry.Updated => incident.Status is Incident.New or Incident.Acked or Incident.Mitigated,
            _ => false
        };
        if (!allowed) return new(400, localizer["The current incident status does not allow this operation."].Value);
        var actorId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == actorId, cancellationToken);
        if (actor == null) return new(403);
        var level = (request as IncidentLevelRequest)?.Level;
        if (action == IncidentTimelineEntry.LevelChanged && level is not ("sev0" or "sev1" or "sev2" or "sev3" or "sev4"))
            return new(400, localizer["Please select an incident severity level from sev0 to sev4."].Value);
        if (action == IncidentTimelineEntry.LevelChanged && level == incident.AlertLevel)
            return new(400, localizer["The incident is already at the selected level."].Value);
        if (action == IncidentTimelineEntry.OwnershipChanged && incident.OwnerId == actorId)
            return new(400, localizer["You are already the current assignee."].Value);
        var entry = new IncidentTimelineEntry
        {
            Incident = incident, Type = action, ActorId = actor.Id, ActorIdSnapshot = actor.Id,
            ActorName = actor.DisplayName, Note = note, CreatedAtUtc = now,
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
                    return new(409, localizer["An incident with the same linked rule already exists. Please resolve that incident first; it cannot be merged or reopened."].Value);
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
            await OnCallEmailQueue.CreateReopenAsync(db, entry, cancellationToken, localizer: localizerFactory.Create(typeof(OnCallEmailQueue)), localizerFactory: localizerFactory);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new(409, localizer["The incident has been modified by another engineer. Please refresh and try again."].Value);
        }
        catch (DbUpdateException) when (action == IncidentTimelineEntry.Reopened)
        {
            db.ChangeTracker.Clear();
            var conflicting = await db.Incidents.AsNoTracking().AnyAsync(item => item.Id != request.IncidentId &&
                item.ApplicationId == incident.ApplicationId && item.ActiveMatchKey == incident.ActiveMatchKey, cancellationToken);
            if (!conflicting) throw;
            return new(409, localizer["The linked rule is already occupied by another active incident. Please refresh and try again."].Value);
        }
        return new(200);
    }
}

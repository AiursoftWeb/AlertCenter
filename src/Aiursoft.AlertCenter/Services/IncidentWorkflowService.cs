using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public sealed record IncidentActionResult(int StatusCode, string? Error = null);

public sealed record ApplicationIncidentResolutionResult(int StatusCode, Incident? Incident = null);

public class IncidentWorkflowService(TemplateDbContext db, IApplicationAccessService applicationAccess, IStringLocalizer<IncidentWorkflowService> localizer, IStringLocalizerFactory localizerFactory)
{
    public static async Task<ApplicationIncidentResolutionResult> ResolveForApplicationAsync(
        TemplateDbContext context, Guid applicationId, Guid incidentId, Guid version, string reason,
        CancellationToken cancellationToken)
    {
        var incident = await context.Incidents.Include(item => item.Owner)
            .SingleOrDefaultAsync(item => item.Id == incidentId && item.ApplicationId == applicationId, cancellationToken);
        if (incident == null) return new(404);
        if (incident.Status == Incident.Resolved) return new(202, incident);
        if (incident.Version != version) return new(409);

        var now = DateTime.UtcNow;
        now = now.AddTicks(-(now.Ticks % 10));
        var ownerName = incident.Owner?.DisplayName ?? incident.OwnerDisplayName;
        var entry = new IncidentTimelineEntry
        {
            Incident = incident, Type = IncidentTimelineEntry.Resolved,
            ActorName = "Application", ActorIdSnapshot = $"application:{applicationId}",
            Note = reason.Trim(), CreatedAtUtc = now,
            PreviousStatus = incident.Status, NewStatus = Incident.Resolved,
            PreviousOwnerId = incident.OwnerId, NewOwnerId = incident.OwnerId,
            PreviousOwnerName = ownerName, NewOwnerName = ownerName,
            PreviousLevel = incident.Level, NewLevel = incident.Level
        };
        context.IncidentTimelineEntries.Add(entry);
        ApplyResolution(incident, now);
        incident.Version = Guid.NewGuid();
        await QueuedIncidentDispatcher.DispatchEmailEventAsync(context, entry, cancellationToken);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            context.ChangeTracker.Clear();
            return new(409);
        }
        return new(200, incident);
    }

    public static async Task<ApplicationIncidentResolutionResult> ChangeSeverityForApplicationAsync(
        TemplateDbContext context, Guid applicationId, Guid incidentId, Guid version, string reason, string level,
        CancellationToken cancellationToken)
    {
        var incident = await context.Incidents.Include(item => item.Owner)
            .SingleOrDefaultAsync(item => item.Id == incidentId && item.ApplicationId == applicationId, cancellationToken);
        if (incident == null) return new(404);
        if (incident.Status == Incident.Resolved) return new(409);
        if (incident.Level == level) return new(202, incident);
        if (incident.Version != version) return new(409);

        var now = DateTime.UtcNow;
        now = now.AddTicks(-(now.Ticks % 10));
        var ownerName = incident.Owner?.DisplayName ?? incident.OwnerDisplayName;
        var entry = new IncidentTimelineEntry
        {
            Incident = incident, Type = IncidentTimelineEntry.LevelChanged,
            ActorName = "Application", ActorIdSnapshot = $"application:{applicationId}",
            Note = reason.Trim(), CreatedAtUtc = now,
            PreviousStatus = incident.Status, NewStatus = incident.Status,
            PreviousOwnerId = incident.OwnerId, NewOwnerId = incident.OwnerId,
            PreviousOwnerName = ownerName, NewOwnerName = ownerName,
            PreviousLevel = incident.Level, NewLevel = level
        };
        context.IncidentTimelineEntries.Add(entry);
        incident.Level = level;
        incident.Version = Guid.NewGuid();
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            context.ChangeTracker.Clear();
            return new(409);
        }
        return new(200, incident);
    }

    private static void ApplyResolution(Incident incident, DateTime now)
    {
        incident.Status = Incident.Resolved;
        incident.ResolvedAtUtc = now;
    }

    public async Task<IncidentActionResult> ExecuteAsync(ClaimsPrincipal user, IncidentNoteRequest request, string action,
        CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.Include(item => item.Owner)
            .SingleOrDefaultAsync(item => item.Id == request.IncidentId, cancellationToken);
        if (incident == null) return new(404);
        var now = DateTime.UtcNow;
        if (!await new IncidentAccessService(applicationAccess).CanManageAsync(user, incident, now, cancellationToken)) return new(403);
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
        var resolveImmediately = action == IncidentTimelineEntry.Mitigated &&
            request is IncidentMitigationRequest { ResolveImmediately: true };
        if (resolveImmediately && incident.Level is not ("sev2" or "sev3" or "sev4"))
            return new(400, localizer["Only sev2, sev3 and sev4 incidents can be resolved during mitigation."].Value);
        var actorId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == actorId, cancellationToken);
        if (actor == null) return new(403);
        var level = (request as IncidentLevelRequest)?.Level;
        if (action == IncidentTimelineEntry.LevelChanged && level is not ("sev0" or "sev1" or "sev2" or "sev3" or "sev4"))
            return new(400, localizer["Please select an incident severity level from sev0 to sev4."].Value);
        if (action == IncidentTimelineEntry.LevelChanged && level == incident.Level)
            return new(400, localizer["The incident is already at the selected level."].Value);
        if (action == IncidentTimelineEntry.OwnershipChanged && incident.OwnerId == actorId)
            return new(400, localizer["You are already the current assignee."].Value);
        var entry = new IncidentTimelineEntry
        {
            Incident = incident, Type = action, ActorId = actor.Id, ActorIdSnapshot = actor.Id,
            ActorName = actor.DisplayName, Note = note, CreatedAtUtc = now,
            PreviousStatus = incident.Status, PreviousOwnerId = incident.OwnerId,
            PreviousOwnerName = incident.Owner?.DisplayName ?? incident.OwnerDisplayName, PreviousLevel = incident.Level
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
                ApplyResolution(incident, now);
                break;
            case IncidentTimelineEntry.Reopened:
                incident.Status = Incident.New;
                incident.OwnerId = null;
                incident.OwnerDisplayName = null;
                incident.AcknowledgedAtUtc = null;
                incident.MitigatedAtUtc = null;
                incident.ResolvedAtUtc = null;
                break;
            case IncidentTimelineEntry.LevelChanged:
                incident.Level = level!;
                break;
        }
        entry.NewStatus = incident.Status;
        entry.NewOwnerId = incident.OwnerId;
        entry.NewOwnerName = incident.OwnerId == entry.PreviousOwnerId ? entry.PreviousOwnerName : incident.OwnerDisplayName;
        entry.NewLevel = incident.Level;
        incident.Version = Guid.NewGuid();
        db.IncidentTimelineEntries.Add(entry);
        var emailEvent = entry;
        if (resolveImmediately)
        {
            // Keep both timeline events ordered at MySQL's microsecond precision.
            var resolvedAt = now.AddTicks(10);
            emailEvent = new IncidentTimelineEntry
            {
                Incident = incident, Type = IncidentTimelineEntry.Resolved,
                ActorId = entry.ActorId, ActorIdSnapshot = entry.ActorIdSnapshot, ActorName = entry.ActorName,
                Note = note, CreatedAtUtc = resolvedAt,
                PreviousStatus = Incident.Mitigated, NewStatus = Incident.Resolved,
                PreviousOwnerId = entry.NewOwnerId, NewOwnerId = entry.NewOwnerId,
                PreviousOwnerName = entry.NewOwnerName, NewOwnerName = entry.NewOwnerName,
                PreviousLevel = incident.Level, NewLevel = incident.Level
            };
            db.IncidentTimelineEntries.Add(emailEvent);
            ApplyResolution(incident, resolvedAt);
        }
        await QueuedIncidentDispatcher.DispatchEmailEventAsync(db, emailEvent, cancellationToken, localizerFactory);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new(409, localizer["The incident has been modified by another engineer. Please refresh and try again."].Value);
        }
        return new(200);
    }
}

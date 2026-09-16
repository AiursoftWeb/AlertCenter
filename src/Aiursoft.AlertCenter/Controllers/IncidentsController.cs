using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class IncidentsController(TemplateDbContext db, IApplicationAccessService applicationAccess) : Controller
{
    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "告警中心", CascadedLinksIcon = "bell", CascadedLinksOrder = 1,
        LinkText = "事故处置", LinkOrder = 1)]
    public async Task<IActionResult> Index([FromQuery] IncidentFilter filter, CancellationToken cancellationToken)
    {
        filter.Normalize();
        var accessibleIds = await applicationAccess.GetAccessibleApplicationIdsAsync(User, cancellationToken: cancellationToken);
        var accessibleIncidents = db.Incidents.AsNoTracking().Include(incident => incident.Application)
            .Where(incident => accessibleIds.Contains(incident.ApplicationId));
        var model = new IncidentsViewModel
        {
            Filter = filter,
            ActiveCount = await accessibleIncidents.CountAsync(incident => incident.Status != Incident.Resolved, cancellationToken),
            Applications = await db.SourceApplications.AsNoTracking().Where(application => accessibleIds.Contains(application.Id))
                .OrderBy(application => application.Name).ToListAsync(cancellationToken)
        };
        if (!ModelState.IsValid) return this.StackView(model);
        model.Incidents = await FilterIncidents(accessibleIncidents, filter)
            .OrderBy(incident => incident.Status == Incident.Resolved).ThenByDescending(incident => incident.TriggeredAtUtc)
            .ToListAsync(cancellationToken);
        return this.StackView(model);
    }

    private static IQueryable<Incident> FilterIncidents(IQueryable<Incident> incidents, IncidentFilter filter)
    {
        if (filter.ApplicationId.HasValue)
            incidents = incidents.Where(incident => incident.ApplicationId == filter.ApplicationId.Value);
        if (filter.Status is Incident.Triggered or Incident.Acknowledged or Incident.Resolved)
            incidents = incidents.Where(incident => incident.Status == filter.Status);
        if (filter.Level != null)
            incidents = incidents.Where(incident => incident.AlertLevel == filter.Level);
        if (filter.Keyword != null)
        {
            var keyword = filter.Keyword.ToLowerInvariant();
            incidents = incidents.Where(incident => incident.Title.ToLower().Contains(keyword) ||
                incident.AlertTitle.ToLower().Contains(keyword) || incident.Application.Name.ToLower().Contains(keyword) ||
                (incident.IncidentKey != null && incident.IncidentKey.ToLower().Contains(keyword)));
        }
        if (filter.FromDate.HasValue)
        {
            var start = filter.FromDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            incidents = incidents.Where(incident => incident.TriggeredAtUtc >= start);
        }
        if (filter.ToDate.HasValue && filter.ToDate.Value < DateOnly.MaxValue)
        {
            var end = filter.ToDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            incidents = incidents.Where(incident => incident.TriggeredAtUtc < end);
        }
        return incidents;
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.AsNoTracking().Include(item => item.Application)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (incident == null) return NotFound();
        if (!await applicationAccess.CanAccessAsync(User, incident.ApplicationId, cancellationToken: cancellationToken)) return Forbid();
        return this.StackView(new IncidentsViewModel
        {
            Incident = incident,
            TimelineEntries = await db.IncidentTimelineEntries.AsNoTracking().Include(entry => entry.Actor)
                .Where(entry => entry.IncidentId == id).OrderByDescending(entry => entry.CreatedAtUtc).ToListAsync(cancellationToken),
            Messages = await db.ReportedMessages.AsNoTracking().Where(message => message.IncidentId == id)
                .OrderByDescending(message => message.ReceivedAtUtc).Take(50).ToListAsync(cancellationToken),
            CanManage = await applicationAccess.CanAccessAsync(User, incident.ApplicationId, requireManage: true, cancellationToken)
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Trigger(TriggerIncidentRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return BadRequest("请填写有效的事故信息。");
        var message = await db.ReportedMessages.SingleOrDefaultAsync(item => item.Id == request.MessageId, cancellationToken);
        if (message == null) return NotFound();
        if (!await applicationAccess.CanAccessAsync(User, message.ApplicationId, requireManage: true, cancellationToken)) return Forbid();
        if (message.IncidentId.HasValue) return RedirectToAction(nameof(Details), new { id = message.IncidentId });
        var incident = await FindOrCreateIncidentAsync(message, request, cancellationToken);
        await db.ReportedMessages.Where(item => item.Id == message.Id && item.IncidentId == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IncidentId, incident.Id), cancellationToken);
        return RedirectToAction(nameof(Details), new { id = incident.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Acknowledge(IncidentNoteRequest request, CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.SingleOrDefaultAsync(item => item.Id == request.IncidentId, cancellationToken);
        if (incident == null) return NotFound();
        if (!await applicationAccess.CanAccessAsync(User, incident.ApplicationId, requireManage: true, cancellationToken)) return Forbid();
        if (incident.Status == Incident.Resolved) return BadRequest("已解决的事故不能确认处理。");
        if (incident.Status == Incident.Acknowledged) return RedirectToAction(nameof(Details), new { id = incident.Id });
        incident.Status = Incident.Acknowledged;
        incident.AcknowledgedAtUtc = DateTime.UtcNow;
        db.IncidentTimelineEntries.Add(CreateTimelineEntry(incident, IncidentTimelineEntry.Acknowledged, request.Note));
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Details), new { id = incident.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddNote(IncidentNoteRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(request.Note)) return BadRequest("处理进展不能为空。");
        var incident = await db.Incidents.SingleOrDefaultAsync(item => item.Id == request.IncidentId, cancellationToken);
        if (incident == null) return NotFound();
        if (!await applicationAccess.CanAccessAsync(User, incident.ApplicationId, requireManage: true, cancellationToken)) return Forbid();
        if (incident.Status == Incident.Resolved) return BadRequest("已解决的事故不能添加处理进展。");
        db.IncidentTimelineEntries.Add(CreateTimelineEntry(incident, IncidentTimelineEntry.Updated, request.Note));
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Details), new { id = incident.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Resolve(IncidentNoteRequest request, CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.SingleOrDefaultAsync(item => item.Id == request.IncidentId, cancellationToken);
        if (incident == null) return NotFound();
        if (!await applicationAccess.CanAccessAsync(User, incident.ApplicationId, requireManage: true, cancellationToken)) return Forbid();
        if (incident.Status == Incident.Resolved) return RedirectToAction(nameof(Details), new { id = incident.Id });
        incident.Status = Incident.Resolved;
        incident.ActiveMatchKey = null;
        incident.ResolvedAtUtc = DateTime.UtcNow;
        db.IncidentTimelineEntries.Add(CreateTimelineEntry(incident, IncidentTimelineEntry.Resolved, request.Note));
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Details), new { id = incident.Id });
    }

    private async Task<Incident> FindOrCreateIncidentAsync(ReportedMessage message, TriggerIncidentRequest request,
        CancellationToken cancellationToken)
    {
        var activeMatchKey = IncidentMatcher.GetActiveMatchKey(message);
        var existing = await db.Incidents.OrderByDescending(incident => incident.TriggeredAtUtc).FirstOrDefaultAsync(incident =>
            incident.ApplicationId == message.ApplicationId && incident.Status != Incident.Resolved &&
            (incident.ActiveMatchKey == activeMatchKey ||
             (incident.ActiveMatchKey == null &&
              (string.IsNullOrWhiteSpace(message.IncidentKey)
                  ? incident.IncidentKey == null && incident.AlertTitle == message.Title && incident.AlertLevel == message.Level
                  : incident.IncidentKey == message.IncidentKey))), cancellationToken);
        if (existing != null) return existing;

        var incident = new Incident
        {
            ApplicationId = message.ApplicationId,
            Title = request.Title.Trim(),
            IncidentKey = message.IncidentKey,
            ActiveMatchKey = activeMatchKey,
            AlertTitle = message.Title,
            AlertLevel = message.Level
        };
        db.Incidents.Add(incident);
        db.IncidentTimelineEntries.Add(CreateTimelineEntry(incident, IncidentTimelineEntry.Triggered, request.Note));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return incident;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return await db.Incidents.SingleAsync(item => item.ApplicationId == message.ApplicationId &&
                item.ActiveMatchKey == activeMatchKey, cancellationToken);
        }
    }

    private IncidentTimelineEntry CreateTimelineEntry(Incident incident, string type, string? note) => new()
    {
        Incident = incident,
        Type = type,
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        ActorId = User.FindFirstValue(ClaimTypes.NameIdentifier)!
    };
}

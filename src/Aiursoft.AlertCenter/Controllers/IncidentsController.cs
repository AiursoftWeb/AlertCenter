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
        var oceIds = await new IncidentAccessService(db, applicationAccess).GetOceApplicationIdsAsync(User, DateTime.UtcNow, cancellationToken);
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var accessibleIncidents = db.Incidents.AsNoTracking().Include(incident => incident.Application).Include(incident => incident.Owner)
            .Where(incident => accessibleIds.Contains(incident.ApplicationId) || oceIds.Contains(incident.ApplicationId) ||
                (userId != null && incident.OwnerId == userId));
        var model = new IncidentsViewModel
        {
            Filter = filter,
            ActiveCount = await accessibleIncidents.CountAsync(incident => incident.Status != Incident.Resolved, cancellationToken),
            Applications = await db.SourceApplications.AsNoTracking().Where(application => accessibleIncidents.Any(incident => incident.ApplicationId == application.Id))
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
        if (filter.Status is Incident.New or Incident.Acked or Incident.Mitigated or Incident.Resolved)
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
        var incident = await db.Incidents.AsNoTracking().Include(item => item.Application).Include(item => item.Owner)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (incident == null) return NotFound();
        var now = DateTime.UtcNow;
        var access = new IncidentAccessService(db, applicationAccess);
        if (!await access.CanViewAsync(User, incident, now, cancellationToken)) return Forbid();
        var oce = incident.Application.OnCallGroupId.HasValue
            ? await new OnCallOceService(db).QueryAsync(incident.Application.OnCallGroupId.Value, now, cancellationToken) : null;
        return this.StackView(new IncidentsViewModel
        {
            Incident = incident,
            TimelineEntries = await db.IncidentTimelineEntries.AsNoTracking().Include(entry => entry.Actor)
                .Where(entry => entry.IncidentId == id).OrderByDescending(entry => entry.CreatedAtUtc).ToListAsync(cancellationToken),
            Messages = await db.ReportedMessages.AsNoTracking().Where(message => message.IncidentId == id)
                .OrderByDescending(message => message.ReceivedAtUtc).Take(50).ToListAsync(cancellationToken),
            CanManage = await access.CanManageAsync(User, incident, now, cancellationToken),
            CurrentOces = oce?.Engineers ?? [],
            CurrentOceError = oce?.Error
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Acknowledge(IncidentNoteRequest request, CancellationToken cancellationToken) =>
        Execute(request, IncidentTimelineEntry.Acked, cancellationToken);

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> TakeOwnership(IncidentNoteRequest request, CancellationToken cancellationToken) =>
        Execute(request, IncidentTimelineEntry.OwnershipChanged, cancellationToken);

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Mitigate(IncidentNoteRequest request, CancellationToken cancellationToken) =>
        Execute(request, IncidentTimelineEntry.Mitigated, cancellationToken);

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Resolve(IncidentNoteRequest request, CancellationToken cancellationToken) =>
        Execute(request, IncidentTimelineEntry.Resolved, cancellationToken);

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Reopen(IncidentNoteRequest request, CancellationToken cancellationToken) =>
        Execute(request, IncidentTimelineEntry.Reopened, cancellationToken);

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> ChangeLevel(IncidentLevelRequest request, CancellationToken cancellationToken) =>
        Execute(request, IncidentTimelineEntry.LevelChanged, cancellationToken);

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> AddNote(IncidentNoteRequest request, CancellationToken cancellationToken) =>
        Execute(request, IncidentTimelineEntry.Updated, cancellationToken);

    private async Task<IActionResult> Execute(IncidentNoteRequest request, string action, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return BadRequest("操作参数无效，理由不能超过 4000 个字符，改级须为 sev0 至 sev4。");
        var result = await new IncidentWorkflowService(db, applicationAccess).ExecuteAsync(User, request, action, cancellationToken);
        return result.StatusCode switch
        {
            200 => RedirectToAction(nameof(Details), new { id = request.IncidentId }),
            403 => Forbid(),
            404 => NotFound(),
            409 => Conflict(result.Error),
            _ => BadRequest(result.Error)
        };
    }
}

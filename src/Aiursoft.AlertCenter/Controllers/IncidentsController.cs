using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class IncidentsController(TemplateDbContext db, IApplicationAccessService applicationAccess, IStringLocalizer<IncidentsController> localizer, IStringLocalizerFactory localizerFactory) : Controller
{
    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "Alert Center", CascadedLinksIcon = "bell", CascadedLinksOrder = 1,
        LinkText = "Incidents", LinkOrder = 1)]
    public async Task<IActionResult> Index([FromQuery] IncidentFilter filter, CancellationToken cancellationToken)
    {
        filter.Normalize();
        var accessibleIds = await applicationAccess.GetAccessibleApplicationIdsAsync(User, cancellationToken: cancellationToken);
        var accessibleIncidents = db.Incidents.AsNoTracking().Include(incident => incident.Application).Include(incident => incident.Owner)
            .Where(incident => accessibleIds.Contains(incident.ApplicationId));
        if (filter.ApplicationId.HasValue && !accessibleIds.Contains(filter.ApplicationId.Value) &&
            !await accessibleIncidents.AnyAsync(incident => incident.ApplicationId == filter.ApplicationId.Value, cancellationToken)) return Forbid();
        var model = new IncidentsViewModel
        {
            Filter = filter,
            ActiveCount = await accessibleIncidents.CountAsync(incident => incident.Status != Incident.Resolved, cancellationToken),
            Applications = await db.SourceApplications.AsNoTracking().Where(application => accessibleIncidents.Any(incident => incident.ApplicationId == application.Id))
                .OrderBy(application => application.Name).ToListAsync(cancellationToken)
        };
        if (!ModelState.IsValid) return this.StackView(model);
        var incidents = await FilterIncidents(accessibleIncidents, filter)
            .OrderBy(incident => incident.Status == Incident.Resolved).ThenByDescending(incident => incident.ReceivedAtUtc).ThenByDescending(incident => incident.Id)
            .Skip((filter.Page - 1) * 20).Take(21)
            .ToListAsync(cancellationToken);
        model.Incidents = incidents.Take(20).ToList();
        model.HasNextPage = incidents.Count > 20;
        return this.StackView(model);
    }

    private static IQueryable<Incident> FilterIncidents(IQueryable<Incident> incidents, IncidentFilter filter)
    {
        if (filter.ApplicationId.HasValue)
            incidents = incidents.Where(incident => incident.ApplicationId == filter.ApplicationId.Value);
        if (filter.Status is Incident.New or Incident.Acked or Incident.Mitigated or Incident.Resolved)
            incidents = incidents.Where(incident => incident.Status == filter.Status);
        if (filter.Level != null)
        {
            var levels = filter.Level.StartsWith("lte-", StringComparison.Ordinal)
                ? new[] { "sev0", "sev1", "sev2", "sev3", "sev4" }.Take(filter.Level[^1] - '0' + 1).ToArray()
                : [filter.Level];
            incidents = incidents.Where(incident => levels.Contains(incident.Level));
        }
        if (filter.Keyword != null)
        {
            var keyword = filter.Keyword.ToLowerInvariant();
            incidents = incidents.Where(incident => incident.Title.ToLower().Contains(keyword) ||
                (incident.Content != null && incident.Content.ToLower().Contains(keyword)) || incident.Application.Name.ToLower().Contains(keyword));
        }
        if (filter.FromDate.HasValue)
        {
            var start = filter.FromDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            incidents = incidents.Where(incident => incident.ReceivedAtUtc >= start);
        }
        if (filter.ToDate.HasValue && filter.ToDate.Value < DateOnly.MaxValue)
        {
            var end = filter.ToDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            incidents = incidents.Where(incident => incident.ReceivedAtUtc < end);
        }
        return incidents;
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken, int commentsPage = 1)
    {
        var incident = await db.Incidents.AsNoTracking().Include(item => item.Application).Include(item => item.Owner)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (incident == null) return NotFound();
        var now = DateTime.UtcNow;
        var access = new IncidentAccessService(applicationAccess);
        if (!await access.CanViewAsync(User, incident, now, cancellationToken)) return Forbid();
        var oce = incident.Application.OnCallGroupId.HasValue
            ? await new OnCallOceService(db, localizer: new StringLocalizer<OnCallOceService>(localizerFactory)).QueryAsync(incident.Application.OnCallGroupId.Value, now, cancellationToken) : null;
        commentsPage = Math.Clamp(commentsPage, 1, 1000000);
        var canManageApplication = await applicationAccess.CanAccessAsync(User, incident.ApplicationId, ApplicationPermission.UpdateIncidents, cancellationToken);
        var comments = await db.IncidentComments.AsNoTracking().Where(comment => comment.IncidentId == id)
            .OrderByDescending(comment => comment.CreatedAtUtc).ThenByDescending(comment => comment.Id)
            .Skip((commentsPage - 1) * 20).Take(21).Select(comment => new IncidentCommentViewModel
            {
                Id = comment.Id, AuthorName = comment.AuthorName, CreatedAtUtc = comment.CreatedAtUtc,
                AuthorAvatarRelativePath = comment.Author != null ? comment.Author.AvatarRelativePath : Entities.User.DefaultAvatarPath,
                Markdown = comment.DeletedAtUtc == null ? comment.Markdown : null,
                DeletedAtUtc = comment.DeletedAtUtc, DeletedByName = comment.DeletedByName,
                CanDelete = comment.DeletedAtUtc == null && canManageApplication
            }).ToListAsync(cancellationToken);
        return this.StackView(new IncidentsViewModel
        {
            Incident = incident,
            Comments = comments.Take(20).ToList(), CommentsPage = commentsPage, HasMoreComments = comments.Count > 20,
            CanComment = canManageApplication,
            TimelineEntries = await db.IncidentTimelineEntries.AsNoTracking().Include(entry => entry.Actor)
                .Where(entry => entry.IncidentId == id).OrderByDescending(entry => entry.CreatedAtUtc).ToListAsync(cancellationToken),
            CanDelete = await applicationAccess.CanAccessAsync(User, incident.ApplicationId, ApplicationPermission.DeleteIncidents, cancellationToken),
            CanManage = await access.CanManageAsync(User, incident, now, cancellationToken),
            CurrentOces = oce?.Engineers ?? [],
            CurrentOceError = oce?.Error
        });
    }

    public async Task<IActionResult> Create(Guid applicationId, CancellationToken cancellationToken)
    {
        if (!await applicationAccess.CanAccessAsync(User, applicationId, ApplicationPermission.CreateIncidents, cancellationToken)) return Forbid();
        return this.StackView(new CreateIncidentViewModel { ApplicationId = applicationId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateIncidentViewModel request, [FromServices] IIncidentDispatcher dispatcher, CancellationToken cancellationToken)
    {
        if (!await applicationAccess.CanAccessAsync(User, request.ApplicationId, ApplicationPermission.CreateIncidents, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return this.StackView(request);
        var application = await db.SourceApplications.SingleAsync(item => item.Id == request.ApplicationId, cancellationToken);
        if (!application.IsEnabled) return BadRequest(localizer["This application is disabled."].Value);
        var now = DateTime.UtcNow;
        now = now.AddTicks(-(now.Ticks % 10));
        var incident = new Incident { ApplicationId = request.ApplicationId, Title = request.Title.Trim(), Content = request.Content,
            Level = request.Level, OccurredAtUtc = now, ReceivedAtUtc = now };
        db.Incidents.Add(incident);
        db.IncidentTimelineEntries.Add(new IncidentTimelineEntry { Incident = incident, Type = IncidentTimelineEntry.Created,
            ActorId = User.FindFirstValue(ClaimTypes.NameIdentifier), ActorName = User.Identity?.Name ?? "User",
            NewStatus = Incident.New, NewLevel = incident.Level, CreatedAtUtc = now });
        await dispatcher.DispatchAsync(incident, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await applicationAccess.CanAccessAsync(User, request.ApplicationId, cancellationToken: cancellationToken)
            ? RedirectToAction(nameof(Details), new { id = incident.Id }) : RedirectToAction("Applications", "Applications");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, Guid version, string? confirmationTitle, CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (incident == null) return NotFound();
        if (!await applicationAccess.CanAccessAsync(User, incident.ApplicationId, ApplicationPermission.DeleteIncidents, cancellationToken)) return Forbid();
        if (!ModelState.IsValid || !string.Equals(confirmationTitle, incident.Title, StringComparison.Ordinal))
            return BadRequest(localizer["Enter the exact incident title to confirm permanent deletion."].Value);
        var deleted = await db.Incidents.Where(item => item.Id == id && item.Version == version).ExecuteDeleteAsync(cancellationToken);
        return deleted == 1 ? RedirectToAction(nameof(Index)) : Conflict();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddComment(AddIncidentCommentRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return BadRequest(localizer["Comment cannot be empty and must not exceed 4,000 characters."].Value);
        var result = await new IncidentCommentService(db, applicationAccess, localizer: new StringLocalizer<IncidentCommentService>(localizerFactory)).AddAsync(User, request, cancellationToken);
        return CommentResult(result, request.IncidentId, 1);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteComment(DeleteIncidentCommentRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return BadRequest(localizer["Invalid comment parameters. Please refresh the page."].Value);
        var result = await new IncidentCommentService(db, applicationAccess, localizer: new StringLocalizer<IncidentCommentService>(localizerFactory)).DeleteAsync(User, request, cancellationToken);
        return CommentResult(result, request.IncidentId, Math.Clamp(request.CommentsPage, 1, 1000000));
    }

    private IActionResult CommentResult(IncidentCommentResult result, Guid id, int page) => result.StatusCode switch
    {
        200 => RedirectToAction(nameof(Details), null, new { id, commentsPage = page }, "incident-comments"),
        403 => Forbid(),
        404 => NotFound(),
        409 => Conflict(result.Error),
        _ => BadRequest(result.Error)
    };

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
        if (!ModelState.IsValid) return BadRequest(localizer["Invalid action parameters. The reason must not exceed 4,000 characters, and the severity level must be between sev0 and sev4."].Value);
        var result = await new IncidentWorkflowService(db, applicationAccess, localizer: new StringLocalizer<IncidentWorkflowService>(localizerFactory), localizerFactory: localizerFactory).ExecuteAsync(User, request, action, cancellationToken);
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

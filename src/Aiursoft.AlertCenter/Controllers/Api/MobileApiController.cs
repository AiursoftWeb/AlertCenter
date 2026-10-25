using System.Security.Claims;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AiurProtocol.Server;
using Aiursoft.AiurProtocol.Server.Attributes;
using Aiursoft.AlertCenter.Configuration;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.SDK.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Services.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Aiursoft.AlertCenter.Controllers.Api;

[Route("api/v1/mobile")]
[Authorize(AuthenticationSchemes = LocalApiAuthenticationDefaults.ApiSchemes)]
[ApiExceptionHandler(PassthroughRemoteErrors = true, PassthroughAiurServerException = true)]
[ApiModelStateChecker]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MobileApiController(
    TemplateDbContext db, IApplicationAccessService access, IOptions<AppSettings> settings,
    IStringLocalizerFactory localizers) : ControllerBase
{
    [HttpGet("/api/v1/config"), AllowAnonymous]
    public IActionResult Configuration()
    {
        var oidc = settings.Value.OIDC;
        var scopes = new List<string> { "openid", "profile", "email", "offline_access" };
        if (!string.IsNullOrWhiteSpace(oidc.ApiScope)) scopes.Add(oidc.ApiScope);
        return this.Protocol(new MobileConfigurationResponse
        {
            Code = Code.ResultShown, Message = "Mobile client configuration.",
            AuthenticationMode = settings.Value.AuthProvider,
            AllowRegistration = settings.Value.LocalEnabled && settings.Value.Local.AllowRegister,
            Authority = settings.Value.OIDCEnabled ? oidc.GetMobileAuthority() : string.Empty,
            ClientId = settings.Value.OIDCEnabled ? oidc.MobileClientId : string.Empty,
            RedirectUri = settings.Value.OIDCEnabled ? oidc.MobileRedirectUri : string.Empty,
            Scopes = settings.Value.OIDCEnabled ? scopes : []
        });
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return user == null ? this.Protocol(Code.Unauthorized, "Sign in again.") : this.Protocol(new MobileUserResponse
        {
            Code = Code.ResultShown, Message = "Current account.", DisplayName = user.DisplayName
        });
    }

    [HttpGet("applications")]
    public async Task<IActionResult> Applications(CancellationToken cancellationToken)
    {
        var view = await access.GetAccessibleApplicationIdsAsync(User, cancellationToken: cancellationToken);
        var create = await access.GetAccessibleApplicationIdsAsync(User, ApplicationPermission.CreateIncidents, cancellationToken);
        var update = await access.GetAccessibleApplicationIdsAsync(User, ApplicationPermission.UpdateIncidents, cancellationToken);
        var ids = view.Union(create).ToArray();
        var applications = await db.SourceApplications.AsNoTracking().Where(item => ids.Contains(item.Id))
            .OrderBy(item => item.Name).ToListAsync(cancellationToken);
        return this.Protocol(new MobileApplicationsResponse
        {
            Code = Code.ResultShown, Message = "Accessible applications.",
            Applications = applications.Select(item => new MobileApplication
            {
                Id = item.Id, Name = item.Name, CanView = view.Contains(item.Id),
                CanCreate = item.IsEnabled && create.Contains(item.Id), CanUpdate = update.Contains(item.Id)
            }).ToList()
        });
    }

    [HttpGet("incidents")]
    public async Task<IActionResult> Incidents([FromQuery] MobileIncidentQuery filter, CancellationToken cancellationToken)
    {
        var ids = await access.GetAccessibleApplicationIdsAsync(User, cancellationToken: cancellationToken);
        if (filter.ApplicationId.HasValue && !ids.Contains(filter.ApplicationId.Value))
            return this.Protocol(Code.Unauthorized, "You cannot view this application's incidents.");
        var query = db.Incidents.AsNoTracking().Include(item => item.Application).Include(item => item.Owner)
            .Where(item => ids.Contains(item.ApplicationId));
        if (filter.ApplicationId.HasValue) query = query.Where(item => item.ApplicationId == filter.ApplicationId.Value);
        if (filter.OnlyMine)
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
            query = query.Where(item => item.OwnerId == id);
        }
        if (filter.Status != "all") query = query.Where(item => item.Status == filter.Status);
        if (filter.Acknowledged.HasValue)
            query = query.Where(item => (item.AcknowledgedAtUtc != null) == filter.Acknowledged.Value);
        if (!string.IsNullOrWhiteSpace(filter.Keyword))
        {
            var keyword = filter.Keyword.Trim().ToLowerInvariant();
            query = query.Where(item => item.Title.ToLower().Contains(keyword) ||
                (item.Content != null && item.Content.ToLower().Contains(keyword)) || item.Application.Name.ToLower().Contains(keyword));
        }
        if (filter.Level == null)
        {
            var policy = await new SeverityPolicyStore(db).LoadAsync(cancellationToken);
            query = query.Where(policy.VisibleIncidents);
        }
        else
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

    [HttpGet("incidents/{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken, int page = 1)
    {
        var incident = await db.Incidents.AsNoTracking().Include(item => item.Application).Include(item => item.Owner)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (incident == null) return this.Protocol(Code.NotFound, "Incident not found.");
        if (!await access.CanAccessAsync(User, incident.ApplicationId, cancellationToken: cancellationToken))
            return this.Protocol(Code.Unauthorized, "You cannot view this incident.");
        page = Math.Clamp(page, 1, 1000000);
        var keys = await db.IncidentTimelineEntries.AsNoTracking().Where(item => item.IncidentId == id)
            .Select(item => new { item.Id, item.CreatedAtUtc, IsComment = false })
            .Concat(db.IncidentComments.AsNoTracking().Where(item => item.IncidentId == id)
                .Select(item => new { item.Id, item.CreatedAtUtc, IsComment = true }))
            .OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.IsComment).ThenByDescending(item => item.Id)
            .Skip((page - 1) * 20).Take(21).ToListAsync(cancellationToken);
        var commentIds = keys.Take(20).Where(item => item.IsComment).Select(item => item.Id).ToArray();
        var entryIds = keys.Take(20).Where(item => !item.IsComment).Select(item => item.Id).ToArray();
        var comments = await db.IncidentComments.AsNoTracking().Where(item => commentIds.Contains(item.Id)).ToListAsync(cancellationToken);
        var entries = await db.IncidentTimelineEntries.AsNoTracking().Include(item => item.Actor)
            .Where(item => entryIds.Contains(item.Id)).ToListAsync(cancellationToken);
        var original = await OnCallEmailQueue.GetOriginalSnapshotAsync(db, id, cancellationToken);
        var oce = incident.Application.OnCallGroupId.HasValue
            ? await new OnCallOceService(db, new StringLocalizer<OnCallOceService>(localizers))
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
        dto.CanManage = await access.CanAccessAsync(User, incident.ApplicationId, ApplicationPermission.UpdateIncidents, cancellationToken);
        return this.Protocol(new MobileIncidentResponse
        {
            Code = Code.ResultShown, Message = "Incident retrieved.", Incident = dto, Activities = activities,
            Page = page, HasNextPage = keys.Count > 20,
            OriginalOces = original?.Recipients.OrderBy(item => item.DisplayName).Select(item => item.DisplayName).ToList() ?? [],
            CurrentOces = oce?.Engineers.Select(item => item.DisplayName).ToList() ?? [], CurrentOceError = oce?.Error
        });
    }

    [HttpPost("incidents/{id:guid}/{operation}")]
    public async Task<IActionResult> Execute(Guid id, string operation, [FromBody] MobileIncidentAction request, CancellationToken cancellationToken)
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
        var result = await new IncidentWorkflowService(db, access, new StringLocalizer<IncidentWorkflowService>(localizers), localizers)
            .ExecuteAsync(User, model, type, cancellationToken);
        return ActionResult(result.StatusCode, result.Error);
    }

    [HttpPost("incidents/{id:guid}/comments")]
    public async Task<IActionResult> Comment(Guid id, [FromBody] MobileCommentRequest request, CancellationToken cancellationToken)
    {
        var result = await new IncidentCommentService(db, access, new StringLocalizer<IncidentCommentService>(localizers))
            .AddAsync(User, new AddIncidentCommentRequest { IncidentId = id, Markdown = request.Markdown }, cancellationToken);
        return ActionResult(result.StatusCode, result.Error);
    }

    [HttpPost("incidents")]
    public async Task<IActionResult> Create([FromBody] MobileCreateIncidentRequest request,
        [FromServices] IIncidentDispatcher dispatcher, CancellationToken cancellationToken)
    {
        if (!await access.CanAccessAsync(User, request.ApplicationId, ApplicationPermission.CreateIncidents, cancellationToken))
            return this.Protocol(Code.Unauthorized, "You cannot create incidents in this application.");
        var application = await db.SourceApplications.SingleOrDefaultAsync(item => item.Id == request.ApplicationId, cancellationToken);
        if (application == null || !application.IsEnabled) return this.Protocol(Code.InvalidInput, "This application is disabled.");
        var now = DateTime.UtcNow;
        now = now.AddTicks(-(now.Ticks % 10));
        var incident = new Incident
        {
            ApplicationId = request.ApplicationId, Title = request.Title.Trim(), Content = request.Content,
            Level = request.Level, OccurredAtUtc = now, ReceivedAtUtc = now
        };
        db.Incidents.Add(incident);
        db.IncidentTimelineEntries.Add(new IncidentTimelineEntry
        {
            Incident = incident, Type = IncidentTimelineEntry.Created,
            ActorId = User.FindFirstValue(ClaimTypes.NameIdentifier), ActorName = User.Identity?.Name ?? "User",
            NewStatus = Incident.New, NewLevel = incident.Level, CreatedAtUtc = now
        });
        await dispatcher.DispatchAsync(incident, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return this.Protocol(new MobileCreatedIncidentResponse
        {
            Code = Code.JobDone, Message = "Incident created.", IncidentId = incident.Id,
            CanView = await access.CanAccessAsync(User, incident.ApplicationId, cancellationToken: cancellationToken)
        });
    }

    private IActionResult ActionResult(int status, string? error) => this.Protocol(status switch
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
        OccurredAtUtc = Utc(item.OccurredAtUtc), ReceivedAtUtc = Utc(item.ReceivedAtUtc),
        AcknowledgedAtUtc = Utc(item.AcknowledgedAtUtc), MitigatedAtUtc = Utc(item.MitigatedAtUtc), ResolvedAtUtc = Utc(item.ResolvedAtUtc)
    };
}

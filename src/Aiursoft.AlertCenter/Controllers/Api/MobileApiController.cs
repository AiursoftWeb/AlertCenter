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
    IStringLocalizerFactory localizers) : EmployeeIncidentApiController(db, access, localizers)
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
        var user = await Db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return user == null ? this.Protocol(Code.Unauthorized, "Sign in again.") : this.Protocol(new MobileUserResponse
        {
            Code = Code.ResultShown, Message = "Current account.", DisplayName = user.DisplayName
        });
    }

    [HttpGet("applications")]
    public async Task<IActionResult> Applications(CancellationToken cancellationToken)
    {
        var view = await Access.GetAccessibleApplicationIdsAsync(User, cancellationToken: cancellationToken);
        var create = await Access.GetAccessibleApplicationIdsAsync(User, ApplicationPermission.CreateIncidents, cancellationToken);
        var update = await Access.GetAccessibleApplicationIdsAsync(User, ApplicationPermission.UpdateIncidents, cancellationToken);
        var ids = view.Union(create).ToArray();
        var applications = await Db.SourceApplications.AsNoTracking().Where(item => ids.Contains(item.Id))
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
    public Task<IActionResult> Incidents([FromQuery] MobileIncidentQuery filter, CancellationToken cancellationToken) =>
        ListIncidents(filter, cancellationToken);

    [HttpGet("incidents/{id:guid}")]
    public Task<IActionResult> Details(Guid id, CancellationToken cancellationToken, int page = 1) =>
        GetIncident(id, page, cancellationToken);

    [HttpPost("incidents/{id:guid}/{operation}")]
    public Task<IActionResult> Execute(Guid id, string operation, [FromBody] MobileIncidentAction request,
        CancellationToken cancellationToken) => ExecuteIncident(id, operation, request, cancellationToken);

    [HttpPost("incidents/{id:guid}/comments")]
    public async Task<IActionResult> Comment(Guid id, [FromBody] MobileCommentRequest request, CancellationToken cancellationToken)
    {
        var result = await new IncidentCommentService(Db, Access, new StringLocalizer<IncidentCommentService>(Localizers))
            .AddAsync(User, new AddIncidentCommentRequest { IncidentId = id, Markdown = request.Markdown }, cancellationToken);
        return ActionResult(result.StatusCode, result.Error);
    }

    [HttpPost("incidents")]
    public async Task<IActionResult> Create([FromBody] MobileCreateIncidentRequest request,
        [FromServices] IIncidentDispatcher dispatcher, CancellationToken cancellationToken)
    {
        if (!await Access.CanAccessAsync(User, request.ApplicationId, ApplicationPermission.CreateIncidents, cancellationToken))
            return this.Protocol(Code.Unauthorized, "You cannot create incidents in this application.");
        var application = await Db.SourceApplications.SingleOrDefaultAsync(item => item.Id == request.ApplicationId, cancellationToken);
        if (application == null || !application.IsEnabled) return this.Protocol(Code.InvalidInput, "This application is disabled.");
        var now = DateTime.UtcNow;
        now = now.AddTicks(-(now.Ticks % 10));
        var incident = new Incident
        {
            ApplicationId = request.ApplicationId, Title = request.Title.Trim(), Content = request.Content,
            Level = request.Level, OccurredAtUtc = now, ReceivedAtUtc = now,
            InvestigateUrl = string.IsNullOrWhiteSpace(request.InvestigateUrl) ? null : request.InvestigateUrl.Trim()
        };
        Db.Incidents.Add(incident);
        Db.IncidentTimelineEntries.Add(new IncidentTimelineEntry
        {
            Incident = incident, Type = IncidentTimelineEntry.Created,
            ActorId = User.FindFirstValue(ClaimTypes.NameIdentifier), ActorName = User.Identity?.Name ?? "User",
            NewStatus = Incident.New, NewLevel = incident.Level, CreatedAtUtc = now
        });
        await dispatcher.DispatchAsync(incident, cancellationToken);
        await Db.SaveChangesAsync(cancellationToken);
        return this.Protocol(new MobileCreatedIncidentResponse
        {
            Code = Code.JobDone, Message = "Incident created.", IncidentId = incident.Id,
            CanView = await Access.CanAccessAsync(User, incident.ApplicationId, cancellationToken: cancellationToken)
        });
    }
}

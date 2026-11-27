using Aiursoft.AiurProtocol.Server.Attributes;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.SDK.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Services.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Controllers.Api;

[Route("api/v1/personal/incidents")]
[Authorize(AuthenticationSchemes = PersonalApiKeyAuthenticationHandler.SchemeName)]
[ApiExceptionHandler(PassthroughRemoteErrors = true, PassthroughAiurServerException = true)]
[ApiModelStateChecker]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PersonalIncidentApiController(
    TemplateDbContext db, IApplicationAccessService access, IStringLocalizerFactory localizers)
    : EmployeeIncidentApiController(db, access, localizers)
{
    [HttpGet]
    public Task<IActionResult> Incidents([FromQuery] PersonalIncidentQuery filter, CancellationToken cancellationToken) =>
        ListIncidents(new MobileIncidentQuery
        {
            Page = filter.Page, ApplicationId = filter.ApplicationId, OnlyMine = filter.OnlyMine,
            Keyword = filter.Keyword, Status = filter.Status == "active" ? "all" : filter.Status, Level = filter.Level
        }, cancellationToken, manageableOnly: !filter.AllVisible, unresolvedOnly: filter.Status == "active",
            applyVisibilityPolicy: false);

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Details(Guid id, CancellationToken cancellationToken, int page = 1) =>
        GetIncident(id, page, cancellationToken, allowUpdates: User.HasClaim(PersonalApiKeyAuthenticationHandler.HandleClaim, "true"));

    [HttpPost("{id:guid}/acknowledge")]
    [Authorize(Policy = PersonalApiKeyAuthenticationHandler.HandlePolicy)]
    public Task<IActionResult> Acknowledge(Guid id, [FromBody] MobileIncidentAction request, CancellationToken cancellationToken) =>
        ExecuteIncident(id, "acknowledge", request, cancellationToken);

    [HttpPost("{id:guid}/mitigate")]
    [Authorize(Policy = PersonalApiKeyAuthenticationHandler.HandlePolicy)]
    public Task<IActionResult> Mitigate(Guid id, [FromBody] MobileIncidentAction request, CancellationToken cancellationToken) =>
        ExecuteIncident(id, "mitigate", request, cancellationToken);

    [HttpPost("{id:guid}/resolve")]
    [Authorize(Policy = PersonalApiKeyAuthenticationHandler.HandlePolicy)]
    public Task<IActionResult> Resolve(Guid id, [FromBody] MobileIncidentAction request, CancellationToken cancellationToken) =>
        ExecuteIncident(id, "resolve", request, cancellationToken);
}

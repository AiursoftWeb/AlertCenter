using System.Net.Http.Headers;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.SDK.Models;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AiurProtocol.Server;
using Aiursoft.AiurProtocol.Server.Attributes;
using Aiursoft.AlertCenter.Filters;
using Aiursoft.AlertCenter.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aiursoft.AlertCenter.Controllers;

[IncidentReportExceptionHandler]
// Run before MVC's unsupported-content-type filter to keep input errors in AiurProtocol.
[ApiModelStateChecker(Order = -3001)]
[AllowAnonymous]
[Route("api/v1/incidents")]
public class IncidentReportsController(TemplateDbContext db, IIncidentDispatcher dispatcher) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> Create([FromBody] CreateIncidentRequest request, CancellationToken cancellationToken)
    {
        var key = await AuthenticateKeyAsync(cancellationToken);
        if (key == null)
            return this.Protocol(Code.WrongKey, "The application API key is invalid or disabled.");
        if (!key.CanCreateIncidents)
            return this.Protocol(Code.Unauthorized, "The API key does not permit this operation.");
        var applicationId = key.ApplicationId;

        var existing = await FindExisting(applicationId, request.ReportId, cancellationToken);
        if (existing != null) return this.Protocol(Receipt(existing, true));

        // MySQL stores microseconds. Keep initial and duplicate receipts identical across providers.
        var receivedAtUtc = DateTime.UtcNow;
        receivedAtUtc = receivedAtUtc.AddTicks(-(receivedAtUtc.Ticks % 10));
        var incident = new Incident
        {
            ApplicationId = applicationId, ReportId = request.ReportId, Title = request.Title,
            ReceivedAtUtc = receivedAtUtc,
            Content = request.Content, Level = request.Level,
            OccurredAtUtc = request.OccurredAtUtc.UtcDateTime, ContextJson = request.Context?.ToString(Formatting.None)
        };

        db.Incidents.Add(incident);
        db.IncidentTimelineEntries.Add(new IncidentTimelineEntry
        {
            Incident = incident, Type = IncidentTimelineEntry.Created,
            NewStatus = Incident.New, NewLevel = incident.Level, CreatedAtUtc = receivedAtUtc
        });
        await dispatcher.DispatchAsync(incident, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            existing = await FindExisting(applicationId, request.ReportId, cancellationToken);
            if (existing != null) return this.Protocol(Receipt(existing, true));
            throw;
        }
        return this.Protocol(Receipt(incident, false));
    }

    [HttpGet]
    public async Task<IActionResult> Query([FromQuery] QueryIncidentsRequest request, CancellationToken cancellationToken)
    {
        var key = await AuthenticateKeyAsync(cancellationToken);
        if (key == null)
            return this.Protocol(Code.WrongKey, "The application API key is invalid or disabled.");
        if (!key.CanQueryIncidents)
            return this.Protocol(Code.Unauthorized, "The API key does not permit this operation.");
        var applicationId = key.ApplicationId;

        var query = db.Incidents.AsNoTracking().Where(incident => incident.ApplicationId == applicationId);
        if (request.ActiveOnly)
            query = query.Where(incident => incident.Status != Incident.Resolved);
        var total = await query.CountAsync(cancellationToken);
        var incidents = await query.OrderByDescending(incident => incident.ReceivedAtUtc)
            .ThenBy(incident => incident.Id)
            .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize)
            .ToListAsync(cancellationToken);
        return this.Protocol(new QueryIncidentsResponse
        {
            Code = Code.JobDone, Message = "Incidents retrieved.",
            Page = request.Page, PageSize = request.PageSize, TotalCount = total,
            Items = incidents.Select(incident => new IncidentDetails
            {
                IncidentId = incident.Id, ReportId = incident.ReportId, Version = incident.Version,
                Title = incident.Title, Content = incident.Content, Level = incident.Level, Status = incident.Status,
                OccurredAtUtc = new DateTimeOffset(DateTime.SpecifyKind(incident.OccurredAtUtc, DateTimeKind.Utc)),
                ReceivedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(incident.ReceivedAtUtc, DateTimeKind.Utc)),
                Context = incident.ContextJson == null ? null : JObject.Parse(incident.ContextJson)
            }).ToList()
        });
    }

    [HttpPost("{incidentId:guid}/resolve")]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> Resolve(Guid incidentId, [FromBody] ResolveIncidentRequest request,
        CancellationToken cancellationToken)
    {
        var key = await AuthenticateKeyAsync(cancellationToken);
        if (key == null)
            return this.Protocol(Code.WrongKey, "The application API key is invalid or disabled.");
        if (!key.CanResolveIncidents)
            return this.Protocol(Code.Unauthorized, "The API key does not permit this operation.");
        var applicationId = key.ApplicationId;

        var result = await IncidentWorkflowService.ResolveForApplicationAsync(
            db, applicationId, incidentId, request.Version, request.Reason, cancellationToken);
        if (result.StatusCode == 404) return this.Protocol(Code.NotFound, "The incident was not found.");
        if (result.StatusCode == 409)
            return this.Protocol(Code.Conflict, "The incident changed. Query it again before deciding whether to resolve it.");
        var incident = result.Incident!;
        return this.Protocol(new ResolveIncidentResponse
        {
            Code = result.StatusCode == 202 ? Code.NoActionTaken : Code.JobDone,
            Message = result.StatusCode == 202 ? "The incident is already resolved." : "The incident was resolved by its application.",
            IncidentId = incident.Id, Version = incident.Version,
            ResolvedAtUtc = incident.ResolvedAtUtc.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(incident.ResolvedAtUtc.Value, DateTimeKind.Utc)) : null
        });
    }

    [HttpPost("{incidentId:guid}/severity")]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> ChangeSeverity(Guid incidentId, [FromBody] ChangeIncidentSeverityRequest request,
        CancellationToken cancellationToken)
    {
        var key = await AuthenticateKeyAsync(cancellationToken);
        if (key == null)
            return this.Protocol(Code.WrongKey, "The application API key is invalid or disabled.");
        if (!key.CanChangeIncidentSeverity)
            return this.Protocol(Code.Unauthorized, "The API key does not permit this operation.");
        var applicationId = key.ApplicationId;

        var result = await IncidentWorkflowService.ChangeSeverityForApplicationAsync(
            db, applicationId, incidentId, request.Version, request.Reason, request.Level, cancellationToken);
        if (result.StatusCode == 404) return this.Protocol(Code.NotFound, "The incident was not found.");
        if (result.StatusCode == 409)
            return this.Protocol(Code.Conflict, "The incident changed. Query it again before deciding whether to change its severity. Resolved incidents cannot change severity.");
        var incident = result.Incident!;
        return this.Protocol(new ChangeIncidentSeverityResponse
        {
            Code = result.StatusCode == 202 ? Code.NoActionTaken : Code.JobDone,
            Message = result.StatusCode == 202 ? "The incident already has this severity." : "The incident severity was changed by its application.",
            IncidentId = incident.Id, Version = incident.Version,
            Level = incident.Level
        });
    }

    private async Task<ApplicationApiKey?> AuthenticateKeyAsync(CancellationToken cancellationToken)
    {
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = header.Parameter?.Split('.', 3);
        if (parts is not { Length: 3 } || !Guid.TryParse(parts[0], out var applicationId) ||
            !Guid.TryParse(parts[1], out var keyId)) return null;
        var key = await db.ApplicationApiKeys.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == keyId && item.ApplicationId == applicationId && item.IsEnabled && item.Application.IsEnabled,
            cancellationToken);
        if (key == null || new PasswordHasher<ApplicationApiKey>().VerifyHashedPassword(key, key.SecretHash, parts[2]) == PasswordVerificationResult.Failed)
            return null;
        var now = DateTime.UtcNow;
        // Count authenticated, valid requests, including permission denials and business failures.
        // Atomic SQL updates avoid lost increments and do not overwrite concurrent key edits.
        var updated = await db.ApplicationApiKeys.Where(item => item.Id == key.Id && item.IsEnabled && item.Application.IsEnabled)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.TotalCalls, item => item.TotalCalls + 1)
                .SetProperty(item => item.LastUsedAtUtc, item => item.LastUsedAtUtc == null || item.LastUsedAtUtc < now ? now : item.LastUsedAtUtc), cancellationToken);
        return updated == 1 ? key : null;
    }

    private Task<Incident?> FindExisting(Guid applicationId, Guid reportId, CancellationToken cancellationToken) =>
        db.Incidents.AsNoTracking().SingleOrDefaultAsync(
            m => m.ApplicationId == applicationId && m.ReportId == reportId, cancellationToken);

    private static CreateIncidentResponse Receipt(Incident incident, bool duplicate) => new()
    {
        Code = duplicate ? Code.NoActionTaken : Code.JobDone,
        Message = duplicate ? "This report has already been received." : "The report was received.",
        IncidentId = incident.Id,
        ReportId = incident.ReportId,
        ReceivedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(incident.ReceivedAtUtc, DateTimeKind.Utc)),
        Duplicate = duplicate
    };
}

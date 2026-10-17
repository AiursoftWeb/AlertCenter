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
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
            return this.Protocol(Code.WrongKey, "The application API key is invalid or disabled.");
        var parts = header.Parameter?.Split('.', 2);
        if (parts is not { Length: 2 } || !Guid.TryParse(parts[0], out var applicationId))
            return this.Protocol(Code.WrongKey, "The application API key is invalid or disabled.");
        var application = await db.SourceApplications.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == applicationId && a.IsEnabled, cancellationToken);
        if (application == null || new PasswordHasher<SourceApplication>()
                .VerifyHashedPassword(application, application.SecretHash, parts[1]) == PasswordVerificationResult.Failed)
            return this.Protocol(Code.WrongKey, "The application API key is invalid or disabled.");

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

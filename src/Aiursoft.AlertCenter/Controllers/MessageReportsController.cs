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

[MessageReportExceptionHandler]
// Run before MVC's unsupported-content-type filter to keep input errors in AiurProtocol.
[ApiModelStateChecker(Order = -3001)]
[AllowAnonymous]
[Route("api/v1/messages")]
public class MessageReportsController(TemplateDbContext db, IAlertDispatcher dispatcher) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> Report([FromBody] ReportMessageRequest request, CancellationToken cancellationToken)
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
        ReportedMessage CreateMessage() => new()
        {
            ApplicationId = applicationId, ReportId = request.ReportId, Title = request.Title,
            ReceivedAtUtc = receivedAtUtc,
            Content = request.Content, Level = request.Level,
            IncidentKey = string.IsNullOrWhiteSpace(request.IncidentKey) ? null : request.IncidentKey.Trim(),
            OccurredAtUtc = request.OccurredAtUtc.UtcDateTime, ContextJson = request.Context?.ToString(Formatting.None)
        };

        var message = CreateMessage();
        db.ReportedMessages.Add(message);
        await dispatcher.DispatchAsync(message, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            existing = await FindExisting(applicationId, request.ReportId, cancellationToken);
            if (existing != null) return this.Protocol(Receipt(existing, true));
            var activeMatchKey = IncidentMatcher.GetActiveMatchKey(message);
            var incidentExists = await db.Incidents.AsNoTracking().AnyAsync(incident =>
                incident.ApplicationId == applicationId && incident.ActiveMatchKey == activeMatchKey, cancellationToken);
            if (!incidentExists) throw;

            message = CreateMessage();
            db.ReportedMessages.Add(message);
            await dispatcher.DispatchAsync(message, cancellationToken);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                existing = await FindExisting(applicationId, request.ReportId, cancellationToken);
                if (existing == null) throw;
                return this.Protocol(Receipt(existing, true));
            }
        }
        return this.Protocol(Receipt(message, false));
    }

    private Task<ReportedMessage?> FindExisting(Guid applicationId, Guid reportId, CancellationToken cancellationToken) =>
        db.ReportedMessages.AsNoTracking().SingleOrDefaultAsync(
            m => m.ApplicationId == applicationId && m.ReportId == reportId, cancellationToken);

    private static ReportMessageResponse Receipt(ReportedMessage message, bool duplicate) => new()
    {
        Code = duplicate ? Code.NoActionTaken : Code.JobDone,
        Message = duplicate ? "This report has already been received." : "The report was received.",
        Id = message.Id,
        ReportId = message.ReportId,
        ReceivedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(message.ReceivedAtUtc, DateTimeKind.Utc)),
        IncidentId = message.IncidentId,
        Archived = message.IsArchived,
        Duplicate = duplicate
    };
}

using System.Net.Http.Headers;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Aiursoft.AlertCenter.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/messages")]
public class MessageReportsController(TemplateDbContext db, IAlertDispatcher dispatcher) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> Report(ReportMessageRequest request, CancellationToken cancellationToken)
    {
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
            return Unauthorized();
        var parts = header.Parameter?.Split('.', 2);
        if (parts is not { Length: 2 } || !Guid.TryParse(parts[0], out var applicationId))
            return Unauthorized();
        var application = await db.SourceApplications.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == applicationId && a.IsEnabled, cancellationToken);
        if (application == null || new PasswordHasher<SourceApplication>()
                .VerifyHashedPassword(application, application.SecretHash, parts[1]) == PasswordVerificationResult.Failed)
            return Unauthorized();
        if (request.ReportId == Guid.Empty || request.OccurredAtUtc == default)
            return BadRequest(new { error = "reportId and occurredAtUtc are required." });

        var existing = await FindExisting(applicationId, request.ReportId, cancellationToken);
        if (existing != null) return Ok(Receipt(existing, true));
        var message = new ReportedMessage
        {
            ApplicationId = applicationId, ReportId = request.ReportId, Title = request.Title,
            Content = request.Content, Level = request.Level, OccurredAtUtc = request.OccurredAtUtc.UtcDateTime,
            ContextJson = request.Context?.ToString(Formatting.None)
        };
        db.ReportedMessages.Add(message);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // 并发重复上报以已落库记录为准，其他数据库错误继续向上传递。
            db.Entry(message).State = EntityState.Detached;
            existing = await FindExisting(applicationId, request.ReportId, cancellationToken);
            if (existing == null) throw;
            return Ok(Receipt(existing, true));
        }
        await dispatcher.DispatchAsync(message, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, Receipt(message, false));
    }

    private Task<ReportedMessage?> FindExisting(Guid applicationId, Guid reportId, CancellationToken cancellationToken) =>
        db.ReportedMessages.AsNoTracking().SingleOrDefaultAsync(
            m => m.ApplicationId == applicationId && m.ReportId == reportId, cancellationToken);

    private static object Receipt(ReportedMessage message, bool duplicate) => new
    {
        id = message.Id, reportId = message.ReportId, receivedAtUtc = message.ReceivedAtUtc, duplicate
    };
}

using System.Net;
using System.Security.Claims;
using Aiursoft.AlertCenter.Controllers;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class IncidentWorkflowTests
{
    private const string Secret = "incident-test-secret";
    private SqliteContext _db = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.MigrateAsync();
        _db.Users.Add(new User { Id = "admin", UserName = "admin", NormalizedUserName = "ADMIN", DisplayName = "Administrator" });
        await _db.SaveChangesAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _db.DisposeAsync();

    private IncidentsController IncidentController() => new(_db, new ApplicationAccessService(_db))
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim(ClaimTypes.Role, "Administrators")], "test"))
            }
        }
    };

    private async Task<SourceApplication> CreateApplication()
    {
        var application = new SourceApplication { Name = "Orders" };
        application.SecretHash = new PasswordHasher<SourceApplication>().HashPassword(application, Secret);
        _db.SourceApplications.Add(application);
        var channel = new PushChannel { Type = "webhook", Name = "Operations", Endpoint = "https://example.test/hook" };
        _db.PushChannels.Add(channel);
        _db.ApplicationPushChannels.Add(new ApplicationPushChannel { Application = application, Channel = channel });
        await _db.SaveChangesAsync();
        return application;
    }

    private async Task<ReportedMessage> Report(SourceApplication application, string title, string level, string? incidentKey)
    {
        _db.ChangeTracker.Clear();
        var controller = new MessageReportsController(_db, new QueuedAlertDispatcher(_db, new IncidentMatcher(_db)))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers.Authorization = new($"Bearer {application.Id}.{Secret}");
        var result = await controller.Report(new ReportMessageRequest
        {
            ReportId = Guid.NewGuid(), Title = title, Level = level, IncidentKey = incidentKey,
            OccurredAtUtc = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        Assert.AreEqual(StatusCodes.Status201Created, ((ObjectResult)result).StatusCode);
        return await _db.ReportedMessages.OrderByDescending(message => message.ReceivedAtUtc).FirstAsync();
    }

    [TestMethod]
    public async Task AcknowledgedIncidentArchivesMatchingAlertsWithoutCancellingExistingDeliveries()
    {
        var application = await CreateApplication();
        var first = await Report(application, "Payment provider unavailable", "sev1", "payment-provider");
        var incidentController = IncidentController();
        var triggered = await incidentController.Trigger(new TriggerIncidentRequest
        {
            MessageId = first.Id, Title = "Payment provider outage", Note = "Investigating."
        }, CancellationToken.None);
        Assert.IsInstanceOfType<RedirectToActionResult>(triggered);
        var incident = await _db.Incidents.SingleAsync();
        Assert.AreEqual(Incident.Triggered, incident.Status);
        Assert.AreEqual(1, await _db.IncidentTimelineEntries.CountAsync());

        var beforeAcknowledgement = await Report(application, "Payment provider unavailable", "sev1", "payment-provider");
        Assert.AreEqual(incident.Id, beforeAcknowledgement.IncidentId);
        Assert.IsFalse(beforeAcknowledgement.IsArchived);
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync());

        var acknowledged = await incidentController.Acknowledge(new IncidentNoteRequest
        {
            IncidentId = incident.Id, Note = "On-call engineer is handling this."
        }, CancellationToken.None);
        Assert.IsInstanceOfType<RedirectToActionResult>(acknowledged);

        var archived = await Report(application, "Payment provider unavailable", "sev1", "payment-provider");
        Assert.AreEqual(incident.Id, archived.IncidentId);
        Assert.IsTrue(archived.IsArchived);
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync());
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync(delivery => delivery.Status == PushDelivery.Pending));

        Assert.IsInstanceOfType<RedirectToActionResult>(await incidentController.AddNote(new IncidentNoteRequest
        {
            IncidentId = incident.Id, Note = "Traffic has been switched to the fallback provider."
        }, CancellationToken.None));
        Assert.IsInstanceOfType<RedirectToActionResult>(await incidentController.Resolve(new IncidentNoteRequest
        {
            IncidentId = incident.Id, Note = "Provider recovered and errors stopped."
        }, CancellationToken.None));
        Assert.AreEqual(Incident.Resolved, (await _db.Incidents.SingleAsync()).Status);
        Assert.AreEqual(4, await _db.IncidentTimelineEntries.CountAsync());

        var afterResolution = await Report(application, "Payment provider unavailable", "sev1", "payment-provider");
        Assert.IsNull(afterResolution.IncidentId);
        Assert.IsFalse(afterResolution.IsArchived);
        Assert.AreEqual(3, await _db.PushDeliveries.CountAsync());
    }

    [TestMethod]
    public async Task IncidentWithoutKeyMatchesOnlySameApplicationTitleAndLevel()
    {
        var application = await CreateApplication();
        var first = await Report(application, "Database connection failures", "sev2", null);
        var controller = IncidentController();
        await controller.Trigger(new TriggerIncidentRequest { MessageId = first.Id, Title = "Database incident" }, CancellationToken.None);
        var incident = await _db.Incidents.SingleAsync();
        await controller.Acknowledge(new IncidentNoteRequest { IncidentId = incident.Id }, CancellationToken.None);

        var matching = await Report(application, "Database connection failures", "sev2", null);
        var differentLevel = await Report(application, "Database connection failures", "sev1", null);
        Assert.IsTrue(matching.IsArchived);
        Assert.AreEqual(incident.Id, matching.IncidentId);
        Assert.IsFalse(differentLevel.IsArchived);
        Assert.IsNull(differentLevel.IncidentId);
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync());
    }
}

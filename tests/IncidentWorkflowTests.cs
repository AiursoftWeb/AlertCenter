using Aiursoft.AlertCenter.Authorization;
using System.Security.Claims;
using Aiursoft.AlertCenter.Controllers;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public partial class IncidentWorkflowTests
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

    private IncidentsController IncidentController() => new(_db, new ApplicationAccessService(_db), localizer: TestLocalization.For<IncidentsController>(), localizerFactory: TestLocalization.Factory)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim(ClaimTypes.Role, "Administrators"), .. AppPermissions.GetAllPermissions().Select(permission => new Claim(AppPermissions.Type, permission.Key))], "test"))
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
        var controller = new MessageReportsController(_db, new QueuedAlertDispatcher(_db, new IncidentMatcher(_db), localizerFactory: TestLocalization.Factory))
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
        var incident = await _db.Incidents.SingleAsync();
        Assert.AreEqual(incident.Id, first.IncidentId);
        Assert.AreEqual("Payment provider unavailable", incident.Title);
        Assert.AreEqual(Incident.New, incident.Status);
        var triggeredEntry = await _db.IncidentTimelineEntries.SingleAsync();
        Assert.AreEqual(IncidentTimelineEntry.Created, triggeredEntry.Type);
        Assert.IsNull(triggeredEntry.ActorId);

        var beforeAcknowledgement = await Report(application, "Payment provider unavailable", "sev1", "payment-provider");
        Assert.AreEqual(incident.Id, beforeAcknowledgement.IncidentId);
        Assert.IsFalse(beforeAcknowledgement.IsArchived);
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync());

        var acknowledged = await incidentController.Acknowledge(new IncidentNoteRequest
        {
            IncidentId = incident.Id, Version = incident.Version, Note = "On-call engineer is handling this."
        }, CancellationToken.None);
        Assert.IsInstanceOfType<RedirectToActionResult>(acknowledged);

        var archived = await Report(application, "Payment provider unavailable", "sev1", "payment-provider");
        Assert.AreEqual(incident.Id, archived.IncidentId);
        Assert.IsTrue(archived.IsArchived);
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync());
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync(delivery => delivery.Status == PushDelivery.Pending));
        incident = await _db.Incidents.SingleAsync();

        Assert.IsInstanceOfType<RedirectToActionResult>(await incidentController.AddNote(new IncidentNoteRequest
        {
            IncidentId = incident.Id, Version = incident.Version, Note = "Traffic has been switched to the fallback provider."
        }, CancellationToken.None));
        Assert.IsInstanceOfType<RedirectToActionResult>(await incidentController.Mitigate(new IncidentNoteRequest
        {
            IncidentId = incident.Id, Version = incident.Version, Note = "Fallback provider mitigated the impact."
        }, CancellationToken.None));
        Assert.IsInstanceOfType<RedirectToActionResult>(await incidentController.Resolve(new IncidentNoteRequest
        {
            IncidentId = incident.Id, Version = incident.Version, Note = "Provider recovered and errors stopped."
        }, CancellationToken.None));
        Assert.AreEqual(Incident.Resolved, (await _db.Incidents.SingleAsync()).Status);
        Assert.AreEqual(5, await _db.IncidentTimelineEntries.CountAsync());

        var afterResolution = await Report(application, "Payment provider unavailable", "sev1", "payment-provider");
        Assert.IsFalse(afterResolution.IsArchived);
        Assert.AreEqual(3, await _db.PushDeliveries.CountAsync());
        Assert.AreEqual(2, await _db.Incidents.CountAsync());
        Assert.AreNotEqual(incident.Id, afterResolution.IncidentId);
    }


    [TestMethod]
    public async Task AutomaticCreationReusesExistingIncidentWithSameMatchKey()
    {
        var application = await CreateApplication();
        var first = await Report(application, "Payment provider unavailable", "sev1", "payment-provider");
        var second = await Report(application, "Payment provider unavailable", "sev1", "payment-provider");

        Assert.AreEqual(1, await _db.Incidents.CountAsync());
        Assert.AreEqual(first.IncidentId, second.IncidentId);
        CollectionAssert.AreEquivalent(new[] { first.Id, second.Id }, await _db.ReportedMessages
            .Where(message => message.IncidentId == first.IncidentId).Select(message => message.Id).ToArrayAsync());
        Assert.AreEqual(1, await _db.IncidentTimelineEntries.CountAsync());
    }

    [TestMethod]
    public async Task ActiveIncidentMatchKeyIsUniquePerApplication()
    {
        var application = await CreateApplication();
        const string matchKey = "key:payment-provider";
        _db.Incidents.Add(new Incident
        {
            ApplicationId = application.Id, Title = "First outage", IncidentKey = "payment-provider",
            ActiveMatchKey = matchKey, AlertTitle = "Payment provider unavailable", AlertLevel = "sev1"
        });
        await _db.SaveChangesAsync();
        _db.Incidents.Add(new Incident
        {
            ApplicationId = application.Id, Title = "Second outage", IncidentKey = "payment-provider",
            ActiveMatchKey = matchKey, AlertTitle = "Payment provider unavailable", AlertLevel = "sev1"
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task ResolvedIncidentReleasesMatchKeyForANewIncident()
    {
        var application = await CreateApplication();
        var first = await Report(application, "Payment provider unavailable", "sev1", "payment-provider");
        var controller = IncidentController();
        var firstIncident = await _db.Incidents.SingleAsync();
        await controller.Acknowledge(new IncidentNoteRequest { IncidentId = firstIncident.Id, Version = firstIncident.Version }, CancellationToken.None);
        await controller.Mitigate(new IncidentNoteRequest { IncidentId = firstIncident.Id, Version = firstIncident.Version, Note = "Impact mitigated" }, CancellationToken.None);
        await controller.Resolve(new IncidentNoteRequest { IncidentId = firstIncident.Id, Version = firstIncident.Version, Note = "Root cause fixed" }, CancellationToken.None);

        var second = await Report(application, "Payment provider unavailable", "sev1", "payment-provider");

        Assert.AreEqual(2, await _db.Incidents.CountAsync());
        Assert.AreEqual(Incident.Resolved, (await _db.Incidents.SingleAsync(incident => incident.Id == firstIncident.Id)).Status);
        Assert.AreNotEqual(firstIncident.Id, second.IncidentId);
        Assert.AreEqual(first.IncidentId, firstIncident.Id);
    }

    [TestMethod]
    public async Task IncidentWithoutKeyMatchesOnlySameApplicationTitleAndLevel()
    {
        var application = await CreateApplication();
        var first = await Report(application, "Database connection failures", "sev2", null);
        var controller = IncidentController();
        var incident = await _db.Incidents.SingleAsync();
        await controller.Acknowledge(new IncidentNoteRequest { IncidentId = incident.Id, Version = incident.Version }, CancellationToken.None);

        var matching = await Report(application, "Database connection failures", "sev2", null);
        var differentLevel = await Report(application, "Database connection failures", "sev1", null);
        Assert.IsTrue(matching.IsArchived);
        Assert.AreEqual(incident.Id, matching.IncidentId);
        Assert.IsFalse(differentLevel.IsArchived);
        Assert.IsNotNull(differentLevel.IncidentId);
        Assert.AreEqual(2, await _db.Incidents.CountAsync());
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync());
        Assert.AreEqual(incident.Id, first.IncidentId);
    }
}

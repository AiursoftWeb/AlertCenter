using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.SDK.Models;
using Aiursoft.AlertCenter.Controllers;
using Aiursoft.AlertCenter.Entities;
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
    private ServiceProvider _apiServices = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _apiServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        _db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.MigrateAsync();
        _db.Users.Add(new User { Id = "admin", UserName = "admin", NormalizedUserName = "ADMIN", DisplayName = "Administrator" });
        await _db.SaveChangesAsync();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _db.DisposeAsync();
        await _apiServices.DisposeAsync();
    }

    private async Task<SourceApplication> CreateApplication()
    {
        var application = new SourceApplication { Name = "Orders", OwnerId = "admin" };
        var key = new ApplicationApiKey { Id = application.Id, Application = application, Name = "Test key", CanQueryIncidents = true, CanCreateIncidents = true, CanResolveIncidents = true };
        key.SecretHash = new PasswordHasher<ApplicationApiKey>().HashPassword(key, Secret);
        _db.ApplicationApiKeys.Add(key);
        _db.SourceApplications.Add(application);
        var channel = new PushChannel { Type = "webhook", Name = "Operations", Endpoint = "https://example.test/hook" };
        _db.PushChannels.Add(channel);
        _db.ApplicationPushChannels.Add(new ApplicationPushChannel { Application = application, Channel = channel });
        await _db.SaveChangesAsync();
        return application;
    }

    private async Task<Incident> Report(SourceApplication application, string title, string level)
    {
        _db.ChangeTracker.Clear();
        var controller = new IncidentReportsController(_db, new QueuedIncidentDispatcher(_db, localizerFactory: TestLocalization.Factory))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = _apiServices } }
        };
        controller.Request.Headers.Authorization = new($"Bearer {application.Id}.{application.Id}.{Secret}");
        var result = await controller.Create(new CreateIncidentRequest
        {
            ReportId = Guid.NewGuid(), Title = title, Level = level,
            OccurredAtUtc = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        Assert.AreEqual(Code.JobDone, ((CreateIncidentResponse)((JsonResult)result).Value!).Code);
        return await _db.Incidents.OrderByDescending(message => message.ReceivedAtUtc).FirstAsync();
    }

    [TestMethod]
    [DataRow(Incident.Acked)]
    [DataRow(Incident.Mitigated)]
    [DataRow(Incident.Resolved)]
    public async Task ExistingIncidentStateDoesNotSuppressNewIncident(string status)
    {
        var application = await CreateApplication();
        var first = await Report(application, "Outage", "sev1");
        first.Status = status == Incident.Acked ? Incident.New : status;
        first.AcknowledgedAtUtc = status == Incident.Acked ? DateTime.UtcNow : null;
        await _db.SaveChangesAsync();
        var second = await Report(application, "Outage", "sev1");
        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreEqual(Incident.New, second.Status);
        Assert.AreEqual(2, await _db.Incidents.CountAsync());
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync());
    }
}

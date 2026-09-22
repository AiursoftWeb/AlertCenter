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
public class ApplicationOnCallRoutingTests
{
    private const string Secret = "oncall-routing-secret";
    private SqliteContext _db = null!;
    private QueuedAlertDispatcher _dispatcher = null!;
    private PushDeliveryProcessor _processor = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.MigrateAsync();
        _dispatcher = new QueuedAlertDispatcher(_db, new IncidentMatcher(_db), new OnCallScheduleService(_db));
        _processor = new PushDeliveryProcessor(_db, [new OceInAppChannelAdapter(_db)], NullLogger<PushDeliveryProcessor>.Instance);
        await AddUserAsync("admin", "Administrator", "admin@example.com");
    }

    [TestCleanup]
    public async Task Cleanup() => await _db.DisposeAsync();

    [TestMethod]
    public async Task BindingRequiresAdministratorAndARealOnCallGroup()
    {
        var application = await AddApplicationAsync();
        var group = new OnCallGroup { Name = "Operations", TimeZoneId = "UTC" };
        _db.OnCallGroups.Add(group);
        await _db.SaveChangesAsync();

        var controller = AlertsController("admin", true);
        var invalid = await controller.SaveOnCallBinding(new ApplicationOnCallBindingRequest
        {
            ApplicationId = application.Id, OnCallGroupId = Guid.NewGuid()
        }, default);
        Assert.IsInstanceOfType<BadRequestObjectResult>(invalid);
        var saved = await controller.SaveOnCallBinding(new ApplicationOnCallBindingRequest
        {
            ApplicationId = application.Id, OnCallGroupId = group.Id
        }, default);
        Assert.IsInstanceOfType<RedirectToActionResult>(saved);
        Assert.AreEqual(group.Id, (await _db.SourceApplications.SingleAsync()).OnCallGroupId);
        Assert.IsInstanceOfType<ForbidResult>(await AlertsController("ordinary", false).SaveOnCallBinding(
            new ApplicationOnCallBindingRequest { ApplicationId = application.Id, OnCallGroupId = group.Id }, default));
    }

    [TestMethod]
    public async Task AlertWithoutOnCallGroupCreatesIncidentAndRecordsFailureWithoutStaticRouting()
    {
        await AddGroupAsync();
        var application = await AddApplicationAsync();
        var message = await ReportAsync(application);

        Assert.IsNotNull(message.IncidentId);
        Assert.AreEqual(1, await _db.AlertRoutingFailures.CountAsync());
        var failure = await _db.AlertRoutingFailures.SingleAsync();
        Assert.AreEqual(AlertRoutingFailure.MissingOnCallGroup, failure.ReasonCode);
        Assert.AreEqual(message.Id, failure.MessageId);
        Assert.AreEqual(0, await _db.PushDeliveries.CountAsync());
        Assert.IsNull((await _db.Incidents.SingleAsync()).OceUserId);
    }

    [TestMethod]
    public async Task AlertWithNoCurrentOceRecordsFailureWithoutDelivery()
    {
        var group = await AddGroupAsync();
        var application = await AddApplicationAsync(group.Id);
        var message = await ReportAsync(application);

        Assert.AreEqual(AlertRoutingFailure.MissingOce, (await _db.AlertRoutingFailures.SingleAsync()).ReasonCode);
        Assert.AreEqual(0, await _db.PushDeliveries.CountAsync());
        Assert.IsNull((await _db.Incidents.SingleAsync()).OceUserId);
        Assert.AreEqual(message.Id, (await _db.AlertRoutingFailures.SingleAsync()).MessageId);
    }

    [TestMethod]
    public async Task NewAlertSnapshotsOceAndDeliversOnlyToThatEngineer()
    {
        var group = await AddGroupAsync();
        await AddUserAsync("oce", "On Call", "oce@example.com");
        await AddUserAsync("other", "Other", "other@example.com");
        _db.OnCallGroupMembers.AddRange(
            new OnCallGroupMember { GroupId = group.Id, UserId = "oce" },
            new OnCallGroupMember { GroupId = group.Id, UserId = "other" });
        _db.OnCallShifts.Add(new OnCallShift
        {
            GroupId = group.Id, UserId = "oce", StartsAtUtc = DateTime.UtcNow.AddHours(-1), EndsAtUtc = DateTime.UtcNow.AddHours(1)
        });
        await _db.SaveChangesAsync();
        var application = await AddApplicationAsync(group.Id);
        var message = await ReportAsync(application);

        var incident = await _db.Incidents.SingleAsync();
        Assert.AreEqual("oce", incident.OceUserId);
        Assert.AreEqual("oce@example.com", incident.OceEmail);
        Assert.AreEqual(group.Id, incident.OnCallGroupId);
        Assert.IsNotNull(incident.OceNotifiedAtUtc);
        var delivery = await _db.PushDeliveries.SingleAsync();
        Assert.AreEqual("oce-inapp", delivery.Channel.Type);
        var recipient = await _db.PushDeliveryRecipients.SingleAsync();
        Assert.AreEqual("oce", recipient.UserId);
        Assert.AreEqual("On Call", recipient.DisplayName);
        Assert.AreEqual("oce@example.com", recipient.Email);

        await _processor.ProcessAsync(delivery.Id, default);
        Assert.AreEqual("oce", (await _db.InAppNotifications.SingleAsync()).UserId);
        Assert.AreEqual(0, await _db.AlertRoutingFailures.CountAsync());

        _db.OnCallShifts.Add(new OnCallShift
        {
            GroupId = group.Id, UserId = "other", StartsAtUtc = DateTime.UtcNow.AddHours(-1), EndsAtUtc = DateTime.UtcNow.AddHours(1)
        });
        await _db.SaveChangesAsync();
        Assert.AreEqual("oce", (await _db.Incidents.SingleAsync()).OceUserId);
        Assert.AreEqual(message.Id, (await _db.ReportedMessages.SingleAsync()).Id);
    }

    [TestMethod]
    public async Task DuplicateReportsRemainIdempotentAndDoNotCreateAdditionalOceDeliveries()
    {
        var group = await AddGroupAsync();
        await AddUserAsync("oce", "On Call", "oce@example.com");
        _db.OnCallGroupMembers.Add(new OnCallGroupMember { GroupId = group.Id, UserId = "oce" });
        _db.OnCallShifts.Add(new OnCallShift
        {
            GroupId = group.Id, UserId = "oce", StartsAtUtc = DateTime.UtcNow.AddHours(-1), EndsAtUtc = DateTime.UtcNow.AddHours(1)
        });
        await _db.SaveChangesAsync();
        var application = await AddApplicationAsync(group.Id);
        var reportId = Guid.NewGuid();
        await ReportAsync(application, reportId);
        var duplicate = await ReportAsync(application, reportId);

        Assert.AreEqual(1, await _db.ReportedMessages.CountAsync());
        Assert.AreEqual(1, await _db.Incidents.CountAsync());
        Assert.AreEqual(1, await _db.PushDeliveries.CountAsync());
        Assert.AreEqual(0, await _db.AlertRoutingFailures.CountAsync());
        Assert.AreEqual(reportId, duplicate.ReportId);
    }

    private async Task<ReportedMessage> ReportAsync(SourceApplication application, Guid? reportId = null)
    {
        _db.ChangeTracker.Clear();
        var controller = new MessageReportsController(_db, _dispatcher)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers.Authorization = new($"Bearer {application.Id}.{Secret}");
        var result = await controller.Report(new ReportMessageRequest
        {
            ReportId = reportId ?? Guid.NewGuid(), Title = "Payment failure", Level = "sev1", OccurredAtUtc = DateTimeOffset.UtcNow
        }, default);
        Assert.IsTrue(result is ObjectResult { StatusCode: StatusCodes.Status201Created } or OkObjectResult);
        return await _db.ReportedMessages.OrderByDescending(item => item.ReceivedAtUtc).FirstAsync();
    }

    private async Task<SourceApplication> AddApplicationAsync(Guid? groupId = null)
    {
        var application = new SourceApplication { Name = $"Orders {Guid.NewGuid():N}", OnCallGroupId = groupId };
        application.SecretHash = new PasswordHasher<SourceApplication>().HashPassword(application, Secret);
        _db.SourceApplications.Add(application);
        await _db.SaveChangesAsync();
        return application;
    }

    private async Task<OnCallGroup> AddGroupAsync()
    {
        var group = new OnCallGroup { Name = $"Operations {Guid.NewGuid():N}", TimeZoneId = "UTC" };
        _db.OnCallGroups.Add(group);
        await _db.SaveChangesAsync();
        return group;
    }

    private async Task AddUserAsync(string id, string name, string email)
    {
        if (await _db.Users.AnyAsync(item => item.Id == id)) return;
        _db.Users.Add(new User { Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant(), DisplayName = name, Email = email });
        await _db.SaveChangesAsync();
    }

    private AlertsController AlertsController(string userId, bool administrator)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        if (administrator) claims.Add(new Claim(ClaimTypes.Role, "Administrators"));
        return new AlertsController(_db, new ApplicationAccessService(_db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
            }
        };
    }
}

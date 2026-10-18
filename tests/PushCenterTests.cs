using Microsoft.Extensions.Options;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.SDK.Models;
using Aiursoft.AlertCenter.Authorization;
using System.Net;
using System.Security.Claims;
using Aiursoft.AlertCenter.Configuration;
using System.Text.Json;
using Aiursoft.AlertCenter.Controllers;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public partial class PushCenterTests
{
    private SqliteContext _db = null!;
    private ServiceProvider _apiServices = null!;
    private FakeWebhookHandler _http = null!;
    private FakeEmailSender _email = null!;
    private INotificationChannelAdapter[] _adapters = [];
    private PushDeliveryProcessor _processor = null!;
    private WebhookConfigurationProtector _webhookConfigurationProtector = null!;
    private IPushChannelAccessService _channelAccess = null!;
    private const string Secret = "push-tests-secret";

    [TestInitialize]
    public async Task Initialize()
    {
        _apiServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        _db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.MigrateAsync();
        _http = new FakeWebhookHandler();
        _webhookConfigurationProtector = new WebhookConfigurationProtector(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Webhook:HeaderProtectionKey"] = Convert.ToBase64String(new byte[32])
            }).Build());
        _email = new FakeEmailSender();
        _adapters = [new WebhookChannelAdapter(new FakeClientFactory(_http), _webhookConfigurationProtector, localizer: TestLocalization.For<WebhookChannelAdapter>()), new InAppChannelAdapter(_db, localizer: TestLocalization.For<InAppChannelAdapter>()), new EmailChannelAdapter(_email, localizer: TestLocalization.For<EmailChannelAdapter>(), options: Options.Create(new EmailSettings()))];
        _processor = new PushDeliveryProcessor(_db, _adapters, NullLogger<PushDeliveryProcessor>.Instance, localizer: TestLocalization.For<PushDeliveryProcessor>());
        _channelAccess = new PushChannelAccessService(_db);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _db.DisposeAsync();
        await _apiServices.DisposeAsync();
        _http.Dispose();
    }

    private PushController Controller(string userId = "admin-one", bool administrator = true, params Claim[] claims) => new(_db, _adapters, _processor, _webhookConfigurationProtector, new ApplicationAccessService(_db), _channelAccess, localizer: TestLocalization.For<PushController>(), localizerFactory: TestLocalization.Factory)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    administrator
                        ? [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, "Administrators"), .. AppPermissions.GetAllPermissions().Select(permission => new Claim(AppPermissions.Type, permission.Key)), .. claims]
                        : [new Claim(ClaimTypes.NameIdentifier, userId), .. claims],
                    "test"))
            }
        }
    };

    private async Task<SourceApplication> Application()
    {
        var owner = await _db.Users.FindAsync("admin-one");
        if (owner == null)
        {
            owner = new User { Id = "admin-one", UserName = "admin-one", DisplayName = "admin-one" };
            _db.Users.Add(owner);
        }
        var application = new SourceApplication { Name = "Test application", Owner = owner };
        application.SecretHash = new PasswordHasher<SourceApplication>().HashPassword(application, Secret);
        _db.SourceApplications.Add(application);
        await _db.SaveChangesAsync();
        return application;
    }

    private async Task<PushChannel> Channel(string type = "webhook", bool enabled = true)
    {
        var channel = new PushChannel
        {
            Type = type, Name = type + " test", Endpoint = type == "webhook" ? "https://receiver.example/hook" : null,
            EmailRecipient = type == "email" ? "oncall@example.com" : string.Empty, IsEnabled = enabled
        };
        _db.PushChannels.Add(channel);
        if (type == "inapp")
        {
            var role = await _db.Roles.SingleOrDefaultAsync(role => role.NormalizedName == "ADMINISTRATORS");
            if (role != null) _db.PushChannelRoles.Add(new PushChannelRole { Channel = channel, RoleId = role.Id });
        }
        await _db.SaveChangesAsync();
        return channel;
    }

    private async Task AddAdministrators()
    {
        var role = new IdentityRole("Administrators") { NormalizedName = "ADMINISTRATORS" };
        _db.Roles.Add(role);
        foreach (var id in new[] { "admin-one", "admin-two", "ordinary-user" })
        {
            if (!await _db.Users.AnyAsync(user => user.Id == id))
                _db.Users.Add(new User { Id = id, DisplayName = id, UserName = id, NormalizedUserName = id.ToUpperInvariant() });
            if (id != "ordinary-user") _db.UserRoles.Add(new IdentityUserRole<string> { UserId = id, RoleId = role.Id });
        }
        await _db.SaveChangesAsync();
    }

    private Task<IActionResult> BindAllLevels(Guid applicationId, params Guid[] channelIds) =>
        Controller().SaveBindings(applicationId, new ApplicationPushBindingsRequest
        {
            Sev0ChannelIds = channelIds, Sev1ChannelIds = channelIds,
            Sev2ChannelIds = channelIds, Sev3ChannelIds = channelIds, Sev4ChannelIds = channelIds
        });

    private async Task<IActionResult> Report(SourceApplication application, Guid reportId, string level = "sev3")
    {
        _db.ChangeTracker.Clear();
        var controller = new IncidentReportsController(_db, new QueuedIncidentDispatcher(_db, localizerFactory: TestLocalization.Factory))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = _apiServices } }
        };
        controller.Request.Headers.Authorization = $"Bearer {application.Id}.{Secret}";
        return await controller.Create(new CreateIncidentRequest
        {
            ReportId = reportId, Title = "<script>unsafe</script>", Content = null, Level = level,
            OccurredAtUtc = DateTimeOffset.UtcNow, Context = Newtonsoft.Json.Linq.JObject.Parse("{\"orderId\":123}")
        }, CancellationToken.None);
    }

    [TestMethod]
    public async Task FreshDatabaseHasNoPushData()
    {
        Assert.AreEqual(0, await _db.PushChannels.CountAsync());
        Assert.AreEqual(0, await _db.PushDeliveries.CountAsync());
        Assert.AreEqual(0, await _db.InAppNotifications.CountAsync());
    }

    [TestMethod]
    public async Task ReportingQueuesOnceAndDeliversBothTypesWithoutOrdinaryUserNotifications()
    {
        await AddAdministrators();
        var application = await Application();
        var webhook = await Channel();
        var inbox = await Channel("inapp");
        var disabled = await Channel(enabled: false);
        await BindAllLevels(application.Id, webhook.Id, inbox.Id, disabled.Id);
        var reportId = Guid.NewGuid();

        var first = (JsonResult)await Report(application, reportId);
        Assert.AreEqual(Code.JobDone, ((CreateIncidentResponse)first.Value!).Code);
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync());
        Assert.AreEqual(0, _http.Requests.Count);
        Assert.AreEqual(Code.NoActionTaken, ((CreateIncidentResponse)((JsonResult)await Report(application, reportId)).Value!).Code);
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync());
        Assert.AreEqual(1, await _db.Incidents.CountAsync());

        var ids = await _db.PushDeliveries.Select(delivery => delivery.Id).ToListAsync();
        foreach (var id in ids)
        {
            await _processor.ProcessAsync(id, CancellationToken.None);
            await _processor.ProcessAsync(id, CancellationToken.None);
        }
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync(delivery => delivery.Status == PushDelivery.Succeeded && delivery.Attempts == 1));
        Assert.AreEqual(2, await _db.InAppNotifications.CountAsync());
        Assert.IsFalse(await _db.InAppNotifications.AnyAsync(notification => notification.UserId == "ordinary-user"));
        Assert.AreEqual(1, _http.Requests.Count);
        var request = _http.Requests.Single();
        using var payload = JsonDocument.Parse(request.Json);
        Assert.AreEqual(123, payload.RootElement.GetProperty("context").GetProperty("orderId").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, payload.RootElement.GetProperty("content").ValueKind);
        Assert.AreEqual(payload.RootElement.GetProperty("deliveryId").GetString(), request.IdempotencyKey);
        Assert.AreEqual("<script>unsafe</script>", payload.RootElement.GetProperty("title").GetString());
    }

    [TestMethod]
    [DataRow("sev0")]
    [DataRow("sev1")]
    [DataRow("sev2")]
    [DataRow("sev3")]
    [DataRow("sev4")]
    public async Task ReportsRouteOnlyToExactSeverityAndMultipleSelectedChannels(string level)
    {
        var application = await Application();
        var otherApplication = await Application();
        var first = await Channel();
        var second = await Channel("inapp");
        var wrongLevel = await Channel();
        var disabled = await Channel(enabled: false);
        var otherApplicationChannel = await Channel();
        Guid[] matching = [first.Id, second.Id, disabled.Id, first.Id];
        await Controller().SaveBindings(application.Id, new ApplicationPushBindingsRequest
        {
            Sev0ChannelIds = level == "sev0" ? matching : [wrongLevel.Id],
            Sev1ChannelIds = level == "sev1" ? matching : [wrongLevel.Id],
            Sev2ChannelIds = level == "sev2" ? matching : [wrongLevel.Id],
            Sev3ChannelIds = level == "sev3" ? matching : [wrongLevel.Id],
            Sev4ChannelIds = level == "sev4" ? matching : [wrongLevel.Id]
        });
        await BindAllLevels(otherApplication.Id, otherApplicationChannel.Id);
        _db.ChangeTracker.Clear();
        var saved = await _db.ApplicationPushChannels.SingleAsync(link => link.ApplicationId == application.Id && link.ChannelId == first.Id);
        Assert.AreEqual(level == "sev0", saved.ReceiveSev0);
        Assert.AreEqual(level == "sev1", saved.ReceiveSev1);
        Assert.AreEqual(level == "sev2", saved.ReceiveSev2);
        Assert.AreEqual(level == "sev3", saved.ReceiveSev3);
        Assert.AreEqual(level == "sev4", saved.ReceiveSev4);

        var reportId = Guid.NewGuid();
        Assert.AreEqual(Code.JobDone, ((CreateIncidentResponse)((JsonResult)await Report(application, reportId, level)).Value!).Code);
        CollectionAssert.AreEquivalent(new[] { first.Id, second.Id },
            await _db.PushDeliveries.Select(delivery => delivery.ChannelId).ToArrayAsync());
        Assert.AreEqual(level, (await _db.Incidents.SingleAsync()).Level);
        Assert.AreEqual(Code.NoActionTaken, ((CreateIncidentResponse)((JsonResult)await Report(application, reportId, level)).Value!).Code);
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync());
    }

    [TestMethod]
    public async Task BindingChangesAffectOnlyNewAlertsAndPreserveOtherApplications()
    {
        var application = await Application();
        var otherApplication = await Application();
        var channel = await Channel();
        await BindAllLevels(application.Id, channel.Id);
        await BindAllLevels(otherApplication.Id, channel.Id);
        var reportId = Guid.NewGuid();
        await Report(application, reportId, "sev1");
        var pending = await _db.PushDeliveries.AsNoTracking().SingleAsync();

        await Controller().SaveBindings(application.Id, new ApplicationPushBindingsRequest { Sev2ChannelIds = [channel.Id] });
        Assert.AreEqual(Code.NoActionTaken, ((CreateIncidentResponse)((JsonResult)await Report(application, reportId, "sev2")).Value!).Code);
        await Report(application, Guid.NewGuid(), "sev1");
        Assert.AreEqual(2, await _db.Incidents.CountAsync());
        Assert.AreEqual(pending.Id, (await _db.PushDeliveries.SingleAsync()).Id);
        Assert.AreEqual(PushDelivery.Pending, (await _db.PushDeliveries.SingleAsync()).Status);
        await Report(application, Guid.NewGuid(), "sev2");
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync());

        await Controller().SaveBindings(application.Id, new ApplicationPushBindingsRequest());
        await Report(application, Guid.NewGuid(), "sev2");
        Assert.AreEqual(4, await _db.Incidents.CountAsync());
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync());
        var remaining = await _db.ApplicationPushChannels.SingleAsync();
        Assert.AreEqual(otherApplication.Id, remaining.ApplicationId);
        Assert.IsTrue(remaining.ReceiveSev0 && remaining.ReceiveSev1 && remaining.ReceiveSev2 && remaining.ReceiveSev3 && remaining.ReceiveSev4);
    }

    [TestMethod]
    public async Task InvalidSeverityBindingsDoNotPartiallyUpdateSelections()
    {
        var application = await Application();
        var channel = await Channel();
        await Controller().SaveBindings(application.Id, new ApplicationPushBindingsRequest { Sev4ChannelIds = [channel.Id] });
        var result = await Controller().SaveBindings(application.Id, new ApplicationPushBindingsRequest
        {
            Sev1ChannelIds = [channel.Id], Sev2ChannelIds = [Guid.NewGuid()]
        });
        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        var controller = Controller();
        controller.ModelState.AddModelError("Sev1ChannelIds", "Invalid channel ID");
        Assert.IsInstanceOfType<BadRequestObjectResult>(await controller.SaveBindings(application.Id, new ApplicationPushBindingsRequest()));
        _db.ChangeTracker.Clear();
        var saved = await _db.ApplicationPushChannels.SingleAsync();
        Assert.IsFalse(saved.ReceiveSev0 || saved.ReceiveSev1 || saved.ReceiveSev2 || saved.ReceiveSev3);
        Assert.IsTrue(saved.ReceiveSev4);
        Assert.IsInstanceOfType<NotFoundResult>(await Controller().SaveBindings(Guid.NewGuid(), new ApplicationPushBindingsRequest()));
    }

    [TestMethod]
    public async Task DisabledChannelRetainsSeveritySelectionWhenReenabled()
    {
        var application = await Application();
        var channel = await Channel(enabled: false);
        await Controller().SaveBindings(application.Id, new ApplicationPushBindingsRequest { Sev4ChannelIds = [channel.Id] });
        await Report(application, Guid.NewGuid(), "sev4");
        Assert.AreEqual(0, await _db.PushDeliveries.CountAsync());
        await Controller().SetEnabled(channel.Id, true);
        await Report(application, Guid.NewGuid(), "sev1");
        Assert.AreEqual(0, await _db.PushDeliveries.CountAsync());
        await Report(application, Guid.NewGuid(), "sev4");
        Assert.AreEqual(channel.Id, (await _db.PushDeliveries.SingleAsync()).ChannelId);
    }

    [TestMethod]
    public async Task FailedDeliveryDoesNotLoseAlertAndRetriesOnlyOnRequest()
    {
        var application = await Application();
        var channel = await Channel();
        await BindAllLevels(application.Id, channel.Id);
        _http.StatusCode = HttpStatusCode.BadGateway;
        Assert.AreEqual(Code.JobDone, ((CreateIncidentResponse)((JsonResult)await Report(application, Guid.NewGuid())).Value!).Code);
        var id = await _db.PushDeliveries.Select(delivery => delivery.Id).SingleAsync();
        await _processor.ProcessAsync(id, CancellationToken.None);
        Assert.AreEqual(1, await _db.Incidents.CountAsync());
        var failed = await _db.PushDeliveries.SingleAsync();
        Assert.AreEqual(PushDelivery.Failed, failed.Status);
        Assert.Contains("502", failed.Error!);
        await _processor.ProcessAsync(id, CancellationToken.None);
        Assert.AreEqual(1, _http.Requests.Count);
        _http.StatusCode = HttpStatusCode.OK;
        Assert.IsInstanceOfType<RedirectToActionResult>(await Controller().Retry(id));
        await _processor.ProcessAsync(id, CancellationToken.None);
        _db.ChangeTracker.Clear();
        var success = await _db.PushDeliveries.SingleAsync();
        Assert.AreEqual(PushDelivery.Succeeded, success.Status);
        Assert.AreEqual(2, success.Attempts);
        Assert.IsNull(success.Error);
        Assert.AreEqual(_http.Requests[0].IdempotencyKey, _http.Requests[1].IdempotencyKey);
        Assert.IsInstanceOfType<ConflictObjectResult>(await Controller().Retry(id));
    }

    [TestMethod]
    public async Task ReadStateIsPrivateAndRepeatedReadPreservesTimestamp()
    {
        await AddAdministrators();
        var channel = await Channel("inapp");
        Assert.IsInstanceOfType<RedirectToActionResult>(await Controller().Test(channel.Id, CancellationToken.None));
        var first = await _db.InAppNotifications.SingleAsync(notification => notification.UserId == "admin-one");
        var second = await _db.InAppNotifications.SingleAsync(notification => notification.UserId == "admin-two");
        Assert.IsInstanceOfType<NotFoundResult>(await Controller().MarkRead(second.Id, new InboxFilter()));
        await Controller().MarkRead(first.Id, new InboxFilter());
        var readAt = first.ReadAtUtc;
        Assert.IsNotNull(readAt);
        await Controller().MarkRead(first.Id, new InboxFilter());
        Assert.AreEqual(readAt, first.ReadAtUtc);
        Assert.IsNull(second.ReadAtUtc);
        await Controller("admin-two").MarkAllRead(new InboxFilter());
        Assert.AreEqual(0, await _db.InAppNotifications.CountAsync(notification => notification.ReadAtUtc == null));
    }

    [TestMethod]
    public async Task CustomTestMessageIsPersistedAndDeliveredThroughWebhook()
    {
        var channel = await Channel();
        const string content = "请检查自定义测试消息。";

        Assert.IsInstanceOfType<RedirectToActionResult>(await Controller().Test(channel.Id, CancellationToken.None, "  " + content + "  "));

        var delivery = await _db.PushDeliveries.SingleAsync();
        Assert.AreEqual(content, delivery.TestContent);
        Assert.AreEqual(PushDelivery.Succeeded, delivery.Status, delivery.Error);
        using var payload = JsonDocument.Parse(_http.Requests.Single().Json);
        Assert.AreEqual(content, payload.RootElement.GetProperty("content").GetString());
    }

    [TestMethod]
    public async Task TestMessageContentMustBePresentAndWithinLimit()
    {
        var channel = await Channel();

        Assert.IsInstanceOfType<BadRequestObjectResult>(await Controller().Test(channel.Id, CancellationToken.None, " \r\n "));
        Assert.IsInstanceOfType<BadRequestObjectResult>(await Controller().Test(channel.Id, CancellationToken.None, new string('x', 4001)));
        Assert.AreEqual(0, await _db.PushDeliveries.CountAsync());
    }

    [TestMethod]
    public async Task InAppDeliveryWithoutRecipientsFailsClearly()
    {
        var channel = await Channel("inapp");
        await Controller().Test(channel.Id, CancellationToken.None);
        var delivery = await _db.PushDeliveries.SingleAsync();
        Assert.AreEqual(PushDelivery.Failed, delivery.Status);
        Assert.Contains("receive", delivery.Error!);
        Assert.AreEqual(0, await _db.InAppNotifications.CountAsync());
    }

    [TestMethod]
    public async Task QueuedInAppDeliveryUsesRecipientsSnapshot()
    {
        await AddAdministrators();
        var application = await Application();
        var channel = await Channel("inapp");
        await BindAllLevels(application.Id, channel.Id);

        await Report(application, Guid.NewGuid());
        var delivery = await _db.PushDeliveries.SingleAsync();
        CollectionAssert.AreEquivalent(new[] { "admin-one", "admin-two" },
            await _db.PushDeliveryRecipients.Where(recipient => recipient.DeliveryId == delivery.Id)
                .Select(recipient => recipient.UserId).ToArrayAsync());

        _db.PushChannelRoles.RemoveRange(await _db.PushChannelRoles.Where(link => link.ChannelId == channel.Id).ToListAsync());
        _db.PushChannelUsers.Add(new PushChannelUser { ChannelId = channel.Id, UserId = "ordinary-user" });
        await _db.SaveChangesAsync();

        await _processor.ProcessAsync(delivery.Id, CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { "admin-one", "admin-two" },
            await _db.InAppNotifications.Where(notification => notification.DeliveryId == delivery.Id)
                .Select(notification => notification.UserId).ToArrayAsync());
    }

    [TestMethod]
    public async Task DisabledChannelDoesNotSendAndCanBeRetriedAfterEnabling()
    {
        var application = await Application();
        var channel = await Channel();
        await BindAllLevels(application.Id, channel.Id);
        await Report(application, Guid.NewGuid());
        await Controller().SetEnabled(channel.Id, false);
        var delivery = await _db.PushDeliveries.SingleAsync();
        await _processor.ProcessAsync(delivery.Id, CancellationToken.None);
        Assert.AreEqual(0, _http.Requests.Count);
        Assert.AreEqual(PushDelivery.Failed, delivery.Status);
        Assert.IsInstanceOfType<ConflictObjectResult>(await Controller().Retry(delivery.Id));
        Assert.IsInstanceOfType<BadRequestObjectResult>(await Controller().Test(channel.Id, CancellationToken.None));
        await Controller().SetEnabled(channel.Id, true);
        await Controller().Retry(delivery.Id);
        await _processor.ProcessAsync(delivery.Id, CancellationToken.None);
        Assert.AreEqual(1, _http.Requests.Count);
    }

    [TestMethod]
    public async Task ApplicationManagerCanBindOnlyGrantedChannels()
    {
        await AddAdministrators();
        var application = await Application();
        var granted = await Channel();
        var forbidden = await Channel();
        _db.ApplicationUserAccesses.Add(new ApplicationUserAccess
        {
            ApplicationId = application.Id,
            UserId = "ordinary-user",
            CanViewIncidents = true, CanUpdateIncidents = true, CanManageApiKeys = true, CanManageApplication = true
        });
        _db.PushChannelBindingUsers.Add(new PushChannelBindingUser
        {
            ChannelId = granted.Id,
            UserId = "ordinary-user"
        });
        await _db.SaveChangesAsync();

        var manager = Controller("ordinary-user", administrator: false);
        Assert.IsInstanceOfType<RedirectToActionResult>(await manager.SaveBindings(application.Id,
            new ApplicationPushBindingsRequest { Sev1ChannelIds = [granted.Id] }));
        Assert.IsInstanceOfType<ForbidResult>(await manager.SaveBindings(application.Id,
            new ApplicationPushBindingsRequest { Sev1ChannelIds = [forbidden.Id] }));
        Assert.AreEqual(granted.Id, (await _db.ApplicationPushChannels.SingleAsync()).ChannelId);
    }

    [TestMethod]
    public async Task ChannelBindingRoleGrantsOnlyItsMembersAccess()
    {
        await AddAdministrators();
        var application = await Application();
        var channel = await Channel();
        var role = new IdentityRole("OnCall");
        _db.Roles.Add(role);
        _db.UserRoles.Add(new IdentityUserRole<string> { UserId = "ordinary-user", RoleId = role.Id });
        _db.ApplicationRoleAccesses.Add(new ApplicationRoleAccess
        {
            ApplicationId = application.Id,
            RoleId = role.Id,
            CanViewIncidents = true, CanUpdateIncidents = true, CanManageApiKeys = true, CanManageApplication = true
        });
        _db.PushChannelBindingRoles.Add(new PushChannelBindingRole { ChannelId = channel.Id, RoleId = role.Id });
        await _db.SaveChangesAsync();

        var member = Controller("ordinary-user", administrator: false);
        Assert.IsInstanceOfType<RedirectToActionResult>(await member.SaveBindings(application.Id,
            new ApplicationPushBindingsRequest { Sev2ChannelIds = [channel.Id] }));
        Assert.IsInstanceOfType<ForbidResult>(await Controller("outsider", administrator: false).SaveBindings(application.Id,
            new ApplicationPushBindingsRequest { Sev2ChannelIds = [channel.Id] }));
        var binding = await _db.ApplicationPushChannels.SingleAsync();
        Assert.IsTrue(binding.ReceiveSev2);
        Assert.IsFalse(binding.ReceiveSev1 || binding.ReceiveSev3 || binding.ReceiveSev4);
    }

    [TestMethod]
    public async Task DeliveryAndRetryRequireBothApplicationAndChannelAccess()
    {
        await AddAdministrators();
        var application = await Application();
        var otherApplication = await Application();
        var channel = await Channel();
        await BindAllLevels(application.Id, channel.Id);
        await Report(application, Guid.NewGuid());
        var delivery = await _db.PushDeliveries.SingleAsync();
        delivery.Status = PushDelivery.Failed;
        _db.ApplicationUserAccesses.Add(new ApplicationUserAccess
        {
            ApplicationId = application.Id,
            UserId = "ordinary-user",
            CanViewIncidents = true, CanUpdateIncidents = true, CanManageApiKeys = true, CanManageApplication = true
        });
        await _db.SaveChangesAsync();

        var manager = Controller("ordinary-user", administrator: false);
        Assert.IsInstanceOfType<ForbidResult>(await manager.Delivery(delivery.Id, CancellationToken.None));
        Assert.IsInstanceOfType<ForbidResult>(await manager.Retry(delivery.Id));
        Assert.IsInstanceOfType<ForbidResult>(await manager.SaveBindings(otherApplication.Id,
            new ApplicationPushBindingsRequest { Sev1ChannelIds = [channel.Id] }));
        Assert.AreEqual(PushDelivery.Failed, (await _db.PushDeliveries.SingleAsync()).Status);

        _db.PushChannelBindingUsers.Add(new PushChannelBindingUser { ChannelId = channel.Id, UserId = "ordinary-user" });
        await _db.SaveChangesAsync();
        Assert.IsTrue(await _channelAccess.CanBindAsync(manager.User, channel.Id));
        Assert.IsInstanceOfType<ForbidResult>(await manager.Retry(delivery.Id));
        ((ClaimsIdentity)manager.User.Identity!).AddClaim(new Claim(AppPermissions.Type, AppPermissionNames.CanManagePush));
        Assert.IsInstanceOfType<RedirectToActionResult>(await manager.Retry(delivery.Id));
        _db.ChangeTracker.Clear();
        Assert.AreEqual(PushDelivery.Pending, (await _db.PushDeliveries.SingleAsync()).Status);
    }

    [TestMethod]
    public async Task ChannelCrudAndBindingsPreserveUnselectedChannels()
    {
        await AddAdministrators();
        var application = await Application();
        await Controller().Save(new PushChannelEditViewModel { Name = "通知", Type = "inapp", Endpoint = "ignored", RecipientUserIds = ["ordinary-user"] });
        var channel = await _db.PushChannels.SingleAsync();
        Assert.IsNull(channel.Endpoint);
        var other = await Channel();
        await BindAllLevels(application.Id, channel.Id, other.Id, other.Id);
        Assert.AreEqual(2, await _db.ApplicationPushChannels.CountAsync());
        Assert.IsInstanceOfType<BadRequestObjectResult>(await BindAllLevels(application.Id, Guid.NewGuid()));
        Assert.AreEqual(2, await _db.ApplicationPushChannels.CountAsync());
        await BindAllLevels(application.Id, channel.Id);
        Assert.AreEqual(channel.Id, (await _db.ApplicationPushChannels.SingleAsync()).ChannelId);
        await Controller().Save(new PushChannelEditViewModel { Id = channel.Id, Name = "Renamed", Type = "inapp", IsEnabled = false, RecipientUserIds = ["ordinary-user"] });
        Assert.AreEqual("Renamed", channel.Name);
        Assert.IsFalse(channel.IsEnabled);
        await BindAllLevels(application.Id);
        Assert.AreEqual(0, await _db.ApplicationPushChannels.CountAsync());
        Assert.AreEqual(2, await _db.PushChannels.CountAsync());
    }

    [TestMethod]
    public async Task DeletingChannelRemovesItsHistoryButPreservesAlert()
    {
        await AddAdministrators();
        var application = await Application();
        var channel = await Channel("inapp");
        var other = await Channel();
        await BindAllLevels(application.Id, channel.Id, other.Id);
        await Report(application, Guid.NewGuid());
        var delivery = await _db.PushDeliveries.SingleAsync(item => item.ChannelId == channel.Id);
        await _processor.ProcessAsync(delivery.Id, CancellationToken.None);
        await Controller().Delete(channel.Id);
        Assert.AreEqual(0, await _db.InAppNotifications.CountAsync());
        Assert.AreEqual(1, await _db.Incidents.CountAsync());
        Assert.AreEqual(other.Id, (await _db.PushDeliveries.SingleAsync()).ChannelId);
        Assert.AreEqual(other.Id, (await _db.ApplicationPushChannels.SingleAsync()).ChannelId);
    }

    [TestMethod]
    public async Task DeletingApplicationCleansItsPushDataButPreservesChannels()
    {
        await AddAdministrators();
        var application = await Application();
        var channel = await Channel("inapp");
        await BindAllLevels(application.Id, channel.Id);
        await Report(application, Guid.NewGuid());
        var id = await _db.PushDeliveries.Select(delivery => delivery.Id).SingleAsync();
        await _processor.ProcessAsync(id, CancellationToken.None);
        await new ApplicationsController(_db, new ApplicationAccessService(_db), localizer: TestLocalization.For<ApplicationsController>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "admin-one"), new Claim(ClaimTypes.Role, "Administrators"), .. AppPermissions.GetAllPermissions().Select(permission => new Claim(AppPermissions.Type, permission.Key))], "test"))
                }
            }
        }.DeleteApplication(application.Id, application.Name, CancellationToken.None);
        Assert.AreEqual(0, await _db.PushDeliveries.CountAsync());
        Assert.AreEqual(0, await _db.InAppNotifications.CountAsync());
        Assert.AreEqual(0, await _db.ApplicationPushChannels.CountAsync());
        Assert.AreEqual(1, await _db.PushChannels.CountAsync());
    }

    [TestMethod]
    public async Task QueueFailureRollsBackAlertInsert()
    {
        var application = await Application();
        var channel = await Channel();
        await BindAllLevels(application.Id, channel.Id);
        await _db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER RejectPushInsert BEFORE INSERT ON PushDeliveries
            BEGIN SELECT RAISE(ABORT, 'Queue insert rejected'); END;
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => Report(application, Guid.NewGuid()));
        Assert.AreEqual(0, await _db.Incidents.CountAsync());
        Assert.AreEqual(0, await _db.PushDeliveries.CountAsync());
    }

    [TestMethod]
    [DataRow("https://example.com/webhook", true)]
    [DataRow("http://localhost:9000/webhook", true)]
    [DataRow("file:///etc/passwd", false)]
    [DataRow("ftp://example.com", false)]
    [DataRow("https://user:password@example.com", false)]
    [DataRow("https://example.com/#fragment", false)]
    [DataRow("relative/path", false)]
    public void ValidateWebhookAddresses(string value, bool expected) =>
        Assert.AreEqual(expected, WebhookChannelAdapter.IsValidEndpoint(value));

    [TestMethod]
    public async Task WebhookNetworkFailuresAreRecordedWithoutLeakingEndpoint()
    {
        var channel = await Channel();
        _http.ThrowNetworkError = true;
        await Controller().Test(channel.Id, CancellationToken.None);
        var delivery = await _db.PushDeliveries.SingleAsync();
        Assert.AreEqual(PushDelivery.Failed, delivery.Status);
        Assert.DoesNotContain(channel.Endpoint!, delivery.Error!);
    }

    [TestMethod]
    public async Task CustomWebhookHeadersAndPayloadAreValidatedProtectedAndDelivered()
    {
        var headers = "{\"Authorization\":\"Bearer token\",\"X-Source\":\"alert-center\"}";
        var template = "{\"event\":\"alert\",\"subject\":\"{{title}}\",\"data\":\"{{context}}\",\"testIncidentId\":\"{{incidentId}}\"}";
        var model = new PushChannelEditViewModel
        {
            Name = "Custom webhook", Type = "webhook", Endpoint = "https://receiver.example/hook",
            WebhookHeadersJson = headers, WebhookPayloadTemplate = template
        };
        Assert.IsInstanceOfType<RedirectToActionResult>(await Controller().Save(model));
        var channel = await _db.PushChannels.SingleAsync();
        Assert.IsNotNull(channel.WebhookHeadersProtected);
        Assert.DoesNotContain("Bearer token", channel.WebhookHeadersProtected!);
        Assert.AreEqual(template, channel.WebhookPayloadTemplate);
        Assert.IsTrue(_webhookConfigurationProtector.TryUnprotect(channel.WebhookHeadersProtected, out var savedHeaders));
        Assert.AreEqual(headers, savedHeaders);
        await Controller().Test(channel.Id, CancellationToken.None);
        var delivery = await _db.PushDeliveries.SingleAsync();
        Assert.AreEqual(PushDelivery.Succeeded, delivery.Status, delivery.Error);
        var request = _http.Requests.Single();
        using var payload = JsonDocument.Parse(request.Json);
        Assert.AreEqual("alert", payload.RootElement.GetProperty("event").GetString());
        Assert.AreEqual("Channel test notification", payload.RootElement.GetProperty("subject").GetString());
        Assert.AreEqual(JsonValueKind.Null, payload.RootElement.GetProperty("testIncidentId").ValueKind);
        Assert.AreEqual("Bearer token", request.Headers["Authorization"]);
        Assert.AreEqual("alert-center", request.Headers["X-Source"]);

        Assert.IsFalse(WebhookChannelAdapter.TryValidateCustomHeaders("{\"Idempotency-Key\":\"override\"}", out _));
        Assert.IsFalse(WebhookChannelAdapter.TryValidatePayloadTemplate("{\"title\":\"prefix {{title}}\"}", out _));
        Assert.IsFalse(WebhookChannelAdapter.TryValidatePayloadTemplate("[\"{{title}}\"]", out _));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmailContextContainsOnlyTopLevelScalarFields(bool reopened)
    {
        var incident = new Incident
        {
            Title = "Context summary", Level = "sev4",
            ContextJson = """
                {"service":"payment-api","request":{"traceId":"abc123","durationMs":3200},
                 "affectedNodes":["global-01","global-02"],"checks":[{"name":"database","healthy":false}],
                 "count":12,"healthy":true,"optional":null,"empty":"","<label>":"<script>unsafe</script>"}
                """
        };
        var result = await _adapters.Single(adapter => adapter.Definition.Key == "email").DeliverAsync(new PushDelivery
        {
            Channel = new PushChannel { EmailRecipient = "anduin@example.com" },
            Incident = reopened ? null : incident,
            IncidentEvent = reopened ? new IncidentTimelineEntry { Incident = incident, IncidentId = incident.Id } : null
        });
        Assert.IsTrue(result.Succeeded, result.ErrorMessage);
        var mail = _email.Messages.Single();
        Assert.IsNotNull(mail.HtmlBody);
        Assert.Contains("<th scope=\"row\"", mail.HtmlBody);
        Assert.Contains("payment-api", mail.HtmlBody);
        Assert.Contains("service: payment-api", mail.Body);
        Assert.Contains("count: 12", mail.Body);
        Assert.Contains("healthy: true", mail.Body);
        Assert.Contains("optional: null", mail.Body);
        Assert.Contains("&lt;label&gt;", mail.HtmlBody);
        Assert.Contains("&lt;script&gt;unsafe&lt;/script&gt;", mail.HtmlBody);
        foreach (var nested in new[] { "traceId", "abc123", "affectedNodes", "global-01", "checks", "database" })
        {
            Assert.DoesNotContain(nested, mail.HtmlBody);
            Assert.DoesNotContain(nested, mail.Body);
        }
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("{}")]
    [DataRow("{\"request\":{\"traceId\":\"abc123\"},\"nodes\":[1,2]}")]
    [DataRow("malformed stored JSON")]
    public async Task EmailOmitsContextTableWhenNoScalarFieldsExist(string? context)
    {
        var result = await _adapters.Single(adapter => adapter.Definition.Key == "email").DeliverAsync(new PushDelivery
        {
            Channel = new PushChannel { EmailRecipient = "anduin@example.com" },
            Incident = new Incident { Title = "No summary", ContextJson = context }
        });
        Assert.IsTrue(result.Succeeded, result.ErrorMessage);
        var mail = _email.Messages.Single();
        Assert.IsNotNull(mail.HtmlBody);
        Assert.DoesNotContain("<th scope=\"row\"", mail.HtmlBody);
        Assert.DoesNotContain("Context:", mail.Body);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IncidentEmailsLinkToDetailsAndEncodeUntrustedContent(bool reopened)
    {
        var incident = new Incident
        {
            Title = "Database <img src=x onerror=alert(1)> & outage",
            Content = "First line\n<script>alert(1)</script>",
            ContextJson = "{\"value\":\"<iframe>\"}", Level = "sev1"
        };
        var delivery = new PushDelivery
        {
            Channel = new PushChannel { EmailRecipient = "anduin@example.com" },
            Incident = reopened ? null : incident,
            IncidentEvent = reopened ? new IncidentTimelineEntry
            {
                Incident = incident, IncidentId = incident.Id, NewLevel = "sev1", Note = "<b>Reopened</b>"
            } : null
        };
        var adapter = new EmailChannelAdapter(_email, TestLocalization.For<EmailChannelAdapter>(),
            Options.Create(new EmailSettings { PublicBaseUrl = "https://alerts.example.com/portal/" }));
        var result = await adapter.DeliverAsync(delivery);
        Assert.IsTrue(result.Succeeded, result.ErrorMessage);
        var mail = _email.Messages.Single();
        var url = $"https://alerts.example.com/portal/Incidents/Details/{incident.Id}";
        Assert.Contains(url, mail.Body);
        Assert.IsNotNull(mail.HtmlBody);
        Assert.Contains($"href=\"{url}\"", mail.HtmlBody);
        Assert.Contains("&lt;img", mail.HtmlBody);
        Assert.Contains("&lt;script&gt;", mail.HtmlBody);
        Assert.Contains("&lt;iframe&gt;", mail.HtmlBody);
        Assert.DoesNotContain("<script>", mail.HtmlBody);
        Assert.DoesNotContain("<img", mail.HtmlBody);
        if (reopened) Assert.Contains("&lt;b&gt;Reopened&lt;/b&gt;", mail.HtmlBody);
    }

    [TestMethod]
    [DataRow("javascript:alert(1)")]
    [DataRow("https://user:password@example.com")]
    [DataRow("https://example.com?redirect=other")]
    public async Task EmailRejectsInvalidPublicSiteAddress(string url)
    {
        var adapter = new EmailChannelAdapter(_email, TestLocalization.For<EmailChannelAdapter>(),
            Options.Create(new EmailSettings { PublicBaseUrl = url }));
        var result = await adapter.DeliverAsync(new PushDelivery
        {
            Channel = new PushChannel { EmailRecipient = "anduin@example.com" }
        });
        Assert.IsFalse(result.Succeeded);
        Assert.IsEmpty(_email.Messages);
    }

    [TestMethod]
    public async Task EmailRecipientAndCcRecipientsAreValidatedPersistedAndDelivered()
    {
        var model = new PushChannelEditViewModel
        {
            Name = "On-call email", Type = "email", EmailRecipient = "oncall@example.com",
            EmailCcRecipients = "manager@example.com; TEAM@example.com\nmanager@example.com"
        };
        Assert.IsInstanceOfType<RedirectToActionResult>(await Controller().Save(model));
        var channel = await _db.PushChannels.SingleAsync();
        Assert.AreEqual("oncall@example.com", channel.EmailRecipient);
        Assert.AreEqual("manager@example.com\nTEAM@example.com", channel.EmailCcRecipients);

        const string content = "请检查邮件测试内容。";
        await Controller().Test(channel.Id, CancellationToken.None, content);
        var delivery = await _db.PushDeliveries.SingleAsync();
        Assert.AreEqual(content, delivery.TestContent);
        Assert.AreEqual(PushDelivery.Succeeded, delivery.Status, delivery.Error);
        var sent = _email.Messages.Single();
        CollectionAssert.AreEqual(new[] { "oncall@example.com" }, sent.Recipients.ToArray());
        CollectionAssert.AreEquivalent(new[] { "manager@example.com", "TEAM@example.com" }, sent.CcRecipients.ToArray());
        StringAssert.StartsWith(sent.Subject, "[AlertCenter][sev3]");
        StringAssert.Contains(sent.Body, content);
        StringAssert.Contains(sent.Body, delivery.Id.ToString());
        Assert.Contains("https://alertcenter.aiursoft.com/", sent.Body);
        Assert.IsNotNull(sent.HtmlBody);
        Assert.Contains("href=\"https://alertcenter.aiursoft.com/\"", sent.HtmlBody);

        Assert.IsFalse(EmailChannelAdapter.TryParseRecipient("", out _, out _));
        Assert.IsFalse(EmailChannelAdapter.TryParseRecipient("not-an-email", out _, out _));
        Assert.IsFalse(EmailChannelAdapter.TryParseCcRecipients("not-an-email", out _, out _));

        var duplicateChannel = new PushChannel
        {
            Name = "Duplicate email", Type = "email", EmailRecipient = "oncall@example.com",
            EmailCcRecipients = "ONCALL@example.com"
        };
        _db.PushChannels.Add(duplicateChannel);
        await _db.SaveChangesAsync();
        await Controller().Test(duplicateChannel.Id, CancellationToken.None);
        Assert.AreEqual(PushDelivery.Failed, (await _db.PushDeliveries.SingleAsync(item => item.ChannelId == duplicateChannel.Id)).Status);
        Assert.IsTrue(EmailChannelAdapter.TryParseCcRecipients("", out var emptyCcRecipients, out _));
        Assert.AreEqual(0, emptyCcRecipients.Count);
    }

    [TestMethod]
    public void EmailSettingsRequireCompleteSmtpConfiguration()
    {
        Assert.IsFalse(new EmailSettings { Host = "smtp.example.com", Port = 587, From = "sender@example.com", UserName = "user" }
            .TryValidate(out _));
        Assert.IsFalse(new EmailSettings { Host = "smtp.example.com", Port = 0, From = "sender@example.com" }.TryValidate(out _));
        Assert.IsTrue(new EmailSettings { Host = "smtp.example.com", Port = 587, From = "sender@example.com" }.TryValidate(out _));
    }

    [TestMethod]
    public async Task RecipientsCombineUsersAndGroupsAndFollowMembershipChanges()
    {
        await AddAdministrators();
        var role = await _db.Roles.SingleAsync();
        var otherRole = new IdentityRole("Support");
        _db.Roles.Add(otherRole);
        _db.UserRoles.Add(new IdentityUserRole<string> { UserId = "admin-one", RoleId = otherRole.Id });
        await _db.SaveChangesAsync();
        var model = new PushChannelEditViewModel
        {
            Name = "Targeted", Type = "inapp", RecipientUserIds = ["ordinary-user", "admin-one", "admin-one"],
            RecipientRoleIds = [role.Id, otherRole.Id, role.Id]
        };
        Assert.IsInstanceOfType<RedirectToActionResult>(await Controller().Save(model));
        var channel = await _db.PushChannels.SingleAsync();
        Assert.AreEqual(2, await _db.PushChannelUsers.CountAsync());
        Assert.AreEqual(2, await _db.PushChannelRoles.CountAsync());
        await Controller().Test(channel.Id, CancellationToken.None);
        Assert.AreEqual(3, await _db.InAppNotifications.CountAsync());
        var delivery = await _db.PushDeliveries.SingleAsync();
        await new InAppChannelAdapter(_db, localizer: TestLocalization.For<InAppChannelAdapter>()).DeliverAsync(delivery);
        await _db.SaveChangesAsync();
        Assert.AreEqual(3, await _db.InAppNotifications.CountAsync());
        var own = await _db.InAppNotifications.SingleAsync(item => item.UserId == "ordinary-user");
        Assert.IsInstanceOfType<NotFoundResult>(await Controller().Notification(own.Id));

        model.Id = channel.Id;
        model.RecipientUserIds = [];
        model.RecipientRoleIds = [otherRole.Id];
        await Controller().Save(model);
        _db.UserRoles.Remove(await _db.UserRoles.SingleAsync(link => link.RoleId == otherRole.Id));
        _db.UserRoles.Add(new IdentityUserRole<string> { UserId = "ordinary-user", RoleId = otherRole.Id });
        await _db.SaveChangesAsync();
        await Controller().Test(channel.Id, CancellationToken.None);
        Assert.AreEqual(4, await _db.InAppNotifications.CountAsync());
        Assert.AreEqual("ordinary-user", (await _db.InAppNotifications.SingleAsync(item => item.DeliveryId != delivery.Id)).UserId);
        await Controller().Delete(channel.Id);
        Assert.AreEqual(0, await _db.PushChannelUsers.CountAsync());
        Assert.AreEqual(0, await _db.PushChannelRoles.CountAsync());
        Assert.AreEqual(0, await _db.PushChannelBindingUsers.CountAsync());
        Assert.AreEqual(0, await _db.PushChannelBindingRoles.CountAsync());
    }

    [TestMethod]
    public async Task EmptyGroupFailsWithoutFallingBackToAdministrators()
    {
        await AddAdministrators();
        var role = new IdentityRole("Empty");
        _db.Roles.Add(role);
        await _db.SaveChangesAsync();
        await Controller().Save(new PushChannelEditViewModel { Name = "Empty", Type = "inapp", RecipientRoleIds = [role.Id] });
        await Controller().Test((await _db.PushChannels.SingleAsync()).Id, CancellationToken.None);
        Assert.AreEqual(PushDelivery.Failed, (await _db.PushDeliveries.SingleAsync()).Status);
        Assert.AreEqual(0, await _db.InAppNotifications.CountAsync());
        _db.Roles.Remove(role);
        await _db.SaveChangesAsync();
        Assert.AreEqual(0, await _db.PushChannelRoles.CountAsync());
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public List<EmailMessage> Messages { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeWebhookHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public bool ThrowNetworkError { get; set; }
        public List<(string Json, string IdempotencyKey, Dictionary<string, string> Headers)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("application/json", request.Content!.Headers.ContentType!.MediaType);
            Requests.Add((await request.Content.ReadAsStringAsync(cancellationToken), request.Headers.GetValues("Idempotency-Key").Single(),
                request.Headers.ToDictionary(header => header.Key, header => header.Value.Single(), StringComparer.OrdinalIgnoreCase)));
            if (ThrowNetworkError) throw new HttpRequestException("Sensitive endpoint must not appear in the delivery error.");
            return new HttpResponseMessage(StatusCode);
        }
    }
}

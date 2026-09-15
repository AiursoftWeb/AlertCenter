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
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class PushCenterTests
{
    private SqliteContext _db = null!;
    private FakeWebhookHandler _http = null!;
    private FakeEmailSender _email = null!;
    private INotificationChannelAdapter[] _adapters = [];
    private PushDeliveryProcessor _processor = null!;
    private WebhookConfigurationProtector _webhookConfigurationProtector = null!;
    private const string Secret = "push-tests-secret";

    [TestInitialize]
    public async Task Initialize()
    {
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
        _adapters = [new WebhookChannelAdapter(new FakeClientFactory(_http), _webhookConfigurationProtector), new InAppChannelAdapter(_db), new EmailChannelAdapter(_email)];
        _processor = new PushDeliveryProcessor(_db, _adapters, NullLogger<PushDeliveryProcessor>.Instance);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _db.DisposeAsync();
        _http.Dispose();
    }

    private PushController Controller(string userId = "admin-one") => new(_db, _adapters, _processor, _webhookConfigurationProtector)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"))
            }
        }
    };

    private async Task<SourceApplication> Application()
    {
        var application = new SourceApplication { Name = "Test application" };
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
            EmailRecipients = type == "email" ? "oncall@example.com" : null, IsEnabled = enabled
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
            _db.Users.Add(new User { Id = id, DisplayName = id, UserName = id, NormalizedUserName = id.ToUpperInvariant() });
            if (id != "ordinary-user") _db.UserRoles.Add(new IdentityUserRole<string> { UserId = id, RoleId = role.Id });
        }
        await _db.SaveChangesAsync();
    }

    private Task<IActionResult> BindAllLevels(Guid applicationId, params Guid[] channelIds) =>
        Controller().SaveBindings(applicationId, new ApplicationPushBindingsRequest
        {
            Sev1ChannelIds = channelIds, Sev2ChannelIds = channelIds,
            Sev3ChannelIds = channelIds, Sev4ChannelIds = channelIds
        });

    private async Task<IActionResult> Report(SourceApplication application, Guid reportId, string level = "sev3")
    {
        _db.ChangeTracker.Clear();
        var controller = new MessageReportsController(_db, new QueuedAlertDispatcher(_db, new IncidentMatcher(_db)))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers.Authorization = $"Bearer {application.Id}.{Secret}";
        return await controller.Report(new ReportMessageRequest
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

        var first = (ObjectResult)await Report(application, reportId);
        Assert.AreEqual(201, first.StatusCode);
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync());
        Assert.AreEqual(0, _http.Requests.Count);
        Assert.IsInstanceOfType<OkObjectResult>(await Report(application, reportId));
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync());
        Assert.AreEqual(1, await _db.ReportedMessages.CountAsync());

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
            Sev1ChannelIds = level == "sev1" ? matching : [wrongLevel.Id],
            Sev2ChannelIds = level == "sev2" ? matching : [wrongLevel.Id],
            Sev3ChannelIds = level == "sev3" ? matching : [wrongLevel.Id],
            Sev4ChannelIds = level == "sev4" ? matching : [wrongLevel.Id]
        });
        await BindAllLevels(otherApplication.Id, otherApplicationChannel.Id);
        _db.ChangeTracker.Clear();
        var saved = await _db.ApplicationPushChannels.SingleAsync(link => link.ApplicationId == application.Id && link.ChannelId == first.Id);
        Assert.AreEqual(level == "sev1", saved.ReceiveSev1);
        Assert.AreEqual(level == "sev2", saved.ReceiveSev2);
        Assert.AreEqual(level == "sev3", saved.ReceiveSev3);
        Assert.AreEqual(level == "sev4", saved.ReceiveSev4);

        var reportId = Guid.NewGuid();
        Assert.AreEqual(201, ((ObjectResult)await Report(application, reportId, level)).StatusCode);
        CollectionAssert.AreEquivalent(new[] { first.Id, second.Id },
            await _db.PushDeliveries.Select(delivery => delivery.ChannelId).ToArrayAsync());
        Assert.AreEqual(level, (await _db.ReportedMessages.SingleAsync()).Level);
        Assert.IsInstanceOfType<OkObjectResult>(await Report(application, reportId, level));
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
        Assert.IsInstanceOfType<OkObjectResult>(await Report(application, reportId, "sev2"));
        await Report(application, Guid.NewGuid(), "sev1");
        Assert.AreEqual(2, await _db.ReportedMessages.CountAsync());
        Assert.AreEqual(pending.Id, (await _db.PushDeliveries.SingleAsync()).Id);
        Assert.AreEqual(PushDelivery.Pending, (await _db.PushDeliveries.SingleAsync()).Status);
        await Report(application, Guid.NewGuid(), "sev2");
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync());

        await Controller().SaveBindings(application.Id, new ApplicationPushBindingsRequest());
        await Report(application, Guid.NewGuid(), "sev2");
        Assert.AreEqual(4, await _db.ReportedMessages.CountAsync());
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync());
        var remaining = await _db.ApplicationPushChannels.SingleAsync();
        Assert.AreEqual(otherApplication.Id, remaining.ApplicationId);
        Assert.IsTrue(remaining.ReceiveSev1 && remaining.ReceiveSev2 && remaining.ReceiveSev3 && remaining.ReceiveSev4);
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
        Assert.IsFalse(saved.ReceiveSev1 || saved.ReceiveSev2 || saved.ReceiveSev3);
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
        Assert.AreEqual(201, ((ObjectResult)await Report(application, Guid.NewGuid())).StatusCode);
        var id = await _db.PushDeliveries.Select(delivery => delivery.Id).SingleAsync();
        await _processor.ProcessAsync(id, CancellationToken.None);
        Assert.AreEqual(1, await _db.ReportedMessages.CountAsync());
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
        Assert.Contains("接收", delivery.Error!);
        Assert.AreEqual(0, await _db.InAppNotifications.CountAsync());
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
        Assert.AreEqual(1, await _db.ReportedMessages.CountAsync());
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
        await new AlertsController(_db).DeleteApplication(application.Id, application.Name, CancellationToken.None);
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
        Assert.AreEqual(0, await _db.ReportedMessages.CountAsync());
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
        var template = "{\"event\":\"alert\",\"subject\":\"{{title}}\",\"data\":\"{{context}}\",\"testMessageId\":\"{{messageId}}\"}";
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
        Assert.AreEqual("渠道测试通知", payload.RootElement.GetProperty("subject").GetString());
        Assert.AreEqual(JsonValueKind.Null, payload.RootElement.GetProperty("testMessageId").ValueKind);
        Assert.AreEqual("Bearer token", request.Headers["Authorization"]);
        Assert.AreEqual("alert-center", request.Headers["X-Source"]);

        Assert.IsFalse(WebhookChannelAdapter.TryValidateCustomHeaders("{\"Idempotency-Key\":\"override\"}", out _));
        Assert.IsFalse(WebhookChannelAdapter.TryValidatePayloadTemplate("{\"title\":\"prefix {{title}}\"}", out _));
        Assert.IsFalse(WebhookChannelAdapter.TryValidatePayloadTemplate("[\"{{title}}\"]", out _));
    }

    [TestMethod]
    public async Task EmailRecipientsAreValidatedPersistedAndDelivered()
    {
        var model = new PushChannelEditViewModel
        {
            Name = "On-call email", Type = "email",
            EmailRecipients = "oncall@example.com; TEAM@example.com\noncall@example.com"
        };
        Assert.IsInstanceOfType<RedirectToActionResult>(await Controller().Save(model));
        var channel = await _db.PushChannels.SingleAsync();
        Assert.AreEqual(model.EmailRecipients, channel.EmailRecipients);

        const string content = "请检查邮件测试内容。";
        await Controller().Test(channel.Id, CancellationToken.None, content);
        var delivery = await _db.PushDeliveries.SingleAsync();
        Assert.AreEqual(content, delivery.TestContent);
        Assert.AreEqual(PushDelivery.Succeeded, delivery.Status, delivery.Error);
        var sent = _email.Messages.Single();
        CollectionAssert.AreEquivalent(new[] { "oncall@example.com", "TEAM@example.com" }, sent.Recipients.ToArray());
        StringAssert.StartsWith(sent.Subject, "[AlertCenter][sev3]");
        StringAssert.Contains(sent.Body, content);
        StringAssert.Contains(sent.Body, delivery.Id.ToString());

        Assert.IsFalse(EmailChannelAdapter.TryParseRecipients("", out _, out _));
        Assert.IsFalse(EmailChannelAdapter.TryParseRecipients("not-an-email", out _, out _));
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
        await new InAppChannelAdapter(_db).DeliverAsync(delivery);
        await _db.SaveChangesAsync();
        Assert.AreEqual(3, await _db.InAppNotifications.CountAsync());
        var own = await _db.InAppNotifications.SingleAsync(item => item.UserId == "ordinary-user");
        Assert.IsInstanceOfType<NotFoundResult>(await Controller("admin-one").Notification(own.Id));

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

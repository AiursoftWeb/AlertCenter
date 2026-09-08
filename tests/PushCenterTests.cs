using System.Net;
using System.Security.Claims;
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
public class PushCenterTests
{
    private SqliteContext _db = null!;
    private FakeWebhookHandler _http = null!;
    private INotificationChannelAdapter[] _adapters = [];
    private PushDeliveryProcessor _processor = null!;
    private const string Secret = "push-tests-secret";

    [TestInitialize]
    public async Task Initialize()
    {
        _db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.MigrateAsync();
        _http = new FakeWebhookHandler();
        _adapters = [new WebhookChannelAdapter(new FakeClientFactory(_http)), new InAppChannelAdapter(_db)];
        _processor = new PushDeliveryProcessor(_db, _adapters, NullLogger<PushDeliveryProcessor>.Instance);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _db.DisposeAsync();
        _http.Dispose();
    }

    private PushController Controller(string userId = "admin-one") => new(_db, _adapters, _processor)
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
            IsEnabled = enabled
        };
        _db.PushChannels.Add(channel);
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

    private async Task<IActionResult> Report(SourceApplication application, Guid reportId)
    {
        _db.ChangeTracker.Clear();
        var controller = new MessageReportsController(_db, new QueuedAlertDispatcher(_db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers.Authorization = $"Bearer {application.Id}.{Secret}";
        return await controller.Report(new ReportMessageRequest
        {
            ReportId = reportId, Title = "<script>unsafe</script>", Content = null,
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
        await Controller().SaveBindings(application.Id, [webhook.Id, inbox.Id, disabled.Id]);
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
    public async Task FailedDeliveryDoesNotLoseAlertAndRetriesOnlyOnRequest()
    {
        var application = await Application();
        var channel = await Channel();
        await Controller().SaveBindings(application.Id, [channel.Id]);
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
        Assert.IsInstanceOfType<NotFoundResult>(await Controller().MarkRead(second.Id));
        await Controller().MarkRead(first.Id);
        var readAt = first.ReadAtUtc;
        Assert.IsNotNull(readAt);
        await Controller().MarkRead(first.Id);
        Assert.AreEqual(readAt, first.ReadAtUtc);
        Assert.IsNull(second.ReadAtUtc);
        await Controller("admin-two").MarkAllRead();
        Assert.AreEqual(0, await _db.InAppNotifications.CountAsync(notification => notification.ReadAtUtc == null));
    }

    [TestMethod]
    public async Task InAppDeliveryWithoutAdministratorsFailsClearly()
    {
        var channel = await Channel("inapp");
        await Controller().Test(channel.Id, CancellationToken.None);
        var delivery = await _db.PushDeliveries.SingleAsync();
        Assert.AreEqual(PushDelivery.Failed, delivery.Status);
        Assert.Contains("管理员", delivery.Error!);
        Assert.AreEqual(0, await _db.InAppNotifications.CountAsync());
    }

    [TestMethod]
    public async Task DisabledChannelDoesNotSendAndCanBeRetriedAfterEnabling()
    {
        var application = await Application();
        var channel = await Channel();
        await Controller().SaveBindings(application.Id, [channel.Id]);
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
        var application = await Application();
        await Controller().Save(new PushChannelEditViewModel { Name = "通知", Type = "inapp", Endpoint = "ignored" });
        var channel = await _db.PushChannels.SingleAsync();
        Assert.IsNull(channel.Endpoint);
        var other = await Channel();
        await Controller().SaveBindings(application.Id, [channel.Id, other.Id, other.Id]);
        Assert.AreEqual(2, await _db.ApplicationPushChannels.CountAsync());
        Assert.IsInstanceOfType<BadRequestObjectResult>(await Controller().SaveBindings(application.Id, [Guid.NewGuid()]));
        Assert.AreEqual(2, await _db.ApplicationPushChannels.CountAsync());
        await Controller().SaveBindings(application.Id, [channel.Id]);
        Assert.AreEqual(channel.Id, (await _db.ApplicationPushChannels.SingleAsync()).ChannelId);
        await Controller().Save(new PushChannelEditViewModel { Id = channel.Id, Name = "Renamed", Type = "inapp", IsEnabled = false });
        Assert.AreEqual("Renamed", channel.Name);
        Assert.IsFalse(channel.IsEnabled);
        await Controller().SaveBindings(application.Id, []);
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
        await Controller().SaveBindings(application.Id, [channel.Id, other.Id]);
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
        await Controller().SaveBindings(application.Id, [channel.Id]);
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
        await Controller().SaveBindings(application.Id, [channel.Id]);
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

    private sealed class FakeClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeWebhookHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public bool ThrowNetworkError { get; set; }
        public List<(string Json, string IdempotencyKey)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("application/json", request.Content!.Headers.ContentType!.MediaType);
            Requests.Add((await request.Content.ReadAsStringAsync(cancellationToken), request.Headers.GetValues("Idempotency-Key").Single()));
            if (ThrowNetworkError) throw new HttpRequestException("Sensitive endpoint must not appear in the delivery error.");
            return new HttpResponseMessage(StatusCode);
        }
    }
}

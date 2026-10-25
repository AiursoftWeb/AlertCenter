using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Sqlite;
using Aiursoft.DbTools;
using Aiursoft.WebTools.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using static Aiursoft.WebTools.Extends;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public class AlertDemoStartup : IWebStartup
{
    public void ConfigureServices(IConfiguration configuration, IWebHostEnvironment environment, IServiceCollection services)
    {
        new Startup().ConfigureServices(configuration, environment, services);
        var databasePath = Path.Combine(Path.GetTempPath(), $"alert-demo-tests-{Guid.NewGuid()}.db");
        services.AddDbContext<SqliteContext>(options => options.UseSqlite($"DataSource={databasePath};Pooling=False"));
        services.AddScoped<TemplateDbContext>(provider => provider.GetRequiredService<SqliteContext>());
    }

    public void Configure(WebApplication app) => new Startup().Configure(app);
}

[TestClass]
public partial class IncidentPagesTests : TestBase
{
    private string? _databasePath;

    [TestInitialize]
    public override async Task CreateServer()
    {
        var webRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../src/Aiursoft.AlertCenter/wwwroot"));
        using var reservation = new TestPortReservation();
        Http.BaseAddress = new Uri($"http://localhost:{reservation.Port}");
        Server = await AppAsync<AlertDemoStartup>(["--webroot", webRoot], port: reservation.Port);
        try
        {
            reservation.ConfigureServer(Server);
            using (var scope = Server.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
                _databasePath = db.Database.GetDbConnection().DataSource;
            }
            await Server.UpdateDbAsync<TemplateDbContext>();
            await Server.SeedAsync();
            await Server.StartAsync();
        }
        catch
        {
            Server.Dispose();
            Server = null;
            throw;
        }
    }

    [TestCleanup]
    public override async Task CleanServer()
    {
        await base.CleanServer();
        if (_databasePath != null) File.Delete(_databasePath);
    }

    [TestMethod]
    public async Task IncidentDetailsShowConversationReferenceAndRetainSystemGuid()
    {
        await LoginAsAdmin();
        var incident = new Incident
        {
            Id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            Application = new SourceApplication { Name = "Conversation reference" },
            Title = "Service unavailable", OccurredAtUtc = DateTime.UtcNow
        };
        await CreateInboxNotification(incident);
        var html = await Http.GetStringAsync($"/Incidents/Details/{incident.Id}");
        Assert.Contains($"data-incident-short-id=\"{incident.ShortId}\"", html);
        Assert.Contains($"#{incident.ShortId}", html);
        Assert.Contains(incident.Id.ToString(), html);
    }

    [TestMethod]
    public async Task EmailAcknowledgementUsesProtectedPostWithoutMutatingOnGet()
    {
        var key = await CreateApplication();
        using var report = await Report(key, Guid.NewGuid());
        Guid id, version;
        using (var scope = Server!.Services.CreateScope())
        {
            var incident = await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().Incidents.SingleAsync();
            id = incident.Id;
            version = incident.Version;
        }
        var url = $"/Incidents/Details/{id}";
        var html = await Http.GetStringAsync(url + "?ack=true");
        Assert.Contains("id=\"acknowledge-incident\"", html);
        Assert.Contains("/scripts/incident-ack.js", html);
        using (var scope = Server!.Services.CreateScope())
        {
            var incident = await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().Incidents.SingleAsync();
            Assert.IsFalse(incident.IsAcknowledged);
            Assert.AreEqual(version, incident.Version);
        }
        var data = new Dictionary<string, string> { ["IncidentId"] = id.ToString(), ["Version"] = version.ToString() };
        using var rejected = await PostForm("/Incidents/Acknowledge", data, includeToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var acknowledged = await PostForm("/Incidents/Acknowledge", data, url);
        AssertRedirect(acknowledged, url);
        using var finalScope = Server!.Services.CreateScope();
        var db = finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.IsTrue((await db.Incidents.SingleAsync()).IsAcknowledged);
        Assert.AreEqual(1, await db.IncidentTimelineEntries.CountAsync(entry => entry.Type == IncidentTimelineEntry.Acked));
    }

    [TestMethod]
    public async Task IncidentDetailsShowCompleteIndentedAndEscapedContext()
    {
        await LoginAsAdmin();
        var incident = new Incident
        {
            Application = new SourceApplication { Name = "Context presentation test" },
            ReportId = Guid.NewGuid(), Title = "Nested context", OccurredAtUtc = DateTime.UtcNow,
            ContextJson = """
                {"service":"payment-api","request":{"traceId":"abc123","durationMs":3200},"affectedNodes":["global-01","global-02"],"checks":[{"name":"database","healthy":false}],"unsafe":"<script>alert(1)</script>","large":9007199254740993}
                """
        };
        await CreateInboxNotification(incident);
        var html = await Http.GetStringAsync($"/Incidents/Details/{incident.Id}");
        var match = Regex.Match(html, "<pre id=\"incident-context\"[^>]*><code>(.*?)</code></pre>", RegexOptions.Singleline);
        Assert.IsTrue(match.Success);
        var formatted = WebUtility.HtmlDecode(match.Groups[1].Value);
        Assert.Contains("\n  \"service\"", formatted);
        var context = JObject.Parse(formatted);
        Assert.AreEqual("abc123", context["request"]!["traceId"]!.Value<string>());
        Assert.AreEqual(2, ((JArray)context["affectedNodes"]!).Count);
        Assert.IsFalse(context["checks"]![0]!["healthy"]!.Value<bool>());
        Assert.AreEqual(9007199254740993L, context["large"]!.Value<long>());
        Assert.AreEqual("<script>alert(1)</script>", context["unsafe"]!.Value<string>());
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("data-copy-target=\"incident-context\"", html);
    }

    [TestMethod]
    public async Task FreshDatabaseHasNoApplicationsOrMessages()
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(0, await db.SourceApplications.CountAsync());
        Assert.AreEqual(0, await db.Incidents.CountAsync());
    }

    private async Task<string> CreateApplication()
    {
        await LoginAsAdmin();
        using var created = await PostForm("/Applications/CreateApplication", new() { ["Name"] = "Demo Orders" }, "/Applications/Applications");
        Assert.AreEqual(HttpStatusCode.Redirect, created.StatusCode);
        var parameters = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(Http.BaseAddress!, created.Headers.Location!).Query);
        var applicationId = Guid.Parse(parameters["applicationId"].ToString());
        return await CreateApplicationKey(applicationId);
    }

    private Task<HttpResponseMessage> Report(string? key, Guid reportId, string title = "Order timeout", string level = "sev1")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/incidents")
        {
            Content = JsonContent.Create(new
            {
                reportId, title, level, occurredAtUtc = DateTimeOffset.UtcNow,
                content = "<script>alert('unsafe')</script>", context = new { orderId = "ORDER-1001" }
            })
        };
        if (key != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return Http.SendAsync(request);
    }

    [TestMethod]
    public async Task ApplicationSearchAndGettingStartedGuideAreAvailable()
    {
        await LoginAsAdmin();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.SourceApplications.AddRange(
                new SourceApplication { Name = "Orders service", OwnerId = db.Users.Single(user => user.Email == "admin@default.com").Id },
                new SourceApplication { Name = "Inventory service", OwnerId = db.Users.Single(user => user.Email == "admin@default.com").Id });
            await db.SaveChangesAsync();
        }

        using var applications = await Http.GetAsync("/Applications/Applications?keyword=%20Orders%20");
        Assert.AreEqual(HttpStatusCode.OK, applications.StatusCode);
        var applicationsHtml = await applications.Content.ReadAsStringAsync();
        Assert.Contains("id=\"application-keyword\"", applicationsHtml);
        Assert.Contains("value=\"Orders\"", applicationsHtml);
        Assert.Contains("Orders service", applicationsHtml);
        Assert.DoesNotContain("Inventory service", applicationsHtml);
        Assert.Contains("/Applications/GettingStarted", applicationsHtml);
        Assert.DoesNotContain("Authorization: Bearer YOUR_API_KEY", applicationsHtml);

        using var guide = await Http.GetAsync("/Applications/GettingStarted");
        Assert.AreEqual(HttpStatusCode.OK, guide.StatusCode);
        Assert.IsTrue(guide.Headers.CacheControl?.NoStore);
        var guideHtml = await guide.Content.ReadAsStringAsync();
        Assert.Contains("Authorization: Bearer YOUR_API_KEY", guideHtml);
        Assert.Contains("/api/v1/incidents", guideHtml);
        Assert.Contains("reportId", guideHtml);
    }

    [TestMethod]
    public async Task RefreshAfterCreationDoesNotCreateAnotherApplicationOrRevealKeyAgain()
    {
        var key = await CreateApplication();
        var applicationId = Guid.Parse(key.Split('.')[0]);
        for (var refresh = 0; refresh < 3; refresh++)
        {
            using var response = await Http.GetAsync("/Applications/Applications");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.CacheControl?.NoStore);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("Demo Orders", html);
            Assert.DoesNotContain(key, html);
            Assert.DoesNotContain("id=\"api-key\"", html);
        }

        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(1, await db.SourceApplications.CountAsync());
        Assert.AreEqual(applicationId, (await db.SourceApplications.SingleAsync()).Id);
        using var report = await Report(key, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Created, report.StatusCode);
    }

    [TestMethod]
    public async Task InvalidApplicationCreationStillShowsValidationWithoutCreatingData()
    {
        await LoginAsAdmin();
        using var response = await PostForm("/Applications/CreateApplication", new() { ["Name"] = " " }, "/Applications/Applications");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("validation-summary-errors", html);
        Assert.DoesNotContain("id=\"api-key\"", html);
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(0, await db.SourceApplications.CountAsync());
    }

    [TestMethod]
    public async Task CreateReportViewAndDisable()
    {
        var key = await CreateApplication();
        var reportId = Guid.NewGuid();
        var first = await Report(key, reportId);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        var receipt = JObject.Parse(await first.Content.ReadAsStringAsync());
        Assert.IsFalse(receipt.Value<bool>("Duplicate"));
        var duplicate = await Report(key, reportId, "Must not replace original");
        Assert.AreEqual(HttpStatusCode.Accepted, duplicate.StatusCode);
        var second = JObject.Parse(await duplicate.Content.ReadAsStringAsync());
        Assert.IsTrue(second.Value<bool>("Duplicate"));
        Assert.AreEqual(receipt.Value<string>("IncidentId"), second.Value<string>("IncidentId"));
        var list = await Http.GetStringAsync("/Incidents");
        Assert.Contains("Order timeout", list);
        Assert.Contains("Demo Orders", list);
        var details = await Http.GetStringAsync($"/Incidents/Details/{receipt.Value<string>("IncidentId")}");
        Assert.Contains("ORDER-1001", details);
        Assert.Contains("&lt;script&gt;", details);
        Assert.DoesNotContain("<script>alert", details);
        Assert.DoesNotContain("Must not replace original", details);
        var disabled = await PostForm("/Applications/SetEnabled", new()
        {
            ["id"] = key.Split('.')[0], ["enabled"] = "false"
        }, "/Applications/Applications");
        Assert.AreEqual(HttpStatusCode.Found, disabled.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Report(key, Guid.NewGuid())).StatusCode);
    }

    [TestMethod]
    public async Task RejectInvalidReportsAndUnauthorizedManagement()
    {
        Assert.AreEqual(HttpStatusCode.Found, (await Http.GetAsync("/Applications/Applications")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Report(null, Guid.NewGuid())).StatusCode);
        var key = await CreateApplication();
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Report(key + "bad", Guid.NewGuid())).StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, (await Report(key, Guid.NewGuid(), level: "sev0")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Report(key, Guid.NewGuid(), level: "sev5")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Report(key, Guid.Empty)).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Report(key, Guid.NewGuid(), title: " ")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Report(key, Guid.NewGuid(), title: new string('a', 201))).StatusCode);
        var oversized = new HttpRequestMessage(HttpMethod.Post, "/api/v1/incidents")
        {
            Content = new StringContent("{\"title\":\"" + new string('a', 66000) + "\"}", Encoding.UTF8, "application/json")
        };
        oversized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, (await Http.SendAsync(oversized)).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PostForm("/Applications/SetEnabled", new()
        {
            ["id"] = key.Split('.')[0], ["enabled"] = "false"
        }, includeToken: false)).StatusCode);
    }

    [TestMethod]
    public async Task DeleteApplicationRequiresExactNameConfirmation()
    {
        var key = await CreateApplication();
        var applicationId = key.Split('.')[0];
        using var report = await Report(key, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Created, report.StatusCode);
        var html = await Http.GetStringAsync("/Applications/Applications");
        Assert.Contains("id=\"delete-application-form\"", html);
        Assert.Contains("name=\"confirmationName\"", html);
        Assert.Contains("id=\"confirm-delete-application\" disabled", html);
        Assert.Contains("user-select: text", html);

        foreach (var confirmationName in new[] { null, "", "demo orders", "Demo Orders " })
        {
            var data = new Dictionary<string, string> { ["id"] = applicationId };
            if (confirmationName != null) data["confirmationName"] = confirmationName;
            using var rejected = await PostForm("/Applications/DeleteApplication", data, "/Applications/Applications");
            Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
            using var scope = Server!.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual(1, await db.SourceApplications.CountAsync());
            Assert.AreEqual(1, await db.Incidents.CountAsync());
        }

        using var missingToken = await PostForm("/Applications/DeleteApplication", new()
        {
            ["id"] = applicationId, ["confirmationName"] = "Demo Orders"
        }, includeToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingToken.StatusCode);
        using var deleted = await PostForm("/Applications/DeleteApplication", new()
        {
            ["id"] = applicationId, ["confirmationName"] = "Demo Orders"
        }, "/Applications/Applications");
        AssertRedirect(deleted, "/Applications/Applications");
        using var finalScope = Server!.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(0, await finalDb.SourceApplications.CountAsync());
        Assert.AreEqual(0, await finalDb.Incidents.CountAsync());
        using var rejectedReport = await Report(key, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Unauthorized, rejectedReport.StatusCode);
    }

    [TestMethod]
    public async Task ConcurrentDuplicatesPersistOnlyOnce()
    {
        var key = await CreateApplication();
        var reportId = Guid.NewGuid();
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Report(key, reportId)));
        Assert.AreEqual(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.AreEqual(4, responses.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(1, await db.Incidents.CountAsync());
        var apiKey = await db.ApplicationApiKeys.SingleAsync();
        Assert.AreNotEqual(key.Split('.')[2], apiKey.SecretHash);
    }

    private async Task<InAppNotification> CreateInboxNotification(Incident? message = null)
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var user = await db.Users.SingleAsync(item => item.Email == "admin@default.com");
        if (message != null) message.Application.OwnerId = user.Id;
        var notification = new InAppNotification
        {
            UserId = user.Id,
            Delivery = new PushDelivery
            {
                Channel = new PushChannel { Name = new string('C', 100), Type = "inapp" },
                Incident = message,
                Status = PushDelivery.Succeeded
            }
        };
        db.InAppNotifications.Add(notification);
        await db.SaveChangesAsync();
        return notification;
    }

    [TestMethod]
    [DataRow("\n")]
    [DataRow("")]
    public async Task InboxLimitsLongNotificationsAndLinksToFullDetails(string separator)
    {
        await LoginAsAdmin();
        var content = "<script>alert('unsafe')</script>" +
            string.Join(separator, Enumerable.Repeat(new string('x', 40), 70)) + "Full content tail";
        var message = new Incident
        {
            Application = new SourceApplication { Name = "Inbox application" },
            ReportId = Guid.NewGuid(),
            Title = new string('T', 200),
            Content = content,
            ContextJson = "{\"detail\":\"Full context\"}",
            OccurredAtUtc = DateTime.UtcNow
        };
        var notification = await CreateInboxNotification(message);

        var inbox = await Http.GetStringAsync("/Push/Inbox");
        Assert.Contains("/styles/inbox.css?v=", inbox);
        Assert.Contains("inbox-notification-title", inbox);
        Assert.Contains("inbox-notification-preview", inbox);
        Assert.Contains("inbox-notification-channel", inbox);
        Assert.Contains($"href=\"/Push/Notification/{notification.Id}\"", inbox);
        Assert.Contains("&lt;script&gt;", inbox);
        Assert.DoesNotContain("<script>alert", inbox);
        Assert.DoesNotContain("Full context", inbox);

        var css = await Http.GetStringAsync("/styles/inbox.css");
        Assert.Contains("-webkit-line-clamp: 2;", css);
        Assert.Contains("-webkit-line-clamp: 3;", css);
        Assert.Contains("max-height: 2.8em;", css);
        Assert.Contains("max-height: 4.5em;", css);
        Assert.Contains("overflow-wrap: anywhere;", css);

        var details = await Http.GetStringAsync($"/Push/Notification/{notification.Id}");
        Assert.Contains(message.Title, details);
        Assert.Contains("Back to In-App Notifications", details);
        Assert.Contains("Mark as read", details);
        Assert.Contains(content, WebUtility.HtmlDecode(details));
        Assert.Contains("Full context", details);
        Assert.DoesNotContain("inbox-notification-preview", details);
        Assert.DoesNotContain("/styles/inbox.css", details);
        Assert.DoesNotContain("<script>alert", details);

        var markRead = await PostForm("/Push/MarkRead", new() { ["id"] = notification.Id.ToString() }, "/Push/Inbox");
        AssertRedirect(markRead, "/Push/Inbox");
        var unread = await Http.GetStringAsync("/Push/Inbox?unreadOnly=true");
        Assert.DoesNotContain($"/Push/Notification/{notification.Id}", unread);
        Assert.Contains($"/Push/Notification/{notification.Id}", await Http.GetStringAsync("/Push/Inbox?readStatus=all"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("Short notification")]
    public async Task InboxSupportsEmptyAndShortContent(string? content)
    {
        await LoginAsAdmin();
        var message = new Incident
        {
            Application = new SourceApplication { Name = "Inbox application" },
            ReportId = Guid.NewGuid(),
            Title = "Short alert",
            Content = content,
            OccurredAtUtc = DateTime.UtcNow
        };
        var notification = await CreateInboxNotification(message);

        var inbox = await Http.GetStringAsync("/Push/Inbox");
        Assert.Contains(message.Title, inbox);
        Assert.Contains($"href=\"/Push/Notification/{notification.Id}\"", inbox);
        if (!string.IsNullOrEmpty(content)) Assert.Contains(content, inbox);
    }

    [TestMethod]
    public async Task InboxTestNotificationLinksToStandaloneDetails()
    {
        await LoginAsAdmin();
        var notification = await CreateInboxNotification();

        var inbox = await Http.GetStringAsync("/Push/Inbox");
        Assert.Contains("inbox-notification-preview", inbox);
        Assert.Contains($"href=\"/Push/Notification/{notification.Id}\"", inbox);
        var details = WebUtility.HtmlDecode(await Http.GetStringAsync($"/Push/Notification/{notification.Id}"));
        Assert.Contains("Channel test notification", details);
        Assert.Contains("Back to In-App Notifications", details);
    }

    [TestMethod]
    public async Task SeverityBindingsRoundTripAndRejectInvalidRequests()
    {
        var key = await CreateApplication();
        var applicationId = Guid.Parse(key.Split('.')[0]);
        Guid firstId;
        Guid secondId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var first = new PushChannel { Name = "<script>alert('unsafe')</script>", Type = "inapp" };
            var second = new PushChannel { Name = "Disabled channel", Type = "inapp", IsEnabled = false };
            db.PushChannels.AddRange(first, second);
            await db.SaveChangesAsync();
            var adminUserId = await db.Users.Where(user => user.UserName == "admin").Select(user => user.Id).SingleAsync();
            db.PushChannelBindingUsers.AddRange(
                new PushChannelBindingUser { ChannelId = first.Id, UserId = adminUserId },
                new PushChannelBindingUser { ChannelId = second.Id, UserId = adminUserId });
            await db.SaveChangesAsync();
            firstId = first.Id;
            secondId = second.Id;
        }
        var url = $"/Push/Bindings/{applicationId}";
        var token = await GetAntiCsrfToken(url);
        using var saved = await Http.PostAsync("/Push/SaveBindings", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("id", applicationId.ToString()),
            new("__RequestVerificationToken", token),
            new("Sev0ChannelIds", firstId.ToString()),
            new("Sev0ChannelIds", secondId.ToString()),
            new("Sev1ChannelIds", firstId.ToString()),
            new("Sev1ChannelIds", secondId.ToString()),
            new("Sev1ChannelIds", firstId.ToString()),
            new("Sev2ChannelIds", secondId.ToString()),
            new("Sev4ChannelIds", firstId.ToString())
        }));
        AssertRedirect(saved, url);
        var html = await Http.GetStringAsync(url);
        Assert.DoesNotContain("name=\"Sev4ChannelIds\"", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>alert('unsafe')</script>", html);
        foreach (var channelId in new[] { firstId, secondId })
        {
            for (var level = 0; level <= 3; level++)
            {
                var input = Regex.Match(html, $"<input[^>]*id=\"channel-{channelId}-sev{level}\"[^>]*>").Value;
                Assert.IsFalse(string.IsNullOrEmpty(input));
                var expected = level is 0 or 1 || (channelId == secondId && level == 2);
                Assert.AreEqual(expected, input.Contains("checked=\"checked\""), input);
            }
        }
        using var invalidId = await PostForm("/Push/SaveBindings", new()
        {
            ["id"] = applicationId.ToString(), ["Sev1ChannelIds"] = "not-a-guid"
        }, url);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidId.StatusCode);
        using var unknownId = await PostForm("/Push/SaveBindings", new()
        {
            ["id"] = applicationId.ToString(), ["Sev2ChannelIds"] = Guid.NewGuid().ToString()
        }, url);
        Assert.AreEqual(HttpStatusCode.BadRequest, unknownId.StatusCode);
        using var missingToken = await PostForm("/Push/SaveBindings", new()
        {
            ["id"] = applicationId.ToString()
        }, includeToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingToken.StatusCode);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var bindings = await db.ApplicationPushChannels.Where(link => link.ApplicationId == applicationId).ToListAsync();
            Assert.AreEqual(2, bindings.Count);
            Assert.IsTrue(bindings.All(link => link.ReceiveSev0));
            Assert.IsFalse(bindings.Single(link => link.ChannelId == firstId).ReceiveSev4);
            Assert.IsTrue(bindings.Single(link => link.ChannelId == secondId).ReceiveSev2);
        }
        using var noChannel = await Report(key, Guid.NewGuid(), level: "sev3");
        Assert.AreEqual(HttpStatusCode.Created, noChannel.StatusCode);
        using var twoSelected = await Report(key, Guid.NewGuid(), level: "sev1");
        Assert.AreEqual(HttpStatusCode.Created, twoSelected.StatusCode);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual(2, await db.Incidents.CountAsync());
            Assert.AreEqual(firstId, (await db.PushDeliveries.SingleAsync()).ChannelId);
        }
        using var cleared = await PostForm("/Push/SaveBindings", new() { ["id"] = applicationId.ToString() }, url);
        AssertRedirect(cleared, url);
        using var finalScope = Server!.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(0, await finalDb.ApplicationPushChannels.CountAsync());
        Assert.AreEqual(1, await finalDb.PushDeliveries.CountAsync());
    }

    [TestMethod]
    public async Task OrdinaryUsersCannotManageAlerts()
    {
        await RegisterAndLoginAsync();
        var response = await Http.GetAsync("/Applications/Applications");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Demo Orders", await response.Content.ReadAsStringAsync());
    }
}

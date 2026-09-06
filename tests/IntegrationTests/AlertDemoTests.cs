using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
public class AlertDemoTests : TestBase
{
    private string? _databasePath;

    [TestInitialize]
    public override async Task CreateServer()
    {
        Server = await AppAsync<AlertDemoStartup>([], port: Port);
        using (var scope = Server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            _databasePath = db.Database.GetDbConnection().DataSource;
        }
        await Server.UpdateDbAsync<TemplateDbContext>();
        await Server.SeedAsync();
        await Server.StartAsync();
    }

    [TestCleanup]
    public override async Task CleanServer()
    {
        await base.CleanServer();
        if (_databasePath != null) File.Delete(_databasePath);
    }

    [TestMethod]
    public async Task FreshDatabaseHasNoApplicationsOrMessages()
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(0, await db.SourceApplications.CountAsync());
        Assert.AreEqual(0, await db.ReportedMessages.CountAsync());
    }

    private async Task<string> CreateApplication()
    {
        await LoginAsAdmin();
        var response = await PostForm("/Alerts/CreateApplication", new() { ["Name"] = "Demo Orders" }, "/Alerts/Applications");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        var html = await response.Content.ReadAsStringAsync();
        var key = Regex.Match(html, "value=\"([a-fA-F0-9-]{36}\\.[A-F0-9]{64})\"").Groups[1].Value;
        Assert.IsFalse(string.IsNullOrEmpty(key));
        var applications = await Http.GetStringAsync("/Alerts/Applications");
        Assert.DoesNotContain(key, applications);
        return key;
    }

    private Task<HttpResponseMessage> Report(string? key, Guid reportId, string title = "Order timeout", string level = "sev1")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/messages")
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
    public async Task CreateReportViewAndDisable()
    {
        var key = await CreateApplication();
        var reportId = Guid.NewGuid();
        var first = await Report(key, reportId);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        var receipt = JObject.Parse(await first.Content.ReadAsStringAsync());
        Assert.IsFalse(receipt.Value<bool>("duplicate"));
        var duplicate = await Report(key, reportId, "Must not replace original");
        Assert.AreEqual(HttpStatusCode.OK, duplicate.StatusCode);
        var second = JObject.Parse(await duplicate.Content.ReadAsStringAsync());
        Assert.IsTrue(second.Value<bool>("duplicate"));
        Assert.AreEqual(receipt.Value<string>("id"), second.Value<string>("id"));
        var list = await Http.GetStringAsync("/Alerts");
        Assert.Contains("Order timeout", list);
        Assert.Contains("Demo Orders", list);
        var details = await Http.GetStringAsync($"/Alerts/Details/{receipt.Value<string>("id")}");
        Assert.Contains("ORDER-1001", details);
        Assert.Contains("&lt;script&gt;", details);
        Assert.DoesNotContain("<script>alert", details);
        Assert.DoesNotContain("Must not replace original", details);
        var disabled = await PostForm("/Alerts/SetEnabled", new()
        {
            ["id"] = key.Split('.')[0], ["enabled"] = "false"
        }, "/Alerts/Applications");
        Assert.AreEqual(HttpStatusCode.Found, disabled.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Report(key, Guid.NewGuid())).StatusCode);
    }

    [TestMethod]
    public async Task RejectInvalidReportsAndUnauthorizedManagement()
    {
        Assert.AreEqual(HttpStatusCode.Found, (await Http.GetAsync("/Alerts/Applications")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Report(null, Guid.NewGuid())).StatusCode);
        var key = await CreateApplication();
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Report(key + "bad", Guid.NewGuid())).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Report(key, Guid.NewGuid(), level: "sev5")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Report(key, Guid.Empty)).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Report(key, Guid.NewGuid(), title: " ")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Report(key, Guid.NewGuid(), title: new string('a', 201))).StatusCode);
        var oversized = new HttpRequestMessage(HttpMethod.Post, "/api/v1/messages")
        {
            Content = new StringContent("{\"title\":\"" + new string('a', 66000) + "\"}", Encoding.UTF8, "application/json")
        };
        oversized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, (await Http.SendAsync(oversized)).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PostForm("/Alerts/SetEnabled", new()
        {
            ["id"] = key.Split('.')[0], ["enabled"] = "false"
        }, includeToken: false)).StatusCode);
    }

    [TestMethod]
    public async Task ConcurrentDuplicatesPersistOnlyOnce()
    {
        var key = await CreateApplication();
        var reportId = Guid.NewGuid();
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Report(key, reportId)));
        Assert.AreEqual(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.AreEqual(4, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(1, await db.ReportedMessages.CountAsync());
        var application = await db.SourceApplications.SingleAsync();
        Assert.AreNotEqual(key.Split('.')[1], application.SecretHash);
    }

    [TestMethod]
    public async Task OrdinaryUsersCannotManageAlerts()
    {
        await RegisterAndLoginAsync();
        var response = await Http.GetAsync("/Alerts/Applications");
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("Code403", response.Headers.Location!.OriginalString);
    }
}

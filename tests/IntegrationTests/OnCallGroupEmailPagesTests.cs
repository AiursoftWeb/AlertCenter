using System.Net;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Sqlite;
using Aiursoft.DbTools;
using Aiursoft.WebTools.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using static Aiursoft.WebTools.Extends;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public class OnCallGroupEmailStartup : IWebStartup
{
    public void ConfigureServices(IConfiguration configuration, IWebHostEnvironment environment, IServiceCollection services)
    {
        new Startup().ConfigureServices(configuration, environment, services);
        var databasePath = Path.Combine(Directory.GetCurrentDirectory(), $"oncall-group-email-tests-{Guid.NewGuid()}.db");
        services.AddDbContext<SqliteContext>(options => options.UseSqlite($"DataSource={databasePath};Pooling=False"));
        services.AddScoped<TemplateDbContext>(provider => provider.GetRequiredService<SqliteContext>());
    }

    public void Configure(WebApplication app) => new Startup().Configure(app);
}

[TestClass]
public class OnCallGroupEmailPagesTests : TestBase
{
    private string? _databasePath;

    [TestInitialize]
    public override async Task CreateServer()
    {
        var webRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../src/Aiursoft.AlertCenter/wwwroot"));
        Server = await AppAsync<OnCallGroupEmailStartup>(["--webroot", webRoot], port: Port);
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
        try
        {
            await base.CleanServer();
        }
        finally
        {
            if (_databasePath != null) File.Delete(_databasePath);
        }
    }

    [TestMethod]
    public async Task AdministratorCreatesEditsAndClearsCanonicalCcWithoutChangingSavedAssignments()
    {
        await LoginAsAdmin();
        var createPage = WebUtility.HtmlDecode(await Http.GetStringAsync("/OnCall/GroupCreate"));
        Assert.Contains("On-call email CC", createPage);
        Assert.Contains("name=\"GroupInput.EmailCcRecipients\"", createPage);
        Assert.Contains("maxlength=\"8000\"", createPage);
        Assert.Contains("user mailboxes (To) of all On-Call Engineers (OCEs)", createPage);
        Assert.Contains("does not automatically CC all team members", createPage);
        Assert.Contains("Custom push channels configured in the application will still deliver additional notifications", createPage);
        var created = await PostForm("/OnCall/CreateGroup", new()
        {
            ["GroupInput.Name"] = "Operations", ["GroupInput.TimeZoneId"] = "UTC",
            ["GroupInput.EmailCcRecipients"] = " Team@example.com; list@example.com,TEAM@example.com\r\nbackup@example.com "
        }, "/OnCall/GroupCreate");
        Assert.AreEqual(HttpStatusCode.Found, created.StatusCode);
        var location = created.Headers.Location!.OriginalString;
        var id = Guid.Parse(location.Split('/').Last());
        const string canonical = "Team@example.com\nlist@example.com\nbackup@example.com";
        Guid version;
        Guid shiftId;
        DateTime anchor;
        string userId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.SingleAsync(item => item.Id == id);
            Assert.AreEqual(canonical, group.EmailCcRecipients);
            version = group.ScheduleVersion;
            anchor = group.AnchorUtc;
            userId = (await db.Users.FirstAsync()).Id;
            group.Members.Add(new OnCallGroupMember { UserId = userId, RotationOrder = 0 });
            var shift = new OnCallShift
            {
                Group = group, StartsAtUtc = anchor, EndsAtUtc = anchor.AddHours(8), RestHours = 24,
                Members = [new OnCallShiftMember { UserId = userId }]
            };
            db.OnCallShifts.Add(shift);
            await db.SaveChangesAsync();
            shiftId = shift.Id;
        }
        var editUrl = $"/OnCall/GroupEdit/{id}";
        var editPage = WebUtility.HtmlDecode(await Http.GetStringAsync(editUrl));
        Assert.Contains(canonical, editPage.Replace("\r\n", "\n"));
        var details = WebUtility.HtmlDecode(await Http.GetStringAsync(location));
        Assert.Contains("On-call email CC", details);
        foreach (var recipient in canonical.Split('\n')) Assert.Contains(recipient, details);
        var update = new Dictionary<string, string>
        {
            ["id"] = id.ToString(), ["GroupInput.Name"] = "Operations", ["GroupInput.TimeZoneId"] = "UTC",
            ["GroupInput.AnchorUtc"] = anchor.ToString("O"), ["GroupInput.Version"] = version.ToString(),
            ["GroupInput.UserIds[0]"] = userId, ["GroupInput.EmailCcRecipients"] = " revised@example.com;REVISED@example.com "
        };
        AssertRedirect(await PostForm("/OnCall/UpdateGroup", update, editUrl), location);
        Assert.AreEqual(HttpStatusCode.Conflict, (await PostForm("/OnCall/UpdateGroup", update, editUrl)).StatusCode);
        using (var scope = Server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.Include(item => item.Members).SingleAsync(item => item.Id == id);
            Assert.AreEqual("revised@example.com", group.EmailCcRecipients);
            Assert.AreNotEqual(version, group.ScheduleVersion);
            Assert.AreEqual(userId, group.Members.Single().UserId);
            var shift = await db.OnCallShifts.Include(item => item.Members).SingleAsync(item => item.GroupId == id);
            Assert.AreEqual(shiftId, shift.Id);
            Assert.AreEqual(anchor, shift.StartsAtUtc);
            Assert.AreEqual(anchor.AddHours(8), shift.EndsAtUtc);
            Assert.AreEqual(24, shift.RestHours);
            Assert.AreEqual(userId, shift.Members.Single().UserId);
            update["GroupInput.Version"] = group.ScheduleVersion.ToString();
        }
        update["GroupInput.EmailCcRecipients"] = " \r\n ";
        AssertRedirect(await PostForm("/OnCall/UpdateGroup", update, editUrl), location);
        using (var scope = Server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.SingleAsync(item => item.Id == id);
            Assert.IsNull(group.EmailCcRecipients);
            Assert.AreNotEqual(Guid.Parse(update["GroupInput.Version"]), group.ScheduleVersion);
        }
        Assert.Contains("CC address not configured", WebUtility.HtmlDecode(await Http.GetStringAsync(location)));
    }

    [TestMethod]
    public async Task InvalidCcIsRedisplayedAndDoesNotCreateOrModifyTeam()
    {
        await LoginAsAdmin();
        var tooMany = string.Join(';', Enumerable.Range(0, 51).Select(index => $"user{index}@example.com"));
        var invalidInputs = new[]
        {
            (Value: "invalid-address", Error: "Invalid CC email format"),
            (Value: "Team <team@example.com>", Error: "Invalid CC email format"),
            (Value: "team@example.com\nBcc: hidden@example.com", Error: "Invalid CC email format"),
            (Value: tooMany, Error: "up to 50 CC recipients"),
            (Value: string.Join(';', Enumerable.Repeat("team@example.com", 501)), Error: "The on-call email CC configuration must not exceed 8,000 characters")
        };
        foreach (var (value, error) in invalidInputs)
        {
            var response = await PostForm("/OnCall/CreateGroup", new()
            {
                ["GroupInput.Name"] = "Invalid", ["GroupInput.TimeZoneId"] = "UTC",
                ["GroupInput.EmailCcRecipients"] = value
            }, "/OnCall/GroupCreate");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            Assert.Contains(error, page);
            Assert.Contains(value, page.Replace("\r\n", "\n"));
            using var scope = Server!.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual(0, await db.OnCallGroups.CountAsync());
        }
        Guid id;
        Guid version;
        DateTime anchor;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = new OnCallGroup { Name = "Existing", EmailCcRecipients = "existing@example.com" };
            db.OnCallGroups.Add(group);
            await db.SaveChangesAsync();
            id = group.Id;
            version = group.ScheduleVersion;
            anchor = group.AnchorUtc;
        }
        var invalidUpdate = await PostForm("/OnCall/UpdateGroup", new()
        {
            ["id"] = id.ToString(), ["GroupInput.Name"] = "Existing", ["GroupInput.TimeZoneId"] = "UTC",
            ["GroupInput.AnchorUtc"] = anchor.ToString("O"), ["GroupInput.Version"] = version.ToString(),
            ["GroupInput.EmailCcRecipients"] = "not-an-email"
        }, $"/OnCall/GroupEdit/{id}");
        Assert.AreEqual(HttpStatusCode.OK, invalidUpdate.StatusCode);
        Assert.Contains("Invalid CC email format", WebUtility.HtmlDecode(await invalidUpdate.Content.ReadAsStringAsync()));
        using (var scope = Server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.SingleAsync(item => item.Id == id);
            Assert.AreEqual("existing@example.com", group.EmailCcRecipients);
            Assert.AreEqual(version, group.ScheduleVersion);
        }
    }

    [TestMethod]
    public async Task BlankCcIsOptionalAndFiftyDistinctRecipientsAreAccepted()
    {
        await LoginAsAdmin();
        foreach (var value in new[] { "", " \r\n ", ";,\r\n" })
        {
            var created = await PostForm("/OnCall/CreateGroup", new()
            {
                ["GroupInput.Name"] = "No CC", ["GroupInput.TimeZoneId"] = "UTC",
                ["GroupInput.EmailCcRecipients"] = value
            }, "/OnCall/GroupCreate");
            Assert.AreEqual(HttpStatusCode.Found, created.StatusCode);
            var id = Guid.Parse(created.Headers.Location!.OriginalString.Split('/').Last());
            using var scope = Server!.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.IsNull((await db.OnCallGroups.SingleAsync(item => item.Id == id)).EmailCcRecipients);
        }
        var addresses = Enumerable.Range(0, 50).Select(index => $"user{index}@example.com").ToArray();
        var accepted = await PostForm("/OnCall/CreateGroup", new()
        {
            ["GroupInput.Name"] = "Fifty CC", ["GroupInput.TimeZoneId"] = "UTC",
            ["GroupInput.EmailCcRecipients"] = string.Join(';', addresses.Concat(addresses.Select(address => address.ToUpperInvariant())))
        }, "/OnCall/GroupCreate");
        Assert.AreEqual(HttpStatusCode.Found, accepted.StatusCode);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual(string.Join('\n', addresses),
                (await db.OnCallGroups.SingleAsync(item => item.Name == "Fifty CC")).EmailCcRecipients);
        }
    }

    [TestMethod]
    public async Task SignedInNonAdministratorCanViewTeamButCannotSeeOrChangeCc()
    {
        await RegisterAndLoginAsync();
        Guid id;
        Guid version;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = new OnCallGroup { Name = "Visible team", EmailCcRecipients = "private-team@example.com" };
            db.OnCallGroups.Add(group);
            await db.SaveChangesAsync();
            id = group.Id;
            version = group.ScheduleVersion;
        }
        var details = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/GroupDetails/{id}"));
        Assert.Contains("Visible team", details);
        Assert.DoesNotContain("private-team@example.com", details);
        Assert.DoesNotContain("On-call email CC", details);
        Assert.DoesNotContain("GroupInput.EmailCcRecipients", details);
        Assert.DoesNotContain("private-team@example.com", await Http.GetStringAsync("/OnCall/Groups"));
        foreach (var action in new[] { "GroupCreate", "GroupEdit" })
        {
            var response = await Http.GetAsync($"/OnCall/{action}/{id}");
            Assert.IsTrue(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Found);
            Assert.DoesNotContain("private-team@example.com", await response.Content.ReadAsStringAsync());
            if (response.StatusCode == HttpStatusCode.Found)
                Assert.Contains("/Error/Code403", response.Headers.Location!.OriginalString);
        }
        foreach (var action in new[] { "CreateGroup", "UpdateGroup" })
        {
            var response = await PostForm($"/OnCall/{action}", new()
            {
                ["id"] = id.ToString(), ["GroupInput.Name"] = "Changed", ["GroupInput.TimeZoneId"] = "UTC",
                ["GroupInput.Version"] = version.ToString(), ["GroupInput.EmailCcRecipients"] = "attacker@example.com"
            }, "/Manage/ChangePassword");
            Assert.IsTrue(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Found);
            if (response.StatusCode == HttpStatusCode.Found)
                Assert.Contains("/Error/Code403", response.Headers.Location!.OriginalString);
        }
        using (var scope = Server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.SingleAsync();
            Assert.AreEqual("private-team@example.com", group.EmailCcRecipients);
            Assert.AreEqual("Visible team", group.Name);
            Assert.AreEqual(version, group.ScheduleVersion);
        }
    }
}

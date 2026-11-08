using System.Net;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class OnCallGroupPagesTests : TestBase
{
    private readonly List<Guid> groupIds = [];
    private readonly List<Guid> ruleIds = [];
    private readonly List<string> userIds = [];

    [TestCleanup]
    public override async Task CleanServer()
    {
        if (Server != null)
        {
            using var scope = Server.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.RemoveRange(await db.Set<OnCallFallbackRecipient>().Where(item => groupIds.Contains(item.GroupId)).ToListAsync());
            db.OnCallGroupMembers.RemoveRange(await db.OnCallGroupMembers.Where(member => groupIds.Contains(member.GroupId)).ToListAsync());
            db.OnCallGroups.RemoveRange(await db.OnCallGroups.Where(group => groupIds.Contains(group.Id)).ToListAsync());
            db.OnCallRules.RemoveRange(await db.OnCallRules.Where(rule => ruleIds.Contains(rule.Id)).ToListAsync());
            db.Users.RemoveRange(await db.Users.Where(user => userIds.Contains(user.Id)).ToListAsync());
            await db.SaveChangesAsync();
        }
        await base.CleanServer();
    }

    [TestMethod]
    public async Task TeamCreateEditAndDeleteShowFeedbackAndPreserveRules()
    {
        await LoginAsAdmin();
        var groups = await Http.GetStringAsync("/OnCall/Groups");
        Assert.Contains("Create Team", groups);
        Assert.Contains("On-call groups", groups);
        var create = await Http.GetStringAsync("/OnCall/GroupCreate");
        Assert.Contains("/OnCall/CreateGroup", create);
        Assert.Contains("/scripts/oncall-group-form.js", create);
        Assert.Contains("id=\"GroupInput_TimeZoneId\"", create);
        Assert.Contains("name=\"GroupInput.TimeZoneId\"", create);
        Assert.Contains("selected=\"selected\" value=\"UTC\"", create);
        Assert.Contains("/UIStack/dist/js/app.js", create);
        Assert.IsTrue(create.IndexOf("/UIStack/dist/js/app.js", StringComparison.Ordinal) <
            create.IndexOf("/scripts/oncall-group-form.js", StringComparison.Ordinal));
        var invalid = await PostForm("/OnCall/CreateGroup", new()
        {
            ["GroupInput.Name"] = "Bad", ["GroupInput.TimeZoneId"] = "Invalid/Zone"
        }, "/OnCall/GroupCreate");
        Assert.AreEqual(HttpStatusCode.OK, invalid.StatusCode);
        var invalidPage = WebUtility.HtmlDecode(await invalid.Content.ReadAsStringAsync());
        Assert.Contains("Please select a valid time zone from the list", invalidPage);
        Assert.Contains("/scripts/oncall-group-form.js", invalidPage);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PostForm("/OnCall/CreateGroup", new()
        {
            ["GroupInput.Name"] = "Wrong", ["GroupInput.TimeZoneId"] = "UTC"
        }, includeToken: false)).StatusCode);
        var created = await PostForm("/OnCall/CreateGroup", new()
        {
            ["GroupInput.Name"] = " Operations ", ["GroupInput.TimeZoneId"] = "UTC"
        }, "/OnCall/GroupCreate");
        Assert.AreEqual(HttpStatusCode.Found, created.StatusCode);
        var location = created.Headers.Location!.OriginalString;
        Assert.Contains("/OnCall/GroupDetails/", location);
        var id = Guid.Parse(location.Split('/').Last());
        groupIds.Add(id);
        var details = WebUtility.HtmlDecode(await Http.GetStringAsync(location));
        Assert.Contains("Team created successfully", details);
        Assert.Contains("Operations", details);
        Assert.Contains("Template not bound; manual scheduling is still available", details);
        Assert.Contains($"/OnCall/ScheduleDetails/{id}", details);
        Assert.Contains("/OnCall/GroupEdit/", details);
        Assert.DoesNotContain("Team created successfully", await Http.GetStringAsync(location));
        var edit = await Http.GetStringAsync($"/OnCall/GroupEdit/{id}");
        Assert.Contains("/OnCall/UpdateGroup", edit);
        Assert.Contains("Operations", edit);
        Assert.Contains("/scripts/oncall-group-form.js", edit);
        Assert.Contains("selected=\"selected\" value=\"UTC\"", edit);
        Guid version;
        DateTime anchor;
        Guid templateId;
        string memberId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.SingleAsync(item => item.Id == id);
            version = group.ScheduleVersion;
            anchor = group.AnchorUtc;
            memberId = (await db.Users.FirstAsync()).Id;
            var template = new OnCallRule { Name = "Week" };
            db.OnCallRules.Add(template);
            await db.SaveChangesAsync();
            templateId = template.Id;
            ruleIds.Add(templateId);
        }
        var update = new Dictionary<string, string>
        {
            ["id"] = id.ToString(), ["GroupInput.Name"] = "Ops", ["GroupInput.TimeZoneId"] = "Asia/Shanghai",
            ["GroupInput.Version"] = version.ToString(), ["GroupInput.AnchorUtc"] = anchor.ToString("O"),
            ["GroupInput.RuleId"] = templateId.ToString(), ["GroupInput.UserIds[0]"] = memberId
        };
        AssertRedirect(await PostForm("/OnCall/UpdateGroup", update, $"/OnCall/GroupEdit/{id}"), location);
        Assert.Contains("Team updated", WebUtility.HtmlDecode(await Http.GetStringAsync(location)));
        Assert.AreEqual(HttpStatusCode.Conflict, (await PostForm("/OnCall/UpdateGroup", update, $"/OnCall/GroupEdit/{id}")).StatusCode);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.Include(item => item.Members).SingleAsync(item => item.Id == id);
            Assert.AreEqual("Ops", group.Name);
            Assert.AreEqual("Asia/Shanghai", group.TimeZoneId);
            Assert.AreEqual(templateId, group.RuleId);
            Assert.AreEqual(memberId, group.Members.Single().UserId);
            Assert.AreNotEqual(version, group.ScheduleVersion);
        }
        var deletePage = await Http.GetStringAsync(location);
        Assert.Contains("data-bs-target=\"#delete-oncall-group\"", deletePage);
        Assert.Contains("id=\"delete-oncall-group\"", deletePage);
        Assert.Contains("aria-describedby=\"delete-oncall-group-description\"", deletePage);
        Assert.Contains("data-action-feedback", deletePage);
        Assert.DoesNotContain("onsubmit=", deletePage);
        Assert.DoesNotContain("confirm(", deletePage);
        AssertRedirect(await PostForm("/OnCall/DeleteGroup", new() { ["id"] = id.ToString() }, location), "/OnCall/Groups");
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual(templateId, (await db.OnCallRules.SingleAsync()).Id);
            Assert.AreEqual(0, await db.OnCallGroupMembers.CountAsync());
        }
        Assert.Contains("Team deleted", WebUtility.HtmlDecode(await Http.GetStringAsync("/OnCall/Groups")));
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync(location)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync($"/OnCall/GroupEdit/{id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await PostForm("/OnCall/DeleteGroup", new() { ["id"] = id.ToString() }, "/OnCall/GroupCreate")).StatusCode);
    }

    [TestMethod]
    public async Task LocalStartPostsRerenderAndPersistWithoutUtcFieldAndKeepManualMode()
    {
        await LoginAsAdmin();
        var values = new Dictionary<string, string>
        {
            ["GroupInput.Name"] = "", ["GroupInput.TimeZoneId"] = "Asia/Shanghai",
            ["GroupInput.AnchorLocal"] = "2026-11-09T22:00", ["GroupInput.AnchorUtc"] = "not-a-date",
            ["OnCallAnchorManual"] = "true", ["GroupInput.AutoExtend"] = "true"
        };
        var response = await PostForm("/OnCall/CreateGroup", values, "/OnCall/GroupCreate");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("value=\"2026-11-09T22:00\"", html);
        Assert.Contains("name=\"OnCallAnchorManual\" value=\"true\"", html);
        values["GroupInput.Name"] = "Local post";
        response = await PostForm("/OnCall/CreateGroup", values, "/OnCall/GroupCreate");
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        var id = Guid.Parse(location.Split('/').Last());
        groupIds.Add(id);
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var group = await db.OnCallGroups.SingleAsync();
        Assert.AreEqual(new DateTime(2026, 11, 9, 14, 0, 0, DateTimeKind.Utc), group.AnchorUtc);
        Assert.IsFalse(group.AutoExtend);
        var edit = await Http.GetStringAsync($"/OnCall/GroupEdit/{group.Id}");
        Assert.Contains("value=\"2026-11-09T22:00\"", edit);
        values.Remove("GroupInput.AnchorUtc");
        values["GroupInput.Name"] = "Gap";
        values["GroupInput.TimeZoneId"] = "America/New_York";
        values["GroupInput.AnchorLocal"] = "2026-03-08T02:30";
        response = await PostForm("/OnCall/CreateGroup", values, "/OnCall/GroupCreate");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("skipped times are invalid", html);
        Assert.Contains("value=\"2026-03-08T02:30\"", html);
        Assert.AreEqual(1, await db.OnCallGroups.CountAsync());
        values["GroupInput.AnchorLocal"] = "";
        response = await PostForm("/OnCall/CreateGroup", values, "/OnCall/GroupCreate");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(1, await db.OnCallGroups.CountAsync());
        Assert.Contains("Local post", await Http.GetStringAsync(location));
    }

    [TestMethod]
    public async Task DetailsKeepOrderedRotationQueueSeparateFromFallbackOnlyRecipients()
    {
        await LoginAsAdmin();
        Guid groupId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var first = new User { UserName = "queue-first", DisplayName = "Queue Zebra" };
            var second = new User { UserName = "queue-second", DisplayName = "Queue Alpha" };
            var fallback = new User { UserName = "fallback-only", DisplayName = "Fallback Only" };
            db.Users.AddRange(first, second, fallback);
            userIds.AddRange([first.Id, second.Id, fallback.Id]);
            var group = new OnCallGroup
            {
                Name = "Separate recipients", Members =
                [new() { UserId = second.Id, RotationOrder = 1 }, new() { UserId = first.Id, RotationOrder = 0 }],
                FallbackRecipients = [new() { UserId = fallback.Id }]
            };
            db.OnCallGroups.Add(group);
            await db.SaveChangesAsync();
            groupId = group.Id;
            groupIds.Add(groupId);
        }
        var html = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/GroupDetails/{groupId}"));
        var queueStart = html.IndexOf("id=\"group-rotation-queue\"", StringComparison.Ordinal);
        var fallbackStart = html.IndexOf("id=\"group-fallback-recipients\"", StringComparison.Ordinal);
        Assert.IsTrue(queueStart >= 0 && fallbackStart > queueStart);
        var queueSection = html[queueStart..fallbackStart];
        Assert.IsTrue(queueSection.IndexOf("Queue Zebra", StringComparison.Ordinal) < queueSection.IndexOf("Queue Alpha", StringComparison.Ordinal));
        Assert.DoesNotContain("Fallback Only", queueSection);
        Assert.Contains("Fallback Only", html[fallbackStart..]);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.RemoveRange(await db.Set<OnCallFallbackRecipient>().Where(item => item.GroupId == groupId).ToListAsync());
            await db.SaveChangesAsync();
        }
        html = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/GroupDetails/{groupId}"));
        Assert.Contains("No fallback recipients configured. Alerts cannot be delivered when nobody is on call.", html);
    }

    [TestMethod]
    public async Task RegularUserCanViewTeamsAndSchedulesButCannotManageTeams()
    {
        await RegisterAndLoginAsync();
        Assert.DoesNotContain("/OnCall/GroupCreate", await Http.GetStringAsync("/OnCall/Groups"));
        Assert.DoesNotContain("/OnCall/Create", await Http.GetStringAsync("/OnCall/Index"));
        Assert.Contains("On-call schedule", await Http.GetStringAsync("/OnCall/Schedule"));
        foreach (var action in new[] { "GroupCreate", "GroupEdit" })
        {
            var response = await Http.GetAsync($"/OnCall/{action}/{Guid.NewGuid()}");
            Assert.IsTrue(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Found);
        }
        foreach (var action in new[] { "CreateGroup", "UpdateGroup", "DeleteGroup" })
        {
            var response = await PostForm($"/OnCall/{action}", new() { ["id"] = Guid.NewGuid().ToString() }, "/Manage/ChangePassword");
            Assert.IsTrue(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Found);
        }
    }
}

using System.Net;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class OnCallGroupPagesTests : TestBase
{
    [TestMethod]
    public async Task TeamCreateEditAndDeleteShowFeedbackAndPreserveRules()
    {
        await LoginAsAdmin();
        var groups = await Http.GetStringAsync("/OnCall/Groups");
        Assert.Contains("创建团队", groups);
        Assert.Contains("已保存团队", groups);
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
        Assert.Contains("请选择列表中的有效时区", invalidPage);
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
        var details = WebUtility.HtmlDecode(await Http.GetStringAsync(location));
        Assert.Contains("团队创建成功", details);
        Assert.Contains("Operations", details);
        Assert.Contains("未绑定规则模板，仍可手动编辑排班", details);
        Assert.Contains($"/OnCall/ScheduleDetails/{id}", details);
        Assert.Contains("/OnCall/GroupEdit/", details);
        Assert.DoesNotContain("团队创建成功", await Http.GetStringAsync(location));
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
        }
        var update = new Dictionary<string, string>
        {
            ["id"] = id.ToString(), ["GroupInput.Name"] = "Ops", ["GroupInput.TimeZoneId"] = "Asia/Shanghai",
            ["GroupInput.Version"] = version.ToString(), ["GroupInput.AnchorUtc"] = anchor.ToString("O"),
            ["GroupInput.RuleId"] = templateId.ToString(), ["GroupInput.UserIds[0]"] = memberId
        };
        AssertRedirect(await PostForm("/OnCall/UpdateGroup", update, $"/OnCall/GroupEdit/{id}"), location);
        Assert.Contains("团队已更新", WebUtility.HtmlDecode(await Http.GetStringAsync(location)));
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
        Assert.Contains("团队已删除", WebUtility.HtmlDecode(await Http.GetStringAsync("/OnCall/Groups")));
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync(location)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync($"/OnCall/GroupEdit/{id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await PostForm("/OnCall/DeleteGroup", new() { ["id"] = id.ToString() }, "/OnCall/GroupCreate")).StatusCode);
    }

    [TestMethod]
    public async Task RegularUserCanViewTeamsAndSchedulesButCannotManageTeams()
    {
        await RegisterAndLoginAsync();
        Assert.DoesNotContain("/OnCall/GroupCreate", await Http.GetStringAsync("/OnCall/Groups"));
        Assert.DoesNotContain("/OnCall/Create", await Http.GetStringAsync("/OnCall/Index"));
        Assert.Contains("值班排班", await Http.GetStringAsync("/OnCall/Schedule"));
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

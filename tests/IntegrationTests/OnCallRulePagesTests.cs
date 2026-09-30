using System.Net;
using System.Text.RegularExpressions;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class OnCallRulePagesTests : TestBase
{
    [TestMethod]
    public async Task RuleListFormEditorAndDetailsAreSeparatePages()
    {
        await LoginAsAdmin();
        Guid ruleId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var member = db.Users.First();
            var second = new User { UserName = "second", DisplayName = "Second" };
            var third = new User { UserName = "third", DisplayName = "Third" };
            db.Users.AddRange(second, third);
            var group = new OnCallGroup { Name = "Platform", TimeZoneId = "UTC" };
            var rule = new OnCallRule
            {
                Group = group, Name = "Day shift", AnchorUtc = DateTime.UtcNow.Date.AddDays(-1),
                Members = [new OnCallRuleMember { UserId = member.Id, RotationOrder = 0 },
                    new OnCallRuleMember { UserId = second.Id, RotationOrder = 1 },
                    new OnCallRuleMember { UserId = third.Id, RotationOrder = 2 }]
            };
            db.OnCallRules.Add(rule);
            await db.SaveChangesAsync();
            ruleId = rule.Id;
        }
        var list = await Http.GetStringAsync("/OnCall/Index");
        Assert.Contains("Day shift", list);
        Assert.Contains($"/OnCall/Details/{ruleId}", list);
        Assert.Contains($"data-rule-id=\"{ruleId}\"", list);
        Assert.Contains("该规则的全部已保存班次及成员关联将一并删除", list);
        Assert.Contains("/OnCall/DeleteRule", list);
        Assert.DoesNotContain("id=\"oncall-rule-form\"", list);

        var create = await Http.GetStringAsync("/OnCall/Create");
        Assert.Contains("创建排班规则", create);
        Assert.Contains("id=\"oncall-rule-form\"", create);
        Assert.DoesNotContain("七天值班预览", create);

        var edit = await Http.GetStringAsync($"/OnCall/Edit/{ruleId}");
        Assert.Contains("编辑排班规则", edit);
        Assert.Contains("id=\"oncall-rule-form\"", edit);
        Assert.Contains("Day shift", edit);
        Assert.DoesNotContain("七天值班预览", edit);

        var details = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/Details/{ruleId}"));
        Assert.Contains("Day shift", details);
        Assert.Contains("七天值班预览", details);
        Assert.Contains("当前值班", details);
        Assert.Contains("七天值班甘特图", details);
        Assert.Contains("ac-gantt-now", details);
        Assert.Contains("ac-gantt-current", details);
        Assert.AreEqual(21, Regex.Matches(details, "class=\"ac-gantt-shift ").Count);
        Assert.DoesNotContain("无法生成预览", details);
        Assert.DoesNotContain("id=\"oncall-rule-form\"", details);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync($"/OnCall/Details/{Guid.NewGuid()}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync($"/OnCall/Edit/{Guid.NewGuid()}")).StatusCode);
    }

    [TestMethod]
    public async Task AdministratorCanDeleteRuleAndItsSavedScheduleButNotItsGroupOrUsers()
    {
        await LoginAsAdmin();
        Guid ruleId;
        Guid groupId;
        string userId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var user = db.Users.First();
            var group = new OnCallGroup { Name = "Delete team", TimeZoneId = "UTC" };
            var rule = new OnCallRule
            {
                Group = group, Name = "To delete", AnchorUtc = DateTime.UtcNow.Date,
                Members = [new OnCallRuleMember { UserId = user.Id, RotationOrder = 0 }]
            };
            var retained = new OnCallRule { Group = group, Name = "Retained", AnchorUtc = DateTime.UtcNow.Date };
            var shift = new OnCallShift
            {
                Rule = rule, StartsAtUtc = DateTime.UtcNow.Date, EndsAtUtc = DateTime.UtcNow.Date.AddHours(8),
                Members = [new OnCallShiftMember { UserId = user.Id }]
            };
            db.OnCallRules.AddRange(rule, retained);
            db.OnCallShifts.Add(shift);
            await db.SaveChangesAsync();
            ruleId = rule.Id;
            groupId = group.Id;
            userId = user.Id;
        }

        var noToken = await PostForm("/OnCall/DeleteRule", new() { ["id"] = ruleId.ToString() }, includeToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, noToken.StatusCode);
        using (var scope = Server!.Services.CreateScope())
            Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallRules.AnyAsync(rule => rule.Id == ruleId));

        Assert.AreEqual(HttpStatusCode.NotFound, (await PostForm("/OnCall/DeleteRule", new() { ["id"] = Guid.NewGuid().ToString() }, "/OnCall/Index")).StatusCode);
        AssertRedirect(await PostForm("/OnCall/DeleteRule", new() { ["id"] = ruleId.ToString() }, "/OnCall/Index"), "/OnCall");
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.IsFalse(await db.OnCallRules.AnyAsync(rule => rule.Id == ruleId));
            Assert.IsFalse(await db.OnCallRuleMembers.AnyAsync(member => member.RuleId == ruleId));
            Assert.IsFalse(await db.OnCallShifts.AnyAsync(shift => shift.RuleId == ruleId));
            Assert.IsFalse(await db.OnCallShiftMembers.AnyAsync(member => member.Shift.RuleId == ruleId));
            Assert.AreEqual(1, await db.OnCallRules.CountAsync(rule => rule.GroupId == groupId));
            Assert.IsTrue(await db.OnCallGroups.AnyAsync(group => group.Id == groupId));
            Assert.IsTrue(await db.Users.AnyAsync(user => user.Id == userId));
        }
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync($"/OnCall/Details/{ruleId}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await PostForm("/OnCall/DeleteRule", new() { ["id"] = ruleId.ToString() }, "/OnCall/Index")).StatusCode);
    }

    [TestMethod]
    public async Task NonAdministratorCannotDeleteRules()
    {
        Guid ruleId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var rule = new OnCallRule { Group = new OnCallGroup { Name = "Visible", TimeZoneId = "UTC" }, Name = "Protected", AnchorUtc = DateTime.UtcNow.Date };
            db.OnCallRules.Add(rule);
            await db.SaveChangesAsync();
            ruleId = rule.Id;
        }
        await RegisterAndLoginAsync();
        Assert.DoesNotContain("delete-oncall-rule", await Http.GetStringAsync("/OnCall/Index"));
        var denied = await PostForm("/OnCall/DeleteRule", new() { ["id"] = ruleId.ToString() }, "/Manage/ChangePassword");
        Assert.IsTrue(denied.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Found);
        if (denied.StatusCode == HttpStatusCode.Found)
            Assert.Contains("/Error/Code403", denied.Headers.Location!.OriginalString);
        using var checkScope = Server!.Services.CreateScope();
        Assert.IsTrue(await checkScope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallRules.AnyAsync(rule => rule.Id == ruleId));
    }

    [TestMethod]
    public async Task GanttKeepsCrossMidnightShiftsContinuousAndGroupsByEmployee()
    {
        await LoginAsAdmin();
        Guid ruleId;
        var anchor = DateTime.UtcNow.Date.AddDays(14).AddHours(12);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var alice = new User { UserName = "gantt-alice", DisplayName = "Alice" };
            var bob = new User { UserName = "gantt-bob", DisplayName = "Bob" };
            var carol = new User { UserName = "gantt-carol", DisplayName = "Carol" };
            db.Users.AddRange(alice, bob, carol);
            var rule = new OnCallRule
            {
                Group = new OnCallGroup { Name = "Gantt team", TimeZoneId = "Asia/Shanghai" },
                Name = "Night rotation", AnchorUtc = anchor, ShiftHours = 8, RestHours = 0,
                Members = [new OnCallRuleMember { UserId = alice.Id, RotationOrder = 0 },
                    new OnCallRuleMember { UserId = bob.Id, RotationOrder = 1 },
                    new OnCallRuleMember { UserId = carol.Id, RotationOrder = 2 }]
            };
            db.OnCallRules.Add(rule);
            await db.SaveChangesAsync();
            ruleId = rule.Id;
        }

        var html = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/Details/{ruleId}"));
        Assert.Contains("即将开始", html);
        Assert.DoesNotContain("class=\"ac-gantt-now\"", html);
        Assert.AreEqual(3, Regex.Matches(html, "class=\"ac-gantt-row\" role=\"group\"").Count);
        Assert.AreEqual(21, Regex.Matches(html, "class=\"ac-gantt-shift ").Count);
        Assert.Contains($"Alice · {anchor.AddHours(8):yyyy-MM-dd HH:mm} +08:00 → {anchor.AddHours(16):yyyy-MM-dd HH:mm} +08:00 · 8 小时 · Asia/Shanghai", html);
        Assert.Contains("left: 0%; width: 4.761905%", html);
        Assert.Contains("left: 4.761905%; width: 4.761905%", html);
        Assert.Contains("oncall-gantt.js", html);
        Assert.DoesNotContain("无法生成预览", html);
    }
}

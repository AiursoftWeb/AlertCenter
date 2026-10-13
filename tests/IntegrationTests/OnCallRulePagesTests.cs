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
        Guid groupId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var member = db.Users.First();
            var second = new User { UserName = "second", DisplayName = "Second" };
            var third = new User { UserName = "third", DisplayName = "Third" };
            db.Users.AddRange(second, third);
            var rule = new OnCallRule { Name = "Day shift" };
            var group = new OnCallGroup
            {
                Name = "Platform", TimeZoneId = "UTC", Rule = rule, AnchorUtc = DateTime.UtcNow.Date.AddDays(-1),
                Members = [new OnCallGroupMember { UserId = member.Id, RotationOrder = 0 },
                    new OnCallGroupMember { UserId = second.Id, RotationOrder = 1 },
                    new OnCallGroupMember { UserId = third.Id, RotationOrder = 2 }]
            };
            db.OnCallGroups.Add(group);
            await db.SaveChangesAsync();
            ruleId = rule.Id;
            groupId = group.Id;
        }
        var list = await Http.GetStringAsync("/OnCall/Index");
        Assert.Contains("Day shift", list);
        Assert.Contains($"/OnCall/Details/{ruleId}", list);
        Assert.DoesNotContain("创建团队</h2>", list);
        Assert.DoesNotContain($"data-rule-id=\"{ruleId}\"", list);
        Assert.Contains("已绑定，不能删除", WebUtility.HtmlDecode(list));
        Assert.Contains("团队已保存的班次和历史记录不会被删除", WebUtility.HtmlDecode(list));
        Assert.Contains("/OnCall/DeleteRule", list);
        Assert.DoesNotContain("id=\"oncall-rule-form\"", list);
        var scheduleList = await Http.GetStringAsync("/OnCall/Schedule");
        Assert.Contains($"/OnCall/ScheduleDetails/{groupId}", scheduleList);
        Assert.DoesNotContain($"/OnCall/ScheduleDetails/{ruleId}", scheduleList);
        Assert.DoesNotContain("创建团队</h2>", scheduleList);
        Assert.DoesNotContain("id=\"oncall-schedule\"", scheduleList);

        var create = await Http.GetStringAsync("/OnCall/Create");
        Assert.Contains("创建排班规则", create);
        Assert.Contains("action=\"/OnCall/SaveRule\"", create);
        Assert.Contains("name=\"RuleInput.Name\"", create);
        Assert.DoesNotContain("七天值班预览", create);
        Assert.DoesNotContain("RuleInput.GroupId", create);
        Assert.DoesNotContain("RuleInput.AnchorUtc", create);
        Assert.DoesNotContain("RuleInput.UserIds", create);
        Assert.DoesNotContain("oncall-member-search.js", create);

        var edit = await Http.GetStringAsync($"/OnCall/Edit/{ruleId}");
        Assert.Contains("编辑排班规则", edit);
        Assert.Contains("action=\"/OnCall/SaveRule\"", edit);
        Assert.Contains("name=\"RuleInput.Id\"", edit);
        Assert.Contains("Day shift", edit);
        Assert.DoesNotContain("七天值班预览", edit);

        var details = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/Details/{ruleId}"));
        Assert.Contains("Day shift", details);
        Assert.Contains("规则配置", details);
        Assert.Contains($"/OnCall/ScheduleDetails/{groupId}", details);
        Assert.Contains("使用该模板的团队", details);
        Assert.DoesNotContain("七天值班预览", details);
        var schedule = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/ScheduleDetails/{groupId}"));
        Assert.Contains("七天值班排班", schedule);
        Assert.Contains("当前值班", schedule);
        Assert.Contains("七天值班甘特图", schedule);
        Assert.Contains("ac-gantt-now", schedule);
        Assert.Contains("ac-gantt-current", schedule);
        Assert.AreEqual(21, Regex.Matches(schedule, "class=\"ac-gantt-shift ").Count);
        Assert.DoesNotContain("无法生成预览", schedule);
        Assert.DoesNotContain("id=\"oncall-rule-form\"", details);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync($"/OnCall/Details/{Guid.NewGuid()}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync($"/OnCall/ScheduleDetails/{Guid.NewGuid()}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync($"/OnCall/Edit/{Guid.NewGuid()}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync($"/OnCall/ScheduleDetails/{ruleId}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync($"/OnCall/Details/{groupId}")).StatusCode);
    }

    [TestMethod]
    public async Task AdministratorCannotDeleteBoundTemplateButCanUnbindAndDeleteWithoutChangingHistory()
    {
        await LoginAsAdmin();
        Guid ruleId;
        Guid groupId;
        Guid retainedRuleId;
        string userId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var user = db.Users.First();
            var rule = new OnCallRule { Name = "To delete" };
            var group = new OnCallGroup
            {
                Name = "Delete team", Rule = rule,
                Members = [new OnCallGroupMember { UserId = user.Id, RotationOrder = 0 }]
            };
            var retained = new OnCallRule { Name = "Retained" };
            db.OnCallRules.Add(retained);
            retainedRuleId = retained.Id;
            db.OnCallShifts.Add(new OnCallShift
            {
                Group = group, StartsAtUtc = DateTime.UtcNow.Date, EndsAtUtc = DateTime.UtcNow.Date.AddHours(8), RestHours = 24,
                Members = [new OnCallShiftMember { UserId = user.Id }]
            });
            await db.SaveChangesAsync();
            ruleId = rule.Id;
            groupId = group.Id;
            userId = user.Id;
        }

        Assert.AreEqual(HttpStatusCode.BadRequest, (await PostForm("/OnCall/DeleteRule", new() { ["id"] = ruleId.ToString() }, includeToken: false)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await PostForm("/OnCall/DeleteRule", new() { ["id"] = Guid.NewGuid().ToString() }, "/OnCall/Index")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, (await PostForm("/OnCall/DeleteRule", new() { ["id"] = ruleId.ToString() }, "/OnCall/Index")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, (await PostForm("/OnCall/DeleteGroup", new() { ["id"] = groupId.ToString() }, "/OnCall/GroupCreate")).StatusCode);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.SingleAsync(item => item.Id == groupId);
            Assert.IsTrue(await db.OnCallRules.AnyAsync(rule => rule.Id == ruleId));
            var unbind = new Dictionary<string, string>
            {
                ["id"] = groupId.ToString(), ["GroupInput.Name"] = group.Name,
                ["GroupInput.TimeZoneId"] = group.TimeZoneId, ["GroupInput.AnchorUtc"] = group.AnchorUtc.ToString("O"),
                ["GroupInput.Version"] = group.ScheduleVersion.ToString(), ["GroupInput.UserIds[0]"] = userId
            };
            AssertRedirect(await PostForm("/OnCall/UpdateGroup", unbind, $"/OnCall/GroupEdit/{groupId}"), $"/OnCall/GroupDetails/{groupId}");
        }
        AssertRedirect(await PostForm("/OnCall/DeleteRule", new() { ["id"] = ruleId.ToString() }, "/OnCall/Index"), "/OnCall");
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.IsFalse(await db.OnCallRules.AnyAsync(rule => rule.Id == ruleId));
            Assert.IsTrue(await db.OnCallRules.AnyAsync(rule => rule.Id == retainedRuleId));
            Assert.AreEqual(1, await db.OnCallGroupMembers.CountAsync(member => member.GroupId == groupId));
            Assert.AreEqual(1, await db.OnCallShifts.CountAsync(shift => shift.GroupId == groupId));
            Assert.AreEqual(1, await db.OnCallShiftMembers.CountAsync(member => member.Shift.GroupId == groupId));
            Assert.AreEqual(24, (await db.OnCallShifts.SingleAsync(shift => shift.GroupId == groupId)).RestHours);
            Assert.IsNull((await db.OnCallGroups.SingleAsync(group => group.Id == groupId)).RuleId);
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
            var rule = new OnCallRule { Name = "Protected" };
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
        Guid groupId;
        var anchor = DateTime.UtcNow.Date.AddDays(14).AddHours(12);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var alice = new User { UserName = "gantt-alice", DisplayName = "Alice" };
            var bob = new User { UserName = "gantt-bob", DisplayName = "Bob" };
            var carol = new User { UserName = "gantt-carol", DisplayName = "Carol" };
            db.Users.AddRange(alice, bob, carol);
            var group = new OnCallGroup
            {
                Name = "Gantt team", TimeZoneId = "Asia/Shanghai", AnchorUtc = anchor,
                Rule = new OnCallRule { Name = "Night rotation", ShiftHours = 8, RestHours = 0 },
                Members = [new OnCallGroupMember { UserId = alice.Id, RotationOrder = 0 },
                    new OnCallGroupMember { UserId = bob.Id, RotationOrder = 1 },
                    new OnCallGroupMember { UserId = carol.Id, RotationOrder = 2 }]
            };
            db.OnCallGroups.Add(group);
            await db.SaveChangesAsync();
            groupId = group.Id;
        }

        var html = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/ScheduleDetails/{groupId}"));
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

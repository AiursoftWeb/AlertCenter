using System.Net;
using System.Text.RegularExpressions;
using Aiursoft.AlertCenter.Entities;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class SchedulingTemplatePagesTests : TestBase
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TemplatePagesShowSplittingStaffingRotationAndUsageWithoutLegacyRest(bool chinese)
    {
        await LoginAsAdmin();
        var fixedTemplate = new OnCallRule
        {
            Name = $"Fixed template {Guid.NewGuid():N}", ShiftMinutes = 37, ShiftHours = 8, RestHours = 99,
            StaffPerShift = 3, SwapAlternateRounds = true
        };
        var weeklyTemplate = new OnCallRule
        {
            Name = $"Weekly template {Guid.NewGuid():N}", StaffPerShift = 2,
            HandoverPoints = [new() { DayOfWeek = DayOfWeek.Friday, MinuteOfDay = 1320 },
                new() { DayOfWeek = DayOfWeek.Monday, MinuteOfDay = 9 * 60 + 17 }]
        };
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.OnCallRules.AddRange(fixedTemplate, weeklyTemplate, new OnCallRule { Name = "Legacy duration", ShiftHours = 6 });
            db.OnCallGroups.Add(new OnCallGroup { Name = "Template users", Rule = fixedTemplate });
            await db.SaveChangesAsync();
        }
        if (chinese)
        {
            using var culture = await Http.GetAsync("/Culture/Set?culture=zh-CN&returnUrl=/OnCall");
            Assert.AreEqual(HttpStatusCode.Found, culture.StatusCode);
        }

        var list = WebUtility.HtmlDecode(await Http.GetStringAsync("/OnCall/Index"));
        Assert.Contains(chinese ? "排班模板" : "Scheduling Templates", list);
        Assert.Contains(chinese ? "切分方式" : "Split mode", list);
        Assert.Contains(chinese ? "每段人数" : "Staff per shift", list);
        Assert.Contains(chinese ? "轮换策略" : "Rotation strategy", list);
        Assert.Contains(chinese ? "使用的团队" : "Teams using", list);
        Assert.Contains(chinese ? "固定时长 37 分钟" : "Fixed duration: 37 minutes", list);
        Assert.Contains(chinese ? "固定时长 360 分钟" : "Fixed duration: 360 minutes", list);
        Assert.Contains(chinese ? "偶数人数隔轮对调" : "Swap adjacent members on alternate rounds for even-sized teams", list);
        Assert.Contains(chinese ? "纯队列" : "Pure queue", list);
        Assert.DoesNotContain(chinese ? "排班规则" : "Scheduling Rules", list);
        Assert.DoesNotContain(chinese ? "轮班 / 休息" : "Shift / Break", list);
        var fixedRow = Regex.Match(list, $"<tr>\\s*<td>{Regex.Escape(fixedTemplate.Name)}</td>(.*?)</tr>", RegexOptions.Singleline).Value;
        var cells = Regex.Matches(fixedRow, "<td>(.*?)</td>", RegexOptions.Singleline);
        Assert.AreEqual(6, cells.Count);
        Assert.AreEqual("3", cells[2].Groups[1].Value);
        Assert.AreEqual("1", cells[4].Groups[1].Value);
        var weeklyRow = Regex.Match(list, $"<tr>\\s*<td>{Regex.Escape(weeklyTemplate.Name)}</td>(.*?)</tr>", RegexOptions.Singleline).Value;
        Assert.Contains("09:17", weeklyRow);
        Assert.Contains("22:00", weeklyRow);
        Assert.IsTrue(weeklyRow.IndexOf("09:17", StringComparison.Ordinal) < weeklyRow.IndexOf("22:00", StringComparison.Ordinal));

        var details = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/Details/{fixedTemplate.Id}"));
        Assert.Contains(chinese ? "固定时长 37 分钟" : "Fixed duration: 37 minutes", details);
        Assert.Contains("Template users", details);
        Assert.Contains(chinese ? "修改模板不影响已排好的班段" : "Changing a template does not affect scheduled shifts.", details);
        Assert.DoesNotContain(chinese ? "休息" : "Min rest", details);
        var weeklyDetails = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/Details/{weeklyTemplate.Id}"));
        Assert.Contains("09:17", weeklyDetails);
        Assert.Contains("22:00", weeklyDetails);
        var groupForm = WebUtility.HtmlDecode(await Http.GetStringAsync("/OnCall/GroupCreate"));
        Assert.Contains(chinese ? "排班模板" : "Scheduling Template", groupForm);
        Assert.DoesNotContain(chinese ? "排班规则" : "Scheduling Rule", groupForm);
        Assert.Contains("name=\"GroupInput.RuleId\"", groupForm);
    }

    [TestMethod]
    public async Task TemplateValidationAndDeletionErrorsUseTemplateTerminology()
    {
        await LoginAsAdmin();
        var missingName = await PostForm("/OnCall/SaveRule", new() { ["RuleInput.Name"] = "" }, "/OnCall/Create");
        Assert.Contains("Please enter the template name.", WebUtility.HtmlDecode(await missingName.Content.ReadAsStringAsync()));
        var longName = await PostForm("/OnCall/SaveRule", new() { ["RuleInput.Name"] = new string('x', 101) }, "/OnCall/Create");
        Assert.Contains("Template name must not exceed 100 characters.", WebUtility.HtmlDecode(await longName.Content.ReadAsStringAsync()));
        var template = new OnCallRule { Name = "Bound template" };
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.OnCallGroups.Add(new OnCallGroup { Name = "Bound team", Rule = template });
            await db.SaveChangesAsync();
        }
        var deletion = await PostForm("/OnCall/DeleteRule", new() { ["id"] = template.Id.ToString() }, "/OnCall/Index");
        Assert.AreEqual(HttpStatusCode.Conflict, deletion.StatusCode);
        Assert.Contains("This template is still bound to a team.", await deletion.Content.ReadAsStringAsync());
    }
}

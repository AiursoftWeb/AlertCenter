using System.Net;
using Aiursoft.AlertCenter.Entities;

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
            var group = new OnCallGroup { Name = "Platform", TimeZoneId = "UTC" };
            var rule = new OnCallRule
            {
                Group = group, Name = "Day shift", AnchorUtc = DateTime.UtcNow.Date.AddDays(-1),
                RestHours = 0, Members = [new OnCallRuleMember { UserId = member.Id, RotationOrder = 0 }]
            };
            db.OnCallRules.Add(rule);
            await db.SaveChangesAsync();
            ruleId = rule.Id;
        }
        var list = await Http.GetStringAsync("/OnCall/Index");
        Assert.Contains("Day shift", list);
        Assert.Contains($"/OnCall/Details/{ruleId}", list);
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
        Assert.Contains("值班员工图例", details);
        Assert.Contains("ac-oncall-track", details);
        Assert.Contains("次日", details);
        Assert.DoesNotContain("无法生成预览", details);
        Assert.DoesNotContain("id=\"oncall-rule-form\"", details);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync($"/OnCall/Details/{Guid.NewGuid()}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync($"/OnCall/Edit/{Guid.NewGuid()}")).StatusCode);
    }
}

using System.Net;
using System.Text.RegularExpressions;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class OnCallFormValidationTests : TestBase
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
            db.OnCallShifts.RemoveRange(await db.OnCallShifts.Where(shift => groupIds.Contains(shift.GroupId)).ToListAsync());
            db.OnCallGroupMembers.RemoveRange(await db.OnCallGroupMembers.Where(member => groupIds.Contains(member.GroupId)).ToListAsync());
            db.OnCallGroups.RemoveRange(await db.OnCallGroups.Where(group => groupIds.Contains(group.Id)).ToListAsync());
            db.OnCallRules.RemoveRange(await db.OnCallRules.Where(rule => ruleIds.Contains(rule.Id)).ToListAsync());
            db.Users.RemoveRange(await db.Users.Where(user => userIds.Contains(user.Id)).ToListAsync());
            await db.SaveChangesAsync();
        }
        await base.CleanServer();
    }

    [TestMethod]
    public async Task AutomaticExtensionRequiresEnoughMembersOnCreateAndEdit()
    {
        await LoginAsAdmin();
        Guid ruleId;
        string firstId;
        string secondId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            firstId = (await db.Users.FirstAsync()).Id;
            var second = new User { UserName = Guid.NewGuid().ToString(), DisplayName = "Second member" };
            db.Users.Add(second);
            secondId = second.Id;
            userIds.Add(secondId);
            var rule = new OnCallRule { Name = "Two people", StaffPerShift = 2 };
            db.OnCallRules.Add(rule);
            await db.SaveChangesAsync();
            ruleId = rule.Id;
            ruleIds.Add(ruleId);
        }
        var values = new Dictionary<string, string>
        {
            ["GroupInput.Name"] = "Staffing team", ["GroupInput.TimeZoneId"] = "UTC",
            ["GroupInput.RuleId"] = ruleId.ToString(), ["GroupInput.AutoExtend"] = "true",
            ["GroupInput.UserIds[0]"] = firstId
        };
        var response = await PostForm("/OnCall/CreateGroup", values, "/OnCall/GroupCreate");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Automatic extension requires at least 2 rotation queue members for the selected template.",
            WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        using (var scope = Server!.Services.CreateScope())
            Assert.AreEqual(0, await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallGroups.CountAsync());

        values["GroupInput.AutoExtend"] = "false";
        response = await PostForm("/OnCall/CreateGroup", values, "/OnCall/GroupCreate");
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        var id = Guid.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
        groupIds.Add(id);
        Guid version;
        using (var scope = Server!.Services.CreateScope())
            version = (await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallGroups.SingleAsync()).ScheduleVersion;
        values["id"] = id.ToString();
        values["GroupInput.Version"] = version.ToString();
        values["GroupInput.AutoExtend"] = "true";
        response = await PostForm("/OnCall/UpdateGroup", values, $"/OnCall/GroupEdit/{id}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Automatic extension requires at least 2 rotation queue members", await response.Content.ReadAsStringAsync());
        using (var scope = Server!.Services.CreateScope())
        {
            var group = await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallGroups.SingleAsync();
            Assert.IsFalse(group.AutoExtend);
            Assert.AreEqual(version, group.ScheduleVersion);
        }

        values["GroupInput.UserIds[1]"] = secondId;
        response = await PostForm("/OnCall/UpdateGroup", values, $"/OnCall/GroupEdit/{id}");
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        using (var scope = Server!.Services.CreateScope())
        {
            var group = await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallGroups.SingleAsync();
            Assert.IsTrue(group.AutoExtend);
            version = group.ScheduleVersion;
        }
        values["GroupInput.Version"] = version.ToString();
        values.Remove("GroupInput.UserIds[1]");
        values.Remove("GroupInput.AutoExtend");
        response = await PostForm("/OnCall/UpdateGroup", values, $"/OnCall/GroupEdit/{id}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Automatic extension requires at least 2 rotation queue members", await response.Content.ReadAsStringAsync());
        using (var scope = Server!.Services.CreateScope())
        {
            var group = await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallGroups.Include(item => item.Members).SingleAsync();
            Assert.IsTrue(group.AutoExtend);
            Assert.AreEqual(2, group.Members.Count);
            Assert.AreEqual(version, group.ScheduleVersion);
        }
        values.Remove("GroupInput.RuleId");
        response = await PostForm("/OnCall/UpdateGroup", values, $"/OnCall/GroupEdit/{id}");
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        using var finalScope = Server!.Services.CreateScope();
        var saved = await finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallGroups.SingleAsync();
        Assert.IsNull(saved.RuleId);
        Assert.IsFalse(saved.AutoExtend);
    }

    [TestMethod]
    public async Task TemplateStaffingRejectsUndersizedAutomaticTeamsAndListsTheirNames()
    {
        await LoginAsAdmin();
        Guid ruleId;
        Dictionary<Guid, Guid> versions;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var userId = (await db.Users.FirstAsync()).Id;
            var rule = new OnCallRule { Name = "Shared staffing", StaffPerShift = 1 };
            var groups = new[]
            {
                new OnCallGroup { Name = "Alpha affected", Rule = rule, AutoExtend = true,
                    Members = [new OnCallGroupMember { UserId = userId }] },
                new OnCallGroup { Name = "Beta affected", Rule = rule, AutoExtend = true,
                    Members = [new OnCallGroupMember { UserId = userId }] },
                new OnCallGroup { Name = "Manual ignored", Rule = rule, AutoExtend = false }
            };
            db.OnCallGroups.AddRange(groups);
            await db.SaveChangesAsync();
            ruleId = rule.Id;
            ruleIds.Add(ruleId);
            groupIds.AddRange(groups.Select(group => group.Id));
            versions = groups.ToDictionary(group => group.Id, group => group.ScheduleVersion);
        }
        var values = new Dictionary<string, string>
        {
            ["RuleInput.Id"] = ruleId.ToString(), ["RuleInput.Name"] = "Changed staffing",
            ["RuleInput.StaffPerShift"] = "2"
        };
        var response = await PostForm("/OnCall/SaveRule", values, $"/OnCall/Edit/{ruleId}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Staff per shift exceeds the member count of these teams with automatic extension enabled: Alpha affected, Beta affected.",
            WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var rule = await db.OnCallRules.SingleAsync();
            Assert.AreEqual(1, rule.StaffPerShift);
            Assert.AreEqual("Shared staffing", rule.Name);
            foreach (var group in await db.OnCallGroups.ToListAsync())
                Assert.AreEqual(versions[group.Id], group.ScheduleVersion);
        }
        values["RuleInput.StaffPerShift"] = "1";
        response = await PostForm("/OnCall/SaveRule", values, $"/OnCall/Edit/{ruleId}");
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        using var finalScope = Server!.Services.CreateScope();
        Assert.AreEqual("Changed staffing", (await finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallRules.SingleAsync()).Name);
    }

    [TestMethod]
    [DataRow("MinimumRestHours", "Minimum rest must be 0–720 hours.")]
    [DataRow("MaximumContinuousHours", "Maximum continuous duty must be 0–720 hours.")]
    public async Task ThresholdFormsRejectAbove720AndAcceptBoundary(string field, string expectedError)
    {
        await LoginAsAdmin();
        var createPage = await Http.GetStringAsync("/OnCall/GroupCreate");
        var input = Regex.Match(createPage, $"<input[^>]*name=\"GroupInput.{field}\"[^>]*>").Value;
        Assert.Contains("max=\"720\"", input);
        var values = new Dictionary<string, string>
        {
            ["GroupInput.Name"] = "Threshold team", ["GroupInput.TimeZoneId"] = "UTC",
            [$"GroupInput.{field}"] = "720.5"
        };
        var response = await PostForm("/OnCall/CreateGroup", values, "/OnCall/GroupCreate");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expectedError, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        using (var scope = Server!.Services.CreateScope())
            Assert.AreEqual(0, await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallGroups.CountAsync());
        values[$"GroupInput.{field}"] = "720";
        response = await PostForm("/OnCall/CreateGroup", values, "/OnCall/GroupCreate");
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        var id = Guid.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
        groupIds.Add(id);
        Guid version;
        using (var scope = Server!.Services.CreateScope())
        {
            var group = await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallGroups.SingleAsync();
            Assert.AreEqual(720d, field == "MinimumRestHours" ? group.MinimumRestHours : group.MaximumContinuousHours);
            version = group.ScheduleVersion;
        }
        values["id"] = id.ToString();
        values["GroupInput.Version"] = version.ToString();
        values[$"GroupInput.{field}"] = "721";
        response = await PostForm("/OnCall/UpdateGroup", values, $"/OnCall/GroupEdit/{id}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expectedError, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        using var finalScope = Server!.Services.CreateScope();
        var saved = await finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallGroups.SingleAsync();
        Assert.AreEqual(720d, field == "MinimumRestHours" ? saved.MinimumRestHours : saved.MaximumContinuousHours);
        Assert.AreEqual(version, saved.ScheduleVersion);
    }
}

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class OnCallAdjacentPagesTests : TestBase
{
    private Guid? groupId;
    private string[] userIds = [];

    [TestCleanup]
    public override async Task CleanServer()
    {
        if (Server != null && groupId.HasValue)
        {
            using var scope = Server.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.OnCallShiftMembers.RemoveRange(await db.OnCallShiftMembers.Where(member => member.Shift.GroupId == groupId).ToListAsync());
            db.OnCallShifts.RemoveRange(await db.OnCallShifts.Where(shift => shift.GroupId == groupId).ToListAsync());
            db.OnCallGroupMembers.RemoveRange(await db.OnCallGroupMembers.Where(member => member.GroupId == groupId).ToListAsync());
            db.OnCallGroups.RemoveRange(await db.OnCallGroups.Where(group => group.Id == groupId).ToListAsync());
            db.Users.RemoveRange(await db.Users.Where(user => userIds.Contains(user.Id)).ToListAsync());
            await db.SaveChangesAsync();
        }
        await base.CleanServer();
    }

    [TestMethod]
    public async Task AdjacentDaysStartCollapsedAndKeepSavedWeekMetadataAndDepartedMembers()
    {
        await LoginAsAdmin();
        var anchor = DateTime.UtcNow.Date.AddDays(-21).AddHours(12);
        var start = anchor.AddDays(7);
        Guid id;
        string departedId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var current = new User { UserName = "adjacent-current", DisplayName = "Current engineer" };
            var departed = new User { UserName = "adjacent-departed", DisplayName = "Departed engineer" };
            db.Users.AddRange(current, departed);
            var group = new OnCallGroup
            {
                Name = "Adjacent team", AnchorUtc = anchor, TimeZoneId = "America/New_York",
                Members = [new OnCallGroupMember { UserId = current.Id }]
            };
            db.OnCallGroups.Add(group);
            foreach (var offset in new[] { -1, 1 })
            {
                for (var index = 0; index < 21; index++)
                {
                    var time = start.AddDays(offset * 7).AddHours(index * 8);
                    db.OnCallShifts.Add(new OnCallShift
                    {
                        Group = group, StartsAtUtc = time, EndsAtUtc = time.AddHours(8),
                        RestHours = offset < 0 ? 0 : 8, MinimumStaff = offset < 0 ? 1 : 2,
                        Members = [new OnCallShiftMember { UserId = offset < 0 ? departed.Id : current.Id }]
                    });
                }
            }
            groupId = group.Id;
            userIds = [current.Id, departed.Id];
            await db.SaveChangesAsync();
            id = group.Id;
            departedId = departed.Id;
        }
        var html = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/ScheduleDetails/{id}?startUtc={Uri.EscapeDataString(start.ToString("O"))}"));
        Assert.Contains("data-oncall-edge=\"previous\" aria-expanded=\"false\"", html);
        Assert.Contains("data-oncall-edge=\"next\" aria-expanded=\"false\"", html);
        Assert.Contains("id=\"oncall-copy-confirm\"", html);
        Assert.Contains("id=\"oncall-copy-next\"", html);
        Assert.Contains($"data-user-id=\"{departedId}\"", html);
        Assert.Contains("Departed engineer", html);
        Assert.Contains("data-departed=\"true\"", html);
        Assert.AreEqual(6, Regex.Matches(html, "class=\"ac-gantt-shift ").Count);
        foreach (Match shift in Regex.Matches(html, "<button[^>]*class=\"ac-gantt-shift [^>]*>"))
            Assert.Contains("hidden=\"hidden\"", shift.Value);
        var root = Regex.Match(html, "<div id=\"oncall-schedule\"[^>]*>").Value;
        var weeks = JsonDocument.Parse(Regex.Match(root, "data-weeks=\"(.*?)\"\\s+data-generate-url", RegexOptions.Singleline).Groups[1].Value);
        Assert.IsTrue(weeks.RootElement[0].GetProperty("saved").GetBoolean());
        Assert.AreEqual(0, weeks.RootElement[0].GetProperty("restHours").GetInt32());
        Assert.AreEqual(8, weeks.RootElement[2].GetProperty("restHours").GetInt32());
        Assert.AreEqual(2, weeks.RootElement[2].GetProperty("minimumStaff").GetInt32());
        Assert.AreEqual(63, Regex.Matches(html, "<option value=\"[0-9]+\"").Count);

        var anchorPage = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/ScheduleDetails/{id}?startUtc={Uri.EscapeDataString(anchor.ToString("O"))}"));
        Assert.DoesNotContain("data-oncall-edge=\"previous\"", anchorPage);
        Assert.DoesNotContain("<optgroup label=\"上一周\"", anchorPage);
    }
}

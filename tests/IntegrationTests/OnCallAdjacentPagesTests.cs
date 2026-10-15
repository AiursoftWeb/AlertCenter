using System.Globalization;
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
    [DataRow(true, 2026, 3, 8)]
    [DataRow(true, 2026, 10, 24)]
    [DataRow(false, 2026, 3, 8)]
    [DataRow(false, 2026, 10, 24)]
    public async Task Adjacent36HoursStayReadOnlyAcrossDstAndKeepSavedWeekMetadataAndDepartedMembers(bool administrator, int year, int month, int day)
    {
        if (administrator) await LoginAsAdmin();
        else await RegisterAndLoginAsync();
        var start = new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Utc);
        var anchor = start.AddDays(-7);
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
        var rawHtml = await Http.GetStringAsync($"/OnCall/ScheduleDetails/{id}?startUtc={Uri.EscapeDataString(start.ToString("O"))}");
        var html = WebUtility.HtmlDecode(rawHtml);
        AssertEdgeFrame(html);
        Assert.Contains($"data-user-id=\"{departedId}\"", html);
        Assert.Contains("Departed engineer", html);
        Assert.Contains("data-departed=\"true\"", html);
        var adjacent = Regex.Matches(rawHtml, "<button[^>]*class=\"ac-gantt-shift ac-gantt-adjacent\"[^>]*>")
            .Select(match => WebUtility.HtmlDecode(match.Value)).ToArray();
        Assert.AreEqual(10, adjacent.Length);
        Assert.IsTrue(adjacent.All(shift => shift.Contains("data-readonly=\"true\"") && shift.Contains("hidden=\"hidden\"") && !shift.Contains("data-slot=")));
        Assert.IsTrue(adjacent.All(shift => shift.Contains($"data-group-id=\"{id}\"") && shift.Contains("Adjacent team")));
        var adjacentContents = Regex.Matches(html, "<button[^>]*class=\"ac-gantt-shift ac-gantt-adjacent\"[^>]*>(.*?)</button>", RegexOptions.Singleline)
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.AreEqual(10, adjacentContents.Length);
        Assert.IsTrue(adjacentContents.All(content => content.Contains("Adjacent team（只读）")));
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        string LocalTime(DateTime time) => TimeZoneInfo.ConvertTime(new DateTimeOffset(time), zone).ToString("yyyy-MM-dd HH:mm zzz");
        var end = start.AddDays(7);
        foreach (var edge in new[] { "previous", "next" })
        {
            var bars = adjacent.Where(shift => shift.Contains($"data-edge=\"{edge}\"")).ToArray();
            Assert.AreEqual(5, bars.Length);
            var hours = bars.Select(Geometry).ToArray();
            Assert.AreEqual(1, hours.Count(span => Math.Abs(span.Width - 4) < 0.000001));
            Assert.AreEqual(4, hours.Count(span => Math.Abs(span.Width - 8) < 0.000001));
            Assert.AreEqual(36, hours.Sum(span => span.Width), 0.000001);
            var boundary = edge == "previous" ? -36 : 168;
            Assert.AreEqual(boundary, hours.Min(span => span.Left), 0.000001);
            Assert.AreEqual(boundary + 36, hours.Max(span => span.Left + span.Width), 0.000001);
            var fullStart = edge == "previous" ? start.AddHours(-40) : end.AddHours(32);
            var clipped = bars.Single(shift => shift.Contains($"data-start=\"{LocalTime(fullStart)}\""));
            Assert.Contains($"data-end=\"{LocalTime(fullStart.AddHours(8))}\"", clipped);
            Assert.AreEqual(4, Geometry(clipped).Width, 0.000001);
            var edgeStart = edge == "previous" ? start.AddHours(-36) : end;
            Assert.Contains($"{LocalTime(edgeStart)[..16]} → {LocalTime(edgeStart.AddHours(36))[..16]}", html);
        }
        Assert.Contains("本周前 36 小时（只读）", html);
        Assert.Contains("本周后 36 小时（只读）", html);
        Assert.IsTrue(adjacent.Any(shift => shift.Contains("-04:00")) && adjacent.Any(shift => shift.Contains("-05:00")));
        if (administrator)
        {
            Assert.Contains("id=\"oncall-copy-confirm\"", html);
            Assert.Contains("id=\"oncall-copy-next\"", html);
            var root = Regex.Match(rawHtml, "<div id=\"oncall-schedule\"[^>]*>").Value;
            Assert.Contains("data-group-name=\"Adjacent team\"", root);
            using var weeks = JsonDocument.Parse(WebUtility.HtmlDecode(Regex.Match(root, "data-weeks=\"([^\"]*)\"").Groups[1].Value));
            Assert.AreEqual(3, weeks.RootElement.GetArrayLength());
            Assert.IsTrue(weeks.RootElement[0].GetProperty("saved").GetBoolean());
            Assert.AreEqual(0, weeks.RootElement[0].GetProperty("restHours").GetInt32());
            Assert.AreEqual(8, weeks.RootElement[2].GetProperty("restHours").GetInt32());
            Assert.AreEqual(2, weeks.RootElement[2].GetProperty("minimumStaff").GetInt32());
            using var slots = JsonDocument.Parse(WebUtility.HtmlDecode(Regex.Match(root, "data-slots=\"([^\"]*)\"").Groups[1].Value));
            Assert.AreEqual(63, slots.RootElement.GetArrayLength());
            AssertCurrentFormSlots(html);
            var cells = Regex.Matches(html, "<button[^>]*class=\"ac-gantt-cell\"[^>]*>").Select(match => match.Value).ToArray();
            Assert.AreEqual(21, cells.Length);
            Assert.IsTrue(cells.All(cell => int.Parse(Regex.Match(cell, "data-slot=\"([0-9]+)\"").Groups[1].Value) is >= 21 and < 42));
        }
        else
        {
            Assert.DoesNotContain("id=\"oncall-schedule\"", html);
            Assert.DoesNotContain("id=\"oncall-copy-next\"", html);
            Assert.DoesNotContain("class=\"ac-gantt-cell\"", html);
            Assert.DoesNotContain("ac-gantt-week\"", html);
        }

        var anchorPage = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/ScheduleDetails/{id}?startUtc={Uri.EscapeDataString(anchor.ToString("O"))}"));
        AssertEdgeFrame(anchorPage);
        if (administrator) AssertCurrentFormSlots(anchorPage);
    }

    private static (double Left, double Width) Geometry(string bar)
    {
        var match = Regex.Match(bar, "style=\"left: ([^%;]+)%; width: ([^%;]+)%\"");
        Assert.IsTrue(match.Success);
        return (double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 168 / 100,
            double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 168 / 100);
    }

    private static void AssertCurrentFormSlots(string html)
    {
        var select = Regex.Match(html, "<select id=\"oncall-edit-slot\"[^>]*>(.*?)</select>", RegexOptions.Singleline).Groups[1].Value;
        var values = Regex.Matches(select, "<option value=\"([0-9]+)\"").Select(match => int.Parse(match.Groups[1].Value)).ToArray();
        CollectionAssert.AreEqual(Enumerable.Range(21, 21).ToArray(), values);
        Assert.DoesNotContain("<optgroup label=\"上一周\"", select);
        Assert.DoesNotContain("<optgroup label=\"下一周\"", select);
    }

    private static void AssertEdgeFrame(string html)
    {
        var frame = html.IndexOf("class=\"ac-gantt-frame\"", StringComparison.Ordinal);
        var previous = html.IndexOf("data-oncall-edge=\"previous\" aria-expanded=\"false\"", StringComparison.Ordinal);
        var chart = html.IndexOf("id=\"oncall-gantt\"", StringComparison.Ordinal);
        var next = html.IndexOf("data-oncall-edge=\"next\" aria-expanded=\"false\"", StringComparison.Ordinal);
        var detail = html.IndexOf("id=\"oncall-shift-detail\"", StringComparison.Ordinal);
        Assert.IsTrue(frame >= 0 && frame < previous && previous < chart && chart < next && next < detail);
        var toggles = Regex.Matches(html, "<button[^>]*data-oncall-edge=\"(?:previous|next)\"[^>]*>").Select(match => match.Value).ToArray();
        Assert.AreEqual(2, toggles.Length);
        Assert.IsTrue(toggles.All(toggle => toggle.Contains("ac-gantt-edge-toggle")));
        Assert.Contains("展开前 36 小时", html);
        Assert.Contains("展开后 36 小时", html);
    }
}

using System.Text;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Aiursoft.AlertCenter.Views.Shared.Components.CurrentOnCall;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class MyOnCallTests
{
    [TestMethod]
    public async Task PersonalDataIncludesDepartedMembershipCoworkersPagingClippedTotalsAndCrossTeamReminders()
    {
        await using var db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        db.Users.AddRange(new User { Id = "me", UserName = "me", DisplayName = "Me" },
            new User { Id = "other", UserName = "other", DisplayName = "Coworker" });
        var now = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
        var a = new OnCallGroup { Name = "A", TimeZoneId = "Asia/Shanghai", MinimumRestHours = 12, MaximumContinuousHours = 24 };
        var b = new OnCallGroup { Name = "B" };
        OnCallShift Shift(OnCallGroup group, DateTime start, DateTime end, params string[] ids) => new()
        {
            Group = group, StartsAtUtc = start, EndsAtUtc = end,
            Members = ids.Select(id => new OnCallShiftMember { UserId = id }).ToList()
        };
        var active = Shift(a, now.AddDays(-31), now.AddHours(2), "me", "other");
        var next = Shift(b, now.AddHours(1), now.AddHours(5), "me");
        db.OnCallShifts.AddRange(active, next,
            Shift(a, now.AddHours(6), now.AddHours(8), "me"),
            Shift(a, now.AddDays(27), now.AddDays(29), "me"),
            Shift(a, now.AddDays(55), now.AddDays(57), "me"),
            Shift(a, now.AddDays(56), now.AddDays(57), "me"),
            Shift(a, now.AddDays(2), now.AddDays(3), "other"));
        for (var index = 0; index < 23; index++)
            db.OnCallShifts.Add(Shift(b, now.AddDays(-40 - index), now.AddDays(-40 - index).AddHours(1), "me"));
        await db.SaveChangesAsync();
        var service = new MyOnCallService(db, TestLocalization.Factory.Create(typeof(OnCallScheduleValidator)));
        var model = await service.LoadAsync("me", now, 1, default);
        Assert.AreEqual(active.Id, model.Current.Single().Id);
        Assert.AreEqual(next.Id, model.Next!.Id);
        Assert.AreEqual(5, model.Upcoming.Count);
        Assert.AreEqual("Coworker", model.Current.Single().Members.Single(member => member.UserId == "other").User.DisplayName);
        Assert.AreEqual(20, model.History.Count);
        Assert.IsTrue(model.HasMoreHistory);
        Assert.AreEqual(720, model.PastHours, 0.001);
        Assert.AreEqual(31, model.FutureHours, 0.001);
        Assert.IsTrue(model.Warnings.Any(warning => warning.Contains("overlapping")));
        Assert.IsTrue(model.Warnings.Any(warning => warning.Contains("insufficient rest")));
        Assert.IsTrue(model.Warnings.Any(warning => warning.Contains("continuous")));
        Assert.IsTrue(model.Warnings.Any(warning => warning.Contains("departed")));
        var second = await service.LoadAsync("me", now, 2, default);
        Assert.AreEqual(3, second.History.Count);
        Assert.IsFalse(second.HasMoreHistory);
        Assert.IsFalse(second.History.Any(shift => model.History.Any(first => first.Id == shift.Id)));
        var component = new CurrentOnCall(db)
        {
            ViewComponentContext = new ViewComponentContext { ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() } }
        };
        var result = (ViewViewComponentResult)await component.InvokeAsync();
        var teams = (List<CurrentOnCallTeam>)result.ViewData!.Model!;
        Assert.AreEqual(2, teams.Count);
    }

    [TestMethod]
    public void CalendarEscapesAndFoldsUtf8WithoutSplittingRunesAndKeepsStableUids()
    {
        var now = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
        var group = new OnCallGroup { Name = new string('中', 60) + ",;\\\r\nTeam" };
        var shift = new OnCallShift { Group = group, GroupId = group.Id, StartsAtUtc = now, EndsAtUtc = now.AddMinutes(95),
            Members = [new OnCallShiftMember { UserId = "a", User = new User { DisplayName = "A,B;C\\D\nE" } }] };
        var output = OnCallCalendar.Render([shift], "https://example.com/prefix/", now);
        var lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.IsTrue(lines.All(line => Encoding.UTF8.GetByteCount(line) <= 75));
        Assert.IsTrue(lines.Any(line => line.StartsWith(' ')));
        Assert.DoesNotContain("�", output);
        var unfolded = output.Replace("\r\n ", "");
        Assert.Contains($"UID:{shift.Id:D}@alertcenter.aiursoft.com\r\n", unfolded);
        Assert.Contains("DTSTART:20261109T000000Z\r\nDTEND:20261109T013500Z", unfolded);
        Assert.Contains("\\,\\;\\\\\\nTeam", unfolded);
        Assert.Contains($"DESCRIPTION:A\\,B\\;C\\\\D\\nE\\nhttps://example.com/prefix/OnCall/ScheduleDetails/{group.Id}", unfolded);
        Assert.Contains($"UID:{shift.Id:D}@alertcenter.aiursoft.com", OnCallCalendar.Render([shift], "https://elsewhere.com", now.AddDays(1)));
        Assert.AreEqual(64, OnCallCalendar.NewToken().Length);
    }
}

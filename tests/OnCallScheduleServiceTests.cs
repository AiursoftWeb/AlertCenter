using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallScheduleServiceTests
{
    private SqliteContext db = null!;
    private OnCallScheduleService service = null!;
    private OnCallRule rule = null!;
    private OnCallGroup group = null!;
    private static readonly DateTime Anchor = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

    [TestInitialize]
    public async Task Setup()
    {
        db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        db.Users.AddRange(Enumerable.Range(0, 4).Select(index => new User { Id = $"u{index}", UserName = $"staff{index}", DisplayName = $"Staff {index}" }));
        rule = new OnCallRule { Name = "Three shifts" };
        group = new OnCallGroup
        {
            Name = "Platform", Rule = rule, AnchorUtc = Anchor,
            Members = Enumerable.Range(0, 4).Select(index => new OnCallGroupMember { UserId = $"u{index}", RotationOrder = index }).ToList()
        };
        db.OnCallGroups.Add(group);
        await db.SaveChangesAsync();
        service = new OnCallScheduleService(db, new OnCallScheduleGenerator());
    }

    [TestCleanup]
    public async Task Cleanup() => await db.DisposeAsync();

    private SaveOnCallScheduleRequest Request(DateTime? start = null) => new()
    {
        GroupId = group.Id, Version = group.ScheduleVersion, StartUtc = start ?? Anchor
    };

    private async Task<SaveOnCallScheduleRequest> EditRequest(DateTime? start = null)
    {
        var request = Request(start);
        var end = request.StartUtc.AddDays(7);
        request.Slots = await db.OnCallShifts.Where(shift => shift.GroupId == group.Id && shift.StartsAtUtc >= request.StartUtc && shift.StartsAtUtc < end)
            .OrderBy(shift => shift.StartsAtUtc).Select(shift => new OnCallSlotRequest
            {
                StartsAtUtc = shift.StartsAtUtc, UserIds = shift.Members.Select(member => member.UserId).ToList()
            }).ToListAsync();
        return request;
    }

    [TestMethod]
    public async Task SavesMultipleStaffAndManualChangesWithoutRegenerationOverwrite()
    {
        var originalVersion = group.ScheduleVersion;
        Assert.AreEqual(0, (await service.Save(Request(), true, default)).Count);
        Assert.AreEqual(21, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(28, await db.OnCallShiftMembers.CountAsync());
        Assert.AreNotEqual(originalVersion, group.ScheduleVersion);
        var request = await EditRequest();
        request.Slots[0].UserIds.Remove("u3");
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        db.ChangeTracker.Clear();
        group = await db.OnCallGroups.Include(item => item.Rule).Include(item => item.Members).SingleAsync();
        Assert.AreEqual(27, await db.OnCallShiftMembers.CountAsync());
        Assert.IsTrue((await service.Save(Request(), true, default)).Any(error => error.Contains("不会覆盖")));
        Assert.AreEqual(27, await db.OnCallShiftMembers.CountAsync());
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.Save(request, false, default));
    }

    [TestMethod]
    public async Task RejectsEmptyDuplicateAndRestViolationsAtomically()
    {
        await service.Save(Request(), true, default);
        var request = await EditRequest();
        var version = group.ScheduleVersion;
        request.Slots[0].UserIds.Clear();
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("空班")));
        request = await EditRequest();
        request.Slots[1].UserIds.Add("u0");
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("休息不足")));
        request = await EditRequest();
        request.Slots[0].UserIds.Add("u0");
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("重叠")));
        Assert.AreEqual(version, group.ScheduleVersion);
        Assert.AreEqual(28, await db.OnCallShiftMembers.CountAsync());
    }

    [TestMethod]
    public async Task ChecksBothAdjacentWeeksAndOtherTeams()
    {
        await service.Save(Request(), true, default);
        Assert.AreEqual(0, (await service.Save(Request(Anchor.AddDays(7)), true, default)).Count);
        var request = await EditRequest();
        request.Slots[^1].UserIds.Add("u0");
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("休息不足")));
        request = await EditRequest(Anchor.AddDays(7));
        request.Slots[0].UserIds.Add("u2");
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("休息不足")));

        var other = new OnCallGroup { Rule = rule, Name = "Other", AnchorUtc = Anchor };
        db.OnCallGroups.Add(other);
        db.OnCallShifts.Add(new OnCallShift
        {
            Group = other, StartsAtUtc = Anchor, EndsAtUtc = Anchor.AddHours(8),
            Members = [new OnCallShiftMember { UserId = "u0" }]
        });
        await db.SaveChangesAsync();
        Assert.IsTrue((await service.Save(await EditRequest(), false, default)).Any(error => error.Contains("重叠")));
    }

    [TestMethod]
    public async Task CanCreateCompleteScheduleManuallyWithoutAutomaticGeneration()
    {
        var request = Request();
        request.Slots = Enumerable.Range(0, 21).Select(index => new OnCallSlotRequest
        {
            StartsAtUtc = Anchor.AddHours(index * 8), UserIds = [$"u{index % 3}"]
        }).ToList();
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        Assert.AreEqual(21, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(21, await db.OnCallShiftMembers.CountAsync());
    }

    [TestMethod]
    public async Task ManualScheduleWithoutTemplateUsesDefaultRestAndDoesNotRequireThreeMembers()
    {
        group.Rule = null;
        group.RuleId = null;
        await db.SaveChangesAsync();
        var request = Request();
        request.Slots = Enumerable.Range(0, 21).Select(index => new OnCallSlotRequest
        {
            StartsAtUtc = Anchor.AddHours(index * 8), UserIds = [$"u{index % 3}"]
        }).ToList();
        Assert.IsTrue((await service.Save(Request(), true, default)).Any(error => error.Contains("绑定排班规则")));
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        Assert.IsTrue(await db.OnCallShifts.AllAsync(shift => shift.RestHours == 16));

        db.OnCallGroupMembers.RemoveRange(group.Members.Where(member => member.UserId != "u0"));
        await db.SaveChangesAsync();
        Assert.AreEqual(1, await db.OnCallGroupMembers.CountAsync());
        Assert.AreEqual(0, (await service.Save(await EditRequest(), false, default)).Count);
        Assert.AreEqual(21, await db.OnCallShiftMembers.CountAsync());
    }

    [TestMethod]
    public async Task ManualCreationBelowGenerationMinimumUsesTemplateRestRatherThanMinimumStaff()
    {
        rule.RestHours = 0;
        db.OnCallGroupMembers.RemoveRange(group.Members.Where(member => member.UserId != "u0"));
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Save(Request(), true, default));
        var request = Request();
        request.Slots = Enumerable.Range(0, 21).Select(index => new OnCallSlotRequest
        {
            StartsAtUtc = Anchor.AddHours(index * 8), UserIds = ["u0"]
        }).ToList();
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        Assert.AreEqual(21, await db.OnCallShiftMembers.CountAsync());
        Assert.IsTrue(await db.OnCallShifts.AllAsync(shift => shift.RestHours == 0));
    }

    [TestMethod]
    public async Task ManualCreationWithoutTemplateStillRequiresDefaultSixteenHoursRest()
    {
        group.Rule = null;
        group.RuleId = null;
        db.OnCallGroupMembers.RemoveRange(group.Members.Where(member => member.UserId != "u0"));
        await db.SaveChangesAsync();
        var request = Request();
        request.Slots = Enumerable.Range(0, 21).Select(index => new OnCallSlotRequest
        {
            StartsAtUtc = Anchor.AddHours(index * 8), UserIds = ["u0"]
        }).ToList();
        var version = group.ScheduleVersion;
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("休息不足")));
        Assert.AreEqual(0, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(version, group.ScheduleVersion);
    }

    [TestMethod]
    public async Task DepartedStaffCanOnlyBeRetainedInTheirOriginalSavedSlots()
    {
        Assert.AreEqual(0, (await service.Save(Request(), true, default)).Count);
        db.OnCallGroupMembers.Remove(group.Members.Single(member => member.UserId == "u3"));
        await db.SaveChangesAsync();
        Assert.AreEqual(0, (await service.Save(await EditRequest(), false, default)).Count);
        var request = await EditRequest();
        request.Slots[1].UserIds.Add("u3");
        var version = group.ScheduleVersion;
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("退出团队")));
        request = await EditRequest();
        request.Slots[0].UserIds.Remove("u3");
        request.Slots[1].UserIds.Add("u3");
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("退出团队")));
        Assert.AreEqual(version, group.ScheduleVersion);
        Assert.AreEqual(28, await db.OnCallShiftMembers.CountAsync());
        request = await EditRequest();
        request.Slots[0].UserIds.Remove("u3");
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        request = await EditRequest();
        request.Slots[0].UserIds.Add("u3");
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("退出团队")));
        Assert.AreEqual(27, await db.OnCallShiftMembers.CountAsync());
    }

    [TestMethod]
    public async Task EditingSavedScheduleUsesRestSnapshotInsteadOfChangedTemplate()
    {
        await service.Save(Request(), true, default);
        rule.RestHours = 168;
        rule.BalanceShiftTimes = true;
        await db.SaveChangesAsync();
        var request = await EditRequest();
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        Assert.IsTrue(await db.OnCallShifts.AllAsync(shift => shift.RestHours == 16));
        Assert.AreEqual(28, await db.OnCallShiftMembers.CountAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CrossTeamRestUsesHistoricalSnapshotEvenIfTemplateIsUnboundOrEdited(bool unbind)
    {
        rule.RestHours = 0;
        var otherRule = new OnCallRule { Name = "Old policy", RestHours = 48 };
        var other = new OnCallGroup { Name = "Other", Rule = otherRule, AnchorUtc = Anchor };
        db.OnCallShifts.Add(new OnCallShift
        {
            Group = other, StartsAtUtc = Anchor.AddHours(-32), EndsAtUtc = Anchor.AddHours(-24), RestHours = 48,
            Members = [new OnCallShiftMember { UserId = "u0" }]
        });
        await db.SaveChangesAsync();
        if (unbind)
        {
            other.Rule = null;
            other.RuleId = null;
            await db.SaveChangesAsync();
            db.OnCallRules.Remove(otherRule);
        }
        else otherRule.RestHours = 0;
        await db.SaveChangesAsync();
        var request = Request();
        request.Slots = Enumerable.Range(0, 21).Select(index => new OnCallSlotRequest
        {
            StartsAtUtc = Anchor.AddHours(index * 8), UserIds = [$"u{index % 3}"]
        }).ToList();
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("休息不足")));
        Assert.AreEqual(1, await db.OnCallShifts.CountAsync());
        var existing = await service.Existing(group.Id, ["u0"], Anchor, default);
        Assert.AreEqual(48, existing.Single().RestHours);
        request.Slots[0].UserIds = ["u3"];
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        Assert.AreEqual(22, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(48, (await db.OnCallShifts.SingleAsync(shift => shift.GroupId == other.Id)).RestHours);
    }

    [TestMethod]
    public void WeekStartUsesTeamAnchorAndRejectsNonWeeklyOrLocalBoundaries()
    {
        Assert.AreEqual(Anchor.AddDays(7), OnCallScheduleService.WeekStart(group, Anchor.AddDays(7)));
        Assert.Throws<ArgumentException>(() => OnCallScheduleService.WeekStart(group, Anchor.AddHours(8)));
        Assert.Throws<ArgumentException>(() => OnCallScheduleService.WeekStart(group, Anchor.AddDays(-7)));
        Assert.Throws<ArgumentException>(() => OnCallScheduleService.WeekStart(group, DateTime.SpecifyKind(Anchor, DateTimeKind.Local)));
        var other = new OnCallGroup { AnchorUtc = Anchor.AddHours(4), Rule = rule };
        Assert.AreEqual(Anchor.AddHours(4), OnCallScheduleService.WeekStart(other, Anchor.AddHours(4)));
    }

    [TestMethod]
    public async Task CanSwapMultipleShiftsInOneSaveWithoutEnforcingBalancePreferences()
    {
        await service.Save(Request(), true, default);
        var request = await EditRequest();
        foreach (var slot in request.Slots)
            slot.UserIds = slot.UserIds.Select(id => id == "u0" ? "u1" : id == "u1" ? "u0" : id).ToList();
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        var saved = await EditRequest();
        Assert.IsTrue(saved.Slots[0].UserIds.Contains("u1"));
        Assert.IsFalse(saved.Slots[0].UserIds.Contains("u0"));
    }
}

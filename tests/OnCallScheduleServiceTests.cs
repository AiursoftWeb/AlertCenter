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
    private static readonly DateTime Anchor = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

    [TestInitialize]
    public async Task Setup()
    {
        db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        db.Users.AddRange(Enumerable.Range(0, 4).Select(index => new User { Id = $"u{index}", UserName = $"staff{index}", DisplayName = $"Staff {index}" }));
        rule = new OnCallRule
        {
            Group = new OnCallGroup { Name = "Platform" }, Name = "Three shifts", AnchorUtc = Anchor,
            Members = Enumerable.Range(0, 4).Select(index => new OnCallRuleMember { UserId = $"u{index}", RotationOrder = index }).ToList()
        };
        db.OnCallRules.Add(rule);
        await db.SaveChangesAsync();
        service = new OnCallScheduleService(db, new OnCallScheduleGenerator());
    }

    [TestCleanup]
    public async Task Cleanup() => await db.DisposeAsync();

    private SaveOnCallScheduleRequest Request(DateTime? start = null) => new()
    {
        RuleId = rule.Id, Version = rule.ScheduleVersion, StartUtc = start ?? Anchor
    };

    private async Task<SaveOnCallScheduleRequest> EditRequest(DateTime? start = null)
    {
        var request = Request(start);
        var end = request.StartUtc.AddDays(7);
        request.Slots = await db.OnCallShifts.Where(shift => shift.RuleId == rule.Id && shift.StartsAtUtc >= request.StartUtc && shift.StartsAtUtc < end)
            .OrderBy(shift => shift.StartsAtUtc).Select(shift => new OnCallSlotRequest
            {
                StartsAtUtc = shift.StartsAtUtc, UserIds = shift.Members.Select(member => member.UserId).ToList()
            }).ToListAsync();
        return request;
    }

    [TestMethod]
    public async Task SavesMultipleStaffAndManualChangesWithoutRegenerationOverwrite()
    {
        var originalVersion = rule.ScheduleVersion;
        Assert.AreEqual(0, (await service.Save(Request(), true, default)).Count);
        Assert.AreEqual(21, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(28, await db.OnCallShiftMembers.CountAsync());
        Assert.AreNotEqual(originalVersion, rule.ScheduleVersion);
        var request = await EditRequest();
        request.Slots[0].UserIds.Remove("u3");
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        db.ChangeTracker.Clear();
        rule = await db.OnCallRules.Include(item => item.Members).SingleAsync();
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
        var version = rule.ScheduleVersion;
        request.Slots[0].UserIds.Clear();
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("空班")));
        request = await EditRequest();
        request.Slots[1].UserIds.Add("u0");
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("休息不足")));
        request = await EditRequest();
        request.Slots[0].UserIds.Add("u0");
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("重叠")));
        Assert.AreEqual(version, rule.ScheduleVersion);
        Assert.AreEqual(28, await db.OnCallShiftMembers.CountAsync());
    }

    [TestMethod]
    public async Task ChecksBothAdjacentWeeksAndOtherRules()
    {
        await service.Save(Request(), true, default);
        Assert.AreEqual(0, (await service.Save(Request(Anchor.AddDays(7)), true, default)).Count);
        var request = await EditRequest();
        request.Slots[^1].UserIds.Add("u0");
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("休息不足")));
        request = await EditRequest(Anchor.AddDays(7));
        request.Slots[0].UserIds.Add("u2");
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("休息不足")));

        var other = new OnCallRule { GroupId = rule.GroupId, Name = "Other", AnchorUtc = Anchor };
        db.OnCallRules.Add(other);
        db.OnCallShifts.Add(new OnCallShift
        {
            Rule = other, StartsAtUtc = Anchor, EndsAtUtc = Anchor.AddHours(8),
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

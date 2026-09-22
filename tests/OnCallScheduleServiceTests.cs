using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallScheduleServiceTests
{
    private SqliteContext _db = null!;
    private OnCallScheduleService _service = null!;
    private readonly DateTime _coverageStartUtc = new(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
    private readonly DateTime _coverageEndUtc = new(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);

    [TestInitialize]
    public async Task Initialize()
    {
        _db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.MigrateAsync();
        _service = new OnCallScheduleService(_db);
    }

    [TestCleanup]
    public async Task Cleanup() => await _db.DisposeAsync();

    [TestMethod]
    public void ValidationRequiresContinuousCoverageAndSixteenHoursOfRest()
    {
        var groupId = Guid.NewGuid();
        var valid = OnCallScheduleService.Validate(groupId,
        [
            Shift(groupId, "one", 0, 8), Shift(groupId, "two", 8, 16), Shift(groupId, "three", 16, 24)
        ], [], ["one", "two", "three"], _coverageStartUtc, _coverageEndUtc);
        Assert.IsTrue(valid.IsValid);

        var gap = OnCallScheduleService.Validate(groupId,
        [
            Shift(groupId, "one", 0, 8), Shift(groupId, "two", 9, 17), Shift(groupId, "three", 17, 24)
        ], [], ["one", "two", "three"], _coverageStartUtc, _coverageEndUtc);
        Assert.IsFalse(gap.IsValid);
        StringAssert.Contains(gap.Errors.Single(), "持续覆盖");

        var shortRest = OnCallScheduleService.Validate(groupId,
        [
            Shift(groupId, "one", 0, 8), Shift(groupId, "two", 8, 12), Shift(groupId, "one", 12, 20), Shift(groupId, "three", 20, 24)
        ], [], ["one", "two", "three"], _coverageStartUtc, _coverageEndUtc);
        Assert.IsFalse(shortRest.IsValid);
        Assert.IsTrue(shortRest.Errors.Any(error => error.Contains("16 小时")));
    }

    [TestMethod]
    public void ValidationRejectsOverlapsLongShiftsAndInvalidOverrides()
    {
        var groupId = Guid.NewGuid();
        var first = Shift(groupId, "one", 0, 8);
        var second = Shift(groupId, "two", 7, 15);
        var overlong = Shift(groupId, "two", 15, 24);
        var invalidOverride = new OnCallShiftOverride
        {
            GroupId = groupId,
            OriginalShiftId = first.Id,
            CoveringUserId = "outsider",
            StartsAtUtc = first.StartsAtUtc.AddHours(-1),
            EndsAtUtc = first.EndsAtUtc,
            Reason = "Coverage"
        };
        var result = OnCallScheduleService.Validate(groupId, [first, second, overlong], [invalidOverride], ["one", "two"],
            _coverageStartUtc, _coverageEndUtc);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(error => error.Contains("重叠")));
        Assert.IsTrue(result.Errors.Any(error => error.Contains("超过 8 小时")));
        Assert.IsTrue(result.Errors.Any(error => error.Contains("代班工程师")));
        Assert.IsTrue(result.Errors.Any(error => error.Contains("完全位于原班次")));
    }

    [TestMethod]
    public async Task OCEQueryUsesOverrideAndTreatsEndAsExclusive()
    {
        var group = await AddGroupAsync();
        await AddUserAsync("primary", "Primary");
        await AddUserAsync("cover", "Cover");
        var shift = Shift(group.Id, "primary", 0, 8);
        _db.OnCallShifts.Add(shift);
        _db.OnCallShiftOverrides.Add(new OnCallShiftOverride
        {
            GroupId = group.Id,
            OriginalShiftId = shift.Id,
            CoveringUserId = "cover",
            StartsAtUtc = _coverageStartUtc.AddHours(2),
            EndsAtUtc = _coverageStartUtc.AddHours(4),
            Reason = "Approved leave"
        });
        await _db.SaveChangesAsync();

        var before = await _service.GetOnCallEngineerAsync(group.Id, _coverageStartUtc.AddHours(1));
        var during = await _service.GetOnCallEngineerAsync(group.Id, _coverageStartUtc.AddHours(2));
        var after = await _service.GetOnCallEngineerAsync(group.Id, _coverageStartUtc.AddHours(4));
        var atEnd = await _service.GetOnCallEngineerAsync(group.Id, _coverageStartUtc.AddHours(8));

        Assert.AreEqual("primary", before?.UserId);
        Assert.AreEqual("cover", during?.UserId);
        Assert.IsTrue(during?.IsOverride);
        Assert.AreEqual("primary", after?.UserId);
        Assert.IsNull(atEnd);
    }

    [TestMethod]
    public async Task DailyRotationUsesGroupTimeZoneAndRejectsInvalidTemplate()
    {
        var group = await AddGroupAsync("America/New_York");
        await AddUserAsync("first", "First");
        await AddUserAsync("second", "Second");
        _db.OnCallShiftTemplates.Add(new OnCallShiftTemplate
        {
            GroupId = group.Id,
            Name = "Morning",
            StartMinuteOfDay = 8 * 60,
            DurationMinutes = 8 * 60
        });
        await _db.SaveChangesAsync();
        var template = await _db.OnCallShiftTemplates.SingleAsync();
        _db.OnCallShiftTemplateMembers.AddRange(
            new OnCallShiftTemplateMember { TemplateId = template.Id, UserId = "first", RotationOrder = 0 },
            new OnCallShiftTemplateMember { TemplateId = template.Id, UserId = "second", RotationOrder = 1 });
        await _db.SaveChangesAsync();

        var shifts = await _service.GenerateDailyRotationAsync(group.Id, new DateOnly(2026, 1, 15), new DateOnly(2026, 1, 15));
        var shift = shifts.Single();
        Assert.AreEqual(new DateTime(2026, 1, 15, 13, 0, 0, DateTimeKind.Utc), shift.StartsAtUtc);
        Assert.AreEqual(new DateTime(2026, 1, 15, 21, 0, 0, DateTimeKind.Utc), shift.EndsAtUtc);

        template.DurationMinutes = 481;
        _db.OnCallShiftTemplates.Update(template);
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.GenerateDailyRotationAsync(group.Id,
            new DateOnly(2026, 1, 16), new DateOnly(2026, 1, 16)));
    }

    [TestMethod]
    public async Task PublishValidatesBeforePersistingAndStoresUtcSchedule()
    {
        var group = await AddGroupAsync();
        await AddUserAsync("one", "One");
        await AddUserAsync("two", "Two");
        await AddUserAsync("three", "Three");
        var invalid = await _service.PublishAsync(group.Id,
            [Shift(group.Id, "one", 0, 8), Shift(group.Id, "two", 9, 17), Shift(group.Id, "three", 17, 24)],
            _coverageStartUtc, _coverageEndUtc);
        Assert.IsFalse(invalid.IsValid);
        Assert.AreEqual(0, await _db.OnCallShifts.CountAsync());

        var valid = await _service.PublishAsync(group.Id,
            [Shift(group.Id, "one", 0, 8), Shift(group.Id, "two", 8, 16), Shift(group.Id, "three", 16, 24)],
            _coverageStartUtc, _coverageEndUtc);
        Assert.IsTrue(valid.IsValid);
        Assert.AreEqual(3, await _db.OnCallShifts.CountAsync());
    }

    [TestMethod]
    public async Task PublishRejectsRestConflictsWithAdjacentPublishedShifts()
    {
        var group = await AddGroupAsync();
        await AddUserAsync("one", "One");
        await AddUserAsync("two", "Two");
        await AddUserAsync("three", "Three");
        _db.OnCallShifts.AddRange(
            Shift(group.Id, "one", 0, 8), Shift(group.Id, "two", 8, 16), Shift(group.Id, "one", 16, 24));
        await _db.SaveChangesAsync();

        var nextDayStart = _coverageEndUtc;
        var result = await _service.PublishAsync(group.Id,
        [
            new OnCallShift
            {
                GroupId = group.Id, UserId = "one", StartsAtUtc = nextDayStart,
                EndsAtUtc = nextDayStart.AddHours(8)
            },
            new OnCallShift
            {
                GroupId = group.Id, UserId = "two", StartsAtUtc = nextDayStart.AddHours(8),
                EndsAtUtc = nextDayStart.AddHours(16)
            },
            new OnCallShift
            {
                GroupId = group.Id, UserId = "three", StartsAtUtc = nextDayStart.AddHours(16),
                EndsAtUtc = nextDayStart.AddHours(24)
            }
        ], nextDayStart, nextDayStart.AddDays(1));

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(error => error.Contains("16 小时")));
        Assert.AreEqual(3, await _db.OnCallShifts.CountAsync());
    }

    private static OnCallShift Shift(Guid groupId, string userId, int startsAtHour, int endsAtHour) => new()
    {
        GroupId = groupId,
        UserId = userId,
        StartsAtUtc = new DateTime(2026, 9, 22, startsAtHour, 0, 0, DateTimeKind.Utc),
        EndsAtUtc = endsAtHour == 24
            ? new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc)
            : new DateTime(2026, 9, 22, endsAtHour, 0, 0, DateTimeKind.Utc)
    };

    private async Task<OnCallGroup> AddGroupAsync(string timeZoneId = "UTC")
    {
        var group = new OnCallGroup { Name = $"Group {Guid.NewGuid():N}", TimeZoneId = timeZoneId };
        _db.OnCallGroups.Add(group);
        await _db.SaveChangesAsync();
        return group;
    }

    private async Task AddUserAsync(string id, string displayName)
    {
        _db.Users.Add(new User { Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant(), DisplayName = displayName });
        _db.OnCallGroupMembers.Add(new OnCallGroupMember
        {
            GroupId = await _db.OnCallGroups.Select(group => group.Id).SingleAsync(),
            UserId = id
        });
        await _db.SaveChangesAsync();
    }
}

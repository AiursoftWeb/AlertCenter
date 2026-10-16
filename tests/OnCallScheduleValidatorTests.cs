using Aiursoft.AlertCenter.Services;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallScheduleValidatorTests
{
    private static readonly DateTime Start = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    [DataRow(0, 0, 0, true)]
    [DataRow(0, 0, -1, false)]
    [DataRow(16, 0, 16 * 3600 - 1, false)]
    [DataRow(16, 0, 16 * 3600, true)]
    [DataRow(0, 16, 16 * 3600 - 1, false)]
    [DataRow(0, 16, 16 * 3600, true)]
    [DataRow(16, 24, 24 * 3600 - 1, false)]
    [DataRow(16, 24, 24 * 3600, true)]
    [DataRow(24, 16, 24 * 3600 - 1, false)]
    [DataRow(24, 16, 24 * 3600, true)]
    public void CanAssignUsesStricterRestOnEitherSide(int restHours, int savedRestHours, int gapSeconds, bool expected)
    {
        var previousEnd = Start.AddSeconds(-gapSeconds);
        var followingStart = Start.AddHours(8).AddSeconds(gapSeconds);
        var previous = new GeneratedOnCallShift("alice", previousEnd.AddHours(-8), previousEnd, savedRestHours);
        var following = new GeneratedOnCallShift("alice", followingStart, followingStart.AddHours(8), savedRestHours);

        Assert.AreEqual(expected, OnCallScheduleValidator.CanAssign("alice", Start, Start.AddHours(8), restHours, [previous]),
            "The previous shift's rest requirement must be respected.");
        Assert.AreEqual(expected, OnCallScheduleValidator.CanAssign("alice", Start, Start.AddHours(8), restHours, [following]),
            "The following shift's rest requirement must be respected.");
    }

    [TestMethod]
    [DataRow(0, 8)]
    [DataRow(1, 2)]
    [DataRow(-1, 9)]
    [DataRow(-1, 1)]
    [DataRow(7, 9)]
    public void CanAssignRejectsOverlapsEvenWithoutRest(int existingStartHour, int existingEndHour)
    {
        var existing = new GeneratedOnCallShift("alice", Start.AddHours(existingStartHour), Start.AddHours(existingEndHour));

        Assert.IsFalse(OnCallScheduleValidator.CanAssign("alice", Start, Start.AddHours(8), 0, [existing]));
    }

    [TestMethod]
    public void CanAssignIgnoresOtherEmployeesAndAllowsAnEmptyHistory()
    {
        Assert.IsTrue(OnCallScheduleValidator.CanAssign("alice", Start, Start.AddHours(8), 168, []));
        Assert.IsTrue(OnCallScheduleValidator.CanAssign("alice", Start, Start.AddHours(8), 168,
            [new GeneratedOnCallShift("bob", Start, Start.AddHours(8), 168)]));
    }

    [TestMethod]
    public void CanAssignChecksAllOfTheEmployeesSavedShifts()
    {
        var existing = new[]
        {
            new GeneratedOnCallShift("alice", Start.AddDays(-2), Start.AddDays(-2).AddHours(8)),
            new GeneratedOnCallShift("bob", Start, Start.AddHours(8)),
            new GeneratedOnCallShift("alice", Start.AddHours(16), Start.AddHours(24))
        };

        Assert.IsFalse(OnCallScheduleValidator.CanAssign("alice", Start, Start.AddHours(8), 16, existing));
    }

    [TestMethod]
    public void ValidateReportsEveryEmptySlotInChronologicalOrder()
    {
        var shifts = new[] { new GeneratedOnCallShift("alice", Start.AddHours(8), Start.AddHours(16)) };

        var errors = OnCallScheduleValidator.Validate(Start, 3, 16, shifts, [],
            new Dictionary<string, string> { ["alice"] = "Alice" });

        Assert.AreEqual(2, errors.Count);
        StringAssert.Contains(errors[0], "2026-09-28 00:00 UTC 出现空班");
        StringAssert.Contains(errors[1], "2026-09-28 16:00 UTC 出现空班");
    }

    [TestMethod]
    [DataRow(-8, 0)]
    [DataRow(8, 16)]
    [DataRow(1, 9)]
    [DataRow(0, 0)]
    [DataRow(0, 7)]
    [DataRow(0, 9)]
    public void ValidateRejectsOutOfWindowMisalignedOrNonEightHourShifts(int startHour, int endHour)
    {
        var shifts = new[] { new GeneratedOnCallShift("alice", Start.AddHours(startHour), Start.AddHours(endHour)) };

        var errors = OnCallScheduleValidator.Validate(Start, 1, 16, shifts, [],
            new Dictionary<string, string> { ["alice"] = "Alice" });

        Assert.Contains("Alice 的班次不在规定的 8 小时时段内。", errors);
    }

    [TestMethod]
    public void ValidateRejectsUnknownEmployeesAndDeduplicatesErrors()
    {
        var unknown = new GeneratedOnCallShift("outsider", Start, Start.AddHours(8));

        var errors = OnCallScheduleValidator.Validate(Start, 1, 16, [unknown, unknown], [],
            new Dictionary<string, string> { ["alice"] = "Alice" });

        CollectionAssert.AreEqual(new[] { "包含不属于当前团队的员工。" }, errors);
    }

    [TestMethod]
    public void ValidateRejectsDuplicateAssignmentsButAllowsDifferentEmployeesInTheSameSlot()
    {
        var alice = new GeneratedOnCallShift("alice", Start, Start.AddHours(8));
        var bob = alice with { UserId = "bob" };
        var members = new Dictionary<string, string> { ["alice"] = "Alice", ["bob"] = "Bob" };

        var errors = OnCallScheduleValidator.Validate(Start, 1, 16, [alice, alice], [], members);

        Assert.AreEqual(1, errors.Count);
        StringAssert.Contains(errors[0], "Alice");
        StringAssert.Contains(errors[0], "重叠或休息不足");
        Assert.AreEqual(0, OnCallScheduleValidator.Validate(Start, 1, 16, [alice, bob], [], members).Count);
    }

    [TestMethod]
    [DataRow(16, true)]
    [DataRow(17, false)]
    public void ValidateSortsDraftShiftsAndAppliesTheRequestedRestWithoutMutatingInputs(int restHours, bool valid)
    {
        var shifts = new[]
        {
            new GeneratedOnCallShift("alice", Start.AddHours(24), Start.AddHours(32)),
            new GeneratedOnCallShift("bob", Start.AddHours(8), Start.AddHours(16)),
            new GeneratedOnCallShift("carol", Start.AddHours(16), Start.AddHours(24)),
            new GeneratedOnCallShift("alice", Start, Start.AddHours(8), 168)
        };
        var history = new[] { new GeneratedOnCallShift("bob", Start.AddDays(-2), Start.AddDays(-2).AddHours(8), 24) };
        var draftSnapshot = shifts.ToArray();
        var historySnapshot = history.ToArray();
        var members = new Dictionary<string, string> { ["alice"] = "Alice", ["bob"] = "Bob", ["carol"] = "Carol" };

        var errors = OnCallScheduleValidator.Validate(Start, 4, restHours, shifts, history, members);

        Assert.AreEqual(valid ? 0 : 1, errors.Count, string.Join("; ", errors));
        if (!valid)
            StringAssert.Contains(errors[0], "Alice 在 2026-09-29 00:00 UTC 的班次与其他班次重叠或休息不足");
        CollectionAssert.AreEqual(draftSnapshot, shifts);
        CollectionAssert.AreEqual(historySnapshot, history);
    }

    [TestMethod]
    public void StaffingWarningsCountDistinctEmployeesAndIncludeEmptySlotsOnlyWithinTheWindow()
    {
        var shifts = new[]
        {
            new GeneratedOnCallShift("alice", Start.AddHours(16), Start.AddHours(24)),
            new GeneratedOnCallShift("bob", Start.AddHours(16), Start.AddHours(24)),
            new GeneratedOnCallShift("carol", Start.AddHours(16), Start.AddHours(24)),
            new GeneratedOnCallShift("alice", Start, Start.AddHours(8)),
            new GeneratedOnCallShift("alice", Start, Start.AddHours(8)),
            new GeneratedOnCallShift("alice", Start.AddHours(8), Start.AddHours(16)),
            new GeneratedOnCallShift("bob", Start.AddHours(8), Start.AddHours(16)),
            new GeneratedOnCallShift("alice", Start.AddHours(-8), Start)
        };

        var warnings = OnCallScheduleValidator.StaffingWarnings(Start, 4, 2, shifts);

        Assert.AreEqual(2, warnings.Count);
        StringAssert.Contains(warnings[0], "2026-09-28 00:00 UTC 仅安排 1 人，低于最低 2 人");
        StringAssert.Contains(warnings[1], "2026-09-29 00:00 UTC 仅安排 0 人，低于最低 2 人");
    }

    [TestMethod]
    public void StaffingWarningsAreEmptyWhenEverySlotMeetsTheMinimum()
    {
        var shifts = new[]
        {
            new GeneratedOnCallShift("alice", Start, Start.AddHours(8)),
            new GeneratedOnCallShift("bob", Start, Start.AddHours(8)),
            new GeneratedOnCallShift("carol", Start.AddHours(8), Start.AddHours(16)),
            new GeneratedOnCallShift("dave", Start.AddHours(8), Start.AddHours(16))
        };

        Assert.AreEqual(0, OnCallScheduleValidator.StaffingWarnings(Start, 2, 2, shifts).Count);
    }
}

using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallScheduleValidatorTests
{
    private static readonly Microsoft.Extensions.Localization.IStringLocalizer Localizer = TestLocalization.Factory.Create(typeof(OnCallScheduleValidator));
    private static readonly DateTime Start = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
    private static OnCallShift Shift(int start, int hours, params string[] ids) => new()
    {
        StartsAtUtc = Start.AddHours(start), EndsAtUtc = Start.AddHours(start + hours),
        Members = ids.Select(id => new OnCallShiftMember { UserId = id }).ToList()
    };

    [TestMethod]
    public void TimelineLocatesEveryReminderCategoryWithoutChangingShiftBoundaries()
    {
        var group = new OnCallGroup { MinimumRestHours = 16, MaximumContinuousHours = 24, MinimumShiftStaff = 2,
            Members = [new() { UserId = "a" }] };
        var shifts = new[] { Shift(1, 26, "a"), Shift(2, 2, "departed"), Shift(40, 1) };
        var warnings = OnCallScheduleValidator.TimelineWarnings(group, Start, Start.AddHours(48), shifts,
            [new("a", Start.AddHours(-5), Start.AddHours(-1))], Localizer);
        foreach (var category in new[] { "gap", "overlapping", "no on-duty", "insufficient rest", "continuous", "below", "departed" })
            Assert.IsTrue(warnings.Any(warning => warning.Message.Contains(category)), category);
        Assert.IsTrue(warnings.Any(warning => warning.StartsAtUtc == Start && warning.EndsAtUtc == Start.AddHours(1)));
        Assert.IsTrue(warnings.Any(warning => warning.StartsAtUtc == Start.AddHours(40) && warning.EndsAtUtc == Start.AddHours(41) && warning.Message.Contains("no on-duty")));
        Assert.AreEqual(Start.AddHours(27), shifts[0].EndsAtUtc);
    }

    [TestMethod]
    public void ValidationOnlyRejectsInvalidIntervalsAndUnknownEmployees()
    {
        var names = new Dictionary<string, string> { ["a"] = "Alice" };
        var arbitrary = new GeneratedOnCallShift("a", Start.AddMinutes(17), Start.AddDays(4));
        Assert.AreEqual(0, OnCallScheduleValidator.Validate(Start, 21, 168, [arbitrary, arbitrary], [arbitrary], names).Count);
        Assert.AreEqual(0, OnCallScheduleValidator.Validate(Start, 21, 168, [], [], names).Count);
        Assert.AreEqual(1, OnCallScheduleValidator.Validate(Start, 21, 0, [arbitrary with { EndsAtUtc = arbitrary.StartsAtUtc }], [], names).Count);
        var unknown = arbitrary with { UserId = "missing" };
        CollectionAssert.AreEqual(new[] { "Includes employees not belonging to the current team." },
            OnCallScheduleValidator.Validate(Start, 21, 0, [unknown, unknown], [], names));
    }

    [TestMethod]
    public void NullThresholdsDisableOptionalChecksButStillReportCoverageAndDepartures()
    {
        var group = new OnCallGroup { Members = [new() { UserId = "a" }] };
        var warnings = OnCallScheduleValidator.Warnings(group, Start, Start.AddDays(7),
            [Shift(1, 40, "a"), Shift(1, 2, "departed"), Shift(42, 1)], [], Localizer);
        foreach (var word in new[] { "gap", "overlapping", "departed", "no on-duty" })
            Assert.IsTrue(warnings.Any(warning => warning.Contains(word)), word);
        Assert.IsFalse(warnings.Any(warning => warning.Contains("rest") || warning.Contains("threshold")));
    }

    [TestMethod]
    public void ContinuousDutyMergesOverlapsAndTouchingShiftsAndChecksCrossTeamRest()
    {
        var group = new OnCallGroup { MinimumRestHours = 16, MaximumContinuousHours = 24, MinimumShiftStaff = 2,
            Members = [new() { UserId = "a" }] };
        var shifts = new[] { Shift(0, 12, "a"), Shift(12, 14, "a"), Shift(20, 2, "a") };
        var snapshot = shifts.Select(shift => (shift.StartsAtUtc, shift.EndsAtUtc)).ToArray();
        var external = new[] { new GeneratedOnCallShift("a", Start.AddHours(-9), Start.AddHours(-1)) };
        var warnings = OnCallScheduleValidator.Warnings(group, Start, Start.AddHours(26), shifts, external, Localizer);
        Assert.IsTrue(warnings.Any(warning => warning.Contains("continuous")));
        Assert.IsTrue(warnings.Any(warning => warning.Contains("insufficient rest")));
        Assert.IsTrue(warnings.Any(warning => warning.Contains("below")));
        CollectionAssert.AreEqual(snapshot, shifts.Select(shift => (shift.StartsAtUtc, shift.EndsAtUtc)).ToArray());
        Assert.AreEqual(Start.AddHours(-1), external[0].EndsAtUtc);
    }

    [TestMethod]
    public void RestWarningAlsoChecksTheFollowingShiftOutsideTheEditedRange()
    {
        var group = new OnCallGroup { MinimumRestHours = 16, Members = [new() { UserId = "a" }] };
        var warnings = OnCallScheduleValidator.Warnings(group, Start, Start.AddHours(8), [Shift(0, 8, "a")],
            [new("a", Start.AddHours(16), Start.AddHours(24))], Localizer);
        Assert.AreEqual(1, warnings.Count);
        Assert.Contains("insufficient rest", warnings[0]);
        Assert.Contains("a:", warnings[0]);
    }

    [TestMethod]
    public void ThresholdBoundariesAndHalfOpenCoverageDoNotWarn()
    {
        var group = new OnCallGroup { MinimumRestHours = 16, MaximumContinuousHours = 8, MinimumShiftStaff = 1,
            Members = [new() { UserId = "a" }, new() { UserId = "b" }] };
        var warnings = OnCallScheduleValidator.Warnings(group, Start, Start.AddHours(16),
            [Shift(0, 8, "a"), Shift(8, 8, "b")],
            [new("a", Start.AddHours(-24), Start.AddHours(-16))], Localizer);
        Assert.AreEqual(0, warnings.Count, string.Join("; ", warnings));
    }
}

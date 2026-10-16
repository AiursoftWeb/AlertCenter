using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallScheduleGeneratorTests
{
    private readonly OnCallScheduleGenerator _generator = new(localizer:TestLocalization.For<OnCallScheduleGenerator>());
    private static readonly DateTime Anchor = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void GeneratesContinuousRotationWithDefaultRest()
    {
        var rule = new OnCallRule();
        var shifts = _generator.Generate(rule, Anchor, ["alice", "bob", "carol"], Anchor, 21);
        Assert.AreEqual(21, shifts.Count);
        for (var i = 0; i < shifts.Count; i++)
        {
            Assert.AreEqual(Anchor.AddHours(i * 8), shifts[i].StartsAtUtc);
            Assert.AreEqual(shifts[i].StartsAtUtc.AddHours(8), shifts[i].EndsAtUtc);
            Assert.AreEqual(new[] { "alice", "bob", "carol" }[i % 3], shifts[i].UserId);
        }
    }

    [TestMethod]
    public void SharedTemplateUsesEachTeamsExplicitAnchorAndOrderedMembers()
    {
        var rule = new OnCallRule();
        var first = _generator.Generate(rule, Anchor, ["a", "b", "c"], Anchor, 21);
        var otherAnchor = Anchor.AddHours(4);
        var second = _generator.Generate(rule, otherAnchor, ["c", "b", "a"], otherAnchor, 21);
        Assert.AreEqual(Anchor, first[0].StartsAtUtc);
        Assert.AreEqual(otherAnchor, second[0].StartsAtUtc);
        Assert.AreEqual("a", first[0].UserId);
        Assert.AreEqual("c", second[0].UserId);
        Assert.IsTrue(first.Concat(second).All(shift => shift.RestHours == 16));
        Assert.Throws<ArgumentException>(() => _generator.Generate(rule, otherAnchor, ["a", "b", "c"], Anchor, 21));
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(10)]
    [DataRow(200)]
    public void DistributesExtraStaffAcrossThreeShifts(int staffCount)
    {
        var rule = new OnCallRule();
        var users = Enumerable.Range(0, staffCount).Select(i => $"staff-{i}").ToArray();
        var shifts = _generator.Generate(rule, Anchor, users, Anchor, 21);
        Assert.AreEqual(21, shifts.Select(shift => shift.StartsAtUtc).Distinct().Count());
        Assert.AreEqual(staffCount * 7, shifts.Count);
        Assert.IsTrue(shifts.GroupBy(shift => shift.UserId).All(group => group.Count() == 7));
        Assert.IsTrue(shifts.GroupBy(shift => shift.StartsAtUtc).All(group => group.Count() >= staffCount / 3 && group.Count() <= (staffCount + 2) / 3));
        Assert.AreEqual(0, OnCallScheduleValidator.Validate(Anchor, 21, 16, shifts, [], users.ToDictionary(id => id)).Count);
    }

    [TestMethod]
    public void RejectsTooFewPeopleAndInvalidBoundaries()
    {
        var rule = new OnCallRule();
        var error = Assert.Throws<InvalidOperationException>(() => _generator.Generate(rule, Anchor, ["a", "b"], Anchor, 21));
        StringAssert.Contains(error.Message, "requires at least 3 staff");
        Assert.Throws<ArgumentException>(() => _generator.Generate(rule, Anchor, ["a", "b", "c"], Anchor.AddHours(1), 1));
        Assert.Throws<ArgumentException>(() => _generator.Generate(rule, Anchor, ["a", "a", "c"], Anchor, 1));
        Assert.Throws<ArgumentException>(() => _generator.Generate(new OnCallRule { ShiftHours = 0 }, Anchor, ["a"], Anchor, 1));
        Assert.Throws<ArgumentException>(() => _generator.Generate(rule, DateTime.SpecifyKind(Anchor, DateTimeKind.Local), ["a", "b", "c"], Anchor, 1));
    }

    [TestMethod]
    public void GenerationRequiresEnoughPeopleForTemplatesLongerRest()
    {
        var rule = new OnCallRule { RestHours = 48 };
        var users = Enumerable.Range(0, 7).Select(index => $"u{index}").ToArray();
        Assert.Throws<InvalidOperationException>(() => _generator.Generate(rule, Anchor, users[..6], Anchor, 21));
        var shifts = _generator.Generate(rule, Anchor, users, Anchor, 21);
        Assert.AreEqual(21, shifts.Select(shift => shift.StartsAtUtc).Distinct().Count());
        Assert.AreEqual(0, OnCallScheduleValidator.Validate(Anchor, 21, 48, shifts, [], users.ToDictionary(id => id)).Count);
    }

    [TestMethod]
    public void RejectsSinglePersonEvenWithZeroRestAndNonEightHourShifts()
    {
        Assert.Throws<InvalidOperationException>(() => _generator.Generate(new OnCallRule { RestHours = 0 }, Anchor, ["a"], Anchor, 21));
        Assert.Throws<ArgumentException>(() => _generator.Generate(new OnCallRule { ShiftHours = 12 }, Anchor, ["a", "b", "c"], Anchor, 21));
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public void OptionalRotationPreservesCoverageAndRestAcrossWeeks(int staffCount)
    {
        var rule = new OnCallRule { BalanceShiftTimes = true };
        var users = Enumerable.Range(0, staffCount).Select(i => $"staff-{i}").ToArray();
        var history = new List<GeneratedOnCallShift>();
        for (var week = 0; week < 8; week++)
        {
            var start = Anchor.AddDays(week * 7);
            var shifts = _generator.Generate(rule, Anchor, users, start, 21, history.Where(shift => shift.EndsAtUtc > start.AddDays(-28)).ToList());
            var errors = OnCallScheduleValidator.Validate(start, 21, 16, shifts, history, users.ToDictionary(id => id));
            Assert.AreEqual(0, errors.Count, string.Join("; ", errors));
            history.AddRange(shifts);
        }
        foreach (var user in users)
        {
            var types = history.Where(shift => shift.UserId == user).Select(shift => shift.StartsAtUtc.Hour).Distinct().Count();
            Assert.IsTrue(staffCount == 3 ? types == 1 : types > 1, $"{user}: {types} shift types");
        }
        var counts = history.GroupBy(shift => shift.UserId).Select(group => group.Count()).ToList();
        Assert.IsTrue(counts.Max() - counts.Min() <= 3, string.Join(",", counts));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MinimumTwoReservesTwoPeoplePerSlotAcrossWeeks(bool balanceShiftTimes)
    {
        var rule = new OnCallRule { MinimumStaff = 2, BalanceShiftTimes = balanceShiftTimes };
        var users = new[] { "a", "b", "c", "d", "e", "f" };
        var history = new List<GeneratedOnCallShift>();
        for (var week = 0; week < 3; week++)
        {
            var start = Anchor.AddDays(week * 7);
            var shifts = _generator.Generate(rule, Anchor, users, start, 21, history);
            Assert.AreEqual(42, shifts.Count);
            Assert.IsTrue(shifts.GroupBy(shift => shift.StartsAtUtc).All(group => group.Count() == 2));
            Assert.AreEqual(0, OnCallScheduleValidator.Validate(start, 21, 16, shifts, history, users.ToDictionary(id => id)).Count);
            if (!balanceShiftTimes)
                for (var slot = 0; slot < 21; slot++)
                    CollectionAssert.AreEqual(users.Skip(slot % 3 * 2).Take(2).ToArray(),
                        shifts.Where(shift => shift.StartsAtUtc == start.AddHours(slot * 8)).Select(shift => shift.UserId).ToArray());
            history.AddRange(shifts);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TargetThreeWithSixPeopleFallsBackToMinimumTwo(bool balanceShiftTimes)
    {
        var rule = new OnCallRule { MinimumStaff = 2, TargetStaff = 3, BalanceShiftTimes = balanceShiftTimes };
        var users = new[] { "a", "b", "c", "d", "e", "f" };
        var shifts = _generator.Generate(rule, Anchor, users, Anchor, 42);
        Assert.IsTrue(shifts.GroupBy(shift => shift.StartsAtUtc).All(group => group.Count() == 2));
        Assert.AreEqual(0, OnCallScheduleValidator.Validate(Anchor, 42, 16, shifts, [], users.ToDictionary(id => id)).Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExplicitTargetOneDoesNotScheduleExcessPeople(bool balanceShiftTimes)
    {
        var rule = new OnCallRule { TargetStaff = 1, BalanceShiftTimes = balanceShiftTimes };
        var users = Enumerable.Range(0, 12).Select(index => $"u{index}").ToArray();
        var shifts = _generator.Generate(rule, Anchor, users, Anchor, 42);
        Assert.AreEqual(42, shifts.Count);
        Assert.IsTrue(shifts.GroupBy(shift => shift.StartsAtUtc).All(group => group.Count() == 1));
        Assert.AreEqual(0, OnCallScheduleValidator.Validate(Anchor, 42, 16, shifts, [], users.ToDictionary(id => id)).Count);
    }

    [TestMethod]
    public void ExplicitTargetIsReachedWhenCoverageAllowsIt()
    {
        var users = Enumerable.Range(0, 9).Select(index => $"u{index}").ToArray();
        var shifts = _generator.Generate(new OnCallRule { MinimumStaff = 2, TargetStaff = 3 }, Anchor, users, Anchor, 21);
        Assert.AreEqual(63, shifts.Count);
        Assert.IsTrue(shifts.GroupBy(shift => shift.StartsAtUtc).All(group => group.Count() == 3));
        Assert.AreEqual(0, OnCallScheduleValidator.Validate(Anchor, 21, 16, shifts, [], users.ToDictionary(id => id)).Count);
    }

    [TestMethod]
    [DataRow(0, null)]
    [DataRow(-1, null)]
    [DataRow(201, null)]
    [DataRow(1, 0)]
    [DataRow(1, -1)]
    [DataRow(1, 201)]
    [DataRow(2, 1)]
    public void RejectsInvalidStaffing(int minimumStaff, int? targetStaff)
    {
        Assert.Throws<ArgumentException>(() => _generator.Generate(
            new OnCallRule { MinimumStaff = minimumStaff, TargetStaff = targetStaff }, Anchor,
            ["a", "b", "c", "d", "e", "f"], Anchor, 21));
    }

    [TestMethod]
    [DataRow(0, 6)]
    [DataRow(16, 6)]
    [DataRow(17, 8)]
    [DataRow(48, 14)]
    [DataRow(168, 44)]
    public void MinimumStaffScalesRequiredMembersWithRest(int restHours, int requiredMembers)
    {
        var rule = new OnCallRule { MinimumStaff = 2, TargetStaff = 2, RestHours = restHours };
        var users = Enumerable.Range(0, requiredMembers).Select(index => $"u{index}").ToArray();
        var error = Assert.Throws<InvalidOperationException>(() => _generator.Generate(rule, Anchor, users[..^1], Anchor, 21));
        StringAssert.Contains(error.Message, $"requires at least {requiredMembers} staff");
        var shifts = _generator.Generate(rule, Anchor, users, Anchor, 42);
        Assert.AreEqual(84, shifts.Count);
        Assert.AreEqual(0, OnCallScheduleValidator.Validate(Anchor, 42, restHours, shifts, [], users.ToDictionary(id => id)).Count);
        Assert.IsTrue(shifts.All(shift => shift.RestHours == restHours));
    }

    [TestMethod]
    public void StaffingHonorsExistingRestSnapshotsAndDoesNotMutateHistory()
    {
        var users = new[] { "a", "b", "c", "d", "e", "f", "g", "h" };
        var rule = new OnCallRule { MinimumStaff = 2, TargetStaff = 2, RestHours = 0 };
        var existing = new[]
        {
            new GeneratedOnCallShift("a", Anchor.AddHours(-8), Anchor, 24),
            new GeneratedOnCallShift("b", Anchor.AddHours(16), Anchor.AddHours(24), 16)
        };
        var snapshot = existing.ToArray();
        var shifts = _generator.Generate(rule, Anchor, users, Anchor, 21, existing);
        Assert.AreEqual(42, shifts.Count);
        Assert.IsFalse(shifts.Any(shift => shift.UserId == "a" && shift.StartsAtUtc < Anchor.AddHours(24)));
        Assert.IsFalse(shifts.Any(shift => shift.UserId == "b" && shift.StartsAtUtc < Anchor.AddHours(40)));
        Assert.AreEqual(0, OnCallScheduleValidator.Validate(Anchor, 21, 0, shifts, existing, users.ToDictionary(id => id)).Count);
        CollectionAssert.AreEqual(snapshot, existing);
        Assert.IsTrue(shifts.All(shift => shift.RestHours == 0));
    }

    [TestMethod]
    public void RejectsWhenExistingAssignmentsPreventMinimumCoverage()
    {
        var users = new[] { "a", "b", "c", "d", "e", "f" };
        var existing = users.Take(5).Select(id => new GeneratedOnCallShift(id, Anchor.AddHours(-8), Anchor, 16)).ToArray();
        Assert.Throws<InvalidOperationException>(() => _generator.Generate(
            new OnCallRule { MinimumStaff = 2, TargetStaff = 2 }, Anchor, users, Anchor, 1, existing));
    }

    [TestMethod]
    public void StaffingPreservesUtcSlotBoundariesAndPreviewLimits()
    {
        var rule = new OnCallRule { MinimumStaff = 2, TargetStaff = 2 };
        var users = new[] { "a", "b", "c", "d", "e", "f" };
        var start = Anchor.AddHours(8);
        var shifts = _generator.Generate(rule, Anchor, users, start, 168);
        Assert.AreEqual(336, shifts.Count);
        Assert.AreEqual(start, shifts[0].StartsAtUtc);
        Assert.AreEqual(start.AddHours(168 * 8), shifts[^1].EndsAtUtc);
        Assert.Throws<ArgumentException>(() => _generator.Generate(rule, Anchor, users, Anchor.AddHours(1), 1));
        Assert.Throws<ArgumentException>(() => _generator.Generate(rule, Anchor, users, Anchor.AddHours(-8), 1));
        Assert.Throws<ArgumentException>(() => _generator.Generate(rule, Anchor, users, DateTime.SpecifyKind(start, DateTimeKind.Unspecified), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _generator.Generate(rule, Anchor, users, start, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _generator.Generate(rule, Anchor, users, start, 169));
    }

    [TestMethod]
    public void ValidatorRejectsEmptySlotsOverlapAndInsufficientRestAcrossBoundaries()
    {
        var names = new Dictionary<string, string> { ["a"] = "Alice", ["b"] = "Bob" };
        var shifts = new[] { new GeneratedOnCallShift("a", Anchor, Anchor.AddHours(8)) };
        Assert.IsTrue(OnCallScheduleValidator.Validate(Anchor, 2, 16, shifts, [], names).Any(error => error.Contains("Empty shift")));
        Assert.IsTrue(OnCallScheduleValidator.Validate(Anchor, 1, 16, [shifts[0], shifts[0]], [], names).Any(error => error.Contains("overlaps")));
        foreach (var other in new[] { new GeneratedOnCallShift("a", Anchor.AddHours(-16), Anchor.AddHours(-8)),
                     new GeneratedOnCallShift("a", Anchor.AddHours(16), Anchor.AddHours(24)) })
            Assert.IsTrue(OnCallScheduleValidator.Validate(Anchor, 1, 16, shifts, [other], names).Any(error => error.Contains("insufficient rest")));
        Assert.AreEqual(0, OnCallScheduleValidator.Validate(Anchor, 1, 16, shifts,
            [new GeneratedOnCallShift("a", Anchor.AddHours(24), Anchor.AddHours(32))], names).Count);
    }
}

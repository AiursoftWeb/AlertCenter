using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallScheduleGeneratorTests
{
    private readonly OnCallScheduleGenerator _generator = new();
    private static readonly DateTime Anchor = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void GeneratesContinuousRotationWithDefaultRest()
    {
        var rule = new OnCallRule { AnchorUtc = Anchor };
        var shifts = _generator.Generate(rule, ["alice", "bob", "carol"], Anchor, 21);
        Assert.AreEqual(21, shifts.Count);
        for (var i = 0; i < shifts.Count; i++)
        {
            Assert.AreEqual(Anchor.AddHours(i * 8), shifts[i].StartsAtUtc);
            Assert.AreEqual(shifts[i].StartsAtUtc.AddHours(8), shifts[i].EndsAtUtc);
            Assert.AreEqual(new[] { "alice", "bob", "carol" }[i % 3], shifts[i].UserId);
        }
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
        var rule = new OnCallRule { AnchorUtc = Anchor };
        var users = Enumerable.Range(0, staffCount).Select(i => $"staff-{i}").ToArray();
        var shifts = _generator.Generate(rule, users, Anchor, 21);
        Assert.AreEqual(21, shifts.Select(shift => shift.StartsAtUtc).Distinct().Count());
        Assert.AreEqual(staffCount * 7, shifts.Count);
        Assert.IsTrue(shifts.GroupBy(shift => shift.UserId).All(group => group.Count() == 7));
        Assert.IsTrue(shifts.GroupBy(shift => shift.StartsAtUtc).All(group => group.Count() >= staffCount / 3 && group.Count() <= (staffCount + 2) / 3));
        Assert.AreEqual(0, OnCallScheduleValidator.Validate(Anchor, 21, 16, shifts, [], users.ToDictionary(id => id)).Count);
    }

    [TestMethod]
    public void RejectsTooFewPeopleAndInvalidBoundaries()
    {
        var rule = new OnCallRule { AnchorUtc = Anchor };
        var error = Assert.Throws<InvalidOperationException>(() => _generator.Generate(rule, ["a", "b"], Anchor, 21));
        StringAssert.Contains(error.Message, "至少需要 3 名员工");
        Assert.Throws<ArgumentException>(() => _generator.Generate(rule, ["a", "b", "c"], Anchor.AddHours(1), 1));
        Assert.Throws<ArgumentException>(() => _generator.Generate(rule, ["a", "a", "c"], Anchor, 1));
        Assert.Throws<ArgumentException>(() => _generator.Generate(new OnCallRule { AnchorUtc = Anchor, ShiftHours = 0 }, ["a"], Anchor, 1));
    }

    [TestMethod]
    public void RejectsSinglePersonEvenWithZeroRestAndNonEightHourShifts()
    {
        Assert.Throws<InvalidOperationException>(() => _generator.Generate(new OnCallRule { AnchorUtc = Anchor, RestHours = 0 }, ["a"], Anchor, 21));
        Assert.Throws<ArgumentException>(() => _generator.Generate(new OnCallRule { AnchorUtc = Anchor, ShiftHours = 12 }, ["a", "b", "c"], Anchor, 21));
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public void OptionalRotationPreservesCoverageAndRestAcrossWeeks(int staffCount)
    {
        var rule = new OnCallRule { AnchorUtc = Anchor, BalanceShiftTimes = true };
        var users = Enumerable.Range(0, staffCount).Select(i => $"staff-{i}").ToArray();
        var history = new List<GeneratedOnCallShift>();
        for (var week = 0; week < 8; week++)
        {
            var start = Anchor.AddDays(week * 7);
            var shifts = _generator.Generate(rule, users, start, 21, history.Where(shift => shift.EndsAtUtc > start.AddDays(-28)).ToList());
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
    public void ValidatorRejectsEmptySlotsOverlapAndInsufficientRestAcrossBoundaries()
    {
        var names = new Dictionary<string, string> { ["a"] = "Alice", ["b"] = "Bob" };
        var shifts = new[] { new GeneratedOnCallShift("a", Anchor, Anchor.AddHours(8)) };
        Assert.IsTrue(OnCallScheduleValidator.Validate(Anchor, 2, 16, shifts, [], names).Any(error => error.Contains("空班")));
        Assert.IsTrue(OnCallScheduleValidator.Validate(Anchor, 1, 16, [shifts[0], shifts[0]], [], names).Any(error => error.Contains("重叠")));
        foreach (var other in new[] { new GeneratedOnCallShift("a", Anchor.AddHours(-16), Anchor.AddHours(-8)),
                     new GeneratedOnCallShift("a", Anchor.AddHours(16), Anchor.AddHours(24)) })
            Assert.IsTrue(OnCallScheduleValidator.Validate(Anchor, 1, 16, shifts, [other], names).Any(error => error.Contains("休息不足")));
        Assert.AreEqual(0, OnCallScheduleValidator.Validate(Anchor, 1, 16, shifts,
            [new GeneratedOnCallShift("a", Anchor.AddHours(24), Anchor.AddHours(32))], names).Count);
    }
}

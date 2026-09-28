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
    public void HonorsConfigurableHoursAndNonZeroPreviewOffset()
    {
        var rule = new OnCallRule { AnchorUtc = Anchor, ShiftHours = 6, RestHours = 13 };
        var shifts = _generator.Generate(rule, ["a", "b", "c", "d"], Anchor.AddHours(18), 8);
        Assert.AreEqual("d", shifts[0].UserId);
        Assert.AreEqual("a", shifts[1].UserId);
        Assert.AreEqual(TimeSpan.FromHours(18), shifts[4].StartsAtUtc - shifts[0].EndsAtUtc);
        Assert.AreEqual("d", shifts[4].UserId);
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
    public void OnePersonCanCoverContinuousShiftsWithZeroRest()
    {
        var shifts = _generator.Generate(new OnCallRule { AnchorUtc = Anchor, ShiftHours = 12, RestHours = 0 },
            ["a"], Anchor, 14);
        Assert.IsTrue(shifts.All(shift => shift.UserId == "a"));
        Assert.AreEqual(Anchor.AddDays(7), shifts[^1].EndsAtUtc);
    }
}

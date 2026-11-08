using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallScheduleGeneratorTests
{
    private readonly OnCallScheduleGenerator generator = new(TestLocalization.For<OnCallScheduleGenerator>());
    private static readonly DateTime Anchor = new(2026, 9, 28, 14, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void PureQueueCyclesAcrossArbitraryDurationsWithoutRestRestrictions()
    {
        var rule = new OnCallRule { ShiftMinutes = 37, RestHours = 168, MinimumStaff = 200 };
        var result = generator.Generate(rule, Anchor, ["a", "b", "c"], Anchor, 7);
        CollectionAssert.AreEqual(new[] { "a", "b", "c", "a", "b", "c", "a" }, result.Select(shift => shift.UserId).ToArray());
        for (var index = 0; index < result.Count; index++)
        {
            Assert.AreEqual(Anchor.AddMinutes(index * 37), result[index].StartsAtUtc);
            Assert.AreEqual(Anchor.AddMinutes((index + 1) * 37), result[index].EndsAtUtc);
        }
        Assert.AreEqual(1, generator.Generate(new OnCallRule(), Anchor, ["a"], Anchor.AddMinutes(17), 1).Count);
    }

    [TestMethod]
    public void WeeklyShanghaiHandoverUsesMondayAndFridayAtTenPm()
    {
        var rule = WeeklyRule();
        var result = generator.GenerateRange(rule, Anchor, ["a", "b", "c"], Anchor, 6,
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"));
        CollectionAssert.AreEqual(new[] { "a", "b", "c", "a", "b", "c" }, result.Select(shift => shift.UserId).ToArray());
        Assert.AreEqual(Anchor.AddDays(4), result[0].EndsAtUtc);
        Assert.AreEqual(Anchor.AddDays(7), result[1].EndsAtUtc);
        Assert.IsTrue(result.All(shift => shift.StartsAtUtc.Hour == 14 && shift.EndsAtUtc.Hour == 14));
    }

    internal static OnCallRule WeeklyRule() => new()
    {
        HandoverPoints = [new() { DayOfWeek = DayOfWeek.Monday, MinuteOfDay = 22 * 60 },
            new() { DayOfWeek = DayOfWeek.Friday, MinuteOfDay = 22 * 60 }]
    };

    [TestMethod]
    [DataRow(2, "abababababab")]
    [DataRow(3, "abcabcabcabc")]
    [DataRow(4, "abcdbadcabcd")]
    public void AlternateRoundSwapFallsBackForSmallOrOddQueuesAndNeverRepeatsAdjacentPeople(int count, string expected)
    {
        var users = new[] { "a", "b", "c", "d" }.Take(count).ToArray();
        var rule = new OnCallRule { SwapAlternateRounds = true };
        var result = generator.Generate(rule, Anchor, users, Anchor, 12);
        Assert.AreEqual(expected, string.Concat(result.Select(shift => shift.UserId)));
        Assert.IsTrue(result.Zip(result.Skip(1)).All(pair => pair.First.UserId != pair.Second.UserId));
        var first = result.Take(5).ToList();
        var continued = generator.Generate(rule, Anchor, users, first[^1].EndsAtUtc, 7, first);
        CollectionAssert.AreEqual(result.Skip(5).ToArray(), continued.ToArray());
    }

    [TestMethod]
    public void MultipleStaffConsumeConsecutiveQueuePositionsAndWrap()
    {
        var result = generator.Generate(new OnCallRule { StaffPerShift = 2 }, Anchor, ["a", "b", "c"], Anchor, 3);
        CollectionAssert.AreEqual(new[] { "a", "b", "c", "a", "b", "c" }, result.Select(shift => shift.UserId).ToArray());
        Assert.IsTrue(result.GroupBy(shift => shift.StartsAtUtc).All(batch => batch.Count() == 2));
    }

    [TestMethod]
    public void ContinuationStartsAfterLastAssigneeOrAtHeadIfTheyLeft()
    {
        var history = new[] { new GeneratedOnCallShift("b", Anchor.AddDays(-4), Anchor) };
        Assert.AreEqual("c", generator.Generate(new(), Anchor, ["a", "b", "c"], Anchor, 1, history)[0].UserId);
        Assert.AreEqual("a", generator.Generate(new(), Anchor, ["a", "c"], Anchor, 1, history)[0].UserId);
    }

    [TestMethod]
    public void WeeklyLocalHandoverHandlesSpringGapAndAutumnAmbiguity()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var gap = OnCallScheduleGenerator.NextHandover([new() { DayOfWeek = DayOfWeek.Sunday, MinuteOfDay = 150 }], zone,
            new DateTime(2026, 3, 8, 0, 0, 0, DateTimeKind.Utc));
        Assert.AreEqual(new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc), gap);
        var repeated = OnCallScheduleGenerator.NextHandover([new() { DayOfWeek = DayOfWeek.Sunday, MinuteOfDay = 90 }], zone,
            new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.AreEqual(new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc), repeated);
    }

    [TestMethod]
    public void RejectsInvalidTemplateOrQueueData()
    {
        Assert.Throws<ArgumentException>(() => generator.Generate(new(), Anchor, ["a", "a"], Anchor, 1));
        Assert.Throws<ArgumentException>(() => generator.Generate(new(), Anchor, [], Anchor, 1));
        Assert.Throws<ArgumentException>(() => generator.Generate(new() { StaffPerShift = 3 }, Anchor, ["a", "b"], Anchor, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.Generate(new() { ShiftMinutes = 0 }, Anchor, ["a"], Anchor, 1));
        Assert.Throws<ArgumentException>(() => generator.Generate(new(), Anchor, ["a"], DateTime.SpecifyKind(Anchor, DateTimeKind.Local), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.Generate(new(), Anchor, ["a"], Anchor, 0));
    }
}

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

    [TestMethod]
    public void WeeklyContinuationWithHistoryStartsAfterPreviousWeeksLastAssignee()
    {
        var rule = WeeklyRule();
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        var firstWeek = generator.GenerateRange(rule, Anchor, ["a", "b", "c"], Anchor, 2, zone);
        var secondWeek = generator.GenerateRange(rule, Anchor, ["a", "b", "c"], Anchor.AddDays(7), 2, zone, firstWeek);
        CollectionAssert.AreEqual(new[] { "c", "a" }, secondWeek.Select(shift => shift.UserId).ToArray());
        Assert.AreEqual(firstWeek[^1].EndsAtUtc, secondWeek[0].StartsAtUtc);
        CollectionAssert.AreEqual(
            generator.GenerateRange(rule, Anchor, ["a", "b", "c"], Anchor, 4, zone).Skip(2).ToArray(),
            secondWeek.ToArray());
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    public void WeeklyNoHistoryAtLaterSegmentMatchesGenerationFromAnchor(int staffPerShift, bool swap)
    {
        var rule = WeeklyRule();
        rule.StaffPerShift = staffPerShift;
        rule.SwapAlternateRounds = swap;
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        var users = new[] { "a", "b", "c", "d" };
        var complete = generator.GenerateRange(rule, Anchor, users, Anchor, 16, zone);
        foreach (var segment in new[] { 0, 1, 2, 7, 12 })
        foreach (var minutesInside in new[] { 0, 37 })
        {
            var start = complete[segment * staffPerShift].StartsAtUtc.AddMinutes(minutesInside);
            var expected = complete.Skip(segment * staffPerShift).Take(2 * staffPerShift)
                .Select(shift => shift.StartsAtUtc < start ? shift with { StartsAtUtc = start } : shift).ToArray();
            CollectionAssert.AreEqual(expected, generator.GenerateRange(rule, Anchor, users, start, 2, zone).ToArray());
        }
    }

    [TestMethod]
    [DataRow(-37)]
    [DataRow(37)]
    public void WeeklyNoHistoryWithUnalignedAnchorMatchesGenerationFromAnchor(int anchorOffsetMinutes)
    {
        var rule = WeeklyRule();
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        var anchor = Anchor.AddMinutes(anchorOffsetMinutes);
        var complete = generator.GenerateRange(rule, anchor, ["a", "b", "c"], anchor, 7, zone);
        foreach (var shift in complete)
        {
            var start = shift.StartsAtUtc.AddMinutes(1);
            Assert.AreEqual(shift with { StartsAtUtc = start },
                generator.GenerateRange(rule, anchor, ["a", "b", "c"], start, 1, zone)[0]);
        }
        Assert.AreEqual(0L, generator.GenerateRange(rule, anchor, ["a", "b", "c"], anchor.AddDays(-14), 1, zone)[0].RotationPosition);
    }

    [TestMethod]
    [DataRow(-1, 0L)]
    [DataRow(0, 1L)]
    [DataRow(1, 1L)]
    public void WeeklyNoHistoryAdvancesExactlyAtHandover(int ticksFromHandover, long expectedPosition)
    {
        var start = Anchor.AddDays(4).AddTicks(ticksFromHandover);
        var result = generator.GenerateRange(WeeklyRule(), Anchor, ["a", "b", "c"], start, 1,
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"));
        Assert.AreEqual(expectedPosition, result[0].RotationPosition);
    }

    [TestMethod]
    public void WeeklyNoHistoryCountsLargeAnchorDistanceWithoutGeneratingElapsedShifts()
    {
        var anchor = new DateTime(1, 1, 1, 14, 0, 0, DateTimeKind.Utc);
        const int weeks = 400000;
        var rule = new OnCallRule
        {
            StaffPerShift = 2,
            HandoverPoints = Enum.GetValues<DayOfWeek>()
                .SelectMany(day => Enumerable.Range(0, 24).Select(hour =>
                    new OnCallHandoverPoint { DayOfWeek = day, MinuteOfDay = hour * 60 })).ToList()
        };
        var start = anchor.AddDays(weeks * 7).AddMinutes(37);
        var result = generator.GenerateRange(rule, anchor, ["a", "b", "c"], start, 1, TimeZoneInfo.Utc);
        Assert.AreEqual((long)weeks * 7 * 24 * rule.StaffPerShift, result[0].RotationPosition);
        Assert.AreEqual(start.AddMinutes(23), result[0].EndsAtUtc);
    }

    [TestMethod]
    [DataRow(3, 1, 150)]
    [DataRow(10, 25, 90)]
    public void WeeklyNoHistoryAcrossDaylightSavingMatchesGenerationFromAnchor(int month, int day, int minuteOfDay)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var rule = new OnCallRule
        {
            HandoverPoints = [new() { DayOfWeek = DayOfWeek.Sunday, MinuteOfDay = minuteOfDay }]
        };
        var anchor = new DateTime(2026, month, day, 0, 0, 0, DateTimeKind.Utc);
        var complete = generator.GenerateRange(rule, anchor, ["a", "b", "c"], anchor, 5, zone);
        foreach (var boundary in complete.Take(4).Select(shift => shift.EndsAtUtc))
        foreach (var ticksFromBoundary in new[] { -45 * TimeSpan.TicksPerMinute, -1, 0, 1 })
        {
            var start = boundary.AddTicks(ticksFromBoundary);
            var expected = complete.Single(shift => shift.StartsAtUtc <= start && shift.EndsAtUtc > start);
            Assert.AreEqual(expected with { StartsAtUtc = start },
                generator.GenerateRange(rule, anchor, ["a", "b", "c"], start, 1, zone)[0]);
        }
    }

    [TestMethod]
    public void WeeklyNoHistoryCountsCoincidentSpringGapHandoversOnce()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var rule = new OnCallRule
        {
            HandoverPoints = [new() { DayOfWeek = DayOfWeek.Sunday, MinuteOfDay = 120 },
                new() { DayOfWeek = DayOfWeek.Sunday, MinuteOfDay = 150 },
                new() { DayOfWeek = DayOfWeek.Sunday, MinuteOfDay = 180 }]
        };
        var anchor = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var complete = generator.GenerateRange(rule, anchor, ["a", "b", "c"], anchor, 12, zone);
        foreach (var shift in complete.Skip(1))
            Assert.AreEqual(shift, generator.GenerateRange(rule, anchor, ["a", "b", "c"], shift.StartsAtUtc, 1, zone)[0]);
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
    public void FullStaffAlternateRoundsRemainUniqueWhenContinuationStartsMidRound()
    {
        var rule = new OnCallRule { StaffPerShift = 4, SwapAlternateRounds = true, ShiftMinutes = 37 };
        var start = Anchor.AddMinutes(17);
        var result = generator.Generate(rule, Anchor, ["a", "b", "c", "d"], start, 8,
            [new("a", Anchor, start)]);
        var batches = result.GroupBy(shift => shift.StartsAtUtc).ToArray();
        Assert.AreEqual(8, batches.Length);
        foreach (var batch in batches)
            CollectionAssert.AreEquivalent(new[] { "a", "b", "c", "d" }, batch.Select(shift => shift.UserId).ToArray());
        for (var index = 1; index < batches.Length; index++)
            Assert.AreEqual(batches[index - 1].First().EndsAtUtc, batches[index].Key);
        var first = batches[0].First();
        var last = new OnCallShift { StartsAtUtc = first.StartsAtUtc, EndsAtUtc = first.EndsAtUtc,
            RotationPosition = first.RotationPosition, Members = batches[0].Select(shift => new OnCallShiftMember { UserId = shift.UserId }).ToList() };
        var group = new OnCallGroup { Rule = rule,
            Members = new[] { "a", "b", "c", "d" }.Select((id, index) => new OnCallGroupMember { UserId = id, RotationOrder = index }).ToList() };
        var continuation = generator.Generate(rule, Anchor, ["a", "b", "c", "d"], last.EndsAtUtc, 7,
            OnCallMaintenanceWorker.RotationHistory(group, last));
        CollectionAssert.AreEqual(result.Skip(4).ToArray(), continuation.ToArray());
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

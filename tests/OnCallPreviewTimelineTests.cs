using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallPreviewTimelineTests
{
    [TestMethod]
    public void SplitsAtTeamMidnightWithoutLosingTimeOrChangingStaff()
    {
        var start = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
        var days = OnCallPreviewTimeline.Build(
            [new GeneratedOnCallShift("alice", start, start.AddHours(8)),
                new GeneratedOnCallShift("bob", start.AddHours(8), start.AddHours(16)),
                new GeneratedOnCallShift("alice", start.AddHours(16), start.AddHours(24))],
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"));

        Assert.AreEqual(2, days.Count);
        Assert.AreEqual(new DateOnly(2026, 9, 28), days[0].Date);
        Assert.AreEqual(new DateOnly(2026, 9, 29), days[1].Date);
        Assert.AreEqual(TimeSpan.FromHours(4), days[0].Segments.Single().Duration);
        Assert.AreEqual(start.AddHours(4), days[0].Segments[0].EndsAtUtc);
        Assert.AreEqual(TimeSpan.FromHours(8), days[0].Segments[0].LocalStart.Offset);
        Assert.AreEqual(0, days[0].Segments[0].LocalEnd.Hour);

        var segments = days.SelectMany(day => day.Segments).ToList();
        Assert.AreEqual(24, segments.Sum(segment => segment.Duration.TotalHours));
        Assert.AreEqual(start, segments[0].StartsAtUtc);
        Assert.AreEqual(start.AddHours(24), segments[^1].EndsAtUtc);
        for (var index = 1; index < segments.Count; index++)
            Assert.AreEqual(segments[index - 1].EndsAtUtc, segments[index].StartsAtUtc);
        CollectionAssert.AreEqual(new[] { "alice", "alice", "bob", "alice" },
            segments.Select(segment => segment.UserId).ToArray());
    }

    [TestMethod]
    [DataRow(3, 8, 5, 23)]
    [DataRow(11, 1, 4, 25)]
    public void DaylightSavingDaysUseActualElapsedHours(int month, int day, int utcHour, int hours)
    {
        var start = new DateTime(2026, month, day, utcHour, 0, 0, DateTimeKind.Utc);
        var days = OnCallPreviewTimeline.Build(
            [new GeneratedOnCallShift("alice", start, start.AddHours(hours + 2))],
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

        Assert.AreEqual(2, days.Count);
        var segment = days[0].Segments.Single();
        Assert.AreEqual(new DateOnly(2026, month, day), days[0].Date);
        Assert.AreEqual(TimeSpan.FromHours(hours), segment.Duration);
        Assert.AreEqual(0, segment.LocalStart.Hour);
        Assert.AreEqual(0, segment.LocalEnd.Hour);
        Assert.AreNotEqual(segment.LocalStart.Offset, segment.LocalEnd.Offset);
        Assert.AreEqual(TimeSpan.FromHours(2), days[1].Segments.Single().Duration);
        Assert.AreEqual(segment.EndsAtUtc, days[1].Segments[0].StartsAtUtc);
    }

    [TestMethod]
    public void HandlesSkippedLocalMidnight()
    {
        var start = new DateTime(2018, 11, 4, 2, 0, 0, DateTimeKind.Utc);
        var days = OnCallPreviewTimeline.Build(
            [new GeneratedOnCallShift("alice", start, start.AddHours(4))],
            TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"));

        Assert.AreEqual(2, days.Count);
        Assert.AreEqual(new DateOnly(2018, 11, 3), days[0].Date);
        Assert.AreEqual(TimeSpan.FromHours(1), days[0].Segments.Single().Duration);
        Assert.AreEqual(new DateOnly(2018, 11, 4), days[1].Date);
        Assert.AreEqual(1, days[1].Segments.Single().LocalStart.Hour);
        Assert.AreEqual(TimeSpan.FromHours(3), days[1].Segments.Single().Duration);
    }

    [TestMethod]
    public void WarningOverlapsSplitIntoNonoverlappingSegmentsWithOrdinalMessages()
    {
        var start = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
        var segments = OnCallWarningTimeline.Build(
            [new(start.AddHours(4), start.AddHours(5), "Beta"),
                new(start, start.AddHours(6), "Zulu"),
                new(start.AddHours(2), start.AddHours(8), "Alpha")], start, start.AddHours(8));

        Assert.AreEqual(5, segments.Count);
        AssertWarningSegment(segments[0], start, start.AddHours(2), "Zulu");
        AssertWarningSegment(segments[1], start.AddHours(2), start.AddHours(4), "Alpha", "Zulu");
        AssertWarningSegment(segments[2], start.AddHours(4), start.AddHours(5), "Alpha", "Beta", "Zulu");
        AssertWarningSegment(segments[3], start.AddHours(5), start.AddHours(6), "Alpha", "Zulu");
        AssertWarningSegment(segments[4], start.AddHours(6), start.AddHours(8), "Alpha");
    }

    [TestMethod]
    public void DuplicateWarningsRemainActiveUntilEveryMatchingIntervalEnds()
    {
        var start = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
        var segments = OnCallWarningTimeline.Build(
            [new(start, start.AddHours(6), "Alpha"),
                new(start, start.AddHours(6), "Alpha"),
                new(start.AddHours(2), start.AddHours(8), "Alpha"),
                new(start.AddHours(3), start.AddHours(4), "Alpha"),
                new(start.AddHours(5), start.AddHours(7), "Beta")], start, start.AddHours(8));

        Assert.AreEqual(3, segments.Count);
        AssertWarningSegment(segments[0], start, start.AddHours(5), "Alpha");
        AssertWarningSegment(segments[1], start.AddHours(5), start.AddHours(7), "Alpha", "Beta");
        AssertWarningSegment(segments[2], start.AddHours(7), start.AddHours(8), "Alpha");
    }

    [TestMethod]
    public void AdjacentEqualWarningSetsMergeButGapsRemainEmpty()
    {
        var start = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
        var segments = OnCallWarningTimeline.Build(
            [new(start, start.AddHours(2), "Alpha"),
                new(start.AddHours(2), start.AddHours(4), "Beta"),
                new(start, start.AddHours(2), "Beta"),
                new(start.AddHours(2), start.AddHours(4), "Alpha"),
                new(start.AddHours(6), start.AddHours(8), "Beta"),
                new(start.AddHours(6), start.AddHours(8), "Alpha")], start, start.AddHours(8));

        Assert.AreEqual(2, segments.Count);
        AssertWarningSegment(segments[0], start, start.AddHours(4), "Alpha", "Beta");
        AssertWarningSegment(segments[1], start.AddHours(6), start.AddHours(8), "Alpha", "Beta");
    }

    [TestMethod]
    public void WarningIntervalsClipToTheVisibleRangeAndDiscardInvalidOrOutsideIntervals()
    {
        var start = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(8);
        var segments = OnCallWarningTimeline.Build(
            [new(start.AddHours(-2), start.AddHours(3), "Alpha"),
                new(start.AddHours(2), end.AddHours(2), "Beta"),
                new(start.AddHours(-1), end.AddHours(1), "Entire range"),
                new(start.AddHours(-3), start.AddHours(-1), "Before"),
                new(start.AddHours(-1), start, "Ends at start"),
                new(end, end.AddHours(1), "Starts at end"),
                new(end.AddHours(1), end.AddHours(2), "After"),
                new(start.AddHours(4), start.AddHours(4), "Empty"),
                new(start.AddHours(6), start.AddHours(5), "Reversed")], start, end);

        Assert.AreEqual(3, segments.Count);
        AssertWarningSegment(segments[0], start, start.AddHours(2), "Alpha", "Entire range");
        AssertWarningSegment(segments[1], start.AddHours(2), start.AddHours(3), "Alpha", "Beta", "Entire range");
        AssertWarningSegment(segments[2], start.AddHours(3), end, "Beta", "Entire range");
        Assert.AreEqual(0, OnCallWarningTimeline.Build([], start, end).Count);
        Assert.AreEqual(0, OnCallWarningTimeline.Build([new(start, end, "Alpha")], start, start).Count);
        Assert.AreEqual(0, OnCallWarningTimeline.Build([new(start, end, "Alpha")], end, start).Count);
    }

    [TestMethod]
    public void WarningMessagesUseCaseSensitiveUtf16OrdinalIdentityAndOrdering()
    {
        var start = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(1);
        var segments = OnCallWarningTimeline.Build(
            [new(start, end, "\uE000"), new(start, end, "\U0001F600"),
                new(start, end, "ä"), new(start, end, "a"), new(start, end, "Z"),
                new(start, end, "A"), new(start, end, "A")], start, end);

        Assert.AreEqual(1, segments.Count);
        AssertWarningSegment(segments[0], start, end, "A", "Z", "a", "ä", "\U0001F600", "\uE000");
    }

    private static void AssertWarningSegment(OnCallWarningSegment segment, DateTime start, DateTime end,
        params string[] messages)
    {
        Assert.AreEqual(start, segment.StartsAtUtc);
        Assert.AreEqual(end, segment.EndsAtUtc);
        CollectionAssert.AreEqual(messages, segment.Messages.ToArray());
    }

    [TestMethod]
    public void EndingAtMidnightDoesNotCreateAnEmptyDay()
    {
        var start = new DateTime(2026, 9, 28, 16, 0, 0, DateTimeKind.Utc);
        var days = OnCallPreviewTimeline.Build(
            [new GeneratedOnCallShift("alice", start, start.AddHours(8))], TimeZoneInfo.Utc);

        Assert.AreEqual(1, days.Count);
        Assert.AreEqual(TimeSpan.FromHours(8), days[0].Segments.Single().Duration);
        Assert.AreEqual(0, OnCallPreviewTimeline.Build([], TimeZoneInfo.Utc).Count);
    }
}

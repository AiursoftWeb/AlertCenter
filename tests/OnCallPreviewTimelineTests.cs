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

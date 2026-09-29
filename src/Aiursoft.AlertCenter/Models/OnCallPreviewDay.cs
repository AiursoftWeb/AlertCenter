using Aiursoft.AlertCenter.Services;

namespace Aiursoft.AlertCenter.Models;

public sealed record OnCallPreviewSegment(string UserId, DateTime StartsAtUtc, DateTime EndsAtUtc,
    DateTimeOffset LocalStart, DateTimeOffset LocalEnd, int ColorIndex)
{
    public TimeSpan Duration => EndsAtUtc - StartsAtUtc;
}

public sealed record OnCallPreviewDay(DateOnly Date, IReadOnlyList<OnCallPreviewSegment> Segments);

public static class OnCallPreviewTimeline
{
    public static IReadOnlyList<OnCallPreviewDay> Build(IReadOnlyList<GeneratedOnCallShift> shifts, TimeZoneInfo zone)
    {
        var days = new SortedDictionary<DateOnly, List<OnCallPreviewSegment>>();
        var colors = shifts.Select(shift => shift.UserId).Distinct(StringComparer.Ordinal)
            .Select((userId, index) => (userId, index))
            .ToDictionary(item => item.userId, item => item.index % 4, StringComparer.Ordinal);
        foreach (var shift in shifts)
        {
            var cursor = shift.StartsAtUtc;
            while (cursor < shift.EndsAtUtc)
            {
                var localStart = TimeZoneInfo.ConvertTime(new DateTimeOffset(cursor), zone);
                var date = DateOnly.FromDateTime(localStart.DateTime);
                var nextDate = date.AddDays(1);
                var end = shift.EndsAtUtc;
                if (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(new DateTimeOffset(end.AddTicks(-1)), zone).DateTime) >= nextDate)
                    end = FindFirstInstantOnOrAfter(nextDate, cursor, end, zone);
                var localEnd = TimeZoneInfo.ConvertTime(new DateTimeOffset(end), zone);
                if (!days.TryGetValue(date, out var segments)) days[date] = segments = [];
                segments.Add(new OnCallPreviewSegment(shift.UserId, cursor, end, localStart, localEnd, colors[shift.UserId]));
                cursor = end;
            }
        }
        return days.Select(day => new OnCallPreviewDay(day.Key, day.Value)).ToList();
    }

    private static DateTime FindFirstInstantOnOrAfter(DateOnly date, DateTime start, DateTime end, TimeZoneInfo zone)
    {
        // 在 UTC 时间轴上查找日期边界，避免夏令时切换导致本地午夜不存在或重复。
        var low = start.Ticks;
        var high = end.Ticks;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(new DateTimeOffset(mid, TimeSpan.Zero), zone).DateTime);
            if (localDate >= date) high = mid;
            else low = mid + 1;
        }
        return new DateTime(low, DateTimeKind.Utc);
    }
}

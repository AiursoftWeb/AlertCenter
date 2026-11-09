using Aiursoft.AlertCenter.Services;

namespace Aiursoft.AlertCenter.Models;

public sealed record OnCallWarningSegment(DateTime StartsAtUtc, DateTime EndsAtUtc, IReadOnlyList<string> Messages);

public static class OnCallWarningTimeline
{
    public static IReadOnlyList<OnCallWarningSegment> Build(
        IReadOnlyList<OnCallScheduleValidator.TimelineWarning> warnings, DateTime start, DateTime end)
    {
        if (end <= start) return [];
        var events = new List<(DateTime Time, string Message, int Delta)>();
        foreach (var warning in warnings)
        {
            var left = warning.StartsAtUtc > start ? warning.StartsAtUtc : start;
            var right = warning.EndsAtUtc < end ? warning.EndsAtUtc : end;
            if (right <= left) continue;
            events.Add((left, warning.Message, 1));
            events.Add((right, warning.Message, -1));
        }

        var segments = new List<OnCallWarningSegment>();
        var active = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var cursor = start;
        foreach (var boundary in events.OrderBy(item => item.Time).GroupBy(item => item.Time))
        {
            if (cursor < boundary.Key && active.Count > 0)
            {
                var messages = active.Keys.ToArray();
                if (segments.Count > 0 && segments[^1].EndsAtUtc == cursor &&
                    segments[^1].Messages.SequenceEqual(messages, StringComparer.Ordinal))
                    segments[^1] = segments[^1] with { EndsAtUtc = boundary.Key };
                else
                    segments.Add(new OnCallWarningSegment(cursor, boundary.Key, messages));
            }

            // 同一提醒可在多个区间重叠，只有全部结束后才能移除。
            foreach (var change in boundary)
            {
                var count = active.GetValueOrDefault(change.Message) + change.Delta;
                if (count == 0) active.Remove(change.Message);
                else active[change.Message] = count;
            }
            cursor = boundary.Key;
        }
        return segments;
    }
}

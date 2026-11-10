using Aiursoft.AlertCenter.Entities;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public sealed record GeneratedOnCallShift(string UserId, DateTime StartsAtUtc, DateTime EndsAtUtc, long? RotationPosition = null);

public interface IOnCallScheduleGenerator
{
    IReadOnlyList<GeneratedOnCallShift> Generate(OnCallRule rule, DateTime anchorUtc, IReadOnlyList<string> orderedUserIds,
        DateTime startUtc, int shiftCount, IReadOnlyList<GeneratedOnCallShift>? existing = null);
    IReadOnlyList<GeneratedOnCallShift> GenerateRange(OnCallRule rule, DateTime anchorUtc, IReadOnlyList<string> orderedUserIds,
        DateTime startUtc, int shiftCount, TimeZoneInfo zone, IReadOnlyList<GeneratedOnCallShift>? existing = null);
}

public sealed class OnCallScheduleGenerator(IStringLocalizer<OnCallScheduleGenerator> localizer) : IOnCallScheduleGenerator
{
    public IReadOnlyList<GeneratedOnCallShift> Generate(OnCallRule rule, DateTime anchorUtc, IReadOnlyList<string> orderedUserIds,
        DateTime startUtc, int shiftCount, IReadOnlyList<GeneratedOnCallShift>? existing = null) =>
        GenerateRange(rule, anchorUtc, orderedUserIds, startUtc, shiftCount, TimeZoneInfo.Utc, existing);

    public IReadOnlyList<GeneratedOnCallShift> GenerateRange(OnCallRule rule, DateTime anchorUtc,
        IReadOnlyList<string> orderedUserIds, DateTime startUtc, int shiftCount, TimeZoneInfo zone,
        IReadOnlyList<GeneratedOnCallShift>? existing = null)
    {
        if (anchorUtc.Kind != DateTimeKind.Utc || startUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException(localizer["The shift start time must be in UTC."].Value);
        if (shiftCount is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(shiftCount));
        if (orderedUserIds.Count == 0 || orderedUserIds.Any(string.IsNullOrWhiteSpace) ||
            orderedUserIds.Distinct(StringComparer.Ordinal).Count() != orderedUserIds.Count)
            throw new ArgumentException(localizer["Please select unique, valid employees."].Value);
        if (rule.StaffPerShift < 1 || rule.StaffPerShift > orderedUserIds.Count)
            throw new ArgumentException(localizer["Staff per shift must not exceed the rotation queue size."].Value);
        var minutes = rule.ShiftMinutes ?? checked(rule.ShiftHours * 60);
        if (minutes <= 0) throw new ArgumentOutOfRangeException(nameof(rule));
        long cursor;
        if (existing is { Count: > 0 })
        {
            var last = existing.OrderBy(shift => shift.EndsAtUtc).ThenBy(shift => shift.StartsAtUtc).Last();
            var lastIndex = orderedUserIds.ToList().IndexOf(last.UserId);
            cursor = lastIndex < 0 ? 0 : last.RotationPosition.HasValue ? last.RotationPosition.Value + 1 : lastIndex + 1;
        }
        else if (rule.HandoverPoints.Count == 0)
            cursor = Math.Max(0, (startUtc - anchorUtc).Ticks / TimeSpan.FromMinutes(minutes).Ticks) * rule.StaffPerShift;
        else
            cursor = ElapsedHandovers(rule.HandoverPoints, zone, anchorUtc, startUtc) * rule.StaffPerShift;
        var result = new List<GeneratedOnCallShift>();
        var startsAt = startUtc;
        for (var index = 0; index < shiftCount; index++)
        {
            var endsAt = rule.HandoverPoints.Count == 0
                ? startsAt.AddMinutes(minutes) : NextHandover(rule.HandoverPoints, zone, startsAt);
            var assignments = AssignShift(rule, orderedUserIds, cursor, rule.StaffPerShift, startsAt, endsAt);
            result.AddRange(assignments);
            cursor = assignments[^1].RotationPosition!.Value + 1;
            startsAt = endsAt;
        }
        return result;
    }

    private static long ElapsedHandovers(IEnumerable<OnCallHandoverPoint> handoverPoints, TimeZoneInfo zone,
        DateTime anchorUtc, DateTime startUtc)
    {
        if (startUtc <= anchorUtc) return 0;
        var points = handoverPoints.DistinctBy(point => (point.DayOfWeek, point.MinuteOfDay)).ToArray();
        var anchorDate = TimeZoneInfo.ConvertTimeFromUtc(anchorUtc, zone).Date;
        var startDate = TimeZoneInfo.ConvertTimeFromUtc(startUtc, zone).Date;
        var elapsedDays = (startDate - anchorDate).Days;
        var weeks = elapsedDays / 7;
        var remainder = elapsedDays % 7;
        // 只统计 (anchorUtc, startUtc] 内的交接；班段内部开始仍属于原来的轮换位置。
        long count = (long)weeks * points.Length;
        foreach (var point in points)
        {
            if (point.MinuteOfDay is < 0 or >= 1440 || !Enum.IsDefined(point.DayOfWeek))
                throw new ArgumentException("Invalid weekly handover point.");
            var day = ((int)point.DayOfWeek - (int)anchorDate.DayOfWeek + 7) % 7;
            if (day <= remainder) count++;
            if (day == 0 && HandoverUtc(anchorDate, point.MinuteOfDay, zone) <= anchorUtc) count--;
            if (point.DayOfWeek == startDate.DayOfWeek && HandoverUtc(startDate, point.MinuteOfDay, zone) > startUtc) count--;
        }
        if (zone.SupportsDaylightSavingTime && points.GroupBy(point => point.DayOfWeek).Any(day => day.Count() > 1))
        {
            // 同一跳时区间内的多个交接点会合并，不能把它们算作多个班段。
            var transitionDates = new HashSet<DateTime>();
            foreach (var adjustment in zone.GetAdjustmentRules())
            {
                if (adjustment.DateEnd < anchorDate || adjustment.DateStart > startDate) continue;
                transitionDates.Add(adjustment.DateStart);
                transitionDates.Add(adjustment.DateEnd);
                for (var year = Math.Max(anchorDate.Year, adjustment.DateStart.Year);
                     year <= Math.Min(startDate.Year, adjustment.DateEnd.Year); year++)
                {
                    transitionDates.Add(TransitionDate(year, adjustment.DaylightTransitionStart));
                    transitionDates.Add(TransitionDate(year, adjustment.DaylightTransitionEnd));
                }
            }
            foreach (var date in transitionDates.Where(date => date >= anchorDate && date <= startDate))
            {
                var boundaries = points.Where(point => point.DayOfWeek == date.DayOfWeek)
                    .Select(point => HandoverUtc(date, point.MinuteOfDay, zone))
                    .Where(utc => utc > anchorUtc && utc <= startUtc).ToArray();
                count -= boundaries.Length - boundaries.Distinct().Count();
            }
        }
        return count;
    }

    private static DateTime TransitionDate(int year, TimeZoneInfo.TransitionTime transition)
    {
        if (transition.IsFixedDateRule)
            return new DateTime(year, transition.Month, Math.Min(transition.Day, DateTime.DaysInMonth(year, transition.Month)));
        var first = new DateTime(year, transition.Month, 1);
        var day = 1 + ((int)transition.DayOfWeek - (int)first.DayOfWeek + 7) % 7 + (transition.Week - 1) * 7;
        if (day > DateTime.DaysInMonth(year, transition.Month)) day -= 7;
        return new DateTime(year, transition.Month, day);
    }

    private static DateTime HandoverUtc(DateTime date, int minuteOfDay, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.AddMinutes(minuteOfDay), DateTimeKind.Unspecified);
        // 春季不存在的本地时刻顺延到第一个有效分钟；秋季重复时刻采用标准时。
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    internal static List<GeneratedOnCallShift> AssignShift(OnCallRule rule, IReadOnlyList<string> ids, long cursor,
        int staffCount, DateTime startsAt, DateTime endsAt)
    {
        var result = new List<GeneratedOnCallShift>();
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        while (assigned.Count < staffCount)
        {
            var userId = ids[QueueIndex(rule, ids.Count, cursor)];
            // 隔轮对调跨轮取整队时可能再次遇到同一人；继续队列补足唯一成员。
            if (assigned.Add(userId)) result.Add(new(userId, startsAt, endsAt, RotationPosition: cursor));
            cursor++;
        }
        return result;
    }

    internal static int QueueIndex(OnCallRule rule, int count, long cursor)
    {
        var position = (int)(cursor % count);
        return rule.SwapAlternateRounds && count >= 4 && count % 2 == 0 && cursor / count % 2 == 1
            ? position ^ 1 : position;
    }

    public static DateTime NextHandover(IEnumerable<OnCallHandoverPoint> points, TimeZoneInfo zone, DateTime afterUtc)
    {
        var localDate = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone).Date;
        var candidates = new List<DateTime>();
        foreach (var point in points)
        {
            if (point.MinuteOfDay is < 0 or >= 1440 || !Enum.IsDefined(point.DayOfWeek))
                throw new ArgumentException("Invalid weekly handover point.");
            for (var day = 0; day <= 14; day++)
            {
                var date = localDate.AddDays(day);
                if (date.DayOfWeek != point.DayOfWeek) continue;
                var utc = HandoverUtc(date, point.MinuteOfDay, zone);
                if (utc > afterUtc) candidates.Add(utc);
            }
        }
        return candidates.Count > 0 ? candidates.Min() : throw new ArgumentException("No weekly handover points.");
    }
}

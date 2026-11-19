using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public sealed record GeneratedOnCallShift(string UserId, DateTime StartsAtUtc, DateTime EndsAtUtc, long? RotationPosition = null)
{
    internal long? SavedShiftPosition { get; init; }
}

public interface IOnCallScheduleGenerator
{
    IReadOnlyList<GeneratedOnCallShift> Generate(OnCallRule rule, DateTime anchorUtc, IReadOnlyList<string> orderedUserIds,
        DateTime startUtc, int shiftCount, IReadOnlyList<GeneratedOnCallShift>? existing = null);
    IReadOnlyList<GeneratedOnCallShift> GenerateRange(OnCallRule rule, DateTime anchorUtc, IReadOnlyList<string> orderedUserIds,
        DateTime startUtc, int shiftCount, TimeZoneInfo zone, IReadOnlyList<GeneratedOnCallShift>? existing = null,
        long? initialPosition = null);
}

public sealed class OnCallScheduleGenerator(IStringLocalizer<OnCallScheduleGenerator> localizer) : IOnCallScheduleGenerator
{
    public IReadOnlyList<GeneratedOnCallShift> Generate(OnCallRule rule, DateTime anchorUtc, IReadOnlyList<string> orderedUserIds,
        DateTime startUtc, int shiftCount, IReadOnlyList<GeneratedOnCallShift>? existing = null) =>
        GenerateRange(rule, anchorUtc, orderedUserIds, startUtc, shiftCount, TimeZoneInfo.Utc, existing);

    public IReadOnlyList<GeneratedOnCallShift> GenerateRange(OnCallRule rule, DateTime anchorUtc,
        IReadOnlyList<string> orderedUserIds, DateTime startUtc, int shiftCount, TimeZoneInfo zone,
        IReadOnlyList<GeneratedOnCallShift>? existing = null, long? initialPosition = null)
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
        if (startUtc < anchorUtc) startUtc = anchorUtc;
        var cursor = initialPosition ?? RotationPositionAt(rule, anchorUtc, zone, orderedUserIds, startUtc, existing);
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

    internal static DateTime BoundaryAtOrBefore(OnCallRule rule, DateTime anchorUtc, TimeZoneInfo zone, DateTime instant)
    {
        instant = DateTime.SpecifyKind(instant, DateTimeKind.Utc);
        if (rule.HandoverPoints.Count == 0)
        {
            var ticks = TimeSpan.FromMinutes(rule.ShiftMinutes ?? checked(rule.ShiftHours * 60)).Ticks;
            var remainder = (instant - anchorUtc).Ticks % ticks;
            return instant.AddTicks(-(remainder < 0 ? remainder + ticks : remainder));
        }
        var boundary = NextHandover(rule.HandoverPoints, zone, instant.AddDays(-9));
        while (true)
        {
            var next = NextHandover(rule.HandoverPoints, zone, boundary);
            if (next > instant) return boundary;
            boundary = next;
        }
    }

    internal static DateTime BoundaryAtOrAfter(OnCallRule rule, DateTime anchorUtc, TimeZoneInfo zone, DateTime instant)
    {
        var before = BoundaryAtOrBefore(rule, anchorUtc, zone, instant);
        return before == instant ? before : NextBoundary(rule, zone, before);
    }

    internal static DateTime NextBoundary(OnCallRule rule, TimeZoneInfo zone, DateTime boundary) =>
        rule.HandoverPoints.Count == 0 ? boundary.AddMinutes(rule.ShiftMinutes ?? checked(rule.ShiftHours * 60))
            : NextHandover(rule.HandoverPoints, zone, boundary);

    internal static int ShiftCount(OnCallRule rule, DateTime start, DateTime end) => rule.HandoverPoints.Count == 0
        ? (int)Math.Ceiling((end - start).TotalMinutes / (rule.ShiftMinutes ?? checked(rule.ShiftHours * 60)))
        : ((int)Math.Ceiling((end - start).TotalDays / 7) + 2) * rule.HandoverPoints.Count;

    internal static DateTime GenerationStart(OnCallRule rule, DateTime anchor, TimeZoneInfo zone, DateTime instant)
    {
        if (instant <= anchor) return anchor;
        var boundary = BoundaryAtOrBefore(rule, anchor, zone, instant);
        return boundary < anchor ? anchor : boundary;
    }

    private static long RotationPositionAt(OnCallRule rule, DateTime anchor, TimeZoneInfo zone,
        IReadOnlyList<string> ids, DateTime start, IReadOnlyList<GeneratedOnCallShift>? history)
    {
        var boundary = history is { Count: > 0 } ? GenerationStart(rule, anchor, zone, start) : start;
        var preceding = history?.Where(shift => shift.StartsAtUtc < start).ToArray();
        var adjacent = preceding?.Where(shift => shift.EndsAtUtc >= boundary)
            .OrderBy(shift => shift.EndsAtUtc).ThenBy(shift => shift.StartsAtUtc).LastOrDefault();
        if (adjacent != null)
        {
            var index = ids.ToList().IndexOf(adjacent.UserId);
            return index < 0 ? 0 : (adjacent.RotationPosition ?? index) + 1;
        }

        long BasePosition(DateTime instant) => (rule.HandoverPoints.Count == 0
            ? Math.Max(0, (instant - anchor).Ticks / TimeSpan.FromMinutes(rule.ShiftMinutes ?? checked(rule.ShiftHours * 60)).Ticks)
            : ElapsedHandovers(rule.HandoverPoints, zone, anchor, instant)) * rule.StaffPerShift;

        var position = BasePosition(start);
        var earlier = preceding?.Where(shift => shift.SavedShiftPosition.HasValue || shift.RotationPosition.HasValue)
            .OrderByDescending(shift => shift.EndsAtUtc).ThenByDescending(shift => shift.StartsAtUtc)
            .ThenBy(shift => shift.RotationPosition).FirstOrDefault();
        return (earlier?.SavedShiftPosition ?? earlier?.RotationPosition) is { } saved && earlier != null
            ? position + saved - BasePosition(DateTime.SpecifyKind(earlier.StartsAtUtc, DateTimeKind.Utc)) : position;
    }

    internal static async Task<List<GeneratedOnCallShift>> LoadHistory(IQueryable<OnCallShift> shifts, OnCallRule rule,
        IReadOnlyList<string> ids, DateTime boundary, DateTime cutoff, CancellationToken token)
    {
        var preceding = shifts.AsNoTracking().Include(shift => shift.Members).Where(shift => shift.StartsAtUtc < cutoff)
            .OrderByDescending(shift => shift.EndsAtUtc).ThenByDescending(shift => shift.StartsAtUtc).ThenBy(shift => shift.Id);
        var last = await preceding.FirstOrDefaultAsync(shift => shift.EndsAtUtc >= boundary, token)
                   ?? await preceding.FirstOrDefaultAsync(shift => shift.RotationPosition.HasValue, token);
        return RotationHistory(rule, ids, last);
    }

    internal static List<GeneratedOnCallShift> RotationHistory(OnCallRule rule, IReadOnlyList<string> userIds, OnCallShift? last)
    {
        if (last == null) return [];
        var ids = userIds.ToArray();
        if (last.RotationPosition is { } cursor && last.Members.Count > 0 && last.Members.Count <= ids.Length)
        {
            var expected = AssignShift(rule, ids, cursor, last.Members.Count, last.StartsAtUtc, last.EndsAtUtc);
            if (expected.Select(shift => shift.UserId).ToHashSet(StringComparer.Ordinal).SetEquals(last.Members.Select(member => member.UserId)))
                return expected;
        }
        if (last.Members.Count == 0)
            return [new(string.Empty, last.StartsAtUtc, last.EndsAtUtc) { SavedShiftPosition = last.RotationPosition }];
        return last.Members.OrderBy(member => Array.IndexOf(ids, member.UserId))
            .Select(member => new GeneratedOnCallShift(member.UserId, last.StartsAtUtc, last.EndsAtUtc)
                { SavedShiftPosition = last.RotationPosition }).ToList();
    }

    internal static long? FragmentPosition(IReadOnlyList<GeneratedOnCallShift> history, DateTime boundary) =>
        history.FirstOrDefault(shift => shift.StartsAtUtc >= boundary && shift.EndsAtUtc > boundary)?.RotationPosition;

    private static long ElapsedHandovers(IEnumerable<OnCallHandoverPoint> handoverPoints, TimeZoneInfo zone,
        DateTime anchorUtc, DateTime startUtc)
    {
        if (startUtc <= anchorUtc) return 0;
        var points = handoverPoints.DistinctBy(point => (point.DayOfWeek, point.MinuteOfDay)).ToArray();
        var anchorDate = TimeZoneInfo.ConvertTimeFromUtc(anchorUtc, zone).Date;
        var startDate = TimeZoneInfo.ConvertTimeFromUtc(startUtc, zone).Date;
        var elapsedDays = (startDate - anchorDate).Days;
        // 按交接点统计 (anchorUtc, startUtc] 内的周数，不需要生成中间的班段。
        long count = 0;
        foreach (var point in points)
        {
            if (point.MinuteOfDay is < 0 or >= 1440 || !Enum.IsDefined(point.DayOfWeek))
                throw new ArgumentException("Invalid weekly handover point.");
            var day = ((int)point.DayOfWeek - (int)anchorDate.DayOfWeek + 7) % 7;
            if (day > elapsedDays) continue;
            var first = day == 0 && HandoverUtc(anchorDate, point.MinuteOfDay, zone) <= anchorUtc ? 7 : day;
            var last = day + (elapsedDays - day) / 7 * 7;
            if (last == elapsedDays && HandoverUtc(startDate, point.MinuteOfDay, zone) > startUtc) last -= 7;
            if (last >= first) count += (last - first) / 7 + 1;
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

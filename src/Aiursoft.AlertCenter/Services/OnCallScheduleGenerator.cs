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
        var cursor = 0L;
        if (existing is { Count: > 0 })
        {
            var last = existing.OrderBy(shift => shift.EndsAtUtc).ThenBy(shift => shift.StartsAtUtc).Last();
            var lastIndex = orderedUserIds.ToList().IndexOf(last.UserId);
            cursor = lastIndex < 0 ? 0 : last.RotationPosition.HasValue ? last.RotationPosition.Value + 1 : lastIndex + 1;
        }
        else if (rule.HandoverPoints.Count == 0)
            cursor = Math.Max(0, (startUtc - anchorUtc).Ticks / TimeSpan.FromMinutes(minutes).Ticks) * rule.StaffPerShift;
        var result = new List<GeneratedOnCallShift>();
        var startsAt = startUtc;
        for (var index = 0; index < shiftCount; index++)
        {
            var endsAt = rule.HandoverPoints.Count == 0
                ? startsAt.AddMinutes(minutes) : NextHandover(rule.HandoverPoints, zone, startsAt);
            for (var person = 0; person < rule.StaffPerShift; person++)
            {
                var position = QueueIndex(rule, orderedUserIds.Count, cursor);
                result.Add(new(orderedUserIds[position], startsAt, endsAt, RotationPosition: cursor));
                cursor++;
            }
            startsAt = endsAt;
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
                var local = DateTime.SpecifyKind(date.AddMinutes(point.MinuteOfDay), DateTimeKind.Unspecified);
                // 春季不存在的本地时刻顺延到第一个有效分钟；秋季重复时刻采用标准时。
                while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
                var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
                if (utc > afterUtc) candidates.Add(utc);
            }
        }
        return candidates.Count > 0 ? candidates.Min() : throw new ArgumentException("No weekly handover points.");
    }
}

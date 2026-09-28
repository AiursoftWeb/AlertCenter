using Aiursoft.AlertCenter.Entities;

namespace Aiursoft.AlertCenter.Services;

public sealed record GeneratedOnCallShift(string UserId, DateTime StartsAtUtc, DateTime EndsAtUtc);

public interface IOnCallScheduleGenerator
{
    IReadOnlyList<GeneratedOnCallShift> Generate(OnCallRule rule, IReadOnlyList<string> orderedUserIds,
        DateTime startUtc, int shiftCount);
}

public sealed class OnCallScheduleGenerator : IOnCallScheduleGenerator
{
    public IReadOnlyList<GeneratedOnCallShift> Generate(OnCallRule rule, IReadOnlyList<string> orderedUserIds,
        DateTime startUtc, int shiftCount)
    {
        if (rule.ShiftHours is < 1 or > 24 || rule.RestHours is < 0 or > 168)
            throw new ArgumentException("班次时长需为 1–24 小时，最短休息需为 0–168 小时。");
        if (rule.AnchorUtc.Kind != DateTimeKind.Utc || startUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("排班起点必须是 UTC 时间。");
        if (shiftCount is < 1 or > 168)
            throw new ArgumentOutOfRangeException(nameof(shiftCount), "预览班次数量需为 1–168。");
        if (orderedUserIds.Count == 0 || orderedUserIds.Any(string.IsNullOrWhiteSpace) ||
            orderedUserIds.Distinct(StringComparer.Ordinal).Count() != orderedUserIds.Count)
            throw new ArgumentException("请选择不重复的有效员工。");
        var requiredStaff = 1 + (int)Math.Ceiling((double)rule.RestHours / rule.ShiftHours);
        if (orderedUserIds.Count < requiredStaff)
            throw new InvalidOperationException($"该规则至少需要 {requiredStaff} 名员工（班次 {rule.ShiftHours} 小时，休息不少于 {rule.RestHours} 小时）。");
        var duration = TimeSpan.FromHours(rule.ShiftHours);
        if ((startUtc - rule.AnchorUtc).Ticks % duration.Ticks != 0)
            throw new ArgumentException("预览起点必须位于规则的班次边界上。");
        var firstIndex = (startUtc - rule.AnchorUtc).Ticks / duration.Ticks;
        var result = new List<GeneratedOnCallShift>(shiftCount);
        for (var index = 0; index < shiftCount; index++)
        {
            var startsAtUtc = startUtc.AddTicks(checked(index * duration.Ticks));
            result.Add(new(orderedUserIds[(int)(((firstIndex + index) % orderedUserIds.Count + orderedUserIds.Count) % orderedUserIds.Count)],
                startsAtUtc, startsAtUtc.Add(duration)));
        }
        return result;
    }
}

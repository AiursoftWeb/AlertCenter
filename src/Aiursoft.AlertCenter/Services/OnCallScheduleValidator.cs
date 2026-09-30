namespace Aiursoft.AlertCenter.Services;

public static class OnCallScheduleValidator
{
    public static bool CanAssign(string userId, DateTime start, DateTime end, int restHours,
        IEnumerable<GeneratedOnCallShift> existing) => existing.Where(shift => shift.UserId == userId).All(shift =>
        start >= shift.EndsAtUtc
            ? (start - shift.EndsAtUtc).TotalHours >= Math.Max(restHours, shift.RestHours)
            : end <= shift.StartsAtUtc && (shift.StartsAtUtc - end).TotalHours >= Math.Max(restHours, shift.RestHours));

    public static List<string> Validate(DateTime start, int count, int restHours,
        IReadOnlyList<GeneratedOnCallShift> shifts, IReadOnlyList<GeneratedOnCallShift> existing,
        IReadOnlyDictionary<string, string> members)
    {
        var errors = new List<string>();
        var slots = shifts.GroupBy(shift => shift.StartsAtUtc).ToDictionary(group => group.Key, group => group.ToList());
        for (var i = 0; i < count; i++)
        {
            var time = start.AddHours(i * 8);
            if (!slots.TryGetValue(time, out var assigned) || assigned.Count == 0)
                errors.Add($"{time:yyyy-MM-dd HH:mm} UTC 出现空班，至少需要一名员工。");
        }
        var accepted = existing.ToList();
        foreach (var shift in shifts.OrderBy(shift => shift.StartsAtUtc))
        {
            if (!members.TryGetValue(shift.UserId, out var name))
            {
                errors.Add("包含不属于当前规则的员工。");
                continue;
            }
            if (shift.StartsAtUtc < start || shift.StartsAtUtc >= start.AddHours(count * 8) ||
                (shift.StartsAtUtc - start).Ticks % TimeSpan.FromHours(8).Ticks != 0 || shift.EndsAtUtc != shift.StartsAtUtc.AddHours(8))
                errors.Add($"{name} 的班次不在规定的 8 小时时段内。");
            if (!CanAssign(shift.UserId, shift.StartsAtUtc, shift.EndsAtUtc, restHours, accepted))
                errors.Add($"{name} 在 {shift.StartsAtUtc:yyyy-MM-dd HH:mm} UTC 的班次与其他班次重叠或休息不足（本规则至少 {restHours} 小时，同时满足相邻规则要求）。");
            accepted.Add(shift with { RestHours = restHours });
        }
        return errors.Distinct().ToList();
    }
}

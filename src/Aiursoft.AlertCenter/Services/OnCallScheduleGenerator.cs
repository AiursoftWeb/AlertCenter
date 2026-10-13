using Aiursoft.AlertCenter.Entities;

namespace Aiursoft.AlertCenter.Services;

public sealed record GeneratedOnCallShift(string UserId, DateTime StartsAtUtc, DateTime EndsAtUtc, int RestHours = 0);

public interface IOnCallScheduleGenerator
{
    IReadOnlyList<GeneratedOnCallShift> Generate(OnCallRule rule, DateTime anchorUtc, IReadOnlyList<string> orderedUserIds,
        DateTime startUtc, int shiftCount, IReadOnlyList<GeneratedOnCallShift>? existing = null);
}

public sealed class OnCallScheduleGenerator : IOnCallScheduleGenerator
{
    public IReadOnlyList<GeneratedOnCallShift> Generate(OnCallRule rule, DateTime anchorUtc, IReadOnlyList<string> orderedUserIds,
        DateTime startUtc, int shiftCount, IReadOnlyList<GeneratedOnCallShift>? existing = null)
    {
        if (rule.ShiftHours != 8 || rule.RestHours is < 0 or > 168)
            throw new ArgumentException("固定每天三班，每班 8 小时；最短休息需为 0–168 小时。");
        if (anchorUtc.Kind != DateTimeKind.Utc || startUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("排班起点必须是 UTC 时间。");
        if (shiftCount is < 1 or > 168)
            throw new ArgumentOutOfRangeException(nameof(shiftCount), "预览班次数量需为 1–168。");
        if (orderedUserIds.Count == 0 || orderedUserIds.Any(string.IsNullOrWhiteSpace) ||
            orderedUserIds.Distinct(StringComparer.Ordinal).Count() != orderedUserIds.Count)
            throw new ArgumentException("请选择不重复的有效员工。");
        var requiredStaff = Math.Max(3, 1 + (int)Math.Ceiling(rule.RestHours / 8.0));
        if (orderedUserIds.Count < requiredStaff)
            throw new InvalidOperationException($"该规则至少需要 {requiredStaff} 名员工（班次 {rule.ShiftHours} 小时，休息不少于 {rule.RestHours} 小时）。");
        var duration = TimeSpan.FromHours(rule.ShiftHours);
        if (startUtc < anchorUtc || (startUtc - anchorUtc).Ticks % duration.Ticks != 0)
            throw new ArgumentException("预览起点必须位于团队的班次边界上。");
        var firstIndex = (startUtc - anchorUtc).Ticks / duration.Ticks;
        var history = orderedUserIds.ToDictionary(id => id,
            id => (existing ?? []).Where(shift => shift.UserId == id).ToList(), StringComparer.Ordinal);
        var result = new List<GeneratedOnCallShift>();
        for (var index = 0; index < shiftCount; index++)
        {
            var slot = firstIndex + index;
            var startsAt = startUtc.AddHours(index * 8);
            var target = orderedUserIds.Count / requiredStaff + (slot % requiredStaff < orderedUserIds.Count % requiredStaff ? 1 : 0);
            // 换班窗口暂时减少冗余，给工程师留出足够休息后换到其他时段。
            if (rule.BalanceShiftTimes && slot >= 21 && slot % 21 < requiredStaff)
                target = Math.Max(1, orderedUserIds.Count / requiredStaff - 1);
            var candidates = orderedUserIds.Select((id, order) => new { Id = id, Order = order })
                .Where(user => OnCallScheduleValidator.CanAssign(user.Id, startsAt, startsAt.AddHours(8), rule.RestHours, history[user.Id]))
                .OrderBy(user => rule.BalanceShiftTimes ? 0 : (user.Order % requiredStaff == slot % requiredStaff ? 0 : 1))
                .ThenBy(user => history[user.Id].Count)
                .ThenBy(user => rule.BalanceShiftTimes
                    ? history[user.Id].Count(shift => ((shift.StartsAtUtc - anchorUtc).Ticks / duration.Ticks % 3 + 3) % 3 == slot % 3) : 0)
                .ThenBy(user => user.Order).ToList();
            var assigned = 0;
            foreach (var candidate in candidates)
            {
                var shift = new GeneratedOnCallShift(candidate.Id, startsAt, startsAt.AddHours(8), rule.RestHours);
                history[candidate.Id].Add(shift);
                if (!CanCoverFollowingSlots(startsAt, Math.Min(requiredStaff - 1, shiftCount - index - 1), rule.RestHours, history))
                {
                    history[candidate.Id].RemoveAt(history[candidate.Id].Count - 1);
                    continue;
                }
                result.Add(shift);
                if (++assigned == target) break;
            }
            if (assigned == 0)
                throw new InvalidOperationException($"{startsAt:yyyy-MM-dd HH:mm} UTC 无法安排满足休息要求的员工，请调整相邻排班或增加成员。");
        }
        return result;
    }

    private static bool CanCoverFollowingSlots(DateTime start, int count, int restHours,
        Dictionary<string, List<GeneratedOnCallShift>> history)
    {
        // 为接下来的班次保留可用人员，防止当前班次的冗余造成后续空班。
        var matched = new Dictionary<string, int>(StringComparer.Ordinal);
        bool Match(int slot, HashSet<string> visited)
        {
            var time = start.AddHours(slot * 8);
            foreach (var (id, shifts) in history)
            {
                if (visited.Contains(id) || !OnCallScheduleValidator.CanAssign(id, time, time.AddHours(8), restHours, shifts)) continue;
                visited.Add(id);
                if (matched.TryGetValue(id, out var previous) && !Match(previous, visited)) continue;
                matched[id] = slot;
                return true;
            }
            return false;
        }
        return Enumerable.Range(1, count).All(slot => Match(slot, new HashSet<string>(StringComparer.Ordinal)));
    }
}

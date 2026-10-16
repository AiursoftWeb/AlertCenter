using System.Data;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public class OnCallScheduleService(TemplateDbContext db, IOnCallScheduleGenerator generator)
{
    public List<string> Warnings { get; private set; } = [];

    public static DateTime WeekStart(OnCallGroup group, DateTime? requested = null)
    {
        if (requested?.Kind == DateTimeKind.Local) throw new ArgumentException("请使用 UTC 时间指定排班周期。");
        var anchor = DateTime.SpecifyKind(group.AnchorUtc, DateTimeKind.Utc);
        var start = requested.HasValue ? DateTime.SpecifyKind(requested.Value, DateTimeKind.Utc)
            : anchor.AddDays(Math.Max(0, (long)((DateTime.UtcNow - anchor).TotalDays / 7)) * 7);
        if (start < anchor || start < DateTime.MinValue.AddDays(28) || start > DateTime.MaxValue.AddDays(-28) ||
            (start - anchor).Ticks % TimeSpan.FromDays(7).Ticks != 0)
            throw new ArgumentException("请选择以团队锚点为起点的完整七天排班周期。");
        return start;
    }

    public Task<List<GeneratedOnCallShift>> Existing(Guid groupId, string[] userIds, DateTime start, CancellationToken cancellationToken) =>
        Existing(groupId, userIds, start, start.AddDays(7), cancellationToken);

    private async Task<List<GeneratedOnCallShift>> Existing(Guid groupId, string[] userIds, DateTime start, DateTime end,
        CancellationToken cancellationToken)
    {
        var from = start.AddDays(-28);
        var until = end.AddDays(7);
        var rows = await db.OnCallShiftMembers.AsNoTracking()
            .Where(member => userIds.Contains(member.UserId) && member.Shift.EndsAtUtc > from && member.Shift.StartsAtUtc < until &&
                !(member.Shift.GroupId == groupId && member.Shift.StartsAtUtc >= start && member.Shift.StartsAtUtc < end))
            .Select(member => new { member.UserId, member.Shift.StartsAtUtc, member.Shift.EndsAtUtc, member.Shift.RestHours })
            .ToListAsync(cancellationToken);
        return rows.Select(row => new GeneratedOnCallShift(row.UserId, DateTime.SpecifyKind(row.StartsAtUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(row.EndsAtUtc, DateTimeKind.Utc), row.RestHours)).ToList();
    }

    public async Task<List<string>> Save(SaveOnCallScheduleRequest? request, bool generate, CancellationToken cancellationToken)
    {
        Warnings = [];
        // JSON input can contain null collections and entries despite the DTO annotations.
        IReadOnlyList<OnCallScheduleWeekRequest?>? adjacentWeeks = request?.AdjacentWeeks;
        if (request == null || adjacentWeeks == null || adjacentWeeks.Count > 2 ||
            adjacentWeeks.Any(week => week == null))
            return ["相邻周排班请求格式不正确，最多只能提交前后两周。"];
        if (generate && request.AdjacentWeeks.Count != 0)
            return ["自动生成不能同时修改相邻周排班。"];
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        var group = await db.OnCallGroups.Include(item => item.Rule).Include(item => item.Members)
            .SingleOrDefaultAsync(item => item.Id == request.GroupId, cancellationToken);
        if (group == null) return ["值班团队不存在。"];
        if (request.Version != group.ScheduleVersion)
            throw new DbUpdateConcurrencyException("团队或排班已被其他管理员修改，请刷新页面后重试。");
        var start = WeekStart(group, request.StartUtc);
        if (request.AdjacentWeeks.Any(week => week.StartUtc.Kind == DateTimeKind.Local ||
                (week.StartUtc != start.AddDays(-7) && week.StartUtc != start.AddDays(7))) ||
            request.AdjacentWeeks.Select(week => week.StartUtc).Distinct().Count() != request.AdjacentWeeks.Count)
            return ["相邻周只能是当前周的前一周或后一周，且不能重复。"];
        var requests = request.AdjacentWeeks.Prepend(new OnCallScheduleWeekRequest { StartUtc = start, Slots = request.Slots })
            .Select(week => new OnCallScheduleWeekRequest { StartUtc = WeekStart(group, week.StartUtc), Slots = week.Slots })
            .OrderBy(week => week.StartUtc).ToList();
        var from = requests[0].StartUtc;
        var until = requests[^1].StartUtc.AddDays(7);
        var savedSlots = await db.OnCallShifts.Include(shift => shift.Members)
            .Where(shift => shift.GroupId == group.Id && shift.StartsAtUtc < until && shift.EndsAtUtc > from)
            .OrderBy(shift => shift.StartsAtUtc).ToListAsync(cancellationToken);
        if (generate && savedSlots.Count != 0) return ["本周已有保存的排班，请使用手动调整；自动生成不会覆盖已有安排。"];
        var ids = group.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray();
        var currentIds = ids.ToHashSet(StringComparer.Ordinal);
        var allowedIds = ids.Concat(savedSlots.SelectMany(slot => slot.Members.Select(member => member.UserId))).Distinct().ToArray();
        var names = await db.Users.Where(user => allowedIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.DisplayName, cancellationToken);
        // 编辑周彼此连续，必须排除整个区间的旧排班，再用各周新草稿互相校验。
        var existing = await Existing(group.Id, allowedIds, from, until, cancellationToken);
        var weeks = new List<ScheduleWeek>();
        foreach (var week in requests)
        {
            var end = week.StartUtc.AddDays(7);
            var slots = savedSlots.Where(slot => slot.StartsAtUtc < end && slot.EndsAtUtc > week.StartUtc).ToList();
            var create = slots.Count == 0;
            if (!create && (slots.Count != 21 || slots.Where((slot, index) =>
                    slot.StartsAtUtc != week.StartUtc.AddHours(index * 8) || slot.EndsAtUtc != slot.StartsAtUtc.AddHours(8)).Any()))
                return ["已有排班的时间边界不完整，不能覆盖。"];
            var restHours = create ? group.Rule?.RestHours ?? 16 : slots[0].RestHours;
            if (restHours is < 0 or > 168 || slots.Any(slot => slot.RestHours != restHours))
                return ["排班的休息时长无效或不一致。"];
            var minimumStaff = create ? group.Rule?.MinimumStaff ?? 1 : slots[0].MinimumStaff;
            if (minimumStaff is < 1 or > 200 || slots.Any(slot => slot.MinimumStaff != minimumStaff))
                return ["排班的最低人数无效或不一致。"];
            List<GeneratedOnCallShift> assignments;
            if (generate)
            {
                if (group.Rule == null) return ["请先为团队绑定排班规则。"];
                var sharedIds = await db.OnCallGroupMembers.AsNoTracking()
                    .Where(member => member.GroupId != group.Id && ids.Contains(member.UserId))
                    .Select(member => member.UserId).Distinct().ToListAsync(cancellationToken);
                var automaticIds = ids.Except(sharedIds, StringComparer.Ordinal).ToArray();
                var required = Math.Max(3, 1 + (int)Math.Ceiling(group.Rule.RestHours / 8.0)) * group.Rule.MinimumStaff;
                if (automaticIds.Length < required)
                    return [$"自动排班跳过多值班组成员后，仅剩 {automaticIds.Length} 人，至少需要 {required} 人；请增加单组成员或手动安排。"];
                assignments = generator.Generate(group.Rule, DateTime.SpecifyKind(group.AnchorUtc, DateTimeKind.Utc),
                    automaticIds, week.StartUtc, 21, existing).ToList();
            }
            else
            {
                if (!HasValidSlots(week.Slots, week.StartUtc, end))
                    return ["必须提交本周全部 21 个班次，且每个时段仅出现一次。"];
                var savedMembers = slots.ToDictionary(slot => slot.StartsAtUtc,
                    slot => slot.Members.Select(member => member.UserId).ToHashSet(StringComparer.Ordinal));
                if (week.Slots.Any(slot => slot.UserIds.Any(id => !currentIds.Contains(id) &&
                        (!savedMembers.TryGetValue(slot.StartsAtUtc, out var saved) || !saved.Contains(id)))))
                    return ["不能新增已退出团队或不属于团队的员工。"];
                assignments = week.Slots.SelectMany(slot => slot.UserIds.Select(id => new GeneratedOnCallShift(id,
                    DateTime.SpecifyKind(slot.StartsAtUtc, DateTimeKind.Utc), DateTime.SpecifyKind(slot.StartsAtUtc.AddHours(8), DateTimeKind.Utc), restHours))).ToList();
            }
            weeks.Add(new ScheduleWeek(week.StartUtc, slots, restHours, minimumStaff, assignments));
        }
        var errors = weeks.SelectMany(week => OnCallScheduleValidator.Validate(week.StartUtc, 21, week.RestHours,
            week.Assignments, existing.Concat(weeks.Where(peer => peer.StartUtc != week.StartUtc)
                .SelectMany(peer => peer.Assignments)).ToList(), names)).Distinct().ToList();
        if (errors.Count != 0) return errors;
        Warnings = weeks.SelectMany(week => OnCallScheduleValidator.StaffingWarnings(week.StartUtc, 21,
            week.MinimumStaff, week.Assignments)).ToList();
        if (generate && Warnings.Count > 0) return Warnings;
        foreach (var week in weeks)
        {
            var create = week.SavedSlots.Count == 0;
            var bySlot = week.Assignments.ToLookup(shift => shift.StartsAtUtc);
            for (var index = 0; index < 21; index++)
            {
                var time = week.StartUtc.AddHours(index * 8);
                var slot = create ? new OnCallShift
                {
                    GroupId = group.Id, StartsAtUtc = time, EndsAtUtc = time.AddHours(8), RestHours = week.RestHours,
                    MinimumStaff = week.MinimumStaff
                } : week.SavedSlots[index];
                if (create) db.OnCallShifts.Add(slot);
                var selected = bySlot[time].Select(shift => shift.UserId).ToHashSet(StringComparer.Ordinal);
                foreach (var member in slot.Members.Where(member => !selected.Contains(member.UserId)).ToList())
                    db.OnCallShiftMembers.Remove(member);
                foreach (var id in selected.Except(slot.Members.Select(member => member.UserId)))
                    slot.Members.Add(new OnCallShiftMember { UserId = id });
            }
        }
        group.ScheduleVersion = Guid.NewGuid();
        await db.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return [];
    }

    private sealed record ScheduleWeek(DateTime StartUtc, List<OnCallShift> SavedSlots, int RestHours,
        int MinimumStaff, List<GeneratedOnCallShift> Assignments);

    private static bool HasValidSlots(IReadOnlyList<OnCallSlotRequest?>? slots, DateTime start, DateTime end) =>
        slots is { Count: 21 } &&
        slots.All(slot => slot is { UserIds: { } userIds } && userIds.Count <= 200 && !userIds.Any(string.IsNullOrWhiteSpace) &&
            slot.StartsAtUtc.Kind != DateTimeKind.Local && slot.StartsAtUtc >= start && slot.StartsAtUtc < end &&
            (slot.StartsAtUtc - start).Ticks % TimeSpan.FromHours(8).Ticks == 0) &&
        slots.Select(slot => slot!.StartsAtUtc).Distinct().Count() == 21;
}

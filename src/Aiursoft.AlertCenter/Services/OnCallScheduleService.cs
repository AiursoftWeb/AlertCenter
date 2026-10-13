using System.Data;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public class OnCallScheduleService(TemplateDbContext db, IOnCallScheduleGenerator generator)
{
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

    public async Task<List<GeneratedOnCallShift>> Existing(Guid groupId, string[] userIds, DateTime start, CancellationToken cancellationToken)
    {
        var end = start.AddDays(7);
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

    public async Task<List<string>> Save(SaveOnCallScheduleRequest request, bool generate, CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        var group = await db.OnCallGroups.Include(item => item.Rule).Include(item => item.Members)
            .SingleOrDefaultAsync(item => item.Id == request.GroupId, cancellationToken);
        if (group == null) return ["值班团队不存在。"];
        if (request.Version != group.ScheduleVersion)
            throw new DbUpdateConcurrencyException("团队或排班已被其他管理员修改，请刷新页面后重试。");
        var start = WeekStart(group, request.StartUtc);
        var end = start.AddDays(7);
        var slots = await db.OnCallShifts.Include(shift => shift.Members)
            .Where(shift => shift.GroupId == group.Id && shift.StartsAtUtc < end && shift.EndsAtUtc > start)
            .OrderBy(shift => shift.StartsAtUtc).ToListAsync(cancellationToken);
        if (generate && slots.Count != 0) return ["本周已有保存的排班，请使用手动调整；自动生成不会覆盖已有安排。"];
        var create = slots.Count == 0;
        if (!create && (slots.Count != 21 || slots.Where((slot, index) =>
                slot.StartsAtUtc != start.AddHours(index * 8) || slot.EndsAtUtc != slot.StartsAtUtc.AddHours(8)).Any()))
            return ["已有排班的时间边界不完整，不能覆盖。"];
        var restHours = create ? group.Rule?.RestHours ?? 16 : slots[0].RestHours;
        if (restHours is < 0 or > 168 || slots.Any(slot => slot.RestHours != restHours))
            return ["排班的休息时长无效或不一致。"];
        var ids = group.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray();
        var savedIds = slots.SelectMany(slot => slot.Members.Select(member => member.UserId)).ToArray();
        var allowedIds = ids.Concat(savedIds).Distinct().ToArray();
        var names = await db.Users.Where(user => allowedIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.DisplayName, cancellationToken);
        var existing = await Existing(group.Id, allowedIds, start, cancellationToken);
        List<GeneratedOnCallShift> assignments;
        if (generate)
        {
            if (group.Rule == null) return ["请先为团队绑定排班规则。"];
            assignments = generator.Generate(group.Rule, DateTime.SpecifyKind(group.AnchorUtc, DateTimeKind.Utc),
                ids, start, 21, existing).ToList();
        }
        else
        {
            if (request.Slots == null || request.Slots.Count != 21 ||
                request.Slots.Any(slot => slot == null || slot.UserIds == null || slot.UserIds.Count > 200 || slot.UserIds.Any(string.IsNullOrWhiteSpace) ||
                    slot.StartsAtUtc.Kind == DateTimeKind.Local || slot.StartsAtUtc < start || slot.StartsAtUtc >= end ||
                    (slot.StartsAtUtc - start).Ticks % TimeSpan.FromHours(8).Ticks != 0) ||
                request.Slots.Select(slot => slot.StartsAtUtc).Distinct().Count() != 21)
                return ["必须提交本周全部 21 个班次，且每个时段仅出现一次。"];
            var currentIds = ids.ToHashSet(StringComparer.Ordinal);
            var savedMembers = slots.ToDictionary(slot => slot.StartsAtUtc,
                slot => slot.Members.Select(member => member.UserId).ToHashSet(StringComparer.Ordinal));
            if (request.Slots.Any(slot => slot.UserIds.Any(id => !currentIds.Contains(id) &&
                    (!savedMembers.TryGetValue(slot.StartsAtUtc, out var saved) || !saved.Contains(id)))))
                return ["不能新增已退出团队或不属于团队的员工。"];
            assignments = request.Slots.SelectMany(slot => slot.UserIds.Select(id => new GeneratedOnCallShift(id,
                DateTime.SpecifyKind(slot.StartsAtUtc, DateTimeKind.Utc), DateTime.SpecifyKind(slot.StartsAtUtc.AddHours(8), DateTimeKind.Utc), restHours))).ToList();
        }
        var errors = OnCallScheduleValidator.Validate(start, 21, restHours, assignments, existing, names);
        if (errors.Count != 0) return errors;
        var bySlot = assignments.ToLookup(shift => shift.StartsAtUtc);
        for (var index = 0; index < 21; index++)
        {
            var time = start.AddHours(index * 8);
            var slot = create ? new OnCallShift
            {
                GroupId = group.Id, StartsAtUtc = time, EndsAtUtc = time.AddHours(8), RestHours = restHours
            } : slots[index];
            if (create) db.OnCallShifts.Add(slot);
            var selected = bySlot[time].Select(shift => shift.UserId).ToHashSet(StringComparer.Ordinal);
            foreach (var member in slot.Members.Where(member => !selected.Contains(member.UserId)).ToList())
                db.OnCallShiftMembers.Remove(member);
            foreach (var id in selected.Except(slot.Members.Select(member => member.UserId)))
                slot.Members.Add(new OnCallShiftMember { UserId = id });
        }
        group.ScheduleVersion = Guid.NewGuid();
        await db.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return [];
    }
}

using System.Data;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public class OnCallScheduleService(TemplateDbContext db, IOnCallScheduleGenerator generator)
{
    public static DateTime WeekStart(OnCallRule rule, DateTime? requested = null)
    {
        if (requested?.Kind == DateTimeKind.Local) throw new ArgumentException("请使用 UTC 时间指定排班周期。");
        var anchor = DateTime.SpecifyKind(rule.AnchorUtc, DateTimeKind.Utc);
        var start = requested.HasValue ? DateTime.SpecifyKind(requested.Value, DateTimeKind.Utc)
            : anchor.AddDays(Math.Max(0, (long)((DateTime.UtcNow - anchor).TotalDays / 7)) * 7);
        if (start < anchor || start < DateTime.MinValue.AddDays(28) || start > DateTime.MaxValue.AddDays(-28) ||
            (start - anchor).Ticks % TimeSpan.FromDays(7).Ticks != 0)
            throw new ArgumentException("请选择以规则锚点为起点的完整七天排班周期。");
        return start;
    }

    public async Task<List<GeneratedOnCallShift>> Existing(Guid ruleId, string[] userIds, DateTime start, CancellationToken cancellationToken)
    {
        var end = start.AddDays(7);
        var from = start.AddDays(-28);
        var until = end.AddDays(7);
        var rows = await db.OnCallShiftMembers.AsNoTracking()
            .Where(member => userIds.Contains(member.UserId) && member.Shift.EndsAtUtc > from && member.Shift.StartsAtUtc < until &&
                !(member.Shift.RuleId == ruleId && member.Shift.StartsAtUtc >= start && member.Shift.StartsAtUtc < end))
            .Select(member => new { member.UserId, member.Shift.StartsAtUtc, member.Shift.EndsAtUtc, member.Shift.Rule.RestHours })
            .ToListAsync(cancellationToken);
        return rows.Select(row => new GeneratedOnCallShift(row.UserId, DateTime.SpecifyKind(row.StartsAtUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(row.EndsAtUtc, DateTimeKind.Utc), row.RestHours)).ToList();
    }

    public async Task<List<string>> Save(SaveOnCallScheduleRequest request, bool generate, CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        var rule = await db.OnCallRules.Include(item => item.Members).SingleOrDefaultAsync(item => item.Id == request.RuleId, cancellationToken);
        if (rule == null) return ["排班规则不存在。"];
        if (rule.ShiftHours != 8 || rule.RestHours is < 0 or > 168) return ["请先将规则调整为每班 8 小时及有效的休息时长。"];
        if (request.Version != rule.ScheduleVersion)
            throw new DbUpdateConcurrencyException("排班已被其他管理员修改，请刷新页面后重试。");
        var start = WeekStart(rule, request.StartUtc);
        var end = start.AddDays(7);
        var slots = await db.OnCallShifts.Include(shift => shift.Members)
            .Where(shift => shift.RuleId == rule.Id && shift.StartsAtUtc < end && shift.EndsAtUtc > start)
            .OrderBy(shift => shift.StartsAtUtc).ToListAsync(cancellationToken);
        if (generate && slots.Count != 0) return ["本周已有保存的排班，请使用手动调整；自动生成不会覆盖已有安排。"];
        var create = slots.Count == 0;
        if (!generate && !create && (slots.Count != 21 || slots.Where((slot, index) => slot.StartsAtUtc != start.AddHours(index * 8)).Any()))
            return ["已有排班的时间边界不完整，不能覆盖。"];
        var ids = rule.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray();
        if (ids.Length < 3) return ["值班组至少需要三名工程师，请先修改规则成员。"];
        var names = await db.Users.Where(user => ids.Contains(user.Id)).ToDictionaryAsync(user => user.Id, user => user.DisplayName, cancellationToken);
        var existing = await Existing(rule.Id, ids, start, cancellationToken);
        List<GeneratedOnCallShift> assignments;
        if (generate)
        {
            rule.AnchorUtc = DateTime.SpecifyKind(rule.AnchorUtc, DateTimeKind.Utc);
            assignments = generator.Generate(rule, ids, start, 21, existing).ToList();
        }
        else
        {
            if (request.Slots == null || request.Slots.Count != 21 ||
                request.Slots.Any(slot => slot == null || slot.UserIds == null || slot.UserIds.Count > 200 || slot.UserIds.Any(string.IsNullOrWhiteSpace) ||
                    slot.StartsAtUtc.Kind == DateTimeKind.Local || slot.StartsAtUtc < start || slot.StartsAtUtc >= end ||
                    (slot.StartsAtUtc - start).Ticks % TimeSpan.FromHours(8).Ticks != 0) ||
                request.Slots.Select(slot => slot.StartsAtUtc).Distinct().Count() != 21)
                return ["必须提交本周全部 21 个班次，且每个时段仅出现一次。"];
            assignments = request.Slots.SelectMany(slot => slot.UserIds.Select(id => new GeneratedOnCallShift(id,
                DateTime.SpecifyKind(slot.StartsAtUtc, DateTimeKind.Utc), DateTime.SpecifyKind(slot.StartsAtUtc.AddHours(8), DateTimeKind.Utc), rule.RestHours))).ToList();
        }
        var errors = OnCallScheduleValidator.Validate(start, 21, rule.RestHours, assignments, existing, names);
        if (errors.Count != 0) return errors;
        var bySlot = assignments.ToLookup(shift => shift.StartsAtUtc);
        for (var index = 0; index < 21; index++)
        {
            var time = start.AddHours(index * 8);
            var slot = create ? new OnCallShift { RuleId = rule.Id, StartsAtUtc = time, EndsAtUtc = time.AddHours(8) } : slots[index];
            if (create) db.OnCallShifts.Add(slot);
            var selected = bySlot[time].Select(shift => shift.UserId).ToHashSet(StringComparer.Ordinal);
            foreach (var member in slot.Members.Where(member => !selected.Contains(member.UserId)).ToList())
                db.OnCallShiftMembers.Remove(member);
            foreach (var id in selected.Except(slot.Members.Select(member => member.UserId)))
                slot.Members.Add(new OnCallShiftMember { UserId = id });
        }
        rule.ScheduleVersion = Guid.NewGuid();
        await db.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return [];
    }
}

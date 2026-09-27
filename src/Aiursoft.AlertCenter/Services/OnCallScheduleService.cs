using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public sealed record OnCallEngineer(Guid GroupId, Guid ShiftId, string UserId, string DisplayName, string? Email,
    DateTime StartsAtUtc, DateTime EndsAtUtc, bool IsOverride);

public sealed record OnCallScheduleValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
    public static OnCallScheduleValidationResult Success { get; } = new([]);
}

public interface IOnCallScheduleService
{
    Task<IReadOnlyList<OnCallShift>> GenerateDailyRotationAsync(Guid groupId, DateOnly startDate, DateOnly endDate,
        CancellationToken cancellationToken = default);
    Task<OnCallScheduleValidationResult> PublishAsync(Guid groupId, IReadOnlyCollection<OnCallShift> shifts,
        DateTime coverageStartUtc, DateTime coverageEndUtc, CancellationToken cancellationToken = default);
    Task<OnCallScheduleValidationResult> ValidatePublishedScheduleAsync(Guid groupId, DateTime coverageStartUtc,
        DateTime coverageEndUtc, CancellationToken cancellationToken = default);
    Task<OnCallScheduleValidationResult> AddOverrideAsync(OnCallShiftOverride overrideItem,
        CancellationToken cancellationToken = default);
    Task<OnCallEngineer?> GetOnCallEngineerAsync(Guid groupId, DateTime atUtc,
        CancellationToken cancellationToken = default);
}

public sealed class OnCallScheduleService(TemplateDbContext db) : IOnCallScheduleService
{
    private static readonly TimeSpan MaximumShiftDuration = TimeSpan.FromHours(8);
    private static readonly TimeSpan MinimumRestDuration = TimeSpan.FromHours(16);

    public async Task<IReadOnlyList<OnCallShift>> GenerateDailyRotationAsync(Guid groupId, DateOnly startDate,
        DateOnly endDate, CancellationToken cancellationToken = default)
    {
        if (startDate > endDate) throw new ArgumentException("开始日期不能晚于结束日期。", nameof(startDate));
        var group = await db.OnCallGroups.AsNoTracking().SingleOrDefaultAsync(item => item.Id == groupId, cancellationToken)
            ?? throw new KeyNotFoundException("值班组不存在。");
        var timeZone = FindTimeZone(group.TimeZoneId);
        var templates = await db.OnCallShiftTemplates.AsNoTracking().Where(item => item.GroupId == groupId)
            .OrderBy(item => item.StartMinuteOfDay).ThenBy(item => item.Id).ToListAsync(cancellationToken);
        if (templates.Count == 0) throw new InvalidOperationException("值班组尚未配置班次模板。");
        var templateIds = templates.Select(item => item.Id).ToArray();
        var members = await db.OnCallShiftTemplateMembers.AsNoTracking()
            .Where(item => templateIds.Contains(item.TemplateId)).OrderBy(item => item.RotationOrder).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var groupMemberIds = await db.OnCallGroupMembers.AsNoTracking().Where(item => item.GroupId == groupId)
            .Select(item => item.UserId).ToHashSetAsync(cancellationToken);
        var shifts = new List<OnCallShift>();
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            for (var templateIndex = 0; templateIndex < templates.Count; templateIndex++)
            {
                var template = templates[templateIndex];
                ValidateTemplate(template);
                var rotation = members.Where(item => item.TemplateId == template.Id).ToList();
                if (rotation.Count == 0) throw new InvalidOperationException($"班次模板“{template.Name}”尚未配置轮转成员。");
                if (rotation.Any(item => !groupMemberIds.Contains(item.UserId)))
                    throw new InvalidOperationException($"班次模板“{template.Name}”包含非值班组成员。");
                var user = rotation[(date.DayNumber + templateIndex) % rotation.Count];
                var localStart = date.ToDateTime(TimeOnly.MinValue).AddMinutes(template.StartMinuteOfDay);
                var localEnd = localStart.AddMinutes(template.DurationMinutes);
                if (timeZone.IsInvalidTime(localStart) || timeZone.IsInvalidTime(localEnd))
                    throw new InvalidOperationException($"{date:yyyy-MM-dd} 在时区 {group.TimeZoneId} 中包含无效的夏令时本地时间。");
                var startsAtUtc = TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone);
                var endsAtUtc = TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone);
                if (endsAtUtc - startsAtUtc > MaximumShiftDuration)
                    throw new InvalidOperationException($"{date:yyyy-MM-dd} 的班次在 UTC 中超过 8 小时，请调整模板。");
                shifts.Add(new OnCallShift
                {
                    GroupId = groupId,
                    TemplateId = template.Id,
                    UserId = user.UserId,
                    StartsAtUtc = startsAtUtc,
                    EndsAtUtc = endsAtUtc
                });
            }
        }
        return shifts;
    }

    public async Task<OnCallScheduleValidationResult> PublishAsync(Guid groupId, IReadOnlyCollection<OnCallShift> shifts,
        DateTime coverageStartUtc, DateTime coverageEndUtc, CancellationToken cancellationToken = default)
    {
        var existingShifts = await db.OnCallShifts.AsNoTracking().Where(item => item.GroupId == groupId)
            .ToListAsync(cancellationToken);
        if (existingShifts.Any(item => item.StartsAtUtc < coverageEndUtc && item.EndsAtUtc > coverageStartUtc))
            return new(["发布范围与已有已发布班次重叠；请先使用新的发布范围。"]);
        var existingOverrides = await db.OnCallShiftOverrides.AsNoTracking().Where(item => item.GroupId == groupId)
            .ToListAsync(cancellationToken);
        var memberIds = await db.OnCallGroupMembers.AsNoTracking().Where(item => item.GroupId == groupId)
            .Select(item => item.UserId).ToListAsync(cancellationToken);
        var validation = Validate(groupId, existingShifts.Concat(shifts), existingOverrides, memberIds,
            coverageStartUtc, coverageEndUtc);
        if (!validation.IsValid) return validation;
        db.OnCallShifts.AddRange(shifts);
        await db.SaveChangesAsync(cancellationToken);
        return validation;
    }

    public async Task<OnCallScheduleValidationResult> ValidatePublishedScheduleAsync(Guid groupId,
        DateTime coverageStartUtc, DateTime coverageEndUtc, CancellationToken cancellationToken = default)
    {
        var shifts = await db.OnCallShifts.AsNoTracking().Where(item => item.GroupId == groupId &&
            item.StartsAtUtc < coverageEndUtc && item.EndsAtUtc > coverageStartUtc).ToListAsync(cancellationToken);
        var shiftIds = shifts.Select(item => item.Id).ToArray();
        var overrides = await db.OnCallShiftOverrides.AsNoTracking().Where(item => item.GroupId == groupId &&
            shiftIds.Contains(item.OriginalShiftId) && item.StartsAtUtc < coverageEndUtc && item.EndsAtUtc > coverageStartUtc)
            .ToListAsync(cancellationToken);
        var memberIds = await db.OnCallGroupMembers.AsNoTracking().Where(item => item.GroupId == groupId)
            .Select(item => item.UserId).ToListAsync(cancellationToken);
        return Validate(groupId, shifts, overrides, memberIds, coverageStartUtc, coverageEndUtc);
    }

    public async Task<OnCallScheduleValidationResult> AddOverrideAsync(OnCallShiftOverride overrideItem,
        CancellationToken cancellationToken = default)
    {
        var originalShift = await db.OnCallShifts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == overrideItem.OriginalShiftId,
            cancellationToken);
        if (originalShift == null) return new(["原始班次不存在。"]);
        if (originalShift.GroupId != overrideItem.GroupId) return new(["临时覆盖必须属于原始班次的值班组。"]);
        var shifts = await db.OnCallShifts.AsNoTracking().Where(item => item.GroupId == overrideItem.GroupId).ToListAsync(cancellationToken);
        var overrides = await db.OnCallShiftOverrides.AsNoTracking().Where(item => item.GroupId == overrideItem.GroupId)
            .ToListAsync(cancellationToken);
        overrides.Add(overrideItem);
        var members = await db.OnCallGroupMembers.AsNoTracking().Where(item => item.GroupId == overrideItem.GroupId)
            .Select(item => item.UserId).ToListAsync(cancellationToken);
        var validation = Validate(overrideItem.GroupId, shifts, overrides, members, originalShift.StartsAtUtc, originalShift.EndsAtUtc);
        if (!validation.IsValid) return validation;
        db.OnCallShiftOverrides.Add(overrideItem);
        await db.SaveChangesAsync(cancellationToken);
        return validation;
    }

    public async Task<OnCallEngineer?> GetOnCallEngineerAsync(Guid groupId, DateTime atUtc,
        CancellationToken cancellationToken = default)
    {
        var overrides = await db.OnCallShiftOverrides.AsNoTracking().Where(item => item.GroupId == groupId &&
            item.StartsAtUtc <= atUtc && item.EndsAtUtc > atUtc).ToListAsync(cancellationToken);
        if (overrides.Count > 1) throw new InvalidOperationException("当前时刻存在多个值班覆盖，无法确定 OCE。");
        var shifts = await db.OnCallShifts.AsNoTracking().Where(item => item.GroupId == groupId &&
            item.StartsAtUtc <= atUtc && item.EndsAtUtc > atUtc).ToListAsync(cancellationToken);
        if (shifts.Count != 1) return null;
        var shift = shifts[0];
        var userId = overrides.Count == 1 ? overrides[0].CoveringUserId : shift.UserId;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user == null) return null;
        return new(groupId, shift.Id, user.Id, user.DisplayName, user.Email, shift.StartsAtUtc, shift.EndsAtUtc,
            overrides.Count == 1);
    }

    public static OnCallScheduleValidationResult Validate(Guid groupId, IEnumerable<OnCallShift> shifts,
        IEnumerable<OnCallShiftOverride> overrides, IEnumerable<string> memberIds, DateTime coverageStartUtc,
        DateTime coverageEndUtc)
    {
        var errors = new List<string>();
        if (coverageStartUtc >= coverageEndUtc)
            errors.Add("发布范围必须具有有效的开始和结束时间。");
        var members = memberIds.ToHashSet(StringComparer.Ordinal);
        var schedule = shifts.OrderBy(item => item.StartsAtUtc).ThenBy(item => item.EndsAtUtc).ToList();
        foreach (var shift in schedule)
        {
            if (shift.GroupId != groupId) errors.Add("班次必须属于待发布的值班组。");
            if (shift.StartsAtUtc >= shift.EndsAtUtc) errors.Add("班次结束时间必须晚于开始时间。");
            if (shift.EndsAtUtc - shift.StartsAtUtc > MaximumShiftDuration) errors.Add("单个班次不能超过 8 小时。");
            if (!members.Contains(shift.UserId)) errors.Add("班次工程师必须是值班组成员。");
        }
        for (var index = 1; index < schedule.Count; index++)
            if (schedule[index].StartsAtUtc < schedule[index - 1].EndsAtUtc)
                errors.Add("排班不能包含重叠班次。");

        var overrideList = overrides.ToList();
        var shiftsById = schedule.ToDictionary(item => item.Id);
        foreach (var overrideItem in overrideList)
        {
            if (overrideItem.GroupId != groupId) errors.Add("临时覆盖必须属于待发布的值班组。");
            if (!shiftsById.TryGetValue(overrideItem.OriginalShiftId, out var original))
                errors.Add("临时覆盖必须关联已发布班次。");
            else if (overrideItem.StartsAtUtc < original.StartsAtUtc || overrideItem.EndsAtUtc > original.EndsAtUtc ||
                     overrideItem.StartsAtUtc >= overrideItem.EndsAtUtc)
                errors.Add("临时覆盖必须完全位于原班次内，且具有有效时段。");
            if (!members.Contains(overrideItem.CoveringUserId)) errors.Add("代班工程师必须是值班组成员。");
        }
        foreach (var group in overrideList.GroupBy(item => item.OriginalShiftId))
        {
            var ordered = group.OrderBy(item => item.StartsAtUtc).ToList();
            for (var index = 1; index < ordered.Count; index++)
                if (ordered[index].StartsAtUtc < ordered[index - 1].EndsAtUtc)
                    errors.Add("同一原班次的临时覆盖不能重叠。");
        }
        if (errors.Count > 0) return new(errors.Distinct().ToList());

        var effectiveShifts = BuildEffectiveShifts(schedule, overrideList);
        effectiveShifts = effectiveShifts.OrderBy(item => item.StartsAtUtc).ThenBy(item => item.EndsAtUtc).ToList();
        for (var index = 1; index < effectiveShifts.Count; index++)
            if (effectiveShifts[index].StartsAtUtc < effectiveShifts[index - 1].EndsAtUtc)
                errors.Add("临时覆盖后的排班不能包含重叠班次。");
        ValidateCoverage(effectiveShifts, coverageStartUtc, coverageEndUtc, errors);
        foreach (var userShifts in effectiveShifts.GroupBy(item => item.UserId))
        {
            var ordered = userShifts.OrderBy(item => item.StartsAtUtc).ToList();
            for (var index = 1; index < ordered.Count; index++)
                if (ordered[index].StartsAtUtc - ordered[index - 1].EndsAtUtc < MinimumRestDuration)
                    errors.Add("同一工程师相邻班次之间必须连续休息至少 16 小时。");
        }
        return errors.Count == 0 ? OnCallScheduleValidationResult.Success : new(errors.Distinct().ToList());
    }

    private static void ValidateCoverage(IReadOnlyList<OnCallShift> schedule, DateTime start, DateTime end,
        ICollection<string> errors)
    {
        var cursor = start;
        foreach (var shift in schedule.Where(item => item.EndsAtUtc > start && item.StartsAtUtc < end))
        {
            if (shift.StartsAtUtc > cursor)
            {
                errors.Add("排班必须在发布范围内持续覆盖，不能存在空档。");
                return;
            }
            if (shift.EndsAtUtc > cursor) cursor = shift.EndsAtUtc;
            if (cursor >= end) return;
        }
        errors.Add("排班必须在发布范围内持续覆盖，不能存在空档。");
    }

    private static IReadOnlyList<OnCallShift> BuildEffectiveShifts(IReadOnlyList<OnCallShift> shifts,
        IReadOnlyList<OnCallShiftOverride> overrides)
    {
        var result = new List<OnCallShift>();
        foreach (var shift in shifts)
        {
            var replacements = overrides.Where(item => item.OriginalShiftId == shift.Id).ToList();
            if (replacements.Count == 0)
            {
                result.Add(shift);
                continue;
            }
            var boundaries = replacements.SelectMany(item => new[] { item.StartsAtUtc, item.EndsAtUtc })
                .Append(shift.StartsAtUtc).Append(shift.EndsAtUtc).Distinct().OrderBy(item => item).ToArray();
            for (var index = 1; index < boundaries.Length; index++)
            {
                var startsAtUtc = boundaries[index - 1];
                var replacement = replacements.SingleOrDefault(item => item.StartsAtUtc <= startsAtUtc && item.EndsAtUtc > startsAtUtc);
                result.Add(new OnCallShift
                {
                    Id = shift.Id,
                    GroupId = shift.GroupId,
                    TemplateId = shift.TemplateId,
                    UserId = replacement?.CoveringUserId ?? shift.UserId,
                    StartsAtUtc = startsAtUtc,
                    EndsAtUtc = boundaries[index],
                    PublishedAtUtc = shift.PublishedAtUtc
                });
            }
        }
        return result;
    }

    private static TimeZoneInfo FindTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new InvalidOperationException($"值班组配置的时区“{timeZoneId}”无效。");
        }
        catch (InvalidTimeZoneException)
        {
            throw new InvalidOperationException($"值班组配置的时区“{timeZoneId}”无效。");
        }
    }

    private static void ValidateTemplate(OnCallShiftTemplate template)
    {
        if (template.StartMinuteOfDay is < 0 or >= 24 * 60)
            throw new InvalidOperationException($"班次模板“{template.Name}”的开始时间无效。");
        if (template.DurationMinutes is <= 0 or > 8 * 60)
            throw new InvalidOperationException($"班次模板“{template.Name}”的时长必须在 1 至 480 分钟之间。");
    }
}

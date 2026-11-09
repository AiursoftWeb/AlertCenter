using System.Data;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public class OnCallScheduleService(TemplateDbContext db, IOnCallScheduleGenerator generator,
    IStringLocalizer<OnCallScheduleService> localizer, IStringLocalizerFactory localizerFactory)
{
    public List<string> Warnings { get; private set; } = [];
    public List<OnCallSlotRequest> ReplacedSlots { get; private set; } = [];
    public List<OnCallSlotRequest> PreviewSlots { get; private set; } = [];
    public List<string> PreviewErrors { get; private set; } = [];
    public List<OnCallScheduleValidator.TimelineWarning> TimelineWarnings { get; private set; } = [];
    public Guid Version { get; private set; }

    internal static async Task LockGroupAsync(TemplateDbContext db, Guid groupId, CancellationToken token)
    {
        // 在读取前取得写锁，避免 MySQL 可串行化事务从共享锁升级时与删除、续排互相死锁。
        if (db.Database.IsRelational())
            await db.OnCallGroups.Where(group => group.Id == groupId)
                .ExecuteUpdateAsync(update => update.SetProperty(group => group.ScheduleVersion, group => group.ScheduleVersion), token);
    }

    public static DateTime LocalToUtc(string value, TimeZoneInfo zone)
    {
        var local = DateTime.ParseExact(value, "yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture);
        // 夏令时回拨的重复时刻采用标准时间；跳过的时刻由 TimeZoneInfo 拒绝。
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
    }

    public static DateTime MoveLocalDays(DateTime utc, TimeZoneInfo zone, int days) =>
        TimeZoneInfo.ConvertTimeToUtc(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).AddDays(days), zone);

    public static DateTime WeekStart(OnCallGroup group, DateTime? requested = null, IStringLocalizer? localizer = null)
    {
        if (requested.HasValue) return DateTime.SpecifyKind(requested.Value, DateTimeKind.Utc);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(group.TimeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).Date;
        return TimeZoneInfo.ConvertTimeToUtc(local.AddDays(-((int)local.DayOfWeek - (int)group.WeekStartsOn + 7) % 7), zone);
    }

    public async Task<List<GeneratedOnCallShift>> Existing(Guid groupId, string[] userIds, DateTime start,
        CancellationToken cancellationToken, DateTime? endUtc = null, double minimumRestHours = 0)
    {
        var end = endUtc ?? start.AddDays(7);
        var buffer = Math.Max(28 * 24, minimumRestHours);
        var from = start.AddHours(-buffer);
        var until = end.AddHours(buffer);
        var rows = await db.OnCallShiftMembers.AsNoTracking()
            .Where(member => userIds.Contains(member.UserId) && member.Shift.EndsAtUtc > from && member.Shift.StartsAtUtc < until &&
                !(member.Shift.GroupId == groupId && member.Shift.EndsAtUtc > start && member.Shift.StartsAtUtc < end))
            .Select(member => new { member.UserId, member.Shift.StartsAtUtc, member.Shift.EndsAtUtc })
            .ToListAsync(cancellationToken);
        return rows.Select(row => new GeneratedOnCallShift(row.UserId, DateTime.SpecifyKind(row.StartsAtUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(row.EndsAtUtc, DateTimeKind.Utc))).ToList();
    }

    public async Task<List<string>> Save(SaveOnCallScheduleRequest? request, bool generate, CancellationToken cancellationToken, bool preview = false, bool clear = false)
    {
        var attempt = 0;
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<List<string>>(async token =>
        {
            if (attempt++ > 0) db.ChangeTracker.Clear();
            Warnings = [];
            TimelineWarnings = [];
            PreviewSlots = [];
            PreviewErrors = [];
            ReplacedSlots = [];
            IReadOnlyList<OnCallScheduleWeekRequest?>? adjacentWeeks = request?.AdjacentWeeks;
            IReadOnlyList<ClearOnCallScheduleRequest?>? clearRanges = request?.ClearRanges;
            if (request?.Slots == null || clearRanges == null || adjacentWeeks == null || adjacentWeeks.Any(week => week?.Slots == null) ||
                clearRanges.Any(range => range == null) || (clear && (!preview || generate || clearRanges.Count == 0)))
                return [localizer["The shift request format is invalid."].Value];
            IReadOnlyList<OnCallSlotRequest?> requestSlots = request.Slots;
            if (requestSlots.Any(slot => slot?.UserIds == null)) return [localizer["The shift request format is invalid."].Value];
            if ((generate && request.AdjacentWeeks.Count != 0) || request.AdjacentWeeks.Count > 2 ||
                request.AdjacentWeeks.Select(week => week.StartUtc).Distinct().Count() != request.AdjacentWeeks.Count ||
                request.AdjacentWeeks.Any(week => week.StartUtc != request.StartUtc.AddDays(-7) && week.StartUtc != request.StartUtc.AddDays(7)))
                return [localizer["Invalid adjacent week schedule request format; you can submit at most two weeks (previous and next)."].Value];
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
            await LockGroupAsync(db, request.GroupId, token);
            var group = await db.OnCallGroups.Include(item => item.Rule).ThenInclude(rule => rule!.HandoverPoints)
                .Include(item => item.Members).SingleOrDefaultAsync(item => item.Id == request.GroupId, token);
            if (group == null) return [localizer["On-call team does not exist."].Value];
            if (request.Version != group.ScheduleVersion)
                throw new DbUpdateConcurrencyException(localizer["The team or schedule was modified by another administrator. Please refresh the page and try again."].Value);
            Version = group.ScheduleVersion;
            var zone = TimeZoneInfo.FindSystemTimeZoneById(group.TimeZoneId);
            try
            {
                if (request.StartLocal != null) request.StartUtc = LocalToUtc(request.StartLocal, zone);
                if (request.EndLocal != null) request.EndUtc = LocalToUtc(request.EndLocal, zone);
                foreach (var slot in request.Slots)
                {
                    if (slot.StartLocal != null) slot.StartsAtUtc = LocalToUtc(slot.StartLocal, zone);
                    if (slot.EndLocal != null) slot.EndsAtUtc = LocalToUtc(slot.EndLocal, zone);
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                return [localizer["Invalid team local time. During daylight saving transitions, skipped times are invalid and repeated times use standard time."].Value];
            }
            var weeks = request.AdjacentWeeks.Prepend(new OnCallScheduleWeekRequest
                { StartUtc = request.StartUtc, Slots = request.Slots }).ToList();
            var from = weeks.Min(week => week.StartUtc);
            var until = request.EndUtc ?? MoveLocalDays(weeks.Max(week => week.StartUtc), zone, 7);
            if (until <= from) return [localizer["Shift end must be after its start."].Value];
            var saved = await db.OnCallShifts.Include(shift => shift.Members)
                .Where(shift => shift.GroupId == group.Id && shift.EndsAtUtc > from && shift.StartsAtUtc < until)
                .ToListAsync(token);
            ReplacedSlots = saved.Select(OnCallScheduleClearing.Slot).ToList();
            var canonical = saved.Select(OnCallScheduleClearing.Slot).ToList();
            var now = DateTime.UtcNow;
            var autoExtend = group.AutoExtend;
            for (var index = 0; index < request.ClearRanges.Count; index++)
            {
                var range = request.ClearRanges[index];
                range.StartUtc = DateTime.SpecifyKind(range.StartUtc, DateTimeKind.Utc);
                range.EndUtc = DateTime.SpecifyKind(range.EndUtc, DateTimeKind.Utc);
                if (range.EndUtc <= range.StartUtc || range.StartUtc < from || range.EndUtc > until ||
                    range.AutoExtendAction is not (null or "disable" or "regenerate") || generate)
                    return [localizer["Invalid schedule clearing range or action."].Value];
                if (range.IncludeHistory && !range.ConfirmHistory)
                    return [localizer["Confirm clearing historical shifts separately."].Value];
                if (autoExtend && range.AutoExtendAction == null)
                    return [localizer["Choose whether to disable automatic extension or regenerate the cleared range."].Value];
                if (range.AutoExtendAction == "disable") autoExtend = false;
                if (range.AutoExtendAction == "regenerate" && group.Rule == null)
                    return [localizer["Please bind a scheduling template to the team first."].Value];
                if (clear && index == request.ClearRanges.Count - 1) range.ClearedAtUtc = now;
                else
                {
                    if (!range.ClearedAtUtc.HasValue || range.ClearedAtUtc > now || range.ClearedAtUtc == DateTime.MinValue)
                        return [localizer["Invalid schedule clearing range or action."].Value];
                    range.ClearedAtUtc = DateTime.SpecifyKind(range.ClearedAtUtc.Value, DateTimeKind.Utc);
                    canonical = OnCallScheduleClearing.Apply(canonical, range);
                }
            }
            if (!preview && request.ClearRanges.Any(range => range.AutoExtendAction == "regenerate") && !request.ConfirmReplace)
                return [localizer["Confirm replacement of the existing schedule before generating."].Value];
            var ids = group.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray();
            var validUsers = await db.Users.Where(user => ids.Contains(user.Id)).Select(user => user.Id).ToListAsync(token);
            var slots = weeks.SelectMany(week => week.Slots).ToList();
            var rotationPositions = new Dictionary<DateTime, long?>();
            if (generate)
            {
                if (group.Rule == null) return [localizer["Please bind a scheduling template to the team first."].Value];
                if (saved.Count > 0 && !request.ConfirmReplace && !preview)
                    return [localizer["Confirm replacement of the existing schedule before generating."].Value];
                var count = group.Rule.HandoverPoints.Count == 0
                    ? (int)Math.Ceiling((until - from).TotalMinutes / (group.Rule.ShiftMinutes ?? group.Rule.ShiftHours * 60))
                    : ((int)Math.Ceiling((until - from).TotalDays / 7) + 2) * group.Rule.HandoverPoints.Count;
                var assignments = generator.GenerateRange(group.Rule, DateTime.SpecifyKind(group.AnchorUtc, DateTimeKind.Utc), ids,
                    DateTime.SpecifyKind(from, DateTimeKind.Utc), count, zone);
                rotationPositions = assignments.GroupBy(shift => shift.StartsAtUtc)
                    .ToDictionary(batch => batch.Key, batch => batch.First().RotationPosition);
                slots = assignments.Where(shift => shift.StartsAtUtc < until).GroupBy(shift => new { shift.StartsAtUtc, shift.EndsAtUtc })
                    .Select(batch => new OnCallSlotRequest { StartsAtUtc = batch.Key.StartsAtUtc, EndsAtUtc = batch.Key.EndsAtUtc,
                        UserIds = batch.Select(shift => shift.UserId).ToList() }).ToList();
                foreach (var slot in slots)
                    if (slot.EndsAtUtc > until) slot.EndsAtUtc = until;
            }
            IReadOnlyList<OnCallSlotRequest?> nullableSlots = slots;
            if (nullableSlots.Any(slot => slot?.UserIds == null)) return [localizer["The shift request format is invalid."].Value];
            foreach (var slot in slots)
            {
                if ((slot.EndsAtUtc ?? slot.StartsAtUtc.AddHours(8)) <= slot.StartsAtUtc)
                    return [localizer["Shift end must be after its start."].Value];
                var original = canonical.FirstOrDefault(item => (!slot.Id.HasValue || item.Id == slot.Id) &&
                    item.StartsAtUtc == slot.StartsAtUtc && OnCallScheduleClearing.End(item) == OnCallScheduleClearing.End(slot));
                if (slot.UserIds.Any(id => !validUsers.Contains(id) && !(original != null && original.UserIds.Contains(id))))
                    return [localizer["Cannot add employees who have left the team or do not belong to the team."].Value];
                var unchanged = original != null && OnCallScheduleClearing.SameAssignment(original, slot);
                slot.Source = generate || unchanged && original!.Source == OnCallShiftSource.Generated ||
                    preview && slot.Id == null && slot.Source == OnCallShiftSource.Generated
                    ? OnCallShiftSource.Generated : OnCallShiftSource.Manual;
                slot.RotationPosition = generate ? rotationPositions.GetValueOrDefault(slot.StartsAtUtc) : unchanged ? original!.RotationPosition : null;
            }
            if (clear)
            {
                ReplacedSlots = [];
                slots = OnCallScheduleClearing.Apply(slots, request.ClearRanges[^1], ReplacedSlots);
            }
            if (!preview && request.ClearRanges.Any(range => range.AutoExtendAction == "regenerate"))
                await RegenerateClearedRanges(group, slots, request.ClearRanges, from, token);
            var draft = slots.Select(slot => new OnCallShift { GroupId = group.Id, StartsAtUtc = slot.StartsAtUtc,
                EndsAtUtc = OnCallScheduleClearing.End(slot),
                Members = slot.UserIds.Distinct(StringComparer.Ordinal).Select(id => new OnCallShiftMember { UserId = id }).ToList() }).ToList();
            var retained = new List<OnCallShift>();
            if (draft.Count > 0)
            {
                var draftStart = draft.Min(shift => shift.StartsAtUtc);
                var draftEnd = draft.Max(shift => shift.EndsAtUtc);
                var replacedIds = saved.Select(shift => shift.Id).ToArray();
                retained = await db.OnCallShifts.AsNoTracking().Include(shift => shift.Members)
                    .Where(shift => shift.GroupId == group.Id && !replacedIds.Contains(shift.Id) &&
                        shift.EndsAtUtc > draftStart && shift.StartsAtUtc < draftEnd).ToListAsync(token);
            }
            var validatorLocalizer = localizerFactory.Create(typeof(OnCallScheduleValidator));
            var overlaps = OnCallScheduleValidator.OverlapErrors(draft, retained, validatorLocalizer);
            if (overlaps.Count > 0)
            {
                var assignedIds = draft.Concat(retained).SelectMany(shift => shift.Members.Select(member => member.UserId)).Distinct().ToArray();
                var names = await db.Users.Where(user => assignedIds.Contains(user.Id))
                    .ToDictionaryAsync(user => user.Id, user => user.DisplayName, token);
                overlaps = OnCallScheduleValidator.OverlapErrors(draft, retained, validatorLocalizer, names);
                if (!preview) return overlaps.Select(overlap => overlap.Message).Distinct().ToList();
            }
            if (preview)
            {
                PreviewSlots = slots;
                PreviewErrors = overlaps.Select(overlap => overlap.Message).Distinct().ToList();
                var existing = await Existing(group.Id, ids.Concat(slots.SelectMany(slot => slot.UserIds)).Distinct().ToArray(),
                    from, token, until, Math.Max(group.MinimumRestHours ?? 0, group.MaximumContinuousHours ?? 0));
                Warnings = OnCallScheduleValidator.Warnings(group, from, until, draft, existing,
                    localizerFactory.Create(typeof(OnCallScheduleValidator)));
                TimelineWarnings = OnCallScheduleValidator.TimelineWarnings(group, from, until, draft, existing,
                    validatorLocalizer).Concat(overlaps).ToList();
                Warnings = Warnings.Concat(overlaps.Select(overlap => overlap.Message)).Distinct().ToList();
                return [];
            }
            var replacements = new List<OnCallShift>();
            foreach (var slot in slots)
            {
                var original = saved.Except(replacements).FirstOrDefault(shift => slot.Id.HasValue ? shift.Id == slot.Id :
                    shift.StartsAtUtc == slot.StartsAtUtc && shift.EndsAtUtc == (slot.EndsAtUtc ?? slot.StartsAtUtc.AddHours(8)));
                var shift = original ?? new OnCallShift { GroupId = group.Id, MinimumStaff = group.MinimumShiftStaff ?? 1 };
                var selected = slot.UserIds.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
                shift.StartsAtUtc = DateTime.SpecifyKind(slot.StartsAtUtc, DateTimeKind.Utc);
                shift.EndsAtUtc = DateTime.SpecifyKind(slot.EndsAtUtc ?? slot.StartsAtUtc.AddHours(8), DateTimeKind.Utc);
                shift.Source = slot.Source;
                shift.RotationPosition = slot.RotationPosition;
                foreach (var member in shift.Members.Where(member => !selected.Contains(member.UserId)).ToList())
                {
                    db.OnCallShiftMembers.Remove(member);
                    shift.Members.Remove(member);
                }
                foreach (var id in selected.Except(shift.Members.Select(member => member.UserId)))
                    shift.Members.Add(new OnCallShiftMember { UserId = id });
                if (original == null) db.OnCallShifts.Add(shift);
                replacements.Add(shift);
            }
            var removed = saved.Except(replacements).ToList();
            var removedIds = removed.Select(shift => shift.Id).ToArray();
            db.RemoveRange(await db.Set<OnCallHandoverReceipt>().Where(receipt => removedIds.Contains(receipt.ShiftId)).ToListAsync(token));
            db.OnCallShifts.RemoveRange(removed);
            var history = await Existing(group.Id, ids.Concat(replacements.SelectMany(shift => shift.Members.Select(member => member.UserId))).Distinct().ToArray(),
                from, token, until, Math.Max(group.MinimumRestHours ?? 0, group.MaximumContinuousHours ?? 0));
            Warnings = OnCallScheduleValidator.Warnings(group, from, until, replacements, history,
                localizerFactory.Create(typeof(OnCallScheduleValidator)));
            if (request.ClearRanges.Any(range => range.AutoExtendAction == "disable")) group.AutoExtend = false;
            group.ScheduleVersion = Guid.NewGuid();
            Version = group.ScheduleVersion;
            await db.SaveChangesAsync(token);
            if (transaction != null) await transaction.CommitAsync(token);
            return [];
        }, cancellationToken);
    }

    private async Task RegenerateClearedRanges(OnCallGroup group, List<OnCallSlotRequest> slots,
        List<ClearOnCallScheduleRequest> ranges, DateTime from, CancellationToken token)
    {
        var previous = await db.OnCallShifts.AsNoTracking().Include(shift => shift.Members)
            .Where(shift => shift.GroupId == group.Id && shift.EndsAtUtc <= from)
            .OrderByDescending(shift => shift.EndsAtUtc).ThenByDescending(shift => shift.StartsAtUtc).ThenBy(shift => shift.Id)
            .FirstOrDefaultAsync(token);
        var rule = group.Rule!;
        var ids = group.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(group.TimeZoneId);
        foreach (var range in ranges.Where(range => range.AutoExtendAction == "regenerate").OrderBy(range => range.StartUtc))
        {
            var start = range.IncludeHistory || range.StartUtc > range.ClearedAtUtc!.Value ? range.StartUtc : range.ClearedAtUtc.Value;
            foreach (var gap in OnCallScheduleClearing.Gaps(slots, start, range.EndUtc))
            {
                var last = slots.Where(slot => OnCallScheduleClearing.End(slot) <= gap.Start)
                    .OrderByDescending(OnCallScheduleClearing.End).ThenByDescending(slot => slot.StartsAtUtc).ThenBy(slot => slot.Id).FirstOrDefault();
                var lastShift = last == null ? previous : new OnCallShift { StartsAtUtc = last.StartsAtUtc,
                    EndsAtUtc = OnCallScheduleClearing.End(last), RotationPosition = last.RotationPosition,
                    Members = last.UserIds.Select(id => new OnCallShiftMember { UserId = id }).ToList() };
                var count = rule.HandoverPoints.Count == 0
                    ? (int)Math.Ceiling((gap.End - gap.Start).TotalMinutes / (rule.ShiftMinutes ?? rule.ShiftHours * 60))
                    : ((int)Math.Ceiling((gap.End - gap.Start).TotalDays / 7) + 2) * rule.HandoverPoints.Count;
                var generated = generator.GenerateRange(rule, DateTime.SpecifyKind(group.AnchorUtc, DateTimeKind.Utc), ids,
                    DateTime.SpecifyKind(gap.Start, DateTimeKind.Utc), count, zone, OnCallMaintenanceWorker.RotationHistory(group, lastShift));
                slots.AddRange(generated.Where(shift => shift.StartsAtUtc < gap.End).GroupBy(shift => new { shift.StartsAtUtc, shift.EndsAtUtc })
                    .Select(batch => new OnCallSlotRequest { StartsAtUtc = batch.Key.StartsAtUtc,
                        EndsAtUtc = batch.Key.EndsAtUtc > gap.End ? gap.End : batch.Key.EndsAtUtc,
                        Source = OnCallShiftSource.Generated, RotationPosition = batch.First().RotationPosition,
                        UserIds = batch.Select(shift => shift.UserId).ToList() }));
            }
        }
    }
}

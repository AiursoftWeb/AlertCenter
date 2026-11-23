using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public class OnCallScheduleService(TemplateDbContext db, IOnCallScheduleGenerator generator,
    IStringLocalizer<OnCallScheduleService> localizer, IStringLocalizerFactory localizerFactory,
    TimeProvider? timeProvider = null)
{
    public const int MaxRangeDays = 92;
    private const int ResetStartToleranceMinutes = 10;

    public string? ValidateRange(DateTime start, DateTime end)
    {
        var now = DateTime.UtcNow;
        var earliest = now.AddYears(-10);
        var latest = now.AddYears(10);
        return start < earliest || start > latest || end < earliest || end > latest ||
            end - start > TimeSpan.FromDays(MaxRangeDays)
            ? localizer["The schedule range must not exceed {0} days and must be within 10 years of now.", MaxRangeDays].Value
            : null;
    }

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

    internal sealed record ResetResult(int DeletedCount, int GeneratedCount, DateTime? StartUtc, DateTime? EndUtc,
        bool AutoExtend, Guid Version, string PlanId);

    internal sealed class ResetPlanChangedException : Exception;

    internal Task<ResetResult?> ResetAsync(Guid id, bool regenerate, bool includeManual, bool preview, Guid version,
        string? planId, CancellationToken token, DateTime? previewStartUtc = null) => db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        db.ChangeTracker.Clear();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
        await LockGroupAsync(db, id, token);
        var group = await db.OnCallGroups.Include(item => item.Rule).ThenInclude(rule => rule!.HandoverPoints)
            .Include(item => item.Members).SingleOrDefaultAsync(item => item.Id == id, token);
        if (group == null) return null;
        if (group.ScheduleVersion != version) throw new DbUpdateConcurrencyException();

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var shifts = db.OnCallShifts.Where(shift => shift.GroupId == id);
        var removed = shifts.Where(shift => shift.StartsAtUtc > now && (includeManual || shift.Source == OnCallShiftSource.Generated));
        var retained = shifts.Where(shift => shift.StartsAtUtc <= now || (!includeManual && shift.Source != OnCallShiftSource.Generated));
        var through = now;
        List<OnCallShift> additions = [];
        if (regenerate)
        {
            var rule = group.Rule ?? throw new ArgumentException(localizer["Please bind a scheduling template to the team first."].Value);
            var error = new OnCallGroupFormService(generator, localizer).AutomaticExtensionError(rule, true, group.Members.Count);
            if (error != null) throw new ArgumentException(error);

            var anchor = DateTime.SpecifyKind(group.AnchorUtc, DateTimeKind.Utc);
            var activeUntil = await retained.Where(shift => shift.StartsAtUtc <= now && shift.EndsAtUtc > now)
                .MaxAsync(shift => (DateTime?)shift.EndsAtUtc, token);
            var start = anchor > now ? anchor : now;
            if (activeUntil > start) start = DateTime.SpecifyKind(activeUntil.Value, DateTimeKind.Utc);
            var zone = TimeZoneInfo.FindSystemTimeZoneById(group.TimeZoneId);
            through = OnCallScheduleGenerator.BoundaryAtOrAfter(rule, anchor, zone, now.AddDays(28));
            if (start >= through)
            {
                if (!preview && !string.IsNullOrEmpty(planId)) throw new ResetPlanChangedException();
                throw new ArgumentException(localizer["The next available shift starts outside the regeneration window."].Value);
            }

            // 空档预览从下一整分钟开始，确认时可沿用容差范围内的预览起点。
            if (start == now)
            {
                start = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(1);
                if (!preview && previewStartUtc is { } previewStart)
                {
                    if (previewStart.Ticks % TimeSpan.TicksPerMinute != 0 || previewStart < anchor ||
                        previewStart < now.AddMinutes(-ResetStartToleranceMinutes) || previewStart > start)
                        throw new ResetPlanChangedException();
                    start = previewStart;
                }
            }
            (additions, through) = await OnCallMaintenanceWorker.PlanExtensionAsync(generator, group, retained,
                start, through, token, localizerFactory.Create(typeof(OnCallScheduleValidator)));
        }

        var orderedRemovals = removed.OrderBy(shift => shift.Id);
        var deleted = preview ? null : await orderedRemovals.ToListAsync(token);
        var ids = deleted == null
            ? await orderedRemovals.Select(shift => shift.Id).ToArrayAsync(token)
            : deleted.Select(shift => shift.Id).ToArray();
        var currentPlanId = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            id, version, regenerate, includeManual, ids,
            Through = regenerate ? (DateTime?)through : null,
            Additions = additions.Select(shift => new
            {
                shift.StartsAtUtc, shift.EndsAtUtc, shift.RotationPosition,
                Users = shift.Members.Select(member => member.UserId).Order(StringComparer.Ordinal)
            })
        })));
        if (deleted != null)
        {
            if (!string.Equals(planId, currentPlanId, StringComparison.Ordinal))
                throw new ResetPlanChangedException();

            var receipts = await db.Set<OnCallHandoverReceipt>().Where(receipt => ids.Contains(receipt.ShiftId)).ToListAsync(token);
            db.RemoveRange(receipts);
            db.OnCallShifts.RemoveRange(deleted);
            db.OnCallShifts.AddRange(additions);
            group.AutoExtendedThroughUtc = through;
            group.ScheduleVersion = Guid.NewGuid();
            await db.SaveChangesAsync(token);
            if (transaction != null) await transaction.CommitAsync(token);
        }

        return new ResetResult(ids.Length, additions.Count, additions.FirstOrDefault()?.StartsAtUtc,
            additions.LastOrDefault()?.EndsAtUtc, group.AutoExtend, group.ScheduleVersion, currentPlanId);
    });

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
        var buffer = Math.Clamp(minimumRestHours, 28 * 24, 720);
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
                request.AdjacentWeeks.Any(week => (week.StartUtc - request.StartUtc).Duration() != TimeSpan.FromDays(7)))
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
            var lastWeekStart = weeks.Max(week => week.StartUtc);
            var rangeError = ValidateRange(from, lastWeekStart);
            if (rangeError != null) return [rangeError];
            var until = request.EndUtc ?? MoveLocalDays(lastWeekStart, zone, 7);
            if (until <= from) return [localizer["Shift end must be after its start."].Value];
            rangeError = ValidateRange(from, until);
            if (rangeError != null) return [rangeError];
            var anchor = DateTime.SpecifyKind(group.AnchorUtc, DateTimeKind.Utc);
            if (generate)
            {
                if (group.Rule == null) return [localizer["Please bind a scheduling template to the team first."].Value];
                if (until <= anchor) return [];
                from = OnCallScheduleGenerator.GenerationStart(group.Rule, anchor, zone, from);
                until = OnCallScheduleGenerator.BoundaryAtOrAfter(group.Rule, anchor, zone, until);
                rangeError = ValidateRange(from, until);
                if (rangeError != null) return [rangeError];
            }
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
            var replaceRange = generate || request.ReplaceRange;
            if (replaceRange && saved.Count > 0 && !request.ConfirmReplace && !preview)
                return [localizer["Confirm replacement of the existing schedule before generating."].Value];
            if (generate)
            {
                var rule = group.Rule!;
                var rotationHistory = await OnCallScheduleGenerator.LoadHistory(db.OnCallShifts.Where(shift => shift.GroupId == group.Id),
                    rule, ids, from, from, token);
                var assignments = generator.GenerateRange(rule, anchor, ids, from,
                    OnCallScheduleGenerator.ShiftCount(rule, from, until), zone, rotationHistory);
                rotationPositions = assignments.GroupBy(shift => shift.StartsAtUtc)
                    .ToDictionary(batch => batch.Key, batch => batch.First().RotationPosition);
                slots = assignments.Where(shift => shift.StartsAtUtc < until).GroupBy(shift => new { shift.StartsAtUtc, shift.EndsAtUtc })
                    .Select(batch => new OnCallSlotRequest { StartsAtUtc = batch.Key.StartsAtUtc, EndsAtUtc = batch.Key.EndsAtUtc,
                        UserIds = batch.Select(shift => shift.UserId).ToList() }).ToList();
            }
            IReadOnlyList<OnCallSlotRequest?> nullableSlots = slots;
            if (nullableSlots.Any(slot => slot?.UserIds == null)) return [localizer["The shift request format is invalid."].Value];
            foreach (var slot in slots)
            {
                if ((slot.EndsAtUtc ?? slot.StartsAtUtc.AddHours(8)) <= slot.StartsAtUtc)
                    return [localizer["Shift end must be after its start."].Value];
                if (replaceRange && (slot.StartsAtUtc < from || OnCallScheduleClearing.End(slot) > until))
                    return [localizer["The shift request format is invalid."].Value];
                var original = canonical.FirstOrDefault(item => (!slot.Id.HasValue || item.Id == slot.Id) &&
                    item.StartsAtUtc == slot.StartsAtUtc && OnCallScheduleClearing.End(item) == OnCallScheduleClearing.End(slot));
                var fragment = slot.FragmentOf.HasValue ? canonical.FirstOrDefault(item => item.Id == slot.FragmentOf &&
                    (!slot.Id.HasValue || slot.Id == item.Id && slot.StartsAtUtc == item.StartsAtUtc) &&
                    item.StartsAtUtc <= slot.StartsAtUtc && OnCallScheduleClearing.End(item) >= OnCallScheduleClearing.End(slot) &&
                    item.UserIds.ToHashSet(StringComparer.Ordinal).SetEquals(slot.UserIds)) : null;
                if (slot.UserIds.Any(id => !validUsers.Contains(id, StringComparer.Ordinal) &&
                    !canonical.Any(item => item.StartsAtUtc < OnCallScheduleClearing.End(slot) &&
                        OnCallScheduleClearing.End(item) > slot.StartsAtUtc && item.UserIds.Contains(id, StringComparer.Ordinal))))
                    return [localizer["Cannot add employees who have left the team or do not belong to the team."].Value];
                var preserved = original != null && OnCallScheduleClearing.SameAssignment(original, slot) ? original : fragment;
                if (!generate && preserved?.Source != OnCallShiftSource.Generated && group.Rule is { } rule && !slot.Id.HasValue &&
                    slot.Source == OnCallShiftSource.Generated && slot.RotationPosition is >= 0 and < long.MaxValue / 2 &&
                    rule.StaffPerShift >= 1 && rule.StaffPerShift <= ids.Length && slot.StartsAtUtc >= from &&
                    OnCallScheduleClearing.End(slot) <= until)
                {
                    var boundary = OnCallScheduleGenerator.GenerationStart(rule, anchor, zone, slot.StartsAtUtc);
                    var end = OnCallScheduleGenerator.NextBoundary(rule, zone, boundary);
                    var assignment = OnCallScheduleGenerator.AssignShift(rule, ids, slot.RotationPosition.Value, rule.StaffPerShift, boundary, end);
                    if (OnCallScheduleClearing.End(slot) <= end && assignment.Select(shift => shift.UserId)
                            .ToHashSet(StringComparer.Ordinal).SetEquals(slot.UserIds))
                    {
                        if (!preview && !request.ConfirmReplace && saved.Any(shift => shift.StartsAtUtc < OnCallScheduleClearing.End(slot) &&
                                shift.EndsAtUtc > slot.StartsAtUtc))
                            return [localizer["Confirm replacement of the existing schedule before generating."].Value];
                        continue;
                    }
                }
                slot.Source = generate || preserved?.Source == OnCallShiftSource.Generated ||
                    preview && slot.Id == null && slot.Source == OnCallShiftSource.Generated
                    ? OnCallShiftSource.Generated : OnCallShiftSource.Manual;
                slot.RotationPosition = generate ? rotationPositions.GetValueOrDefault(slot.StartsAtUtc) : preserved?.RotationPosition;
            }
            if (replaceRange)
                slots.AddRange(canonical.SelectMany(slot => OnCallScheduleClearing.Outside(slot, from, until, replacement: true)));
            if (clear)
            {
                ReplacedSlots = [];
                slots = OnCallScheduleClearing.Apply(slots, request.ClearRanges[^1], ReplacedSlots);
            }
            if (!preview && request.ClearRanges.Any(range => range.AutoExtendAction == "regenerate"))
                await RegenerateClearedRanges(group, slots, request.ClearRanges, from, token);
            if (!preview) slots = OnCallScheduleClearing.MergeGenerated(slots);
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
            var assignedIds = ids.Concat(draft.Concat(retained).SelectMany(shift => shift.Members.Select(member => member.UserId))).Distinct().ToArray();
            var names = await db.Users.AsNoTracking().Where(user => assignedIds.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, user => user.DisplayName, token);
            var overlaps = OnCallScheduleValidator.OverlapErrors(draft, retained, validatorLocalizer, names);
            if (overlaps.Count > 0 && !preview) return overlaps.Select(overlap => overlap.Message).Distinct().ToList();
            if (preview)
            {
                PreviewSlots = slots;
                PreviewErrors = overlaps.Select(overlap => overlap.Message).Distinct().ToList();
                var existing = await Existing(group.Id, ids.Concat(slots.SelectMany(slot => slot.UserIds)).Distinct().ToArray(),
                    from, token, until, Math.Max(group.MinimumRestHours ?? 0, group.MaximumContinuousHours ?? 0));
                Warnings = OnCallScheduleValidator.Warnings(group, from, until, draft, existing, validatorLocalizer, names);
                TimelineWarnings = OnCallScheduleValidator.TimelineWarnings(group, from, until, draft, existing,
                    validatorLocalizer, names).Concat(overlaps).ToList();
                Warnings = Warnings.Concat(overlaps.Select(overlap => overlap.Message)).Distinct().ToList();
                if (generate)
                {
                    request.StartUtc = from;
                    request.EndUtc = until;
                }
                return [];
            }
            var replacements = new List<OnCallShift>();
            var removedMembers = new HashSet<(Guid ShiftId, string UserId)>();
            foreach (var slot in slots)
            {
                var original = saved.Except(replacements).FirstOrDefault(shift => slot.Id.HasValue ? shift.Id == slot.Id :
                    !slot.FragmentOf.HasValue && shift.StartsAtUtc == slot.StartsAtUtc && shift.EndsAtUtc == OnCallScheduleClearing.End(slot));
                var shift = original ?? new OnCallShift { GroupId = group.Id, MinimumStaff = group.MinimumShiftStaff ?? 1 };
                var selected = slot.UserIds.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
                shift.StartsAtUtc = DateTime.SpecifyKind(slot.StartsAtUtc, DateTimeKind.Utc);
                shift.EndsAtUtc = DateTime.SpecifyKind(slot.EndsAtUtc ?? slot.StartsAtUtc.AddHours(8), DateTimeKind.Utc);
                shift.Source = slot.Source;
                shift.RotationPosition = slot.RotationPosition;
                foreach (var member in shift.Members.Where(member => !selected.Contains(member.UserId)).ToList())
                {
                    removedMembers.Add((shift.Id, member.UserId));
                    db.OnCallShiftMembers.Remove(member);
                    shift.Members.Remove(member);
                }
                foreach (var id in selected.Except(shift.Members.Select(member => member.UserId)))
                    shift.Members.Add(new OnCallShiftMember { UserId = id });
                if (original == null) db.OnCallShifts.Add(shift);
                replacements.Add(shift);
            }
            var removed = saved.Except(replacements).ToList();
            var removedIds = removed.Select(shift => shift.Id).ToHashSet();
            var receiptShiftIds = removedIds.Concat(removedMembers.Select(member => member.ShiftId)).Distinct().ToArray();
            var receipts = await db.Set<OnCallHandoverReceipt>().Where(receipt => receiptShiftIds.Contains(receipt.ShiftId)).ToListAsync(token);
            db.RemoveRange(receipts.Where(receipt => removedIds.Contains(receipt.ShiftId) || removedMembers.Contains((receipt.ShiftId, receipt.UserId))));
            db.OnCallShifts.RemoveRange(removed);
            var history = await Existing(group.Id, ids.Concat(replacements.SelectMany(shift => shift.Members.Select(member => member.UserId))).Distinct().ToArray(),
                from, token, until, Math.Max(group.MinimumRestHours ?? 0, group.MaximumContinuousHours ?? 0));
            Warnings = OnCallScheduleValidator.Warnings(group, from, until, replacements, history, validatorLocalizer, names);
            if (request.ClearRanges.Any(range => range.AutoExtendAction == "disable")) group.AutoExtend = false;
            group.ScheduleVersion = Guid.NewGuid();
            Version = group.ScheduleVersion;
            await db.SaveChangesAsync(token);
            if (transaction != null) await transaction.CommitAsync(token);
            if (generate)
            {
                request.StartUtc = from;
                request.EndUtc = until;
            }
            return [];
        }, cancellationToken);
    }

    private async Task RegenerateClearedRanges(OnCallGroup group, List<OnCallSlotRequest> slots,
        List<ClearOnCallScheduleRequest> ranges, DateTime from, CancellationToken token)
    {
        var previous = db.OnCallShifts.Where(shift => shift.GroupId == group.Id && shift.EndsAtUtc <= from);
        var rule = group.Rule!;
        var ids = group.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(group.TimeZoneId);
        var anchor = DateTime.SpecifyKind(group.AnchorUtc, DateTimeKind.Utc);
        foreach (var range in ranges.Where(range => range.AutoExtendAction == "regenerate").OrderBy(range => range.StartUtc))
        {
            var start = range.IncludeHistory || range.StartUtc > range.ClearedAtUtc!.Value ? range.StartUtc : range.ClearedAtUtc.Value;
            if (start < anchor) start = anchor;
            if (start >= range.EndUtc) continue;
            foreach (var gap in OnCallScheduleClearing.Gaps(slots, start, range.EndUtc))
            {
                var boundary = OnCallScheduleGenerator.GenerationStart(rule, anchor, zone, gap.Start);
                var history = await OnCallScheduleGenerator.LoadHistory(previous, rule, ids, boundary, gap.Start, token);
                var preceding = slots.Where(slot => OnCallScheduleClearing.End(slot) <= gap.Start)
                    .OrderByDescending(OnCallScheduleClearing.End).ThenByDescending(slot => slot.StartsAtUtc).ThenBy(slot => slot.Id)
                    .ToList();
                var last = preceding.FirstOrDefault(slot => OnCallScheduleClearing.End(slot) >= boundary)
                    ?? preceding.FirstOrDefault(slot => slot.RotationPosition.HasValue);
                if (last != null)
                    history.AddRange(OnCallScheduleGenerator.RotationHistory(rule, ids, new OnCallShift
                    {
                        StartsAtUtc = last.StartsAtUtc, EndsAtUtc = OnCallScheduleClearing.End(last),
                        RotationPosition = last.RotationPosition,
                        Members = last.UserIds.Select(id => new OnCallShiftMember { UserId = id }).ToList()
                    }));
                var generated = generator.GenerateRange(rule, anchor, ids, boundary,
                    OnCallScheduleGenerator.ShiftCount(rule, boundary, gap.End), zone, history,
                    OnCallScheduleGenerator.FragmentPosition(history, boundary));
                foreach (var batch in generated.Where(shift => shift.StartsAtUtc < gap.End)
                             .GroupBy(shift => (shift.StartsAtUtc, shift.EndsAtUtc)))
                {
                    var (left, right) = batch.Key;
                    var fragment = slots.FirstOrDefault(slot => slot.Source == OnCallShiftSource.Generated &&
                        slot.RotationPosition.HasValue && slot.StartsAtUtc >= left && OnCallScheduleClearing.End(slot) <= right);
                    slots.Add(new OnCallSlotRequest
                    {
                        StartsAtUtc = left < gap.Start ? gap.Start : left,
                        EndsAtUtc = right > gap.End ? gap.End : right,
                        Source = OnCallShiftSource.Generated,
                        RotationPosition = fragment?.RotationPosition ?? batch.First().RotationPosition,
                        UserIds = fragment?.UserIds.ToList() ?? batch.Select(shift => shift.UserId).ToList()
                    });
                }
            }
        }
    }
}

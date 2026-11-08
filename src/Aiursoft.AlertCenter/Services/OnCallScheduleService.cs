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
    public List<OnCallScheduleValidator.TimelineWarning> TimelineWarnings { get; private set; } = [];
    public Guid Version { get; private set; }

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

    public async Task<List<string>> Save(SaveOnCallScheduleRequest? request, bool generate, CancellationToken cancellationToken, bool preview = false)
    {
        var attempt = 0;
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<List<string>>(async token =>
        {
            if (attempt++ > 0) db.ChangeTracker.Clear();
            Warnings = [];
            IReadOnlyList<OnCallScheduleWeekRequest?>? adjacentWeeks = request?.AdjacentWeeks;
            if (request?.Slots == null || adjacentWeeks == null || adjacentWeeks.Any(week => week?.Slots == null))
                return [localizer["The shift request format is invalid."].Value];
            IReadOnlyList<OnCallSlotRequest?> requestSlots = request.Slots;
            if (requestSlots.Any(slot => slot?.UserIds == null)) return [localizer["The shift request format is invalid."].Value];
            if ((generate && request.AdjacentWeeks.Count != 0) || request.AdjacentWeeks.Count > 2 ||
                request.AdjacentWeeks.Select(week => week.StartUtc).Distinct().Count() != request.AdjacentWeeks.Count ||
                request.AdjacentWeeks.Any(week => week.StartUtc != request.StartUtc.AddDays(-7) && week.StartUtc != request.StartUtc.AddDays(7)))
                return [localizer["Invalid adjacent week schedule request format; you can submit at most two weeks (previous and next)."].Value];
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
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
            ReplacedSlots = saved.Select(shift => new OnCallSlotRequest { Id = shift.Id,
                StartsAtUtc = DateTime.SpecifyKind(shift.StartsAtUtc, DateTimeKind.Utc),
                EndsAtUtc = DateTime.SpecifyKind(shift.EndsAtUtc, DateTimeKind.Utc),
                UserIds = shift.Members.Select(member => member.UserId).ToList() }).ToList();
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
                var original = saved.FirstOrDefault(shift => slot.Id.HasValue ? shift.Id == slot.Id :
                    shift.StartsAtUtc == slot.StartsAtUtc && shift.EndsAtUtc == (slot.EndsAtUtc ?? slot.StartsAtUtc.AddHours(8)));
                if (slot.UserIds.Any(id => !validUsers.Contains(id) && !(original != null && original.StartsAtUtc == slot.StartsAtUtc &&
                    original.EndsAtUtc == (slot.EndsAtUtc ?? slot.StartsAtUtc.AddHours(8)) && original.Members.Any(member => member.UserId == id))))
                    return [localizer["Cannot add employees who have left the team or do not belong to the team."].Value];
            }
            if (preview)
            {
                PreviewSlots = slots;
                var draft = slots.Select(slot => new OnCallShift { StartsAtUtc = slot.StartsAtUtc,
                    EndsAtUtc = slot.EndsAtUtc ?? slot.StartsAtUtc.AddHours(8),
                    Members = slot.UserIds.Distinct().Select(id => new OnCallShiftMember { UserId = id }).ToList() }).ToList();
                var existing = await Existing(group.Id, ids.Concat(slots.SelectMany(slot => slot.UserIds)).Distinct().ToArray(),
                    from, token, until, Math.Max(group.MinimumRestHours ?? 0, group.MaximumContinuousHours ?? 0));
                Warnings = OnCallScheduleValidator.Warnings(group, from, until, draft, existing,
                    localizerFactory.Create(typeof(OnCallScheduleValidator)));
                TimelineWarnings = OnCallScheduleValidator.TimelineWarnings(group, from, until, draft, existing,
                    localizerFactory.Create(typeof(OnCallScheduleValidator)));
                return [];
            }
            var replacements = new List<OnCallShift>();
            foreach (var slot in slots)
            {
                var original = saved.Except(replacements).FirstOrDefault(shift => slot.Id.HasValue ? shift.Id == slot.Id :
                    shift.StartsAtUtc == slot.StartsAtUtc && shift.EndsAtUtc == (slot.EndsAtUtc ?? slot.StartsAtUtc.AddHours(8)));
                var shift = original ?? new OnCallShift { GroupId = group.Id, MinimumStaff = group.MinimumShiftStaff ?? 1 };
                var selected = slot.UserIds.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
                var changed = original == null || shift.StartsAtUtc != slot.StartsAtUtc || shift.EndsAtUtc != (slot.EndsAtUtc ?? slot.StartsAtUtc.AddHours(8)) ||
                    !selected.SetEquals(shift.Members.Select(member => member.UserId));
                shift.StartsAtUtc = DateTime.SpecifyKind(slot.StartsAtUtc, DateTimeKind.Utc);
                shift.EndsAtUtc = DateTime.SpecifyKind(slot.EndsAtUtc ?? slot.StartsAtUtc.AddHours(8), DateTimeKind.Utc);
                if (generate)
                {
                    shift.Source = OnCallShiftSource.Generated;
                    shift.RotationPosition = rotationPositions[shift.StartsAtUtc];
                }
                else if (changed)
                {
                    shift.Source = OnCallShiftSource.Manual;
                    shift.RotationPosition = null;
                }
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
            db.OnCallShifts.RemoveRange(saved.Except(replacements));
            var history = await Existing(group.Id, ids.Concat(replacements.SelectMany(shift => shift.Members.Select(member => member.UserId))).Distinct().ToArray(),
                from, token, until, Math.Max(group.MinimumRestHours ?? 0, group.MaximumContinuousHours ?? 0));
            Warnings = OnCallScheduleValidator.Warnings(group, from, until, replacements, history,
                localizerFactory.Create(typeof(OnCallScheduleValidator)));
            group.ScheduleVersion = Guid.NewGuid();
            Version = group.ScheduleVersion;
            await db.SaveChangesAsync(token);
            if (transaction != null) await transaction.CommitAsync(token);
            return [];
        }, cancellationToken);
    }
}

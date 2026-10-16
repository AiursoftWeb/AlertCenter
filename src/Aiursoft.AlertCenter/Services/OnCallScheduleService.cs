using System.Data;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public class OnCallScheduleService(TemplateDbContext db, IOnCallScheduleGenerator generator, IStringLocalizer<OnCallScheduleService> localizer, IStringLocalizerFactory localizerFactory)
{
    public List<string> Warnings { get; private set; } = [];

    public static DateTime WeekStart(OnCallGroup group, DateTime? requested = null, IStringLocalizer? localizer = null)
    {
        if (requested?.Kind == DateTimeKind.Local) throw new ArgumentException((localizer is null ? "Please specify the scheduling period using UTC time." : localizer["Please specify the scheduling period using UTC time."].Value));
        var anchor = DateTime.SpecifyKind(group.AnchorUtc, DateTimeKind.Utc);
        var start = requested.HasValue ? DateTime.SpecifyKind(requested.Value, DateTimeKind.Utc)
            : anchor.AddDays(Math.Max(0, (long)((DateTime.UtcNow - anchor).TotalDays / 7)) * 7);
        if (start < anchor || start < DateTime.MinValue.AddDays(28) || start > DateTime.MaxValue.AddDays(-28) ||
            (start - anchor).Ticks % TimeSpan.FromDays(7).Ticks != 0)
            throw new ArgumentException((localizer is null ? "Please select a complete seven-day on-call cycle starting from the team anchor." : localizer["Please select a complete seven-day on-call cycle starting from the team anchor."].Value));
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
        var attempt = 0;
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<List<string>>(async retryToken =>
        {
            if (attempt++ > 0) db.ChangeTracker.Clear();
            Warnings = [];
            // JSON input can contain null collections and entries despite the DTO annotations.
            IReadOnlyList<OnCallScheduleWeekRequest?>? adjacentWeeks = request?.AdjacentWeeks;
            if (request == null || adjacentWeeks == null || adjacentWeeks.Count > 2 ||
                adjacentWeeks.Any(week => week == null))
                return [localizer["Invalid adjacent week schedule request format; you can submit at most two weeks (previous and next)."].Value];
            if (generate && request.AdjacentWeeks.Count != 0)
                return [localizer["Auto-generation cannot modify adjacent weekly schedules simultaneously."].Value];
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, retryToken) : null;
            var group = await db.OnCallGroups.Include(item => item.Rule).Include(item => item.Members)
                .SingleOrDefaultAsync(item => item.Id == request.GroupId, retryToken);
            if (group == null) return [localizer["On-call team does not exist."].Value];
            if (request.Version != group.ScheduleVersion)
                throw new DbUpdateConcurrencyException(localizer["The team or schedule was modified by another administrator. Please refresh the page and try again."].Value);
            var start = WeekStart(group, request.StartUtc, localizer: localizer);
            if (request.AdjacentWeeks.Any(week => week.StartUtc.Kind == DateTimeKind.Local ||
                    (week.StartUtc != start.AddDays(-7) && week.StartUtc != start.AddDays(7))) ||
                request.AdjacentWeeks.Select(week => week.StartUtc).Distinct().Count() != request.AdjacentWeeks.Count)
                return [localizer["Adjacent weeks can only be the previous or next week relative to the current week, and must not be duplicated."].Value];
            var requests = request.AdjacentWeeks.Prepend(new OnCallScheduleWeekRequest { StartUtc = start, Slots = request.Slots })
                .Select(week => new OnCallScheduleWeekRequest { StartUtc = WeekStart(group, week.StartUtc, localizer: localizer), Slots = week.Slots })
                .OrderBy(week => week.StartUtc).ToList();
            var from = requests[0].StartUtc;
            var until = requests[^1].StartUtc.AddDays(7);
            var savedSlots = await db.OnCallShifts.Include(shift => shift.Members)
                .Where(shift => shift.GroupId == group.Id && shift.StartsAtUtc < until && shift.EndsAtUtc > from)
                .OrderBy(shift => shift.StartsAtUtc).ToListAsync(retryToken);
            if (generate && savedSlots.Count != 0) return [localizer["A saved schedule exists for this week. Please use manual adjustments; auto-generation will not overwrite existing arrangements."].Value];
            var ids = group.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray();
            var currentIds = ids.ToHashSet(StringComparer.Ordinal);
            var allowedIds = ids.Concat(savedSlots.SelectMany(slot => slot.Members.Select(member => member.UserId))).Distinct().ToArray();
            var names = await db.Users.Where(user => allowedIds.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, user => user.DisplayName, retryToken);
            // Exclude old assignments throughout the edited range before validating the new weeks together.
            var existing = await Existing(group.Id, allowedIds, from, until, retryToken);
            var weeks = new List<ScheduleWeek>();
            foreach (var week in requests)
            {
                var end = week.StartUtc.AddDays(7);
                var slots = savedSlots.Where(slot => slot.StartsAtUtc < end && slot.EndsAtUtc > week.StartUtc).ToList();
                var create = slots.Count == 0;
                if (!create && (slots.Count != 21 || slots.Where((slot, index) =>
                        slot.StartsAtUtc != week.StartUtc.AddHours(index * 8) || slot.EndsAtUtc != slot.StartsAtUtc.AddHours(8)).Any()))
                    return [localizer["Existing schedule time boundaries are incomplete; cannot overwrite."].Value];
                var restHours = create ? group.Rule?.RestHours ?? 16 : slots[0].RestHours;
                if (restHours is < 0 or > 168 || slots.Any(slot => slot.RestHours != restHours))
                    return [localizer["The rest duration in the schedule is invalid or inconsistent."].Value];
                var minimumStaff = create ? group.Rule?.MinimumStaff ?? 1 : slots[0].MinimumStaff;
                if (minimumStaff is < 1 or > 200 || slots.Any(slot => slot.MinimumStaff != minimumStaff))
                    return [localizer["The minimum headcount in the schedule is invalid or inconsistent."].Value];
                List<GeneratedOnCallShift> assignments;
                if (generate)
                {
                    if (group.Rule == null) return [localizer["Please bind a scheduling rule to the team first."].Value];
                    var sharedIds = await db.OnCallGroupMembers.AsNoTracking()
                        .Where(member => member.GroupId != group.Id && ids.Contains(member.UserId))
                        .Select(member => member.UserId).Distinct().ToListAsync(retryToken);
                    var automaticIds = ids.Except(sharedIds, StringComparer.Ordinal).ToArray();
                    var required = Math.Max(3, 1 + (int)Math.Ceiling(group.Rule.RestHours / 8.0)) * group.Rule.MinimumStaff;
                    if (automaticIds.Length < required)
                        return [string.Format(localizer["After auto-scheduling skips members of multi-on-call groups, only {0} remain, but at least {1} are required. Please add members to single groups or arrange manually."].Value, automaticIds.Length, required)];
                    assignments = generator.Generate(group.Rule, DateTime.SpecifyKind(group.AnchorUtc, DateTimeKind.Utc),
                        automaticIds, week.StartUtc, 21, existing).ToList();
                }
                else
                {
                    if (!HasValidSlots(week.Slots, week.StartUtc, end))
                        return [localizer["All 21 shifts for this week must be submitted, and each time slot must appear only once."].Value];
                    var savedMembers = slots.ToDictionary(slot => slot.StartsAtUtc,
                        slot => slot.Members.Select(member => member.UserId).ToHashSet(StringComparer.Ordinal));
                    if (week.Slots.Any(slot => slot.UserIds.Any(id => !currentIds.Contains(id) &&
                            (!savedMembers.TryGetValue(slot.StartsAtUtc, out var saved) || !saved.Contains(id)))))
                        return [localizer["Cannot add employees who have left the team or do not belong to the team."].Value];
                    assignments = week.Slots.SelectMany(slot => slot.UserIds.Select(id => new GeneratedOnCallShift(id,
                        DateTime.SpecifyKind(slot.StartsAtUtc, DateTimeKind.Utc), DateTime.SpecifyKind(slot.StartsAtUtc.AddHours(8), DateTimeKind.Utc), restHours))).ToList();
                }
                weeks.Add(new ScheduleWeek(week.StartUtc, slots, restHours, minimumStaff, assignments));
            }
            var errors = weeks.SelectMany(week => OnCallScheduleValidator.Validate(week.StartUtc, 21, week.RestHours,
                week.Assignments, existing.Concat(weeks.Where(peer => peer.StartUtc != week.StartUtc)
                    .SelectMany(peer => peer.Assignments)).ToList(), names, localizer: localizerFactory.Create(typeof(OnCallScheduleValidator)))).Distinct().ToList();
            if (errors.Count != 0) return errors;
            Warnings = weeks.SelectMany(week => OnCallScheduleValidator.StaffingWarnings(week.StartUtc, 21,
                week.MinimumStaff, week.Assignments, localizerFactory.Create(typeof(OnCallScheduleValidator)))).ToList();
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
            await db.SaveChangesAsync(retryToken);
            if (transaction != null) await transaction.CommitAsync(retryToken);
            return [];
        }, cancellationToken);
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

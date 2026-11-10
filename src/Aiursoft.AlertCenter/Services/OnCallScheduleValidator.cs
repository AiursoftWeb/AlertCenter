using Microsoft.Extensions.Localization;
using Aiursoft.AlertCenter.Entities;
namespace Aiursoft.AlertCenter.Services;

public static class OnCallScheduleValidator
{
    public record TimelineWarning(DateTime StartsAtUtc, DateTime EndsAtUtc, string Message);
    public record ScheduleWarning(string? UserId, string Message);

    public static List<TimelineWarning> OverlapErrors(IReadOnlyList<OnCallShift> shifts,
        IReadOnlyList<OnCallShift> retained, IStringLocalizer? localizer = null, IReadOnlyDictionary<string, string>? names = null)
    {
        var result = new List<TimelineWarning>();
        var assignments = shifts.Select(shift => (Shift: shift, IsDraft: true))
            .Concat(retained.Select(shift => (Shift: shift, IsDraft: false)))
            .SelectMany(item => item.Shift.Members.Select(member => member.UserId).Distinct(StringComparer.Ordinal)
                .DefaultIfEmpty(string.Empty).Select(userId => (item.Shift, item.IsDraft, UserId: userId)));
        foreach (var person in assignments.GroupBy(item => (item.Shift.GroupId, item.UserId)))
        {
            var active = new List<(OnCallShift Shift, bool IsDraft, string UserId)>();
            foreach (var current in person.OrderBy(item => item.Shift.StartsAtUtc))
            {
                if (current.Shift.EndsAtUtc <= current.Shift.StartsAtUtc) continue;
                active.RemoveAll(item => item.Shift.EndsAtUtc <= current.Shift.StartsAtUtc);
                foreach (var previous in active.Where(item => item.IsDraft || current.IsDraft))
                {
                    var name = person.Key.UserId.Length == 0 ? localizer?["Unassigned"].Value ?? "Unassigned"
                        : names?.GetValueOrDefault(person.Key.UserId) ?? person.Key.UserId;
                    const string format = "Cannot save overlapping shifts for {0}: {1:yyyy-MM-dd HH:mm}–{2:yyyy-MM-dd HH:mm} UTC and {3:yyyy-MM-dd HH:mm}–{4:yyyy-MM-dd HH:mm} UTC.";
                    var message = string.Format(System.Globalization.CultureInfo.InvariantCulture, localizer?[format].Value ?? format,
                        name, previous.Shift.StartsAtUtc, previous.Shift.EndsAtUtc, current.Shift.StartsAtUtc, current.Shift.EndsAtUtc);
                    result.Add(new(current.Shift.StartsAtUtc,
                        previous.Shift.EndsAtUtc < current.Shift.EndsAtUtc ? previous.Shift.EndsAtUtc : current.Shift.EndsAtUtc, message));
                }
                active.Add(current);
            }
        }
        return result;
    }

    public static List<TimelineWarning> TimelineWarnings(OnCallGroup group, DateTime start, DateTime end,
        IReadOnlyList<OnCallShift> shifts, IReadOnlyList<GeneratedOnCallShift> existing, IStringLocalizer localizer,
        IReadOnlyDictionary<string, string>? names = null)
    {
        var result = new List<TimelineWarning>();
        var covered = start;
        foreach (var shift in shifts.OrderBy(shift => shift.StartsAtUtc))
        {
            if (shift.Members.Count > 0)
            {
                if (shift.StartsAtUtc > covered)
                    result.Add(new(covered, shift.StartsAtUtc < end ? shift.StartsAtUtc : end, localizer["Schedule contains a coverage gap."].Value));
                if (shift.EndsAtUtc > covered) covered = shift.EndsAtUtc;
            }
            var others = shifts.Where(other => other != shift).SelectMany(other => other.Members.Select(member =>
                new GeneratedOnCallShift(member.UserId, other.StartsAtUtc, other.EndsAtUtc))).Concat(existing).ToList();
            foreach (var warning in Warnings(group, shift.StartsAtUtc, shift.EndsAtUtc, [shift], others, localizer, names)
                         .Where(warning => warning != localizer["Schedule contains a coverage gap."].Value))
                result.Add(new(shift.StartsAtUtc, shift.EndsAtUtc, warning));
            if (shifts.Any(other => other != shift && other.StartsAtUtc < shift.EndsAtUtc && other.EndsAtUtc > shift.StartsAtUtc))
                result.Add(new(shift.StartsAtUtc, shift.EndsAtUtc, localizer["Schedule contains overlapping shifts."].Value));
        }
        if (covered < end) result.Add(new(covered, end, localizer["Schedule contains a coverage gap."].Value));
        return result;
    }

    public static List<string> Warnings(OnCallGroup group, DateTime start, DateTime end,
        IReadOnlyList<OnCallShift> shifts, IReadOnlyList<GeneratedOnCallShift> existing, IStringLocalizer localizer,
        IReadOnlyDictionary<string, string>? names = null) =>
        WarningDetails(group, start, end, shifts, existing, localizer)
            .Select(warning => warning.UserId == null ? warning.Message
                : $"{names?.GetValueOrDefault(warning.UserId) ?? warning.UserId}: {warning.Message}").Distinct().ToList();

    public static List<ScheduleWarning> WarningDetails(OnCallGroup group, DateTime start, DateTime end,
        IReadOnlyList<OnCallShift> shifts, IReadOnlyList<GeneratedOnCallShift> existing, IStringLocalizer localizer)
    {
        var warnings = new List<ScheduleWarning>();
        var coveredUntil = start;
        DateTime? previousEnd = null;
        foreach (var shift in shifts.OrderBy(shift => shift.StartsAtUtc))
        {
            if (shift.StartsAtUtc > coveredUntil) warnings.Add(new(null, localizer["Schedule contains a coverage gap."].Value));
            if (previousEnd.HasValue && shift.StartsAtUtc < previousEnd) warnings.Add(new(null, localizer["Schedule contains overlapping shifts."].Value));
            if (!previousEnd.HasValue || shift.EndsAtUtc > previousEnd) previousEnd = shift.EndsAtUtc;
            if (shift.Members.Count > 0 && shift.EndsAtUtc > coveredUntil) coveredUntil = shift.EndsAtUtc;
            if (shift.Members.Count == 0) warnings.Add(new(null, localizer["Shift has no on-duty engineers."].Value));
            if (group.MinimumShiftStaff.HasValue && shift.Members.Count < group.MinimumShiftStaff)
                warnings.Add(new(null, localizer["Shift staffing is below the team threshold."].Value));
            if (shift.Members.Any(member => group.Members.All(person => person.UserId != member.UserId)))
                warnings.Add(new(null, localizer["Future shift includes a departed team member."].Value));
        }
        if (coveredUntil < end) warnings.Add(new(null, localizer["Schedule contains a coverage gap."].Value));
        var assignments = shifts.SelectMany(shift => shift.Members.Select(member =>
            new GeneratedOnCallShift(member.UserId, shift.StartsAtUtc, shift.EndsAtUtc))).Concat(existing);
        foreach (var person in assignments.GroupBy(shift => shift.UserId))
        {
            DateTime? continuousStart = null;
            DateTime? previousAssignmentEnd = null;
            var previousInRange = false;
            foreach (var shift in person.OrderBy(shift => shift.StartsAtUtc))
            {
                var inRange = shift.EndsAtUtc > start && shift.StartsAtUtc < end;
                if ((inRange || previousInRange) && previousAssignmentEnd.HasValue && shift.StartsAtUtc < previousAssignmentEnd)
                    warnings.Add(new(null, localizer["Schedule contains overlapping shifts."].Value));
                if ((inRange || previousInRange) && previousAssignmentEnd.HasValue && group.MinimumRestHours.HasValue &&
                    (shift.StartsAtUtc - previousAssignmentEnd.Value).TotalHours < group.MinimumRestHours)
                    warnings.Add(new(person.Key, $"{localizer["An engineer has insufficient rest between shifts."].Value} ({shift.StartsAtUtc:yyyy-MM-dd HH:mm} UTC)"));
                if (!previousAssignmentEnd.HasValue || shift.StartsAtUtc > previousAssignmentEnd) continuousStart = shift.StartsAtUtc;
                previousAssignmentEnd = previousAssignmentEnd > shift.EndsAtUtc ? previousAssignmentEnd : shift.EndsAtUtc;
                previousInRange = inRange;
                if (inRange && group.MaximumContinuousHours.HasValue &&
                    (previousAssignmentEnd.Value - continuousStart!.Value).TotalHours > group.MaximumContinuousHours)
                    warnings.Add(new(person.Key, $"{localizer["An engineer exceeds the continuous on-call threshold."].Value} ({shift.StartsAtUtc:yyyy-MM-dd HH:mm} UTC)"));
            }
        }
        return warnings.Distinct().ToList();
    }
    public static List<string> Validate(DateTime start, int count, int restHours, IReadOnlyList<GeneratedOnCallShift> shifts, IReadOnlyList<GeneratedOnCallShift> existing, IReadOnlyDictionary<string, string> members, IStringLocalizer? localizer = null)
    {
        var errors = new List<string>();
        foreach (var shift in shifts.OrderBy(shift => shift.StartsAtUtc))
        {
            if (!members.ContainsKey(shift.UserId))
            {
                errors.Add((localizer is null ? "Includes employees not belonging to the current team." : localizer["Includes employees not belonging to the current team."].Value));
                continue;
            }
            if (shift.EndsAtUtc <= shift.StartsAtUtc)
                errors.Add(localizer is null ? "Shift end must be after its start." : localizer["Shift end must be after its start."].Value);
        }
        var intervals = shifts.Where(shift => members.ContainsKey(shift.UserId)).Select(shift => new OnCallShift
        {
            StartsAtUtc = shift.StartsAtUtc, EndsAtUtc = shift.EndsAtUtc,
            Members = [new() { UserId = shift.UserId }]
        }).ToList();
        errors.AddRange(OverlapErrors(intervals, [], localizer, members).Select(error => error.Message));
        return errors.Distinct().ToList();
    }
}

using Microsoft.Extensions.Localization;
using Aiursoft.AlertCenter.Entities;
namespace Aiursoft.AlertCenter.Services;

public static class OnCallScheduleValidator
{
    public record TimelineWarning(DateTime StartsAtUtc, DateTime EndsAtUtc, string Message);

    public static List<TimelineWarning> TimelineWarnings(OnCallGroup group, DateTime start, DateTime end,
        IReadOnlyList<OnCallShift> shifts, IReadOnlyList<GeneratedOnCallShift> existing, IStringLocalizer localizer)
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
            foreach (var warning in Warnings(group, shift.StartsAtUtc, shift.EndsAtUtc, [shift], others, localizer)
                         .Where(warning => warning != localizer["Schedule contains a coverage gap."].Value))
                result.Add(new(shift.StartsAtUtc, shift.EndsAtUtc, warning));
            if (shifts.Any(other => other != shift && other.StartsAtUtc < shift.EndsAtUtc && other.EndsAtUtc > shift.StartsAtUtc))
                result.Add(new(shift.StartsAtUtc, shift.EndsAtUtc, localizer["Schedule contains overlapping shifts."].Value));
        }
        if (covered < end) result.Add(new(covered, end, localizer["Schedule contains a coverage gap."].Value));
        return result;
    }

    public static List<string> Warnings(OnCallGroup group, DateTime start, DateTime end,
        IReadOnlyList<OnCallShift> shifts, IReadOnlyList<GeneratedOnCallShift> existing, IStringLocalizer localizer)
    {
        var warnings = new List<string>();
        var coveredUntil = start;
        DateTime? previousEnd = null;
        foreach (var shift in shifts.OrderBy(shift => shift.StartsAtUtc))
        {
            if (shift.StartsAtUtc > coveredUntil) warnings.Add(localizer["Schedule contains a coverage gap."].Value);
            if (previousEnd.HasValue && shift.StartsAtUtc < previousEnd) warnings.Add(localizer["Schedule contains overlapping shifts."].Value);
            if (!previousEnd.HasValue || shift.EndsAtUtc > previousEnd) previousEnd = shift.EndsAtUtc;
            if (shift.Members.Count > 0 && shift.EndsAtUtc > coveredUntil) coveredUntil = shift.EndsAtUtc;
            if (shift.Members.Count == 0) warnings.Add(localizer["Shift has no on-duty engineers."].Value);
            if (group.MinimumShiftStaff.HasValue && shift.Members.Count < group.MinimumShiftStaff)
                warnings.Add(localizer["Shift staffing is below the team threshold."].Value);
            if (shift.Members.Any(member => group.Members.All(person => person.UserId != member.UserId)))
                warnings.Add(localizer["Future shift includes a departed team member."].Value);
        }
        if (coveredUntil < end) warnings.Add(localizer["Schedule contains a coverage gap."].Value);
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
                if ((inRange || previousInRange) && previousAssignmentEnd.HasValue && group.MinimumRestHours.HasValue &&
                    (shift.StartsAtUtc - previousAssignmentEnd.Value).TotalHours < group.MinimumRestHours)
                    warnings.Add($"{person.Key}: {localizer["An engineer has insufficient rest between shifts."].Value} ({shift.StartsAtUtc:yyyy-MM-dd HH:mm} UTC)");
                if (!previousAssignmentEnd.HasValue || shift.StartsAtUtc > previousAssignmentEnd) continuousStart = shift.StartsAtUtc;
                previousAssignmentEnd = previousAssignmentEnd > shift.EndsAtUtc ? previousAssignmentEnd : shift.EndsAtUtc;
                previousInRange = inRange;
                if (inRange && group.MaximumContinuousHours.HasValue &&
                    (previousAssignmentEnd.Value - continuousStart!.Value).TotalHours > group.MaximumContinuousHours)
                    warnings.Add($"{person.Key}: {localizer["An engineer exceeds the continuous on-call threshold."].Value} ({shift.StartsAtUtc:yyyy-MM-dd HH:mm} UTC)");
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
        return errors.Distinct().ToList();
    }
}

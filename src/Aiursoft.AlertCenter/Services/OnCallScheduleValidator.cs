using Microsoft.Extensions.Localization;
namespace Aiursoft.AlertCenter.Services;

public static class OnCallScheduleValidator
{
    public static List<string> StaffingWarnings(DateTime start, int count, int minimumStaff,
        IReadOnlyList<GeneratedOnCallShift> shifts, IStringLocalizer? localizer = null)
    {
        var counts = shifts.GroupBy(shift => shift.StartsAtUtc)
            .ToDictionary(group => group.Key, group => group.Select(shift => shift.UserId).Distinct().Count());
        return Enumerable.Range(0, count).Select(index => start.AddHours(index * 8))
            .Where(time => counts.GetValueOrDefault(time) < minimumStaff)
            .Select(time => string.Format((localizer is null ? "Only {0} staff scheduled at {1:yyyy-MM-dd HH:mm} UTC, below the minimum of {2}. Manual save is still allowed despite insufficient staffing, except for empty shifts." : localizer["Only {0} staff scheduled at {1:yyyy-MM-dd HH:mm} UTC, below the minimum of {2}. Manual save is still allowed despite insufficient staffing, except for empty shifts."].Value), counts.GetValueOrDefault(time), time, minimumStaff))
            .ToList();
    }

    public static bool CanAssign(string userId, DateTime start, DateTime end, int restHours,
        IEnumerable<GeneratedOnCallShift> existing) => existing.Where(shift => shift.UserId == userId).All(shift =>
        start >= shift.EndsAtUtc
            ? (start - shift.EndsAtUtc).TotalHours >= Math.Max(restHours, shift.RestHours)
            : end <= shift.StartsAtUtc && (shift.StartsAtUtc - end).TotalHours >= Math.Max(restHours, shift.RestHours));

    public static List<string> Validate(DateTime start, int count, int restHours, IReadOnlyList<GeneratedOnCallShift> shifts, IReadOnlyList<GeneratedOnCallShift> existing, IReadOnlyDictionary<string, string> members, IStringLocalizer? localizer = null)
    {
        var errors = new List<string>();
        var slots = shifts.GroupBy(shift => shift.StartsAtUtc).ToDictionary(group => group.Key, group => group.ToList());
        for (var i = 0; i < count; i++)
        {
            var time = start.AddHours(i * 8);
            if (!slots.TryGetValue(time, out var assigned) || assigned.Count == 0)
                errors.Add(string.Format((localizer is null ? "Empty shift detected at {0:yyyy-MM-dd HH:mm} UTC. At least one staff member is required." : localizer["Empty shift detected at {0:yyyy-MM-dd HH:mm} UTC. At least one staff member is required."].Value), time));
        }
        var accepted = existing.ToList();
        foreach (var shift in shifts.OrderBy(shift => shift.StartsAtUtc))
        {
            if (!members.TryGetValue(shift.UserId, out var name))
            {
                errors.Add((localizer is null ? "Includes employees not belonging to the current team." : localizer["Includes employees not belonging to the current team."].Value));
                continue;
            }
            if (shift.StartsAtUtc < start || shift.StartsAtUtc >= start.AddHours(count * 8) ||
                (shift.StartsAtUtc - start).Ticks % TimeSpan.FromHours(8).Ticks != 0 || shift.EndsAtUtc != shift.StartsAtUtc.AddHours(8))
                errors.Add(string.Format((localizer is null ? "{0}'s shift is outside the designated 8-hour window." : localizer["{0}'s shift is outside the designated 8-hour window."].Value), name));
            if (!CanAssign(shift.UserId, shift.StartsAtUtc, shift.EndsAtUtc, restHours, accepted))
                errors.Add(string.Format((localizer is null ? "{0}'s shift at {1:yyyy-MM-dd HH:mm} UTC overlaps with another shift or has insufficient rest (minimum {2} hours this week, while meeting adjacent shift requirements)." : localizer["{0}'s shift at {1:yyyy-MM-dd HH:mm} UTC overlaps with another shift or has insufficient rest (minimum {2} hours this week, while meeting adjacent shift requirements)."].Value), name, shift.StartsAtUtc, restHours));
            accepted.Add(shift with { RestHours = restHours });
        }
        return errors.Distinct().ToList();
    }
}

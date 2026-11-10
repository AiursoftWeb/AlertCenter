using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public class MyOnCallService(TemplateDbContext db, IStringLocalizer localizer)
{
    public async Task<MyOnCallViewModel> LoadAsync(string userId, DateTime now, int page, CancellationToken token)
    {
        page = Math.Clamp(page, 1, 1000000);
        var query = db.OnCallShifts.AsNoTracking().AsSingleQuery().Include(shift => shift.Group).ThenInclude(group => group.Members)
            .Include(shift => shift.Members).ThenInclude(member => member.User)
            .Where(shift => shift.Members.Any(member => member.UserId == userId));
        var from = now.AddDays(-30);
        var until = now.AddDays(56);
        var shifts = await query.Where(shift => shift.EndsAtUtc > from && shift.StartsAtUtc < until)
            .OrderBy(shift => shift.StartsAtUtc).ThenBy(shift => shift.Id).ToListAsync(token);
        var history = await query.Where(shift => shift.EndsAtUtc <= now).OrderByDescending(shift => shift.EndsAtUtc)
            .ThenBy(shift => shift.Id).Skip((page - 1) * 20).Take(21).ToListAsync(token);
        var next = await query.Where(shift => shift.StartsAtUtc > now).OrderBy(shift => shift.StartsAtUtc)
            .ThenBy(shift => shift.Id).FirstOrDefaultAsync(token);
        var warnings = new List<string>();
        foreach (var shift in shifts.Where(shift => shift.EndsAtUtc > now))
        {
            if (shifts.Any(other => other.Id != shift.Id && other.GroupId != shift.GroupId &&
                    other.StartsAtUtc < shift.EndsAtUtc && other.EndsAtUtc > shift.StartsAtUtc))
                warnings.Add($"{shift.Group.Name}: {localizer["Schedule contains overlapping shifts."].Value}");
        }
        foreach (var group in shifts.Where(shift => shift.EndsAtUtc > now).Select(shift => shift.Group).DistinctBy(group => group.Id))
        {
            var own = shifts.Where(shift => shift.GroupId == group.Id && shift.EndsAtUtc > now).ToList();
            var others = shifts.Except(own).Select(shift => new GeneratedOnCallShift(userId, shift.StartsAtUtc, shift.EndsAtUtc)).ToList();
            // 只校验当前用户的责任时段，不把同班其他人的休息问题归给用户。
            var personal = own.Select(shift => new OnCallShift { StartsAtUtc = shift.StartsAtUtc, EndsAtUtc = shift.EndsAtUtc,
                Members = [new OnCallShiftMember { UserId = userId }] }).ToList();
            warnings.AddRange(OnCallScheduleValidator.WarningDetails(group, now, until, personal, others, localizer)
                .Where(warning => warning.UserId == userId)
                .Select(warning => $"{group.Name}: {warning.Message}"));
            foreach (var shift in own)
            {
                if (group.MinimumShiftStaff.HasValue && shift.Members.Count < group.MinimumShiftStaff)
                    warnings.Add($"{group.Name}: {localizer["Shift staffing is below the team threshold."].Value}");
                if (group.Members.All(member => member.UserId != userId))
                    warnings.Add($"{group.Name}: {localizer["Future shift includes a departed team member."].Value}");
            }
        }
        return new MyOnCallViewModel
        {
            Current = shifts.Where(shift => shift.StartsAtUtc <= now && shift.EndsAtUtc > now).ToList(), Next = next,
            Upcoming = shifts.Where(shift => shift.EndsAtUtc > now).ToList(), History = history.Take(20).ToList(),
            HistoryPage = page, HasMoreHistory = history.Count > 20,
            PastHours = CoveredHours(shifts, from, now), FutureHours = CoveredHours(shifts, now, now.AddDays(28)),
            Warnings = warnings.Distinct().ToList(),
            HasSubscription = await db.Users.AnyAsync(user => user.Id == userId && user.OnCallSubscriptionTokenHash != null, token)
        };
    }

    public static double CoveredHours(IEnumerable<OnCallShift> shifts, DateTime from, DateTime until)
    {
        var end = from;
        double hours = 0;
        foreach (var shift in shifts.Where(shift => shift.EndsAtUtc > from && shift.StartsAtUtc < until).OrderBy(shift => shift.StartsAtUtc))
        {
            var start = shift.StartsAtUtc > end ? shift.StartsAtUtc : end;
            var clippedEnd = shift.EndsAtUtc < until ? shift.EndsAtUtc : until;
            if (clippedEnd <= start) continue;
            hours += (clippedEnd - start).TotalHours;
            end = clippedEnd;
        }
        return hours;
    }
}

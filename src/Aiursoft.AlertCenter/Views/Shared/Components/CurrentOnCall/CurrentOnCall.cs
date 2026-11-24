using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Views.Shared.Components.CurrentOnCall;

public sealed record CurrentOnCallTeam(OnCallGroup Group, OnCallOceResult Current, OnCallShift? Next);

public sealed record DashboardOnCall(IReadOnlyList<CurrentOnCallTeam> Teams, IReadOnlyList<OnCallShift> Shifts, OnCallShift? Next, DateTime Now);

public class CurrentOnCall(TemplateDbContext db) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid? groupId = null, bool dashboard = false)
    {
        var token = HttpContext.RequestAborted;
        var now = DateTime.UtcNow;
        var userId = HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var groups = await db.OnCallGroups.AsNoTracking().Where(group => (groupId == null || group.Id == groupId) &&
                (!dashboard || group.Members.Any(member => member.UserId == userId)))
            .OrderBy(group => group.Name).ToListAsync(token);
        var ids = groups.Select(group => group.Id).ToArray();
        var current = await new OnCallOceService(db).QueryManyAsync(ids, now, token);
        var nextIds = await db.OnCallShifts.AsNoTracking().Where(shift => ids.Contains(shift.GroupId) && shift.StartsAtUtc > now &&
                shift.StartsAtUtc == db.OnCallShifts.Where(other => other.GroupId == shift.GroupId && other.StartsAtUtc > now)
                    .Min(other => other.StartsAtUtc))
            .Select(shift => shift.Id).ToListAsync(token);
        var next = await db.OnCallShifts.AsNoTracking().AsSingleQuery().Include(shift => shift.Members).ThenInclude(member => member.User)
            .Where(shift => nextIds.Contains(shift.Id)).ToListAsync(token);
        var mergedNext = next.GroupBy(shift => shift.GroupId).ToDictionary(group => group.Key, group => new OnCallShift
        {
            GroupId = group.Key, StartsAtUtc = group.First().StartsAtUtc, EndsAtUtc = group.Min(shift => shift.EndsAtUtc),
            Members = group.SelectMany(shift => shift.Members).DistinctBy(member => member.UserId).OrderBy(member => member.UserId, StringComparer.Ordinal).ToList()
        });
        var teams = groups.Select(group => new CurrentOnCallTeam(group, current[group.Id], mergedNext.GetValueOrDefault(group.Id))).ToList();
        if (!dashboard) return View(teams);
        // The browser may be in the previous/next month relative to UTC. Include those
        // complete months and their surrounding calendar weeks in every time zone.
        var from = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1).AddDays(-7);
        var lastMonth = now.AddDays(56);
        var until = new DateTime(lastMonth.Year, lastMonth.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(2).AddDays(7);
        var personal = db.OnCallShifts.AsNoTracking().AsSingleQuery().Include(shift => shift.Group)
            .Include(shift => shift.Members).ThenInclude(member => member.User)
            .Where(shift => shift.Members.Any(member => member.UserId == userId));
        var shifts = await personal.Where(shift => shift.EndsAtUtc > from && shift.StartsAtUtc < until)
            .OrderBy(shift => shift.StartsAtUtc).ThenBy(shift => shift.Id).ToListAsync(token);
        var upcoming = await personal.Where(shift => shift.StartsAtUtc > now)
            .OrderBy(shift => shift.StartsAtUtc).ThenBy(shift => shift.Id).FirstOrDefaultAsync(token);
        return View("Dashboard", new DashboardOnCall(teams, shifts, upcoming, now));
    }
}

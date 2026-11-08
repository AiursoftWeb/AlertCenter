using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Views.Shared.Components.CurrentOnCall;

public sealed record CurrentOnCallTeam(OnCallGroup Group, OnCallOceResult Current, OnCallShift? Next);

public class CurrentOnCall(TemplateDbContext db) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid? groupId = null)
    {
        var token = HttpContext.RequestAborted;
        var now = DateTime.UtcNow;
        var groups = await db.OnCallGroups.AsNoTracking().Where(group => groupId == null || group.Id == groupId)
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
            GroupId = group.Key, StartsAtUtc = group.First().StartsAtUtc,
            Members = group.SelectMany(shift => shift.Members).DistinctBy(member => member.UserId).OrderBy(member => member.UserId).ToList()
        });
        return View(groups.Select(group => new CurrentOnCallTeam(group, current[group.Id], mergedNext.GetValueOrDefault(group.Id))).ToList());
    }
}

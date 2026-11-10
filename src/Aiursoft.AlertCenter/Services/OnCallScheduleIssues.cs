using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public sealed record OnCallTeamIssues(Guid GroupId, string GroupName, IReadOnlyList<string> Warnings);

public static class OnCallScheduleIssues
{
    public static async Task<List<OnCallTeamIssues>> UpcomingAsync(TemplateDbContext db, DateTime start,
        IStringLocalizerFactory localizerFactory, CancellationToken token, Guid? groupId = null)
    {
        var groups = await db.OnCallGroups.AsNoTracking().Include(group => group.Members).Include(group => group.Rule)
            .Where(group => !groupId.HasValue || group.Id == groupId).ToListAsync(token);
        if (groups.Count == 0) return [];
        var buffer = Math.Clamp(groups.Max(group => Math.Max(group.MinimumRestHours ?? 0, group.MaximumContinuousHours ?? 0)), 28 * 24, 720);
        var from = start.AddHours(-buffer);
        var end = start.AddDays(7);
        var until = end.AddHours(buffer);
        var shifts = await db.OnCallShifts.AsNoTracking().Include(shift => shift.Members)
            .Where(shift => shift.EndsAtUtc > from && shift.StartsAtUtc < until).ToListAsync(token);
        var assignedIds = shifts.SelectMany(shift => shift.Members.Select(member => member.UserId)).Distinct().ToArray();
        var names = await db.Users.AsNoTracking().Where(user => assignedIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.DisplayName, token);
        var localizer = localizerFactory.Create(typeof(OnCallScheduleValidator));
        return groups.Select(group =>
        {
            var own = shifts.Where(shift => shift.GroupId == group.Id && shift.EndsAtUtc > start && shift.StartsAtUtc < end).ToList();
            var userIds = own.SelectMany(shift => shift.Members.Select(member => member.UserId)).ToHashSet(StringComparer.Ordinal);
            var external = shifts.Except(own).SelectMany(shift => shift.Members.Where(member => userIds.Contains(member.UserId))
                .Select(member => new GeneratedOnCallShift(member.UserId, shift.StartsAtUtc, shift.EndsAtUtc))).ToList();
            var warnings = OnCallScheduleValidator.Warnings(group, start, end, own, external, localizer, names);
            if (group.AutoExtend && group.Rule != null && group.Members.Count < group.Rule.StaffPerShift)
                warnings.Add(localizer["Automatic extension cannot proceed: the member count is below the template staff per shift."].Value);
            return new OnCallTeamIssues(group.Id, group.Name, warnings);
        }).Where(team => team.Warnings.Count > 0).ToList();
    }
}

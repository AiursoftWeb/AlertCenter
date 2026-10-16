using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public sealed record OnCallOce(string UserId, string DisplayName, string? Email);
public sealed record OnCallOceResult(Guid? ShiftId, DateTime? StartsAtUtc, DateTime? EndsAtUtc,
    IReadOnlyList<OnCallOce> Engineers, string? Error);

public class OnCallOceService(TemplateDbContext db, IStringLocalizer<OnCallOceService>? localizer = null)
{
    public async Task<OnCallOceResult> QueryAsync(Guid groupId, DateTime atUtc, CancellationToken cancellationToken = default)
    {
        if (atUtc.Kind != DateTimeKind.Utc) throw new ArgumentException((localizer is null ? "OCE query time must be in UTC." : localizer["OCE query time must be in UTC."].Value), nameof(atUtc));
        var shifts = await db.OnCallShifts.AsNoTracking().AsSingleQuery().Include(shift => shift.Members).ThenInclude(member => member.User)
            .Where(shift => shift.GroupId == groupId && shift.StartsAtUtc <= atUtc && shift.EndsAtUtc > atUtc)
            .OrderBy(shift => shift.StartsAtUtc).Take(2).ToListAsync(cancellationToken);
        if (shifts.Count == 0) return new(null, null, null, [], (localizer is null ? "On-call group has no schedule coverage at notification time." : localizer["On-call group has no schedule coverage at notification time."].Value));
        if (shifts.Count > 1) return new(null, null, null, [], (localizer is null ? "On-call group has overlapping shifts at notification time, unable to determine OCE." : localizer["On-call group has overlapping shifts at notification time, unable to determine OCE."].Value));
        var shift = shifts[0];
        var engineers = shift.Members.OrderBy(member => member.UserId)
            .Select(member => new OnCallOce(member.UserId, member.User.DisplayName, member.User.Email)).ToList();
        return new(shift.Id, DateTime.SpecifyKind(shift.StartsAtUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(shift.EndsAtUtc, DateTimeKind.Utc), engineers,
            engineers.Count == 0 ? (localizer is null ? "The current shift has no on-duty engineer (OCE)." : localizer["The current shift has no on-duty engineer (OCE)."].Value) : null);
    }
}

using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public sealed record OnCallOce(string UserId, string DisplayName, string? Email);
public sealed record OnCallOceResult(Guid? ShiftId, DateTime? StartsAtUtc, DateTime? EndsAtUtc,
    IReadOnlyList<OnCallOce> Engineers, string? Error, bool IsFallback = false);

public class OnCallOceService(TemplateDbContext db, IStringLocalizer<OnCallOceService>? localizer = null)
{
    public async Task<OnCallOceResult> QueryAsync(Guid groupId, DateTime atUtc, CancellationToken cancellationToken = default)
    {
        if (atUtc.Kind != DateTimeKind.Utc) throw new ArgumentException((localizer is null ? "OCE query time must be in UTC." : localizer["OCE query time must be in UTC."].Value), nameof(atUtc));
        var shifts = await db.OnCallShifts.AsNoTracking().AsSingleQuery().Include(shift => shift.Members).ThenInclude(member => member.User)
            .Where(shift => shift.GroupId == groupId && shift.StartsAtUtc <= atUtc && shift.EndsAtUtc > atUtc)
            .OrderBy(shift => shift.StartsAtUtc).ThenBy(shift => shift.Id).ToListAsync(cancellationToken);
        var engineers = shifts.SelectMany(shift => shift.Members).DistinctBy(member => member.UserId).OrderBy(member => member.UserId)
            .Select(member => new OnCallOce(member.UserId, member.User.DisplayName, member.User.Email)).ToList();
        if (engineers.Count == 0)
        {
            var fallback = await db.Set<OnCallFallbackRecipient>().AsNoTracking().Where(person => person.GroupId == groupId)
                .OrderBy(person => person.UserId).Select(person => new OnCallOce(person.UserId, person.User.DisplayName, person.User.Email))
                .ToListAsync(cancellationToken);
            if (fallback.Count > 0) return new(null, null, null, fallback, null, true);
        }
        if (shifts.Count == 0) return new(null, null, null, [], (localizer is null ? "On-call group has no schedule coverage at notification time." : localizer["On-call group has no schedule coverage at notification time."].Value));
        var shift = shifts[0];
        return new(shift.Id, DateTime.SpecifyKind(shifts.Max(item => item.StartsAtUtc), DateTimeKind.Utc),
            DateTime.SpecifyKind(shifts.Min(item => item.EndsAtUtc), DateTimeKind.Utc), engineers,
            engineers.Count == 0 ? (localizer is null ? "The current shift has no on-duty engineer (OCE)." : localizer["The current shift has no on-duty engineer (OCE)."].Value) : null);
    }
}

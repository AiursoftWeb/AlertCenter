using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public sealed record OnCallOce(string UserId, string DisplayName, string? Email);
public sealed record OnCallOceResult(Guid? ShiftId, DateTime? StartsAtUtc, DateTime? EndsAtUtc,
    IReadOnlyList<OnCallOce> Engineers, string? Error);

public class OnCallOceService(TemplateDbContext db)
{
    public async Task<OnCallOceResult> QueryAsync(Guid groupId, DateTime atUtc, CancellationToken cancellationToken = default)
    {
        if (atUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("OCE 查询时间必须为 UTC。", nameof(atUtc));
        var shifts = await db.OnCallShifts.AsNoTracking().AsSingleQuery().Include(shift => shift.Members).ThenInclude(member => member.User)
            .Where(shift => shift.GroupId == groupId && shift.StartsAtUtc <= atUtc && shift.EndsAtUtc > atUtc)
            .OrderBy(shift => shift.StartsAtUtc).Take(2).ToListAsync(cancellationToken);
        if (shifts.Count == 0) return new(null, null, null, [], "值班组在通知时刻没有排班覆盖。");
        if (shifts.Count > 1) return new(null, null, null, [], "值班组在通知时刻存在重叠班次，无法确定 OCE。");
        var shift = shifts[0];
        var engineers = shift.Members.OrderBy(member => member.UserId)
            .Select(member => new OnCallOce(member.UserId, member.User.DisplayName, member.User.Email)).ToList();
        return new(shift.Id, DateTime.SpecifyKind(shift.StartsAtUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(shift.EndsAtUtc, DateTimeKind.Utc), engineers,
            engineers.Count == 0 ? "当前班次没有当班工程师（OCE）。" : null);
    }
}

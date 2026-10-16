using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public class IncidentAccessService(TemplateDbContext db, IApplicationAccessService applicationAccess, IStringLocalizerFactory localizerFactory)
{
    public async Task<bool> CanManageAsync(ClaimsPrincipal user, Incident incident, DateTime atUtc,
        CancellationToken cancellationToken = default)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return false;
        if (incident.OwnerId == userId || await applicationAccess.CanAccessAsync(user, incident.ApplicationId, true, cancellationToken)) return true;
        return await IsOceAsync(userId, incident.ApplicationId, atUtc, cancellationToken);
    }

    public async Task<bool> CanViewAsync(ClaimsPrincipal user, Incident incident, DateTime atUtc,
        CancellationToken cancellationToken = default) =>
        await applicationAccess.CanAccessAsync(user, incident.ApplicationId, cancellationToken: cancellationToken) ||
        await CanManageAsync(user, incident, atUtc, cancellationToken);

    public async Task<List<Guid>> GetOceApplicationIdsAsync(ClaimsPrincipal user, DateTime atUtc, CancellationToken cancellationToken)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return [];
        return await db.SourceApplications.AsNoTracking().Where(application => application.OnCallGroupId != null &&
            db.OnCallShifts.Any(shift => shift.GroupId == application.OnCallGroupId && shift.StartsAtUtc <= atUtc && shift.EndsAtUtc > atUtc &&
                shift.Members.Any(member => member.UserId == userId)) &&
            db.OnCallShifts.Count(shift => shift.GroupId == application.OnCallGroupId && shift.StartsAtUtc <= atUtc && shift.EndsAtUtc > atUtc) == 1)
            .Select(application => application.Id).ToListAsync(cancellationToken);
    }

    private async Task<bool> IsOceAsync(string userId, Guid applicationId, DateTime atUtc, CancellationToken cancellationToken)
    {
        var groupId = await db.SourceApplications.Where(application => application.Id == applicationId)
            .Select(application => application.OnCallGroupId).SingleOrDefaultAsync(cancellationToken);
        if (!groupId.HasValue) return false;
        var oce = await new OnCallOceService(db, localizer: new StringLocalizer<OnCallOceService>(localizerFactory)).QueryAsync(groupId.Value, atUtc, cancellationToken);
        return oce.Error == null && oce.Engineers.Any(engineer => engineer.UserId == userId);
    }
}

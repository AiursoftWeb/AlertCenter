using Aiursoft.AlertCenter.Authorization;
using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public interface IApplicationAccessService
{
    bool CanManageApplications(ClaimsPrincipal user);
    Task<List<Guid>> GetAccessibleApplicationIdsAsync(ClaimsPrincipal user, bool requireManage = false,
        CancellationToken cancellationToken = default);
    Task<bool> CanAccessAsync(ClaimsPrincipal user, Guid applicationId, bool requireManage = false,
        CancellationToken cancellationToken = default);
}

public sealed class ApplicationAccessService(TemplateDbContext db) : IApplicationAccessService
{
    public bool CanManageApplications(ClaimsPrincipal user) => user.HasClaim(AppPermissions.Type, AppPermissionNames.CanManageApplications);

    public async Task<List<Guid>> GetAccessibleApplicationIdsAsync(ClaimsPrincipal user, bool requireManage = false,
        CancellationToken cancellationToken = default)
    {
        if (CanManageApplications(user))
            return await db.SourceApplications.Select(application => application.Id).ToListAsync(cancellationToken);
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return [];
        var roleIds = db.UserRoles.Where(link => link.UserId == userId).Select(link => link.RoleId);
        var direct = db.ApplicationUserAccesses.Where(access => access.UserId == userId &&
            (!requireManage || access.CanManage)).Select(access => access.ApplicationId);
        var inherited = db.ApplicationRoleAccesses.Where(access => roleIds.Contains(access.RoleId) &&
            (!requireManage || access.CanManage)).Select(access => access.ApplicationId);
        return await direct.Concat(inherited).Distinct().ToListAsync(cancellationToken);
    }

    public async Task<bool> CanAccessAsync(ClaimsPrincipal user, Guid applicationId, bool requireManage = false,
        CancellationToken cancellationToken = default)
    {
        if (CanManageApplications(user)) return true;
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return false;
        var direct = await db.ApplicationUserAccesses.AnyAsync(access => access.ApplicationId == applicationId &&
            access.UserId == userId && (!requireManage || access.CanManage), cancellationToken);
        if (direct) return true;
        return await db.ApplicationRoleAccesses.AnyAsync(access => access.ApplicationId == applicationId &&
            db.UserRoles.Any(link => link.UserId == userId && link.RoleId == access.RoleId) &&
            (!requireManage || access.CanManage), cancellationToken);
    }
}

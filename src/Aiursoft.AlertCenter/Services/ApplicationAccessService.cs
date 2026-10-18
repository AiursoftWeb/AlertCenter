using System.Security.Claims;
using Aiursoft.AlertCenter.Authorization;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public interface IApplicationAccessService
{
    bool CanManageApplications(ClaimsPrincipal user);
    Task<bool> IsOwnerAsync(ClaimsPrincipal user, Guid applicationId, CancellationToken cancellationToken = default);
    Task<List<Guid>> GetAccessibleApplicationIdsAsync(ClaimsPrincipal user, ApplicationPermission permission = ApplicationPermission.ViewIncidents,
        CancellationToken cancellationToken = default);
    Task<bool> CanAccessAsync(ClaimsPrincipal user, Guid applicationId, ApplicationPermission permission = ApplicationPermission.ViewIncidents,
        CancellationToken cancellationToken = default);
}

public sealed class ApplicationAccessService(TemplateDbContext db) : IApplicationAccessService
{
    // Platform capability permits creating applications, not access to somebody else's application.
    public bool CanManageApplications(ClaimsPrincipal user) => user.HasClaim(AppPermissions.Type, AppPermissionNames.CanManageApplications);

    public Task<bool> IsOwnerAsync(ClaimsPrincipal user, Guid applicationId, CancellationToken cancellationToken = default)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return userId == null ? Task.FromResult(false) : db.SourceApplications.AnyAsync(
            application => application.Id == applicationId && application.OwnerId == userId, cancellationToken);
    }

    public async Task<List<Guid>> GetAccessibleApplicationIdsAsync(ClaimsPrincipal user, ApplicationPermission permission = ApplicationPermission.ViewIncidents,
        CancellationToken cancellationToken = default)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return [];
        var roleIds = db.UserRoles.Where(link => link.UserId == userId).Select(link => link.RoleId);
        var direct = db.ApplicationUserAccesses.Where(access => access.UserId == userId);
        var inherited = db.ApplicationRoleAccesses.Where(access => roleIds.Contains(access.RoleId));
        switch (permission)
        {
            case ApplicationPermission.ViewIncidents:
                direct = direct.Where(access => access.CanViewIncidents);
                inherited = inherited.Where(access => access.CanViewIncidents);
                break;
            case ApplicationPermission.CreateIncidents:
                direct = direct.Where(access => access.CanCreateIncidents);
                inherited = inherited.Where(access => access.CanCreateIncidents);
                break;
            case ApplicationPermission.UpdateIncidents:
                direct = direct.Where(access => access.CanUpdateIncidents);
                inherited = inherited.Where(access => access.CanUpdateIncidents);
                break;
            case ApplicationPermission.ManageApiKeys:
                direct = direct.Where(access => access.CanManageApiKeys);
                inherited = inherited.Where(access => access.CanManageApiKeys);
                break;
            case ApplicationPermission.DeleteIncidents:
                direct = direct.Where(access => access.CanDeleteIncidents);
                inherited = inherited.Where(access => access.CanDeleteIncidents);
                break;
            case ApplicationPermission.ManageApplication:
                direct = direct.Where(access => access.CanManageApplication);
                inherited = inherited.Where(access => access.CanManageApplication);
                break;
            default: return [];
        }
        var granted = direct.Select(access => access.ApplicationId).Concat(inherited.Select(access => access.ApplicationId));
        var owned = db.SourceApplications.Where(application => application.OwnerId == userId).Select(application => application.Id);
        var result = await owned.Concat(granted).Distinct().ToListAsync(cancellationToken);
        if (permission is ApplicationPermission.UpdateIncidents or ApplicationPermission.DeleteIncidents)
            result = result.Intersect(await GetAccessibleApplicationIdsAsync(user, ApplicationPermission.ViewIncidents, cancellationToken)).ToList();
        return result;
    }

    public async Task<bool> CanAccessAsync(ClaimsPrincipal user, Guid applicationId, ApplicationPermission permission = ApplicationPermission.ViewIncidents,
        CancellationToken cancellationToken = default) =>
        (await GetAccessibleApplicationIdsAsync(user, permission, cancellationToken)).Contains(applicationId);
}

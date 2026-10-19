using System.Security.Claims;
using Aiursoft.AlertCenter.Authorization;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public interface IApplicationAccessService
{
    bool CanCreateApplications(ClaimsPrincipal user);
    Task<bool> IsOwnerAsync(ClaimsPrincipal user, Guid applicationId, CancellationToken cancellationToken = default);
    Task<List<Guid>> GetAccessibleApplicationIdsAsync(ClaimsPrincipal user, ApplicationPermission permission = ApplicationPermission.ViewIncidents,
        CancellationToken cancellationToken = default);
    Task<bool> CanAccessAsync(ClaimsPrincipal user, Guid applicationId, ApplicationPermission permission = ApplicationPermission.ViewIncidents,
        CancellationToken cancellationToken = default);
}

public sealed class ApplicationAccessService(TemplateDbContext db) : IApplicationAccessService
{
    // Platform capability permits creating applications, not access to somebody else's application.
    public bool CanCreateApplications(ClaimsPrincipal user) => user.HasClaim(AppPermissions.Type, AppPermissionNames.CanCreateApplications);

    public Task<bool> IsOwnerAsync(ClaimsPrincipal user, Guid applicationId, CancellationToken cancellationToken = default)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return userId == null ? Task.FromResult(false) : db.SourceApplications.AnyAsync(
            application => application.Id == applicationId && application.OwnerId == userId, cancellationToken);
    }

    public Task<List<Guid>> GetAccessibleApplicationIdsAsync(ClaimsPrincipal user, ApplicationPermission permission = ApplicationPermission.ViewIncidents,
        CancellationToken cancellationToken = default) =>
        AccessibleApplicationIds(user, permission).ToListAsync(cancellationToken);

    public Task<bool> CanAccessAsync(ClaimsPrincipal user, Guid applicationId, ApplicationPermission permission = ApplicationPermission.ViewIncidents,
        CancellationToken cancellationToken = default) =>
        AccessibleApplicationIds(user, permission).AnyAsync(id => id == applicationId, cancellationToken);

    private IQueryable<Guid> AccessibleApplicationIds(ClaimsPrincipal user, ApplicationPermission permission)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return db.SourceApplications.Where(application => false).Select(application => application.Id);
        var roleIds = db.UserRoles.Where(link => link.UserId == userId).Select(link => link.RoleId);
        var grants = db.ApplicationUserAccesses.Where(access => access.UserId == userId).Select(access => new
        {
            access.ApplicationId, access.CanViewIncidents, access.CanCreateIncidents, access.CanUpdateIncidents,
            access.CanManageApiKeys, access.CanDeleteIncidents, access.CanManageApplication
        }).Concat(db.ApplicationRoleAccesses.Where(access => roleIds.Contains(access.RoleId)).Select(access => new
        {
            access.ApplicationId, access.CanViewIncidents, access.CanCreateIncidents, access.CanUpdateIncidents,
            access.CanManageApiKeys, access.CanDeleteIncidents, access.CanManageApplication
        }));
        var permitted = permission switch
        {
            ApplicationPermission.ViewIncidents => grants.Where(access => access.CanViewIncidents),
            ApplicationPermission.CreateIncidents => grants.Where(access => access.CanCreateIncidents),
            ApplicationPermission.UpdateIncidents => grants.Where(access => access.CanUpdateIncidents),
            ApplicationPermission.ManageApiKeys => grants.Where(access => access.CanManageApiKeys),
            ApplicationPermission.DeleteIncidents => grants.Where(access => access.CanDeleteIncidents),
            ApplicationPermission.ManageApplication => grants.Where(access => access.CanManageApplication),
            _ => grants.Where(access => false)
        };
        if (!Enum.IsDefined(permission)) return permitted.Select(access => access.ApplicationId);
        var owned = db.SourceApplications.Where(application => application.OwnerId == userId).Select(application => application.Id);
        var result = owned.Union(permitted.Select(access => access.ApplicationId));
        // View and update/delete may come from different direct or role grants.
        if (permission is ApplicationPermission.UpdateIncidents or ApplicationPermission.DeleteIncidents)
            result = result.Intersect(owned.Union(grants.Where(access => access.CanViewIncidents).Select(access => access.ApplicationId)));
        return result;
    }
}

using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ApplicationsController(TemplateDbContext db, IApplicationAccessService applicationAccess, IStringLocalizer<ApplicationsController> localizer) : Controller
{
    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "Alert Center", CascadedLinksIcon = "bell", CascadedLinksOrder = 1,
        LinkText = "Applications", LinkOrder = 1)]
    public async Task<IActionResult> Applications(string? keyword, CancellationToken cancellationToken)
    {
        keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        var model = await ApplicationsModel(keyword, cancellationToken);
        return this.StackView(model);
    }

    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "Alert Center", CascadedLinksIcon = "bell", CascadedLinksOrder = 1,
        LinkText = "Integration Guide", LinkOrder = 2)]
    public IActionResult GettingStarted() => this.StackView(new ApplicationsViewModel { PageTitle = localizer["Integration Guide"].Value });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateApplication(CreateApplicationRequest request, CancellationToken cancellationToken)
    {
        if (!applicationAccess.CanManageApplications(User)) return Forbid();
        if (!await OnCallGroupExists(request.OnCallGroupId, cancellationToken))
            ModelState.AddModelError(nameof(request.OnCallGroupId), localizer["The selected on-call group does not exist. Please select again."].Value);
        if (!ModelState.IsValid)
        {
            var model = await ApplicationsModel(cancellationToken: cancellationToken);
            model.ApplicationInput = request;
            return this.StackView(model, "Applications");
        }
        var application = new SourceApplication { Name = request.Name.Trim(), OnCallGroupId = request.OnCallGroupId, OwnerId = User.FindFirstValue(ClaimTypes.NameIdentifier) };
        db.SourceApplications.Add(application);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (await OnCallGroupExists(request.OnCallGroupId, cancellationToken)) throw;
            ModelState.AddModelError(nameof(request.OnCallGroupId), localizer["On-call group has been deleted. Please select another."].Value);
            var model = await ApplicationsModel(cancellationToken: cancellationToken);
            model.ApplicationInput = request;
            return this.StackView(model, "Applications");
        }
        return RedirectToAction("Index", "ApplicationApiKeys", new { applicationId = application.Id });
    }

    public async Task<IActionResult> OnCall(Guid id, CancellationToken cancellationToken)
    {
        var application = await db.SourceApplications.AsNoTracking().Include(item => item.Owner).SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (application == null) return NotFound();
        if (!await applicationAccess.CanAccessAsync(User, id, permission: ApplicationPermission.ManageApplication, cancellationToken)) return Forbid();
        return this.StackView(await OnCallModel(application, application.OnCallGroupId, cancellationToken));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveOnCall(SaveApplicationOnCallRequest request, CancellationToken cancellationToken)
    {
        var application = await db.SourceApplications.SingleOrDefaultAsync(item => item.Id == request.ApplicationId, cancellationToken);
        if (application == null) return NotFound();
        if (!await applicationAccess.CanAccessAsync(User, application.Id, permission: ApplicationPermission.ManageApplication, cancellationToken)) return Forbid();
        if (!await OnCallGroupExists(request.OnCallGroupId, cancellationToken))
            ModelState.AddModelError(nameof(request.OnCallGroupId), localizer["The selected on-call group does not exist. Please select again."].Value);
        if (!ModelState.IsValid)
            return this.StackView(await OnCallModel(application, request.OnCallGroupId, cancellationToken), "OnCall");
        application.OnCallGroupId = request.OnCallGroupId;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (await OnCallGroupExists(request.OnCallGroupId, cancellationToken)) throw;
            ModelState.AddModelError(nameof(request.OnCallGroupId), localizer["On-call group has been deleted. Please select another."].Value);
            return this.StackView(await OnCallModel(application, request.OnCallGroupId, cancellationToken), "OnCall");
        }
        TempData["ApplicationOnCallSuccess"] = request.OnCallGroupId.HasValue ? localizer["App on-call group updated."].Value : localizer["The application has been unbound from the on-call group."].Value;
        return RedirectToAction(nameof(OnCall), new { id = application.Id });
    }

    private async Task<bool> OnCallGroupExists(Guid? id, CancellationToken cancellationToken) =>
        !id.HasValue || await db.OnCallGroups.AnyAsync(group => group.Id == id, cancellationToken);

    private async Task<ApplicationOnCallViewModel> OnCallModel(SourceApplication application, Guid? groupId,
        CancellationToken cancellationToken) => new()
    {
        Application = application,
        OnCallGroupId = groupId,
        Groups = await db.OnCallGroups.AsNoTracking().OrderBy(group => group.Name).ToListAsync(cancellationToken)
    };

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetEnabled(Guid id, bool enabled, CancellationToken cancellationToken)
    {
        var application = await db.SourceApplications.FindAsync([id], cancellationToken);
        if (application == null) return NotFound();
        if (!await applicationAccess.CanAccessAsync(User, id, permission: ApplicationPermission.ManageApplication, cancellationToken)) return Forbid();
        application.IsEnabled = enabled;
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Applications));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteApplication(Guid id, string? confirmationName, CancellationToken cancellationToken)
    {
        var attempt = 0;
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async retryToken =>
        {
            if (attempt++ > 0) db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(retryToken);
            var applicationName = await db.SourceApplications.AsNoTracking()
                .Where(application => application.Id == id)
                .Select(application => application.Name).SingleOrDefaultAsync(retryToken);
            if (applicationName == null) return NotFound();
            if (!await applicationAccess.IsOwnerAsync(User, id, retryToken)) return Forbid();
            if (!ModelState.IsValid || string.IsNullOrEmpty(confirmationName) ||
                !string.Equals(applicationName, confirmationName, StringComparison.Ordinal))
            {
                return BadRequest(localizer["Enter the exact app name to confirm deletion."].Value);
            }
            // Delete the application and its history atomically without loading every message.
            await db.Incidents.Where(incident => incident.ApplicationId == id)
                .ExecuteDeleteAsync(retryToken);
            var ownerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var deleted = await db.SourceApplications.Where(application => application.Id == id && application.OwnerId == ownerId)
                .ExecuteDeleteAsync(retryToken);
            if (deleted == 0)
            {
                await transaction.RollbackAsync(retryToken);
                return Conflict();
            }
            await transaction.CommitAsync(retryToken);
            return RedirectToAction(nameof(Applications));
        }, cancellationToken);
    }

    public async Task<IActionResult> Access(Guid id, CancellationToken cancellationToken)
    {
        if (!await applicationAccess.CanAccessAsync(User, id, ApplicationPermission.ManageApplication, cancellationToken)) return Forbid();
        var application = await db.SourceApplications.AsNoTracking().Include(item => item.Owner).SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (application == null) return NotFound();
        return this.StackView(new ApplicationAccessViewModel
        {
            Application = application,
            IsOwner = await applicationAccess.IsOwnerAsync(User, id, cancellationToken),
            UserAccesses = await db.ApplicationUserAccesses.AsNoTracking().Include(access => access.User)
                .Where(access => access.ApplicationId == id).OrderBy(access => access.User.DisplayName).ToListAsync(cancellationToken),
            RoleAccesses = await db.ApplicationRoleAccesses.AsNoTracking().Include(access => access.Role)
                .Where(access => access.ApplicationId == id).OrderBy(access => access.Role.Name).ToListAsync(cancellationToken),
            Users = await db.Users.AsNoTracking().OrderBy(user => user.DisplayName)
                .Select(user => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem($"{user.DisplayName} ({user.UserName})", user.Id)).ToListAsync(cancellationToken),
            Roles = await db.Roles.AsNoTracking().OrderBy(role => role.Name)
                .Select(role => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem(role.Name, role.Id)).ToListAsync(cancellationToken)
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAccess(SaveApplicationAccessRequest request, CancellationToken cancellationToken)
    {
        if (!await applicationAccess.CanAccessAsync(User, request.ApplicationId, ApplicationPermission.ManageApplication, cancellationToken)) return Forbid();
        if (!ModelState.IsValid || (string.IsNullOrWhiteSpace(request.UserId) == string.IsNullOrWhiteSpace(request.RoleId))) return BadRequest();
        if ((request.CanUpdateIncidents || request.CanDeleteIncidents) && !request.CanViewIncidents)
            return BadRequest(localizer["Viewing incidents is required to update or delete them."].Value);
        // Delegates cannot grant permissions they do not possess, including through API keys later.
        if (request.CanViewIncidents && !await applicationAccess.CanAccessAsync(User, request.ApplicationId, ApplicationPermission.ViewIncidents, cancellationToken)) return Forbid();
        if (request.CanCreateIncidents && !await applicationAccess.CanAccessAsync(User, request.ApplicationId, ApplicationPermission.CreateIncidents, cancellationToken)) return Forbid();
        if (request.CanUpdateIncidents && !await applicationAccess.CanAccessAsync(User, request.ApplicationId, ApplicationPermission.UpdateIncidents, cancellationToken)) return Forbid();
        if (request.CanManageApiKeys && !await applicationAccess.CanAccessAsync(User, request.ApplicationId, ApplicationPermission.ManageApiKeys, cancellationToken)) return Forbid();
        if (request.CanDeleteIncidents && !await applicationAccess.CanAccessAsync(User, request.ApplicationId, ApplicationPermission.DeleteIncidents, cancellationToken)) return Forbid();
        if (request.CanManageApplication && !await applicationAccess.CanAccessAsync(User, request.ApplicationId, ApplicationPermission.ManageApplication, cancellationToken)) return Forbid();

        if (request.UserId != null)
        {
            if (!await db.Users.AnyAsync(user => user.Id == request.UserId, cancellationToken)) return BadRequest();
            var grant = await db.ApplicationUserAccesses.SingleOrDefaultAsync(access => access.ApplicationId == request.ApplicationId && access.UserId == request.UserId, cancellationToken);
            if (grant == null)
            {
                grant = new ApplicationUserAccess { ApplicationId = request.ApplicationId, UserId = request.UserId };
                db.ApplicationUserAccesses.Add(grant);
            }
            grant.CanViewIncidents = request.CanViewIncidents;
            grant.CanCreateIncidents = request.CanCreateIncidents;
            grant.CanUpdateIncidents = request.CanUpdateIncidents;
            grant.CanManageApiKeys = request.CanManageApiKeys;
            grant.CanDeleteIncidents = request.CanDeleteIncidents;
            grant.CanManageApplication = request.CanManageApplication;
        }
        else
        {
            if (!await db.Roles.AnyAsync(role => role.Id == request.RoleId, cancellationToken)) return BadRequest();
            var grant = await db.ApplicationRoleAccesses.SingleOrDefaultAsync(access => access.ApplicationId == request.ApplicationId && access.RoleId == request.RoleId, cancellationToken);
            if (grant == null)
            {
                grant = new ApplicationRoleAccess { ApplicationId = request.ApplicationId, RoleId = request.RoleId! };
                db.ApplicationRoleAccesses.Add(grant);
            }
            grant.CanViewIncidents = request.CanViewIncidents;
            grant.CanCreateIncidents = request.CanCreateIncidents;
            grant.CanUpdateIncidents = request.CanUpdateIncidents;
            grant.CanManageApiKeys = request.CanManageApiKeys;
            grant.CanDeleteIncidents = request.CanDeleteIncidents;
            grant.CanManageApplication = request.CanManageApplication;
        }
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Access), new { id = request.ApplicationId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveAccess(Guid id, string? userId, string? roleId, CancellationToken cancellationToken)
    {
        if (!await applicationAccess.CanAccessAsync(User, id, ApplicationPermission.ManageApplication, cancellationToken)) return Forbid();
        if ((userId == null) == (roleId == null)) return BadRequest();
        if (userId != null)
            await db.ApplicationUserAccesses.Where(access => access.ApplicationId == id && access.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        else
            await db.ApplicationRoleAccesses.Where(access => access.ApplicationId == id && access.RoleId == roleId).ExecuteDeleteAsync(cancellationToken);
        return RedirectToAction(nameof(Access), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TransferOwnership(Guid id, string ownerId, CancellationToken cancellationToken)
    {
        if (!await applicationAccess.IsOwnerAsync(User, id, cancellationToken)) return Forbid();
        if (!await db.Users.AnyAsync(user => user.Id == ownerId, cancellationToken)) return BadRequest();
        var currentOwnerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var changed = await db.SourceApplications.Where(application => application.Id == id && application.OwnerId == currentOwnerId)
            .ExecuteUpdateAsync(update => update.SetProperty(application => application.OwnerId, ownerId), cancellationToken);
        return changed == 1 ? RedirectToAction(nameof(Applications)) : Conflict();
    }

    private async Task<ApplicationsViewModel> ApplicationsModel(string? keyword = null, CancellationToken cancellationToken = default)
    {
        var accessibleIds = await applicationAccess.GetAccessibleApplicationIdsAsync(User, cancellationToken: cancellationToken);
        var visibleIds = new HashSet<Guid>(accessibleIds);
        foreach (var permission in Enum.GetValues<ApplicationPermission>())
            visibleIds.UnionWith(await applicationAccess.GetAccessibleApplicationIdsAsync(User, permission, cancellationToken));
        var applications = db.SourceApplications.AsNoTracking().Where(application => visibleIds.Contains(application.Id));
        if (!string.IsNullOrEmpty(keyword)) applications = applications.Where(application => application.Name.Contains(keyword));
        return new ApplicationsViewModel
        {
            Applications = await applications.Include(application => application.OnCallGroup)
                .OrderByDescending(application => application.CreatedAtUtc).ToListAsync(cancellationToken),
            OnCallGroups = applicationAccess.CanManageApplications(User)
                ? await db.OnCallGroups.AsNoTracking().OrderBy(group => group.Name).ToListAsync(cancellationToken) : [],
            ApplicationSearchKeyword = keyword,
            CanManageApplications = applicationAccess.CanManageApplications(User),
            CreatableApplicationIds = (await applicationAccess.GetAccessibleApplicationIdsAsync(User, ApplicationPermission.CreateIncidents, cancellationToken)).ToHashSet(),
            ViewableApplicationIds = accessibleIds.ToHashSet(),
            KeyManageableApplicationIds = (await applicationAccess.GetAccessibleApplicationIdsAsync(User, ApplicationPermission.ManageApiKeys, cancellationToken)).ToHashSet(),
            OwnedApplicationIds = (await db.SourceApplications.Where(application => application.OwnerId == User.FindFirstValue(ClaimTypes.NameIdentifier)).Select(application => application.Id).ToListAsync(cancellationToken)).ToHashSet(),
            ManageableApplicationIds = (await applicationAccess.GetAccessibleApplicationIdsAsync(User, ApplicationPermission.ManageApplication, cancellationToken)).ToHashSet()
        };
    }
}

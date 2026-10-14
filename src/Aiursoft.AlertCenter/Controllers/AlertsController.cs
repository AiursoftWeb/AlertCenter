using System.Security.Cryptography;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class AlertsController(TemplateDbContext db, IApplicationAccessService applicationAccess) : Controller
{
    private const string CreatedApplicationApiKey = "CreatedApplicationApiKey";
    private const string ApiKeyWasReset = "ApiKeyWasReset";

    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "告警中心", CascadedLinksIcon = "bell", CascadedLinksOrder = 1,
        LinkText = "消息列表", LinkOrder = 0)]
    public async Task<IActionResult> Index([FromQuery] AlertFilter filter, CancellationToken cancellationToken)
    {
        filter.Normalize();
        var accessibleIds = await applicationAccess.GetAccessibleApplicationIdsAsync(User, cancellationToken: cancellationToken);
        var model = new AlertsViewModel
        {
            AlertFilter = filter,
            Page = filter.Page,
            IsAdministrator = applicationAccess.IsAdministrator(User),
            ManageableApplicationIds = (await applicationAccess.GetAccessibleApplicationIdsAsync(User, true, cancellationToken)).ToHashSet(),
            Applications = await db.SourceApplications.AsNoTracking().Where(application => accessibleIds.Contains(application.Id) &&
                db.ReportedMessages.Any(message => message.ApplicationId == application.Id))
                .OrderBy(application => application.Name).ToListAsync(cancellationToken)
        };
        if (filter.ApplicationId.HasValue && !accessibleIds.Contains(filter.ApplicationId.Value)) return Forbid();
        if (!ModelState.IsValid) return this.StackView(model);
        var query = FilterMessages(db.ReportedMessages.AsNoTracking().Where(message => accessibleIds.Contains(message.ApplicationId)), filter);
        var messages = await query.Include(message => message.Application).Include(message => message.Incident)
            .OrderByDescending(message => message.ReceivedAtUtc).ThenByDescending(message => message.Id)
            .Skip((filter.Page - 1) * 20).Take(21).ToListAsync();
        model.Messages = messages.Take(20).ToList();
        model.HasNextPage = messages.Count > 20;
        return this.StackView(model);
    }

    private static IQueryable<ReportedMessage> FilterMessages(IQueryable<ReportedMessage> query, AlertFilter filter)
    {
        if (filter.ApplicationId.HasValue)
            query = query.Where(message => message.ApplicationId == filter.ApplicationId.Value);
        if (filter.Level != null)
            query = query.Where(message => message.Level == filter.Level);
        if (filter.Keyword != null)
        {
            var keyword = filter.Keyword.ToLowerInvariant();
            query = query.Where(message => message.Title.ToLower().Contains(keyword) ||
                (message.Content != null && message.Content.ToLower().Contains(keyword)));
        }
        if (filter.FromDate.HasValue)
        {
            var start = filter.FromDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(message => message.ReceivedAtUtc >= start);
        }
        if (filter.ToDate.HasValue && filter.ToDate.Value < DateOnly.MaxValue)
        {
            var end = filter.ToDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(message => message.ReceivedAtUtc < end);
        }
        return query;
    }

    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "告警中心", CascadedLinksIcon = "bell", CascadedLinksOrder = 1,
        LinkText = "接入应用", LinkOrder = 1)]
    public async Task<IActionResult> Applications(string? keyword, CancellationToken cancellationToken)
    {
        keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        var model = await ApplicationsModel(keyword, cancellationToken);
        model.ApiKey = TempData[CreatedApplicationApiKey] as string;
        model.ApiKeyWasReset = TempData[ApiKeyWasReset] is true;
        return this.StackView(model);
    }

    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "告警中心", CascadedLinksIcon = "bell", CascadedLinksOrder = 1,
        LinkText = "接入指南", LinkOrder = 2)]
    public IActionResult GettingStarted() => this.StackView(new AlertsViewModel { PageTitle = "接入指南" });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateApplication(CreateApplicationRequest request, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        if (!await OnCallGroupExists(request.OnCallGroupId, cancellationToken))
            ModelState.AddModelError(nameof(request.OnCallGroupId), "所选值班组不存在，请重新选择。");
        if (!ModelState.IsValid)
        {
            var model = await ApplicationsModel(cancellationToken: cancellationToken);
            model.ApplicationInput = request;
            return this.StackView(model, "Applications");
        }
        var application = new SourceApplication { Name = request.Name.Trim(), OnCallGroupId = request.OnCallGroupId };
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        application.SecretHash = new PasswordHasher<SourceApplication>().HashPassword(application, secret);
        db.SourceApplications.Add(application);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (await OnCallGroupExists(request.OnCallGroupId, cancellationToken)) throw;
            ModelState.AddModelError(nameof(request.OnCallGroupId), "值班组已被删除，请重新选择。");
            var model = await ApplicationsModel(cancellationToken: cancellationToken);
            model.ApplicationInput = request;
            return this.StackView(model, "Applications");
        }
        TempData[CreatedApplicationApiKey] = $"{application.Id}.{secret}";
        TempData[ApiKeyWasReset] = false;
        return RedirectToAction(nameof(Applications));
    }

    public async Task<IActionResult> OnCall(Guid id, CancellationToken cancellationToken)
    {
        var application = await db.SourceApplications.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (application == null) return NotFound();
        if (!await applicationAccess.CanAccessAsync(User, id, requireManage: true, cancellationToken)) return Forbid();
        return this.StackView(await OnCallModel(application, application.OnCallGroupId, cancellationToken));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveOnCall(SaveApplicationOnCallRequest request, CancellationToken cancellationToken)
    {
        var application = await db.SourceApplications.SingleOrDefaultAsync(item => item.Id == request.ApplicationId, cancellationToken);
        if (application == null) return NotFound();
        if (!await applicationAccess.CanAccessAsync(User, application.Id, requireManage: true, cancellationToken)) return Forbid();
        if (!await OnCallGroupExists(request.OnCallGroupId, cancellationToken))
            ModelState.AddModelError(nameof(request.OnCallGroupId), "所选值班组不存在，请重新选择。");
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
            ModelState.AddModelError(nameof(request.OnCallGroupId), "值班组已被删除，请重新选择。");
            return this.StackView(await OnCallModel(application, request.OnCallGroupId, cancellationToken), "OnCall");
        }
        TempData["ApplicationOnCallSuccess"] = request.OnCallGroupId.HasValue ? "应用值班组已更新。" : "应用已解除值班组绑定。";
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
    public async Task<IActionResult> ResetApiKey(Guid id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || id == Guid.Empty) return BadRequest("请选择有效的应用。");
        var application = await db.SourceApplications.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (application == null) return NotFound();
        if (!await applicationAccess.CanAccessAsync(User, id, true, cancellationToken)) return Forbid();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        application.SecretHash = new PasswordHasher<SourceApplication>().HashPassword(application, secret);
        await db.SaveChangesAsync(cancellationToken);
        TempData[CreatedApplicationApiKey] = $"{application.Id}.{secret}";
        TempData[ApiKeyWasReset] = true;
        return RedirectToAction(nameof(Applications));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetEnabled(Guid id, bool enabled, CancellationToken cancellationToken)
    {
        var application = await db.SourceApplications.FindAsync([id], cancellationToken);
        if (application == null) return NotFound();
        if (!await applicationAccess.CanAccessAsync(User, id, requireManage: true, cancellationToken)) return Forbid();
        application.IsEnabled = enabled;
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Applications));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteApplication(Guid id, string? confirmationName, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var applicationName = await db.SourceApplications.AsNoTracking()
            .Where(application => application.Id == id)
            .Select(application => application.Name).SingleOrDefaultAsync(cancellationToken);
        if (applicationName == null) return NotFound();
        if (!await applicationAccess.CanAccessAsync(User, id, requireManage: true, cancellationToken)) return Forbid();
        if (!ModelState.IsValid || string.IsNullOrEmpty(confirmationName) ||
            !string.Equals(applicationName, confirmationName, StringComparison.Ordinal))
        {
            return BadRequest("请输入完全一致的应用名称以确认删除。");
        }
        // 在同一事务中批量删除历史消息和应用，避免部分删除或逐条加载消息。
        await db.Incidents.Where(incident => incident.ApplicationId == id)
            .ExecuteDeleteAsync(cancellationToken);
        await db.ReportedMessages.Where(message => message.ApplicationId == id)
            .ExecuteDeleteAsync(cancellationToken);
        var deleted = await db.SourceApplications.Where(application => application.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted == 0 ? NotFound() : RedirectToAction(nameof(Applications));
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var message = await db.ReportedMessages.AsNoTracking().Include(m => m.Application).Include(m => m.Incident)
            .SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (message == null) return NotFound();
        if (!await applicationAccess.CanAccessAsync(User, message.ApplicationId, cancellationToken: cancellationToken) &&
            (message.Incident == null || !await new IncidentAccessService(db, applicationAccess).CanViewAsync(User, message.Incident, DateTime.UtcNow, cancellationToken))) return Forbid();
        return this.StackView(new AlertsViewModel
        {
            Message = message,
            IsAdministrator = applicationAccess.IsAdministrator(User),
            ManageableApplicationIds = (await applicationAccess.GetAccessibleApplicationIdsAsync(User, true, cancellationToken)).ToHashSet()
        });
    }

    public async Task<IActionResult> Access(Guid id, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        var application = await db.SourceApplications.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (application == null) return NotFound();
        return this.StackView(new ApplicationAccessViewModel
        {
            Application = application,
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
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        if (!await db.SourceApplications.AnyAsync(application => application.Id == request.ApplicationId, cancellationToken)) return NotFound();
        var viewUsers = request.ViewUserIds.Concat(request.ManageUserIds).Distinct().ToHashSet();
        var manageUsers = request.ManageUserIds.ToHashSet();
        var viewRoles = request.ViewRoleIds.Concat(request.ManageRoleIds).Distinct().ToHashSet();
        var manageRoles = request.ManageRoleIds.ToHashSet();
        if (await db.Users.CountAsync(user => viewUsers.Contains(user.Id), cancellationToken) != viewUsers.Count ||
            await db.Roles.CountAsync(role => viewRoles.Contains(role.Id), cancellationToken) != viewRoles.Count)
            return BadRequest("所选用户或角色不存在，请刷新页面后重试。");
        var users = await db.ApplicationUserAccesses.Where(access => access.ApplicationId == request.ApplicationId).ToListAsync(cancellationToken);
        var roles = await db.ApplicationRoleAccesses.Where(access => access.ApplicationId == request.ApplicationId).ToListAsync(cancellationToken);
        db.ApplicationUserAccesses.RemoveRange(users.Where(access => !viewUsers.Contains(access.UserId)));
        db.ApplicationRoleAccesses.RemoveRange(roles.Where(access => !viewRoles.Contains(access.RoleId)));
        foreach (var userId in viewUsers)
        {
            var access = users.SingleOrDefault(item => item.UserId == userId);
            if (access == null) db.ApplicationUserAccesses.Add(new ApplicationUserAccess { ApplicationId = request.ApplicationId, UserId = userId, CanManage = manageUsers.Contains(userId) });
            else access.CanManage = manageUsers.Contains(userId);
        }
        foreach (var roleId in viewRoles)
        {
            var access = roles.SingleOrDefault(item => item.RoleId == roleId);
            if (access == null) db.ApplicationRoleAccesses.Add(new ApplicationRoleAccess { ApplicationId = request.ApplicationId, RoleId = roleId, CanManage = manageRoles.Contains(roleId) });
            else access.CanManage = manageRoles.Contains(roleId);
        }
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Access), new { id = request.ApplicationId });
    }

    private async Task<AlertsViewModel> ApplicationsModel(string? keyword = null, CancellationToken cancellationToken = default)
    {
        var accessibleIds = await applicationAccess.GetAccessibleApplicationIdsAsync(User, cancellationToken: cancellationToken);
        var applications = db.SourceApplications.AsNoTracking().Where(application => accessibleIds.Contains(application.Id));
        if (!string.IsNullOrEmpty(keyword)) applications = applications.Where(application => application.Name.Contains(keyword));
        return new AlertsViewModel
        {
            Applications = await applications.Include(application => application.OnCallGroup)
                .OrderByDescending(application => application.CreatedAtUtc).ToListAsync(cancellationToken),
            OnCallGroups = applicationAccess.IsAdministrator(User)
                ? await db.OnCallGroups.AsNoTracking().OrderBy(group => group.Name).ToListAsync(cancellationToken) : [],
            ApplicationSearchKeyword = keyword,
            IsAdministrator = applicationAccess.IsAdministrator(User),
            ManageableApplicationIds = (await applicationAccess.GetAccessibleApplicationIdsAsync(User, true, cancellationToken)).ToHashSet()
        };
    }
}

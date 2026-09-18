using System.Security.Claims;
using Aiursoft.AlertCenter.Authorization;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class PushController(TemplateDbContext db, IEnumerable<INotificationChannelAdapter> adapters,
    PushDeliveryProcessor processor, WebhookConfigurationProtector webhookConfigurationProtector,
    IApplicationAccessService applicationAccess, IPushChannelAccessService channelAccess) : Controller
{
    private List<ChannelTypeDefinition> Types => adapters.Select(adapter => adapter.Definition).ToList();

    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "推送中心", CascadedLinksIcon = "send", CascadedLinksOrder = 2,
        LinkText = "推送渠道", LinkOrder = 0)]
    [Authorize(Policy = AppPermissionNames.CanManagePush)]
    public async Task<IActionResult> Index() => this.StackView(new PushViewModel
    {
        ChannelTypes = Types,
        Channels = await db.PushChannels.AsNoTracking().OrderByDescending(channel => channel.CreatedAtUtc).ToListAsync()
    });

    [HttpGet]
    [Authorize(Policy = AppPermissionNames.CanManagePush)]
    public async Task<IActionResult> Create(string? type)
    {
        var model = new PushChannelEditViewModel
        {
            Type = Types.Any(item => item.Key == type) ? type! : string.Empty, ChannelTypes = Types
        };
        await PopulateRecipients(model);
        return this.StackView(model, "Edit");
    }

    [HttpGet]
    [Authorize(Policy = AppPermissionNames.CanManagePush)]
    public async Task<IActionResult> Edit(Guid id)
    {
        var channel = await db.PushChannels.FindAsync(id);
        if (channel == null) return NotFound();
        string? webhookHeadersJson = null;
        if (channel.Type == "webhook" && !webhookConfigurationProtector.TryUnprotect(channel.WebhookHeadersProtected, out webhookHeadersJson))
            return BadRequest("无法读取已保存的 Webhook 请求头。请检查数据保护密钥后重试。");
        var model = new PushChannelEditViewModel
        {
            Id = channel.Id, Name = channel.Name, Type = channel.Type, Endpoint = channel.Endpoint,
            WebhookHeadersJson = webhookHeadersJson, WebhookPayloadTemplate = channel.WebhookPayloadTemplate,
            EmailRecipients = channel.EmailRecipients, IsEnabled = channel.IsEnabled, ChannelTypes = Types,
            RecipientUserIds = await db.PushChannelUsers.Where(link => link.ChannelId == id).Select(link => link.UserId).ToArrayAsync(),
            RecipientRoleIds = await db.PushChannelRoles.Where(link => link.ChannelId == id).Select(link => link.RoleId).ToArrayAsync(),
            BindingUserIds = await db.PushChannelBindingUsers.Where(link => link.ChannelId == id).Select(link => link.UserId).ToArrayAsync(),
            BindingRoleIds = await db.PushChannelBindingRoles.Where(link => link.ChannelId == id).Select(link => link.RoleId).ToArrayAsync()
        };
        await PopulateRecipients(model);
        return this.StackView(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AppPermissionNames.CanManagePush)]
    public async Task<IActionResult> Save(PushChannelEditViewModel model)
    {
        model.ChannelTypes = Types;
        await PopulateRecipients(model);
        model.RecipientUserIds = model.RecipientUserIds.Distinct().ToArray();
        model.RecipientRoleIds = model.RecipientRoleIds.Distinct().ToArray();
        model.BindingUserIds = model.BindingUserIds.Distinct().ToArray();
        model.BindingRoleIds = model.BindingRoleIds.Distinct().ToArray();
        if (await db.Users.CountAsync(user => model.BindingUserIds.Contains(user.Id)) != model.BindingUserIds.Length)
            ModelState.AddModelError(nameof(model.BindingUserIds), "所选可绑定用户不存在，请重新选择。");
        if (await db.Roles.CountAsync(role => model.BindingRoleIds.Contains(role.Id)) != model.BindingRoleIds.Length)
            ModelState.AddModelError(nameof(model.BindingRoleIds), "所选可绑定用户组不存在，请重新选择。");
        if (await db.Users.CountAsync(user => model.RecipientUserIds.Contains(user.Id)) != model.RecipientUserIds.Length)
            ModelState.AddModelError(nameof(model.RecipientUserIds), "所选接收用户不存在，请重新选择。");
        if (await db.Roles.CountAsync(role => model.RecipientRoleIds.Contains(role.Id)) != model.RecipientRoleIds.Length)
            ModelState.AddModelError(nameof(model.RecipientRoleIds), "所选接收用户组不存在，请重新选择。");
        if (model.Type == "inapp" && model.RecipientUserIds.Length + model.RecipientRoleIds.Length == 0)
            ModelState.AddModelError(nameof(model.RecipientUserIds), "请至少选择一个接收用户或用户组。");
        var type = Types.SingleOrDefault(item => item.Key == model.Type);
        if (type == null) ModelState.AddModelError(nameof(model.Type), "请选择可用的渠道类型。");
        if (type?.RequiresEndpoint == true)
        {
            if (!WebhookChannelAdapter.IsValidEndpoint(model.Endpoint?.Trim()))
                ModelState.AddModelError(nameof(model.Endpoint), "请输入有效的 HTTP 或 HTTPS 地址，不支持用户名、密码或 URL 片段。");
            if (!WebhookChannelAdapter.TryValidateCustomHeaders(model.WebhookHeadersJson, out var headersError))
                ModelState.AddModelError(nameof(model.WebhookHeadersJson), headersError!);
            if (!WebhookChannelAdapter.TryValidatePayloadTemplate(model.WebhookPayloadTemplate, out var payloadError))
                ModelState.AddModelError(nameof(model.WebhookPayloadTemplate), payloadError!);
            if (!string.IsNullOrWhiteSpace(model.WebhookHeadersJson) && !webhookConfigurationProtector.IsAvailable)
                ModelState.AddModelError(nameof(model.WebhookHeadersJson), "自定义请求头需要配置 Webhook:HeaderProtectionKey。请通过受保护的环境变量提供该密钥。");
        }
        if (model.Type == "email" && !EmailChannelAdapter.TryParseRecipients(model.EmailRecipients, out _, out var recipientsError))
            ModelState.AddModelError(nameof(model.EmailRecipients), recipientsError!);
        var channel = model.Id.HasValue ? await db.PushChannels.FindAsync(model.Id.Value) : new PushChannel();
        if (channel == null) return NotFound();
        if (model.Id.HasValue && channel.Type != model.Type)
            ModelState.AddModelError(nameof(model.Type), "已有渠道不能更改类型，请创建新的渠道。");
        if (!ModelState.IsValid) return this.StackView(model, "Edit");
        channel.Name = model.Name.Trim();
        channel.Type = model.Type;
        channel.Endpoint = type!.RequiresEndpoint ? model.Endpoint!.Trim() : null;
        string? protectedHeaders = null;
        if (type.RequiresEndpoint && !webhookConfigurationProtector.TryProtect(NullIfWhiteSpace(model.WebhookHeadersJson), out protectedHeaders))
            return BadRequest("无法保护 Webhook 自定义请求头。请检查 Webhook:HeaderProtectionKey 配置。");
        channel.WebhookHeadersProtected = type.RequiresEndpoint ? protectedHeaders : null;
        channel.WebhookPayloadTemplate = type.RequiresEndpoint ? NullIfWhiteSpace(model.WebhookPayloadTemplate) : null;
        channel.EmailRecipients = model.Type == "email" ? NullIfWhiteSpace(model.EmailRecipients) : null;
        channel.IsEnabled = model.IsEnabled;
        if (!model.Id.HasValue) db.PushChannels.Add(channel);
        var users = await db.PushChannelUsers.Where(link => link.ChannelId == channel.Id).ToListAsync();
        var roles = await db.PushChannelRoles.Where(link => link.ChannelId == channel.Id).ToListAsync();
        var selectedUsers = model.Type == "inapp" ? model.RecipientUserIds : [];
        var selectedRoles = model.Type == "inapp" ? model.RecipientRoleIds : [];
        db.PushChannelUsers.RemoveRange(users.Where(link => !selectedUsers.Contains(link.UserId)));
        db.PushChannelRoles.RemoveRange(roles.Where(link => !selectedRoles.Contains(link.RoleId)));
        db.PushChannelUsers.AddRange(selectedUsers.Except(users.Select(link => link.UserId))
            .Select(id => new PushChannelUser { Channel = channel, UserId = id }));
        db.PushChannelRoles.AddRange(selectedRoles.Except(roles.Select(link => link.RoleId))
            .Select(id => new PushChannelRole { Channel = channel, RoleId = id }));
        var bindingUsers = await db.PushChannelBindingUsers.Where(link => link.ChannelId == channel.Id).ToListAsync();
        var bindingRoles = await db.PushChannelBindingRoles.Where(link => link.ChannelId == channel.Id).ToListAsync();
        db.PushChannelBindingUsers.RemoveRange(bindingUsers.Where(link => !model.BindingUserIds.Contains(link.UserId)));
        db.PushChannelBindingRoles.RemoveRange(bindingRoles.Where(link => !model.BindingRoleIds.Contains(link.RoleId)));
        db.PushChannelBindingUsers.AddRange(model.BindingUserIds.Except(bindingUsers.Select(link => link.UserId))
            .Select(id => new PushChannelBindingUser { Channel = channel, UserId = id }));
        db.PushChannelBindingRoles.AddRange(model.BindingRoleIds.Except(bindingRoles.Select(link => link.RoleId))
            .Select(id => new PushChannelBindingRole { Channel = channel, RoleId = id }));
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    [Authorize(Policy = AppPermissionNames.CanManagePush)]
    public async Task<IActionResult> SearchRecipients(string query, int page = 1, int pageSize = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Json(new { users = Array.Empty<object>(), totalCount = 0, page = 1, pageSize = 10, totalPages = 0 });

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 20);
        var normalizedQuery = query.Trim();
        var users = db.Users.AsNoTracking().Where(user =>
            user.UserName!.Contains(normalizedQuery) || user.DisplayName.Contains(normalizedQuery));
        var totalCount = await users.CountAsync();
        var results = await users.OrderBy(user => user.UserName).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(user => new { id = user.Id, displayName = user.DisplayName, userName = user.UserName })
            .ToListAsync();
        return Json(new
        {
            users = results,
            totalCount,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
        });
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<List<PushChannel>> GetVisibleChannelsAsync(bool canManagePush, CancellationToken cancellationToken)
    {
        if (canManagePush)
        {
            return await db.PushChannels.AsNoTracking().OrderBy(channel => channel.Name).ToListAsync(cancellationToken);
        }

        var bindableChannelIds = await channelAccess.GetBindableChannelIdsAsync(User, cancellationToken);
        return await db.PushChannels.AsNoTracking().Where(channel => bindableChannelIds.Contains(channel.Id))
            .OrderBy(channel => channel.Name).ToListAsync(cancellationToken);
    }

    private async Task PopulateRecipients(PushChannelEditViewModel model)
    {
        model.AvailableRoles = await db.Roles.AsNoTracking().OrderBy(role => role.Name)
            .Select(role => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem(role.Name, role.Id)).ToListAsync();
        model.AvailableUsers = await db.Users.AsNoTracking().OrderBy(user => user.UserName)
            .Select(user => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem(
                $"{user.DisplayName} ({user.UserName})", user.Id)).ToListAsync();
        model.SelectedUsers = await db.Users.AsNoTracking().Where(user => model.RecipientUserIds.Contains(user.Id))
            .OrderBy(user => user.UserName).Select(user => new RecipientSelection
            {
                Id = user.Id, Name = user.DisplayName, UserName = user.UserName
            }).ToListAsync();
        model.SelectedRoles = await db.Roles.AsNoTracking().Where(role => model.RecipientRoleIds.Contains(role.Id))
            .OrderBy(role => role.Name).Select(role => new RecipientSelection { Id = role.Id, Name = role.Name! })
            .ToListAsync();
        model.SelectedBindingUsers = await db.Users.AsNoTracking().Where(user => model.BindingUserIds.Contains(user.Id))
            .OrderBy(user => user.UserName).Select(user => new RecipientSelection
            {
                Id = user.Id, Name = user.DisplayName, UserName = user.UserName
            }).ToListAsync();
        model.SelectedBindingRoles = await db.Roles.AsNoTracking().Where(role => model.BindingRoleIds.Contains(role.Id))
            .OrderBy(role => role.Name).Select(role => new RecipientSelection { Id = role.Id, Name = role.Name! })
            .ToListAsync();
    }

    public async Task<IActionResult> Notification(Guid id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var notification = await db.InAppNotifications.AsNoTracking()
            .Include(item => item.Delivery).ThenInclude(delivery => delivery.Message)!.ThenInclude(message => message!.Application)
            .SingleOrDefaultAsync(item => item.Id == id && item.UserId == userId);
        if (notification == null) return NotFound();
        return this.StackView(new PushViewModel { PageTitle = "站内通知详情", Notifications = [notification] });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AppPermissionNames.CanManagePush)]
    public async Task<IActionResult> SetEnabled(Guid id, bool enabled)
    {
        var channel = await db.PushChannels.FindAsync(id);
        if (channel == null) return NotFound();
        channel.IsEnabled = enabled;
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AppPermissionNames.CanManagePush)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var deleted = await db.PushChannels.Where(channel => channel.Id == id).ExecuteDeleteAsync();
        return deleted == 0 ? NotFound() : RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Bindings(Guid id, CancellationToken cancellationToken)
    {
        var application = await db.SourceApplications.FindAsync([id], cancellationToken);
        if (application == null) return NotFound();
        if (!await applicationAccess.CanAccessAsync(User, id, requireManage: true, cancellationToken)) return Forbid();
        var bindableChannelIds = await channelAccess.GetBindableChannelIdsAsync(User, cancellationToken);
        var bindings = await db.ApplicationPushChannels.AsNoTracking()
            .Where(link => link.ApplicationId == id && bindableChannelIds.Contains(link.ChannelId)).ToListAsync(cancellationToken);
        return this.StackView(new PushViewModel
        {
            Application = application, ChannelTypes = Types,
            Channels = await db.PushChannels.AsNoTracking().Where(channel => bindableChannelIds.Contains(channel.Id))
                .OrderBy(channel => channel.Name).ToListAsync(cancellationToken),
            Bindings = new ApplicationPushBindingsRequest
            {
                Sev1ChannelIds = bindings.Where(link => link.ReceiveSev1).Select(link => link.ChannelId).ToArray(),
                Sev2ChannelIds = bindings.Where(link => link.ReceiveSev2).Select(link => link.ChannelId).ToArray(),
                Sev3ChannelIds = bindings.Where(link => link.ReceiveSev3).Select(link => link.ChannelId).ToArray(),
                Sev4ChannelIds = bindings.Where(link => link.ReceiveSev4).Select(link => link.ChannelId).ToArray()
            }
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBindings(Guid id, ApplicationPushBindingsRequest bindings, CancellationToken cancellationToken = default)
    {
        if (!await db.SourceApplications.AnyAsync(application => application.Id == id, cancellationToken)) return NotFound();
        if (!await applicationAccess.CanAccessAsync(User, id, requireManage: true, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return BadRequest("渠道配置无效，请刷新页面后重试。");
        var sev1 = bindings.Sev1ChannelIds.ToHashSet();
        var sev2 = bindings.Sev2ChannelIds.ToHashSet();
        var sev3 = bindings.Sev3ChannelIds.ToHashSet();
        var sev4 = bindings.Sev4ChannelIds.ToHashSet();
        var selected = sev1.Concat(sev2).Concat(sev3).Concat(sev4).ToHashSet();
        if (await db.PushChannels.CountAsync(channel => selected.Contains(channel.Id)) != selected.Count)
            return BadRequest("所选渠道已不存在，请刷新页面后重试。");
        var bindableChannelIds = await channelAccess.GetBindableChannelIdsAsync(User, cancellationToken);
        if (selected.Any(channelId => !bindableChannelIds.Contains(channelId)))
            return Forbid();
        var existing = await db.ApplicationPushChannels.Where(link =>
                link.ApplicationId == id && bindableChannelIds.Contains(link.ChannelId))
            .ToDictionaryAsync(link => link.ChannelId);
        db.ApplicationPushChannels.RemoveRange(existing.Values.Where(link => !selected.Contains(link.ChannelId)));
        foreach (var channelId in selected)
        {
            if (!existing.TryGetValue(channelId, out var link))
            {
                link = new ApplicationPushChannel { ApplicationId = id, ChannelId = channelId };
                db.ApplicationPushChannels.Add(link);
            }
            link.ReceiveSev1 = sev1.Contains(channelId);
            link.ReceiveSev2 = sev2.Contains(channelId);
            link.ReceiveSev3 = sev3.Contains(channelId);
            link.ReceiveSev4 = sev4.Contains(channelId);
        }
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Bindings), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AppPermissionNames.CanManagePush)]
    public async Task<IActionResult> Test(Guid id, CancellationToken cancellationToken, [FromForm] string? content = null)
    {
        content ??= PushDelivery.DefaultTestContent;
        content = content.Trim();
        if (content.Length == 0 || content.Length > 4000)
            return BadRequest("测试消息内容不能为空，且不能超过 4000 个字符。");
        var channel = await db.PushChannels.FindAsync(id);
        if (channel == null) return NotFound();
        if (!channel.IsEnabled) return BadRequest("请先启用渠道。");
        var delivery = new PushDelivery { ChannelId = id, TestContent = content };
        db.PushDeliveries.Add(delivery);
        if (channel.Type == "inapp") await InAppRecipientSnapshot.CreateAsync(db, delivery, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await processor.ProcessAsync(delivery.Id, cancellationToken);
        return RedirectToAction(nameof(Delivery), new { id = delivery.Id });
    }

    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "推送中心", CascadedLinksIcon = "send", CascadedLinksOrder = 2,
        LinkText = "推送记录", LinkOrder = 1)]
    public async Task<IActionResult> Records(int page = 1, Guid? channelId = null, string? status = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Clamp(page, 1, 1000000);
        var canManagePush = User.HasClaim(AppPermissions.Type, AppPermissionNames.CanManagePush) ||
                            applicationAccess.IsAdministrator(User);
        var accessibleIds = await applicationAccess.GetAccessibleApplicationIdsAsync(User, cancellationToken: cancellationToken);
        var query = db.PushDeliveries.AsNoTracking().Where(delivery => delivery.Message != null &&
            (canManagePush || accessibleIds.Contains(delivery.Message.ApplicationId)));
        if (channelId.HasValue) query = query.Where(delivery => delivery.ChannelId == channelId);
        if (!canManagePush)
        {
            var bindableChannelIds = await channelAccess.GetBindableChannelIdsAsync(User, cancellationToken);
            query = query.Where(delivery => bindableChannelIds.Contains(delivery.ChannelId));
        }
        if (status is PushDelivery.Pending or PushDelivery.Sending or PushDelivery.Succeeded or PushDelivery.Failed)
            query = query.Where(delivery => delivery.Status == status);
        else status = null;
        var deliveries = await query.Include(delivery => delivery.Channel).Include(delivery => delivery.Message)!
            .ThenInclude(message => message!.Application).OrderByDescending(delivery => delivery.CreatedAtUtc)
            .ThenByDescending(delivery => delivery.Id).Skip((page - 1) * 20).Take(21).ToListAsync();
        return this.StackView(new PushViewModel
        {
            Deliveries = deliveries.Take(20).ToList(), Page = page, HasNextPage = deliveries.Count > 20,
            ChannelId = channelId, Status = status, ChannelTypes = Types,
            Channels = await GetVisibleChannelsAsync(canManagePush, cancellationToken)
        });
    }

    public async Task<IActionResult> Delivery(Guid id, CancellationToken cancellationToken)
    {
        var delivery = await db.PushDeliveries.AsNoTracking().Include(item => item.Channel).Include(item => item.Message)!
            .ThenInclude(message => message!.Application).SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (delivery == null) return NotFound();
        var canManagePush = User.HasClaim(AppPermissions.Type, AppPermissionNames.CanManagePush) ||
                            applicationAccess.IsAdministrator(User);
        if (delivery.Message == null || (!canManagePush && !await applicationAccess.CanAccessAsync(
                User, delivery.Message.ApplicationId, cancellationToken: cancellationToken))) return Forbid();
        if (!canManagePush && !await channelAccess.CanBindAsync(User, delivery.ChannelId, cancellationToken)) return Forbid();
        return this.StackView(new PushViewModel
        {
            Delivery = delivery,
            ChannelTypes = Types,
            IsAdministrator = canManagePush
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AppPermissionNames.CanManagePush)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken = default)
    {
        var deliveryAccess = await db.PushDeliveries.Where(delivery => delivery.Id == id && delivery.MessageId != null)
            .Select(delivery => new { delivery.ChannelId, ApplicationId = delivery.Message!.ApplicationId })
            .SingleOrDefaultAsync(cancellationToken);
        if (deliveryAccess == null) return NotFound();
        if (!await applicationAccess.CanAccessAsync(User, deliveryAccess.ApplicationId, requireManage: true, cancellationToken) ||
            !await channelAccess.CanBindAsync(User, deliveryAccess.ChannelId, cancellationToken)) return Forbid();
        var updated = await db.PushDeliveries.Where(delivery => delivery.Id == id &&
                delivery.Status == PushDelivery.Failed && delivery.Channel.IsEnabled)
            .ExecuteUpdateAsync(setters => setters.SetProperty(delivery => delivery.Status, PushDelivery.Pending)
                .SetProperty(delivery => delivery.Error, (string?)null)
                .SetProperty(delivery => delivery.CompletedAtUtc, (DateTime?)null));
        if (updated == 0) return Conflict("仅可重试已启用渠道的失败记录，请刷新页面。");
        return RedirectToAction(nameof(Delivery), new { id });
    }

    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "推送中心", CascadedLinksIcon = "send", CascadedLinksOrder = 2,
        LinkText = "站内通知", LinkOrder = 2)]
    public async Task<IActionResult> Inbox([FromQuery] InboxFilter filter)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        filter.Normalize();
        var ownNotifications = db.InAppNotifications.AsNoTracking().Where(notification => notification.UserId == userId);
        var model = new PushViewModel
        {
            InboxFilter = filter, Page = filter.Page,
            UnreadCount = await ownNotifications.CountAsync(notification => notification.ReadAtUtc == null),
            Applications = await db.SourceApplications.AsNoTracking().Where(application => ownNotifications.Any(
                    notification => notification.Delivery.Message != null && notification.Delivery.Message.ApplicationId == application.Id))
                .OrderBy(application => application.Name).ToListAsync(),
            Channels = await db.PushChannels.AsNoTracking().Where(channel => ownNotifications.Any(
                    notification => notification.Delivery.ChannelId == channel.Id))
                .OrderBy(channel => channel.Name).ToListAsync()
        };
        if (!ModelState.IsValid) return this.StackView(model);
        var query = FilterInbox(ownNotifications, filter);
        var notifications = await query.Include(notification => notification.Delivery).ThenInclude(delivery => delivery.Message)!
            .ThenInclude(message => message!.Application)
            .Include(notification => notification.Delivery).ThenInclude(delivery => delivery.Channel)
            .OrderByDescending(notification => notification.CreatedAtUtc).ThenByDescending(notification => notification.Id)
            .Skip((filter.Page - 1) * 20).Take(21).ToListAsync();
        model.Notifications = notifications.Take(20).ToList();
        model.HasNextPage = notifications.Count > 20;
        return this.StackView(model);
    }

    private static IQueryable<InAppNotification> FilterInbox(IQueryable<InAppNotification> query, InboxFilter filter)
    {
        if (filter.ReadStatus == "unread") query = query.Where(notification => notification.ReadAtUtc == null);
        if (filter.ReadStatus == "read") query = query.Where(notification => notification.ReadAtUtc != null);
        if (filter.ApplicationId.HasValue)
            query = query.Where(notification => notification.Delivery.Message != null &&
                notification.Delivery.Message.ApplicationId == filter.ApplicationId.Value);
        if (filter.ChannelId.HasValue)
            query = query.Where(notification => notification.Delivery.ChannelId == filter.ChannelId.Value);
        if (filter.Level != null)
            query = query.Where(notification => notification.Delivery.Message != null && notification.Delivery.Message.Level == filter.Level);
        if (filter.Keyword != null)
        {
            var keyword = filter.Keyword.ToLowerInvariant();
            query = query.Where(notification => notification.Delivery.Message == null
                ? "渠道测试通知".Contains(keyword) ||
                  (notification.Delivery.TestContent != null && notification.Delivery.TestContent.ToLower().Contains(keyword)) ||
                  (notification.Delivery.TestContent == null && PushDelivery.DefaultTestContent.Contains(keyword))
                : notification.Delivery.Message.Title.ToLower().Contains(keyword) ||
                  (notification.Delivery.Message.Content != null && notification.Delivery.Message.Content.ToLower().Contains(keyword)));
        }
        if (filter.FromDate.HasValue)
        {
            var start = filter.FromDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(notification => notification.CreatedAtUtc >= start);
        }
        if (filter.ToDate.HasValue && filter.ToDate.Value < DateOnly.MaxValue)
        {
            var end = filter.ToDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(notification => notification.CreatedAtUtc < end);
        }
        return query;
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(Guid id, [FromQuery] InboxFilter filter, bool returnToNotification = false)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!ModelState.IsValid) return BadRequest("筛选条件无效，请刷新页面后重试。");
        filter.Normalize();
        var notification = await db.InAppNotifications.SingleOrDefaultAsync(item => item.Id == id && item.UserId == userId);
        if (notification == null) return NotFound();
        notification.ReadAtUtc ??= DateTime.UtcNow;
        await db.SaveChangesAsync();
        return returnToNotification
            ? RedirectToAction(nameof(Notification), new { id })
            : RedirectToAction(nameof(Inbox), new RouteValueDictionary(filter.ToRouteValues()));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead([FromQuery] InboxFilter filter)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!ModelState.IsValid) return BadRequest("筛选条件无效，请刷新页面后重试。");
        filter.Normalize();
        await db.InAppNotifications.Where(notification => notification.UserId == userId && notification.ReadAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(notification => notification.ReadAtUtc, DateTime.UtcNow));
        return RedirectToAction(nameof(Inbox), new RouteValueDictionary(filter.ToRouteValues()));
    }
}

using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Controllers;

[Authorize(Roles = "Administrators")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class PushController(TemplateDbContext db, IEnumerable<INotificationChannelAdapter> adapters,
    PushDeliveryProcessor processor) : Controller
{
    private List<ChannelTypeDefinition> Types => adapters.Select(adapter => adapter.Definition).ToList();

    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "推送中心", CascadedLinksIcon = "send", CascadedLinksOrder = 2,
        LinkText = "推送渠道", LinkOrder = 0)]
    public async Task<IActionResult> Index() => this.StackView(new PushViewModel
    {
        ChannelTypes = Types,
        Channels = await db.PushChannels.AsNoTracking().OrderByDescending(channel => channel.CreatedAtUtc).ToListAsync()
    });

    [HttpGet]
    public IActionResult Create(string? type) => this.StackView(new PushChannelEditViewModel
    {
        Type = Types.Any(item => item.Key == type) ? type! : string.Empty, ChannelTypes = Types
    }, "Edit");

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var channel = await db.PushChannels.FindAsync(id);
        if (channel == null) return NotFound();
        return this.StackView(new PushChannelEditViewModel
        {
            Id = channel.Id, Name = channel.Name, Type = channel.Type, Endpoint = channel.Endpoint,
            IsEnabled = channel.IsEnabled, ChannelTypes = Types
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(PushChannelEditViewModel model)
    {
        model.ChannelTypes = Types;
        var type = Types.SingleOrDefault(item => item.Key == model.Type);
        if (type == null) ModelState.AddModelError(nameof(model.Type), "请选择可用的渠道类型。");
        if (type?.RequiresEndpoint == true && !WebhookChannelAdapter.IsValidEndpoint(model.Endpoint?.Trim()))
            ModelState.AddModelError(nameof(model.Endpoint), "请输入有效的 HTTP 或 HTTPS 地址，不支持用户名、密码或 URL 片段。");
        var channel = model.Id.HasValue ? await db.PushChannels.FindAsync(model.Id.Value) : new PushChannel();
        if (channel == null) return NotFound();
        if (model.Id.HasValue && channel.Type != model.Type)
            ModelState.AddModelError(nameof(model.Type), "已有渠道不能更改类型，请创建新的渠道。");
        if (!ModelState.IsValid) return this.StackView(model, "Edit");
        channel.Name = model.Name.Trim();
        channel.Type = model.Type;
        channel.Endpoint = type!.RequiresEndpoint ? model.Endpoint!.Trim() : null;
        channel.IsEnabled = model.IsEnabled;
        if (!model.Id.HasValue) db.PushChannels.Add(channel);
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetEnabled(Guid id, bool enabled)
    {
        var channel = await db.PushChannels.FindAsync(id);
        if (channel == null) return NotFound();
        channel.IsEnabled = enabled;
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var deleted = await db.PushChannels.Where(channel => channel.Id == id).ExecuteDeleteAsync();
        return deleted == 0 ? NotFound() : RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Bindings(Guid id)
    {
        var application = await db.SourceApplications.FindAsync(id);
        if (application == null) return NotFound();
        var bindings = await db.ApplicationPushChannels.AsNoTracking()
            .Where(link => link.ApplicationId == id).ToListAsync();
        return this.StackView(new PushViewModel
        {
            Application = application, ChannelTypes = Types,
            Channels = await db.PushChannels.AsNoTracking().OrderBy(channel => channel.Name).ToListAsync(),
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
    public async Task<IActionResult> SaveBindings(Guid id, ApplicationPushBindingsRequest bindings)
    {
        if (!await db.SourceApplications.AnyAsync(application => application.Id == id)) return NotFound();
        if (!ModelState.IsValid) return BadRequest("渠道配置无效，请刷新页面后重试。");
        var sev1 = bindings.Sev1ChannelIds.ToHashSet();
        var sev2 = bindings.Sev2ChannelIds.ToHashSet();
        var sev3 = bindings.Sev3ChannelIds.ToHashSet();
        var sev4 = bindings.Sev4ChannelIds.ToHashSet();
        var selected = sev1.Concat(sev2).Concat(sev3).Concat(sev4).ToHashSet();
        if (await db.PushChannels.CountAsync(channel => selected.Contains(channel.Id)) != selected.Count)
            return BadRequest("所选渠道已不存在，请刷新页面后重试。");
        var existing = await db.ApplicationPushChannels.Where(link => link.ApplicationId == id)
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
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Bindings), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Test(Guid id, CancellationToken cancellationToken)
    {
        var channel = await db.PushChannels.FindAsync(id);
        if (channel == null) return NotFound();
        if (!channel.IsEnabled) return BadRequest("请先启用渠道。");
        var delivery = new PushDelivery { ChannelId = id };
        db.PushDeliveries.Add(delivery);
        await db.SaveChangesAsync(cancellationToken);
        await processor.ProcessAsync(delivery.Id, cancellationToken);
        return RedirectToAction(nameof(Delivery), new { id = delivery.Id });
    }

    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "推送中心", CascadedLinksIcon = "send", CascadedLinksOrder = 2,
        LinkText = "推送记录", LinkOrder = 1)]
    public async Task<IActionResult> Records(int page = 1, Guid? channelId = null, string? status = null)
    {
        page = Math.Clamp(page, 1, 1000000);
        var query = db.PushDeliveries.AsNoTracking().AsQueryable();
        if (channelId.HasValue) query = query.Where(delivery => delivery.ChannelId == channelId);
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
            Channels = await db.PushChannels.AsNoTracking().OrderBy(channel => channel.Name).ToListAsync()
        });
    }

    public async Task<IActionResult> Delivery(Guid id)
    {
        var delivery = await db.PushDeliveries.AsNoTracking().Include(item => item.Channel).Include(item => item.Message)!
            .ThenInclude(message => message!.Application).SingleOrDefaultAsync(item => item.Id == id);
        if (delivery == null) return NotFound();
        return this.StackView(new PushViewModel { Delivery = delivery, ChannelTypes = Types });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Retry(Guid id)
    {
        if (!await db.PushDeliveries.AnyAsync(delivery => delivery.Id == id)) return NotFound();
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
    public async Task<IActionResult> Inbox(int page = 1, bool unreadOnly = false)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        page = Math.Clamp(page, 1, 1000000);
        var query = db.InAppNotifications.AsNoTracking().Where(notification => notification.UserId == userId);
        var unread = await query.CountAsync(notification => notification.ReadAtUtc == null);
        if (unreadOnly) query = query.Where(notification => notification.ReadAtUtc == null);
        var notifications = await query.Include(notification => notification.Delivery).ThenInclude(delivery => delivery.Message)
            .Include(notification => notification.Delivery).ThenInclude(delivery => delivery.Channel)
            .OrderByDescending(notification => notification.CreatedAtUtc).ThenByDescending(notification => notification.Id)
            .Skip((page - 1) * 20).Take(21).ToListAsync();
        return this.StackView(new PushViewModel
        {
            Notifications = notifications.Take(20).ToList(), Page = page, HasNextPage = notifications.Count > 20,
            UnreadCount = unread, UnreadOnly = unreadOnly
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(Guid id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        var notification = await db.InAppNotifications.SingleOrDefaultAsync(item => item.Id == id && item.UserId == userId);
        if (notification == null) return NotFound();
        notification.ReadAtUtc ??= DateTime.UtcNow;
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Inbox));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        await db.InAppNotifications.Where(notification => notification.UserId == userId && notification.ReadAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(notification => notification.ReadAtUtc, DateTime.UtcNow));
        return RedirectToAction(nameof(Inbox));
    }
}

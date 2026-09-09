using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public class InAppChannelAdapter(TemplateDbContext db) : INotificationChannelAdapter
{
    public ChannelTypeDefinition Definition { get; } = new("inapp", "站内通知",
        "投递到所选用户和用户组的站内收件箱，每人独立管理已读状态。", "bell-ring", false);

    public async Task<NotificationDeliveryResult> DeliverAsync(PushDelivery delivery, CancellationToken cancellationToken = default)
    {
        var directUsers = db.PushChannelUsers.Where(link => link.ChannelId == delivery.ChannelId)
            .Select(link => link.UserId);
        var groupUsers = from membership in db.UserRoles
            join target in db.PushChannelRoles on membership.RoleId equals target.RoleId
            where target.ChannelId == delivery.ChannelId
            select membership.UserId;
        var recipients = await directUsers.Union(groupUsers).ToListAsync(cancellationToken);
        if (recipients.Count == 0) return new(false, ErrorMessage: "没有可接收站内通知的用户，请配置接收用户或用户组。");
        var existing = await db.InAppNotifications.Where(notification => notification.DeliveryId == delivery.Id)
            .Select(notification => notification.UserId).ToListAsync(cancellationToken);
        db.InAppNotifications.AddRange(recipients.Except(existing).Select(userId => new InAppNotification
        {
            DeliveryId = delivery.Id, UserId = userId
        }));
        return new(true);
    }
}

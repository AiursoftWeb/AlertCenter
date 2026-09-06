using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public class InAppChannelAdapter(TemplateDbContext db) : INotificationChannelAdapter
{
    public ChannelTypeDefinition Definition { get; } = new("inapp", "站内通知",
        "投递到所有管理员的站内收件箱，每人独立管理已读状态。", "bell-ring", false);

    public async Task<NotificationDeliveryResult> DeliverAsync(PushDelivery delivery, CancellationToken cancellationToken = default)
    {
        var recipients = await (from membership in db.UserRoles
            join role in db.Roles on membership.RoleId equals role.Id
            where role.NormalizedName == "ADMINISTRATORS"
            select membership.UserId).Distinct().ToListAsync(cancellationToken);
        if (recipients.Count == 0) return new(false, ErrorMessage: "没有可接收站内通知的管理员。");
        var existing = await db.InAppNotifications.Where(notification => notification.DeliveryId == delivery.Id)
            .Select(notification => notification.UserId).ToListAsync(cancellationToken);
        db.InAppNotifications.AddRange(recipients.Except(existing).Select(userId => new InAppNotification
        {
            DeliveryId = delivery.Id, UserId = userId
        }));
        return new(true);
    }
}

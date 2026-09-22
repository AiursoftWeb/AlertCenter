using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public sealed class OceInAppChannelAdapter(TemplateDbContext db) : INotificationChannelAdapter
{
    public ChannelTypeDefinition Definition { get; } = new("oce-inapp", "当班工程师站内通知",
        "系统根据告警发生时的 OCE 快照投递到当班工程师的收件箱。", "radio-tower", false);

    public async Task<NotificationDeliveryResult> DeliverAsync(PushDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        var recipients = await db.PushDeliveryRecipients.Where(recipient => recipient.DeliveryId == delivery.Id)
            .Select(recipient => recipient.UserId).ToListAsync(cancellationToken);
        if (recipients.Count != 1)
            return new(false, ErrorMessage: "OCE 投递缺少唯一的当班工程师快照。");
        var userExists = await db.Users.AnyAsync(user => user.Id == recipients[0], cancellationToken);
        if (!userExists)
            return new(false, ErrorMessage: "OCE 投递目标用户已不存在，无法创建站内通知。");
        var exists = await db.InAppNotifications.AnyAsync(notification => notification.DeliveryId == delivery.Id &&
            notification.UserId == recipients[0], cancellationToken);
        if (!exists)
            db.InAppNotifications.Add(new InAppNotification { DeliveryId = delivery.Id, UserId = recipients[0] });
        return new(true);
    }
}

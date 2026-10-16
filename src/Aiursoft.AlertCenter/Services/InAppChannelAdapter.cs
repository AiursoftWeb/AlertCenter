using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public class InAppChannelAdapter(TemplateDbContext db, IStringLocalizer<InAppChannelAdapter> localizer) : INotificationChannelAdapter
{
    public ChannelTypeDefinition Definition { get; } = new("inapp", localizer["In-app notifications"].Value,
        localizer["Deliver to the in-site inbox of the selected users and user groups. Each user independently manages read status."].Value, "bell-ring", false);

    public async Task<NotificationDeliveryResult> DeliverAsync(PushDelivery delivery, CancellationToken cancellationToken = default)
    {
        var recipients = await db.PushDeliveryRecipients.Where(recipient => recipient.DeliveryId == delivery.Id)
            .Select(recipient => recipient.UserId).ToListAsync(cancellationToken);
        if (recipients.Count == 0) return new(false, ErrorMessage: localizer["No users available to receive in-site notifications. Please configure recipient users or user groups."].Value);
        var existing = await db.InAppNotifications.Where(notification => notification.DeliveryId == delivery.Id)
            .Select(notification => notification.UserId).ToListAsync(cancellationToken);
        db.InAppNotifications.AddRange(recipients.Except(existing).Select(userId => new InAppNotification
        {
            DeliveryId = delivery.Id, UserId = userId
        }));
        return new(true);
    }
}

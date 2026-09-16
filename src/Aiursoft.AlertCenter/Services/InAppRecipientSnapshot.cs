using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public static class InAppRecipientSnapshot
{
    public static async Task CreateAsync(TemplateDbContext db, PushDelivery delivery, CancellationToken cancellationToken)
    {
        var directUsers = db.PushChannelUsers.Where(link => link.ChannelId == delivery.ChannelId).Select(link => link.UserId);
        var groupUsers = from membership in db.UserRoles
            join target in db.PushChannelRoles on membership.RoleId equals target.RoleId
            where target.ChannelId == delivery.ChannelId
            select membership.UserId;
        var recipients = await directUsers.Union(groupUsers).ToListAsync(cancellationToken);
        db.PushDeliveryRecipients.AddRange(recipients.Select(userId => new PushDeliveryRecipient
        {
            Delivery = delivery, UserId = userId
        }));
    }
}

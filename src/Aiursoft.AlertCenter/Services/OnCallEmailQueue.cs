using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public static class OnCallEmailQueue
{
    public static async Task CreateAsync(TemplateDbContext db, ReportedMessage message, CancellationToken cancellationToken)
    {
        var group = await db.SourceApplications.AsNoTracking().Where(application => application.Id == message.ApplicationId)
            .Select(application => application.OnCallGroup).SingleOrDefaultAsync(cancellationToken);
        if (group == null) return;
        var atUtc = DateTime.SpecifyKind(message.ReceivedAtUtc, DateTimeKind.Utc);
        var oce = await new OnCallOceService(db).QueryAsync(group.Id, atUtc, cancellationToken);
        // 快照不关联可变的用户或班次外键，避免换班、删用户和重试改写历史收件人。
        var snapshot = new OnCallEmailSnapshot
        {
            GroupId = group.Id, GroupName = group.Name, ShiftId = oce.ShiftId,
            StartsAtUtc = oce.StartsAtUtc, EndsAtUtc = oce.EndsAtUtc, NotificationAtUtc = atUtc,
            ValidationError = oce.Error,
            Recipients = oce.Engineers.Select(engineer => new OnCallEmailRecipient
            {
                UserId = engineer.UserId, DisplayName = engineer.DisplayName, Email = engineer.Email
            }).ToList()
        };
        if (!EmailChannelAdapter.TryParseCcRecipients(group.EmailCcRecipients, out var cc, out _))
        {
            snapshot.ValidationError ??= "值班组抄送邮箱配置无效。";
            cc.Clear();
        }
        var to = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var engineer in snapshot.Recipients)
        {
            if (!EmailChannelAdapter.TryParseRecipient(engineer.Email, out var email, out _))
            {
                engineer.Email = null;
                snapshot.ValidationError ??= "当前 OCE 缺少有效的系统登记邮箱，值班邮件未发送。";
            }
            else
            {
                engineer.Email = email;
                to.Add(email);
            }
        }
        snapshot.CcRecipients = string.Join('\n', cc.Where(address => !to.Contains(address)));
        var delivery = new PushDelivery
        {
            Message = message, MessageId = message.Id, CreatedAtUtc = atUtc, OnCallEmail = snapshot
        };
        if (snapshot.ValidationError != null)
        {
            delivery.Status = PushDelivery.Failed;
            delivery.Error = snapshot.ValidationError;
            delivery.CompletedAtUtc = DateTime.UtcNow;
        }
        db.PushDeliveries.Add(delivery);
    }
}

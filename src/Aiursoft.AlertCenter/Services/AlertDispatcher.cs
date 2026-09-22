using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public sealed record NotificationDelivery(Guid MessageId, string Title, string? Content,
    string Level, DateTime OccurredAtUtc, string? ContextJson);

public sealed record NotificationDeliveryResult(bool Succeeded, string? ProviderMessageId = null,
    string? ErrorCode = null, string? ErrorMessage = null, bool Retryable = false);

public interface INotificationChannelAdapter
{
    ChannelTypeDefinition Definition { get; }
    Task<NotificationDeliveryResult> DeliverAsync(PushDelivery delivery, CancellationToken cancellationToken = default);
}

public sealed record ChannelTypeDefinition(string Key, string Name, string Description, string Icon, bool RequiresEndpoint);

public interface IAlertDispatcher
{
    Task DispatchAsync(ReportedMessage message, CancellationToken cancellationToken = default);
}

public class QueuedAlertDispatcher(TemplateDbContext db, IIncidentMatcher incidentMatcher,
    IOnCallScheduleService onCallScheduleService) : IAlertDispatcher
{
    public async Task DispatchAsync(ReportedMessage message, CancellationToken cancellationToken = default)
    {
        var incident = await incidentMatcher.FindOrCreateActiveIncidentAsync(message, cancellationToken);
        message.IncidentId = incident.Id;
        if (incident.Status == Incident.Acknowledged)
        {
            message.IsArchived = true;
            return;
        }

        var application = await db.SourceApplications.AsNoTracking().SingleAsync(item => item.Id == message.ApplicationId,
            cancellationToken);
        if (!application.OnCallGroupId.HasValue && await db.OnCallGroups.AnyAsync(cancellationToken))
        {
            AddRoutingFailure(message, AlertRoutingFailure.MissingOnCallGroup, "应用尚未绑定值班组，无法确定当班工程师。");
            return;
        }
        if (application.OnCallGroupId.HasValue)
        {
            var oce = await onCallScheduleService.GetOnCallEngineerAsync(application.OnCallGroupId.Value, DateTime.UtcNow,
                cancellationToken);
            if (oce == null)
            {
                AddRoutingFailure(message, AlertRoutingFailure.MissingOce, "应用的值班组在当前时刻没有有效 OCE，告警未生成通知。");
                return;
            }

            if (incident.OceUserId == null)
            {
                incident.OnCallGroupId = oce.GroupId;
                incident.OnCallShiftId = oce.ShiftId;
                incident.OceUserId = oce.UserId;
                incident.OceDisplayName = oce.DisplayName;
                incident.OceEmail = oce.Email;
                incident.OceNotifiedAtUtc = DateTime.UtcNow;
            }
            var channel = await GetOceInAppChannelAsync(cancellationToken);
            var delivery = new PushDelivery
            {
                Channel = channel, MessageId = message.Id, Message = message
            };
            db.PushDeliveries.Add(delivery);
            db.PushDeliveryRecipients.Add(new PushDeliveryRecipient
            {
                Delivery = delivery, UserId = oce.UserId, DisplayName = oce.DisplayName, Email = oce.Email
            });
            return;
        }

        var channels = await db.ApplicationPushChannels.AsNoTracking()
            .Where(link => link.ApplicationId == message.ApplicationId && link.Channel.IsEnabled &&
                ((message.Level == "sev0" && link.ReceiveSev0) ||
                 (message.Level == "sev1" && link.ReceiveSev1) ||
                 (message.Level == "sev2" && link.ReceiveSev2) ||
                 (message.Level == "sev3" && link.ReceiveSev3) ||
                 (message.Level == "sev4" && link.ReceiveSev4)))
            .Select(link => new { link.ChannelId, link.Channel.Type }).ToListAsync(cancellationToken);
        foreach (var channel in channels)
        {
            var delivery = new PushDelivery
            {
                ChannelId = channel.ChannelId, MessageId = message.Id, Message = message
            };
            db.PushDeliveries.Add(delivery);
            if (channel.Type == "inapp") await InAppRecipientSnapshot.CreateAsync(db, delivery, cancellationToken);
        }
    }

    private async Task<PushChannel> GetOceInAppChannelAsync(CancellationToken cancellationToken)
    {
        var channel = await db.PushChannels.SingleOrDefaultAsync(item => item.SystemKey == "oce-inapp", cancellationToken);
        if (channel != null) return channel;
        channel = new PushChannel
        {
            Name = "当班工程师站内通知", Type = "oce-inapp", SystemKey = "oce-inapp", IsEnabled = true
        };
        db.PushChannels.Add(channel);
        return channel;
    }

    private void AddRoutingFailure(ReportedMessage message, string reasonCode, string reason) =>
        db.AlertRoutingFailures.Add(new AlertRoutingFailure
        {
            ApplicationId = message.ApplicationId, Message = message, ReasonCode = reasonCode, Reason = reason
        });
}

public class NullAlertDispatcher(ILogger<NullAlertDispatcher> logger) : IAlertDispatcher
{
    public Task DispatchAsync(ReportedMessage message, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Message {Id} persisted. External delivery is disabled in the demo.", message.Id);
        return Task.CompletedTask;
    }
}

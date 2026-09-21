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

public class QueuedAlertDispatcher(TemplateDbContext db, IIncidentMatcher incidentMatcher) : IAlertDispatcher
{
    public async Task DispatchAsync(ReportedMessage message, CancellationToken cancellationToken = default)
    {
        var incident = await incidentMatcher.FindActiveIncidentAsync(message, cancellationToken);
        if (incident != null)
        {
            message.IncidentId = incident.Id;
            if (incident.Status == Incident.Acknowledged)
            {
                message.IsArchived = true;
                return;
            }
        }
        var channels = await db.ApplicationPushChannels.AsNoTracking()
            .Where(link => link.ApplicationId == message.ApplicationId && link.Channel.IsEnabled &&
                ((message.Level == "sev0" && link.ReceiveSev0) ||
                 (message.Level == "sev1" && link.ReceiveSev1) ||
                 (message.Level == "sev2" && link.ReceiveSev2) ||
                 (message.Level == "sev3" && link.ReceiveSev3) ||
                 (message.Level == "sev4" && link.ReceiveSev4)))
            .Select(link => new { link.ChannelId, link.Channel.Type }).ToListAsync(cancellationToken);
        // 由调用方与告警一起保存，确保消息、待投递记录和站内通知收件人快照同时提交。
        foreach (var channel in channels)
        {
            var delivery = new PushDelivery
            {
                ChannelId = channel.ChannelId, MessageId = message.Id, Message = message
            };
            db.PushDeliveries.Add(delivery);
            if (channel.Type != "inapp") continue;
            await InAppRecipientSnapshot.CreateAsync(db, delivery, cancellationToken);
        }
    }
}

public class NullAlertDispatcher(ILogger<NullAlertDispatcher> logger) : IAlertDispatcher
{
    public Task DispatchAsync(ReportedMessage message, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Message {Id} persisted. External delivery is disabled in the demo.", message.Id);
        return Task.CompletedTask;
    }
}

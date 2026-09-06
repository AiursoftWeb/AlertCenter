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

public class QueuedAlertDispatcher(TemplateDbContext db) : IAlertDispatcher
{
    public async Task DispatchAsync(ReportedMessage message, CancellationToken cancellationToken = default)
    {
        var channelIds = await db.ApplicationPushChannels.AsNoTracking()
            .Where(link => link.ApplicationId == message.ApplicationId && link.Channel.IsEnabled)
            .Select(link => link.ChannelId).ToListAsync(cancellationToken);
        // 由调用方与告警一起保存，确保消息和待投递记录同时提交。
        db.PushDeliveries.AddRange(channelIds.Select(id => new PushDelivery
        {
            ChannelId = id, MessageId = message.Id, Message = message
        }));
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

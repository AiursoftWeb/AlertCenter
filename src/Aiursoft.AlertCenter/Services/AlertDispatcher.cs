using Aiursoft.AlertCenter.Entities;

namespace Aiursoft.AlertCenter.Services;

public sealed record NotificationDelivery(Guid MessageId, string Title, string? Content,
    string Level, DateTime OccurredAtUtc, string? ContextJson);

public sealed record NotificationDeliveryResult(bool Succeeded, string? ProviderMessageId = null,
    string? ErrorCode = null, string? ErrorMessage = null, bool Retryable = false);

public interface INotificationChannelAdapter
{
    string ChannelKey { get; }
    Task<NotificationDeliveryResult> DeliverAsync(NotificationDelivery delivery, CancellationToken cancellationToken = default);
}

public interface IAlertDispatcher
{
    Task DispatchAsync(ReportedMessage message, CancellationToken cancellationToken = default);
}

public class NullAlertDispatcher(ILogger<NullAlertDispatcher> logger) : IAlertDispatcher
{
    public Task DispatchAsync(ReportedMessage message, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Message {Id} persisted. External delivery is disabled in the demo.", message.Id);
        return Task.CompletedTask;
    }
}

using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public class PushDeliveryProcessor(TemplateDbContext db, IEnumerable<INotificationChannelAdapter> adapters, ILogger<PushDeliveryProcessor> logger, IStringLocalizer<PushDeliveryProcessor> localizer)
{
    public async Task ProcessAsync(Guid id, CancellationToken cancellationToken)
    {
        // 条件更新抢占记录，使后台投递和手动测试不会同时发送同一条消息。
        var claimed = await db.PushDeliveries.Where(delivery => delivery.Id == id && delivery.Status == PushDelivery.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(delivery => delivery.Status, PushDelivery.Sending)
                .SetProperty(delivery => delivery.AttemptedAtUtc, DateTime.UtcNow)
                .SetProperty(delivery => delivery.Attempts, delivery => delivery.Attempts + 1), cancellationToken);
        if (claimed == 0) return;
        var delivery = await db.PushDeliveries.Include(item => item.Channel).Include(item => item.Message)
            .Include(item => item.IncidentEvent).ThenInclude(entry => entry!.Incident)
            .Include(item => item.OnCallEmail).ThenInclude(snapshot => snapshot!.Recipients)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (delivery == null) return;
        await db.Entry(delivery).ReloadAsync(cancellationToken);

        NotificationDeliveryResult result;
        var adapter = adapters.SingleOrDefault(item => item.Definition.Key ==
            (delivery.OnCallEmail != null ? "email" : delivery.Channel?.Type));
        if (delivery.OnCallEmail == null && delivery.Channel?.IsEnabled != true)
            result = new(false, ErrorMessage: localizer["Channel is disabled. Enable it to retry manually."].Value);
        else if (adapter == null)
            result = new(false, ErrorMessage: localizer["This channel type is currently unavailable."].Value);
        else
        {
            try
            {
                result = await adapter.DeliverAsync(delivery, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError("Delivery {Id} failed with {ExceptionType}.", id, exception.GetType().Name);
                // 适配器可能已经暂存了部分通知；失败时不能保存这些未完成的副作用。
                db.ChangeTracker.Clear();
                db.PushDeliveries.Attach(delivery);
                result = new(false, ErrorMessage: localizer["Push execution failed. Please check the channel configuration and try again."].Value);
            }
        }
        delivery.Status = result.Succeeded ? PushDelivery.Succeeded : PushDelivery.Failed;
        delivery.Error = result.ErrorMessage is { Length: > 2000 } error ? error[..2000] : result.ErrorMessage;
        delivery.CompletedAtUtc = DateTime.UtcNow;
        // 站内通知和成功状态在同一次 SaveChanges 事务中提交。
        await db.SaveChangesAsync(cancellationToken);
    }
}

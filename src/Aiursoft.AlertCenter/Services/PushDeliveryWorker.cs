using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public class PushDeliveryWorker(IServiceScopeFactory scopes, ILogger<PushDeliveryWorker> logger) : BackgroundService
{
    private const int BatchSize = 20;

    internal async Task ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        // 中断时接收端是否已收到无法判断，不自动补发，转失败供管理员核实。
        var expired = DateTime.UtcNow.AddMinutes(-2);
        var expiredSending = await db.PushDeliveries
            .Where(delivery => delivery.Status == PushDelivery.Sending && delivery.AttemptedAtUtc < expired)
            .ToListAsync(cancellationToken);
        if (expiredSending.Count > 0)
        {
            foreach (var delivery in expiredSending)
            {
                delivery.Status = PushDelivery.Failed;
                delivery.Error = "投递中断，结果未知。请核实接收端后手动重试。";
                delivery.CompletedAtUtc = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        var ids = await db.PushDeliveries.AsNoTracking().Where(delivery => delivery.Status == PushDelivery.Pending)
            .OrderBy(delivery => delivery.CreatedAtUtc).Take(BatchSize).Select(delivery => delivery.Id).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            await using var deliveryScope = scopes.CreateAsyncScope();
            await deliveryScope.ServiceProvider.GetRequiredService<PushDeliveryProcessor>().ProcessAsync(id, cancellationToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError("Push worker iteration failed with {ExceptionType}.", exception.GetType().Name);
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

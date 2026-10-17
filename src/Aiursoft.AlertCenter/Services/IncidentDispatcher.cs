using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public sealed record NotificationDeliveryResult(bool Succeeded, string? ErrorMessage = null);

public interface INotificationChannelAdapter
{
    ChannelTypeDefinition Definition { get; }
    Task<NotificationDeliveryResult> DeliverAsync(PushDelivery delivery, CancellationToken cancellationToken = default);
}

public sealed record ChannelTypeDefinition(string Key, string Name, string Description, string Icon, bool RequiresEndpoint);

public interface IIncidentDispatcher
{
    Task DispatchAsync(Incident incident, CancellationToken cancellationToken = default);
}

public class QueuedIncidentDispatcher(TemplateDbContext db, IStringLocalizerFactory localizerFactory) : IIncidentDispatcher
{
    public async Task DispatchAsync(Incident incident, CancellationToken cancellationToken = default)
    {
        await OnCallEmailQueue.CreateAsync(db, incident, cancellationToken, localizer: localizerFactory.Create(typeof(OnCallEmailQueue)), localizerFactory: localizerFactory);
        var channels = await db.ApplicationPushChannels.AsNoTracking()
            .Where(link => link.ApplicationId == incident.ApplicationId && link.Channel.IsEnabled &&
                ((incident.Level == "sev0" && link.ReceiveSev0) ||
                 (incident.Level == "sev1" && link.ReceiveSev1) ||
                 (incident.Level == "sev2" && link.ReceiveSev2) ||
                 (incident.Level == "sev3" && link.ReceiveSev3) ||
                 (incident.Level == "sev4" && link.ReceiveSev4)))
            .Select(link => new { link.ChannelId, link.Channel.Type }).ToListAsync(cancellationToken);
        // Commit the incident, pending deliveries and recipient snapshots together.
        foreach (var channel in channels)
        {
            var delivery = new PushDelivery
            {
                ChannelId = channel.ChannelId, IncidentId = incident.Id, Incident = incident
            };
            db.PushDeliveries.Add(delivery);
            if (channel.Type != "inapp") continue;
            await InAppRecipientSnapshot.CreateAsync(db, delivery, cancellationToken);
        }
    }
}

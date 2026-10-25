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
        if (incident.Level == "sev4") return;
        var channels = await MatchingChannels(db, incident)
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

    public static async Task DispatchEmailEventAsync(TemplateDbContext db, IncidentTimelineEntry entry,
        CancellationToken cancellationToken, IStringLocalizerFactory? localizerFactory = null)
    {
        if (entry.Incident.Level == "sev4") return;
        if (entry.Type is not (IncidentTimelineEntry.Mitigated or IncidentTimelineEntry.Resolved or IncidentTimelineEntry.Reopened) && !entry.IsEscalation) return;
        // Each committed transition queues its own email. Version checks prevent stale retries
        // from committing another transition; reopening starts another notification cycle.
        await OnCallEmailQueue.CreateEventAsync(db, entry, cancellationToken,
            localizer: localizerFactory?.Create(typeof(OnCallEmailQueue)), localizerFactory: localizerFactory);
        var incident = entry.Incident;
        var channels = await MatchingChannels(db, incident).Where(link => link.Channel.Type == "email")
            .Select(link => link.ChannelId).ToListAsync(cancellationToken);
        foreach (var channelId in channels)
            db.PushDeliveries.Add(new PushDelivery
            {
                ChannelId = channelId, IncidentEvent = entry, IncidentEventId = entry.Id,
                CreatedAtUtc = entry.CreatedAtUtc
            });
    }

    private static IQueryable<ApplicationPushChannel> MatchingChannels(TemplateDbContext db, Incident incident) =>
        db.ApplicationPushChannels.AsNoTracking()
            .Where(link => link.ApplicationId == incident.ApplicationId && link.Channel.IsEnabled &&
                ((incident.Level == "sev0" && link.ReceiveSev0) ||
                 (incident.Level == "sev1" && link.ReceiveSev1) ||
                 (incident.Level == "sev2" && link.ReceiveSev2) ||
                 (incident.Level == "sev3" && link.ReceiveSev3) ||
                 (incident.Level == "sev4" && link.ReceiveSev4)));

}

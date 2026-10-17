using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public static class OnCallEmailQueue
{
    public static async Task CreateAsync(TemplateDbContext db, Incident incident, CancellationToken cancellationToken, IStringLocalizer? localizer = null, IStringLocalizerFactory? localizerFactory = null)
    {
        await CreateAsync(db, incident.ApplicationId, new PushDelivery { Incident = incident, IncidentId = incident.Id },
            DateTime.SpecifyKind(incident.ReceivedAtUtc, DateTimeKind.Utc), cancellationToken, localizer: localizer, localizerFactory: localizerFactory);
    }

    public static Task CreateReopenAsync(TemplateDbContext db, IncidentTimelineEntry entry, CancellationToken cancellationToken, IStringLocalizer? localizer = null, IStringLocalizerFactory? localizerFactory = null) =>
        CreateAsync(db, entry.Incident.ApplicationId, new PushDelivery { IncidentEvent = entry, IncidentEventId = entry.Id },
            entry.CreatedAtUtc, cancellationToken, localizer: localizer, localizerFactory: localizerFactory);

    private static async Task CreateAsync(TemplateDbContext db, Guid applicationId, PushDelivery delivery, DateTime atUtc, CancellationToken cancellationToken, IStringLocalizer? localizer = null, IStringLocalizerFactory? localizerFactory = null)
    {
        var group = await db.SourceApplications.AsNoTracking().Where(application => application.Id == applicationId)
            .Select(application => application.OnCallGroup).SingleOrDefaultAsync(cancellationToken);
        if (group == null) return;
        var oce = await new OnCallOceService(db, localizer: localizerFactory == null ? null : new StringLocalizer<OnCallOceService>(localizerFactory)).QueryAsync(group.Id, atUtc, cancellationToken);
        // Keep recipient history independent of mutable users and shifts, including retries.
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
        if (!EmailChannelAdapter.TryParseCcRecipients(group.EmailCcRecipients, out var cc, out _, localizer: localizerFactory?.Create(typeof(EmailChannelAdapter))))
        {
            snapshot.ValidationError ??= (localizer is null ? "On-call group CC email configuration is invalid." : localizer["On-call group CC email configuration is invalid."].Value);
            cc.Clear();
        }
        var to = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var engineer in snapshot.Recipients)
        {
            if (!EmailChannelAdapter.TryParseRecipient(engineer.Email, out var email, out _, localizer: localizerFactory?.Create(typeof(EmailChannelAdapter))))
            {
                engineer.Email = null;
                snapshot.ValidationError ??= (localizer is null ? "Current OCE lacks a valid system registered email; on-call email not sent." : localizer["Current OCE lacks a valid system registered email; on-call email not sent."].Value);
            }
            else
            {
                engineer.Email = email;
                to.Add(email);
            }
        }
        snapshot.CcRecipients = string.Join('\n', cc.Where(address => !to.Contains(address)));
        delivery.CreatedAtUtc = atUtc;
        delivery.OnCallEmail = snapshot;
        if (snapshot.ValidationError != null)
        {
            delivery.Status = PushDelivery.Failed;
            delivery.Error = snapshot.ValidationError;
            delivery.CompletedAtUtc = DateTime.UtcNow;
        }
        db.PushDeliveries.Add(delivery);
    }
}

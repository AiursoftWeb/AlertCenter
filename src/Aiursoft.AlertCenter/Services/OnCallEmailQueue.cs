using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public static class OnCallEmailQueue
{
    public static async Task CreateAsync(TemplateDbContext db, Incident incident, CancellationToken cancellationToken, IStringLocalizer? localizer = null, IStringLocalizerFactory? localizerFactory = null)
    {
        await CreateAsync(db, incident.ApplicationId, new PushDelivery { Incident = incident, IncidentId = incident.Id },
            DateTime.SpecifyKind(incident.OccurredAtUtc, DateTimeKind.Utc), cancellationToken, localizer: localizer, localizerFactory: localizerFactory);
    }

    public static Task<OnCallEmailSnapshot?> GetOriginalSnapshotAsync(TemplateDbContext db, Guid incidentId,
        CancellationToken cancellationToken) =>
        db.OnCallEmailSnapshots.AsNoTracking().Include(snapshot => snapshot.Recipients)
            .Where(snapshot => snapshot.Delivery.IncidentId == incidentId)
            .OrderBy(snapshot => snapshot.Delivery.CreatedAtUtc).ThenBy(snapshot => snapshot.DeliveryId)
            .FirstOrDefaultAsync(cancellationToken);

    public static async Task CreateEventAsync(TemplateDbContext db, IncidentTimelineEntry entry, CancellationToken cancellationToken,
        IStringLocalizer? localizer = null, IStringLocalizerFactory? localizerFactory = null)
    {
        localizer ??= localizerFactory?.Create(typeof(OnCallEmailQueue));
        var original = await GetOriginalSnapshotAsync(db, entry.Incident.Id, cancellationToken);
        var delivery = new PushDelivery
        {
            IncidentEvent = entry, IncidentEventId = entry.Id, CreatedAtUtc = entry.CreatedAtUtc
        };
        if (original == null)
        {
            // Historical incidents without a captured roster must never notify today's OCE instead.
            var group = await db.SourceApplications.AsNoTracking().Where(application => application.Id == entry.Incident.ApplicationId)
                .Select(application => application.OnCallGroup).SingleOrDefaultAsync(cancellationToken);
            if (group == null) return;
            delivery.OnCallEmail = new OnCallEmailSnapshot
            {
                GroupId = group.Id, GroupName = group.Name,
                NotificationAtUtc = DateTime.SpecifyKind(entry.Incident.OccurredAtUtc, DateTimeKind.Utc),
                ValidationError = localizer is null ? "The original incident OCE snapshot is unavailable; email was not sent to the current OCE."
                    : localizer["The original incident OCE snapshot is unavailable; email was not sent to the current OCE."].Value
            };
        }
        else
        {
            delivery.OnCallEmail = new OnCallEmailSnapshot
            {
                GroupId = original.GroupId, GroupName = original.GroupName, ShiftId = original.ShiftId,
                StartsAtUtc = original.StartsAtUtc, EndsAtUtc = original.EndsAtUtc,
                NotificationAtUtc = original.NotificationAtUtc, CcRecipients = original.CcRecipients,
                ValidationError = original.ValidationError,
                Recipients = original.Recipients.Select(person => new OnCallEmailRecipient
                {
                    UserId = person.UserId, DisplayName = person.DisplayName, Email = person.Email
                }).ToList()
            };
        }
        if (original != null)
        {
            // CC is team configuration, independent of the incident's original shift.
            // Capture the current configuration for this delivery; retries retain it.
            var group = await db.OnCallGroups.AsNoTracking().Where(item => item.Id == original.GroupId)
                .Select(item => new { item.EmailCcRecipients }).SingleOrDefaultAsync(cancellationToken);
            // A corrected CC configuration can recover later notifications. Invalid or missing
            // original OCE addresses remain frozen and cannot be replaced by today's roster.
            if (group != null && original.Recipients.Count > 0 && original.Recipients.All(person =>
                    EmailChannelAdapter.TryParseRecipient(person.Email, out _, out _)))
                delivery.OnCallEmail!.ValidationError = null;
            var ccSource = group == null ? original.CcRecipients : group.EmailCcRecipients;
            if (!EmailChannelAdapter.TryParseCcRecipients(ccSource, out var cc, out _,
                    localizer: localizerFactory?.Create(typeof(EmailChannelAdapter))))
            {
                delivery.OnCallEmail!.ValidationError ??= localizer is null ? "On-call group CC email configuration is invalid."
                    : localizer["On-call group CC email configuration is invalid."].Value;
            }
            var to = original.Recipients.Select(person => person.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
            delivery.OnCallEmail!.CcRecipients = string.Join('\n', cc.Where(address => !to.Contains(address)));
        }
        await AddDeliveryAsync(db, delivery, cancellationToken);
    }

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
        delivery.CreatedAtUtc = delivery.Incident?.ReceivedAtUtc ?? atUtc;
        delivery.OnCallEmail = snapshot;
        await AddDeliveryAsync(db, delivery, cancellationToken);
    }

    private static async Task AddDeliveryAsync(TemplateDbContext db, PushDelivery delivery, CancellationToken cancellationToken)
    {
        var policy = await new SeverityPolicyStore(db).LoadAsync(cancellationToken);
        if (!policy.Allows(delivery))
        {
            // Keep the original OCE snapshot for a possible escalation, without sending mail.
            delivery.Status = PushDelivery.Suppressed;
            delivery.CompletedAtUtc = DateTime.UtcNow;
        }
        else if (delivery.OnCallEmail?.ValidationError is { } error)
        {
            delivery.Status = PushDelivery.Failed;
            delivery.Error = error;
            delivery.CompletedAtUtc = DateTime.UtcNow;
        }
        db.PushDeliveries.Add(delivery);
    }
}

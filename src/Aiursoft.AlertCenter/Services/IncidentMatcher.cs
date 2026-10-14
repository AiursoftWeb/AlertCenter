using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public interface IIncidentMatcher
{
    Task<Incident?> FindActiveIncidentAsync(ReportedMessage message, CancellationToken cancellationToken = default);
    Task<Incident> FindOrCreateActiveIncidentAsync(ReportedMessage message, CancellationToken cancellationToken = default);
}

public sealed class IncidentMatcher(TemplateDbContext db) : IIncidentMatcher
{
    public async Task<Incident?> FindActiveIncidentAsync(ReportedMessage message, CancellationToken cancellationToken = default)
    {
        var active = db.Incidents.Where(incident => incident.ApplicationId == message.ApplicationId &&
            incident.Status != Incident.Resolved);
        var match = await active.OrderByDescending(incident => incident.TriggeredAtUtc).FirstOrDefaultAsync(incident =>
            incident.ActiveMatchKey == GetActiveMatchKey(message), cancellationToken);
        if (match != null) return match;
        if (!string.IsNullOrWhiteSpace(message.IncidentKey))
            return await active.OrderByDescending(incident => incident.TriggeredAtUtc).FirstOrDefaultAsync(incident =>
                incident.ActiveMatchKey == null && incident.IncidentKey == message.IncidentKey, cancellationToken);
        return await active.OrderByDescending(incident => incident.TriggeredAtUtc).FirstOrDefaultAsync(incident =>
            incident.ActiveMatchKey == null && incident.IncidentKey == null && incident.AlertTitle == message.Title &&
            incident.MatchLevel == message.Level, cancellationToken);
    }

    public async Task<Incident> FindOrCreateActiveIncidentAsync(ReportedMessage message,
        CancellationToken cancellationToken = default)
    {
        var incident = await FindActiveIncidentAsync(message, cancellationToken);
        if (incident != null) return incident;

        incident = new Incident
        {
            ApplicationId = message.ApplicationId,
            Title = message.Title,
            IncidentKey = message.IncidentKey,
            ActiveMatchKey = GetActiveMatchKey(message),
            AlertTitle = message.Title,
            AlertLevel = message.Level,
            MatchLevel = message.Level
        };
        db.Incidents.Add(incident);
        db.IncidentTimelineEntries.Add(new IncidentTimelineEntry
        {
            Incident = incident,
            Type = IncidentTimelineEntry.Created,
            NewStatus = Incident.New,
            NewLevel = message.Level
        });
        return incident;
    }

    public static string GetActiveMatchKey(ReportedMessage message) => string.IsNullOrWhiteSpace(message.IncidentKey)
        ? $"alert:{message.Title}\u001f{message.Level}"
        : $"key:{message.IncidentKey}";
}

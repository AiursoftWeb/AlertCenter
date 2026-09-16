using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public interface IIncidentMatcher
{
    Task<Incident?> FindActiveIncidentAsync(ReportedMessage message, CancellationToken cancellationToken = default);
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
            incident.AlertLevel == message.Level, cancellationToken);
    }

    public static string GetActiveMatchKey(ReportedMessage message) => string.IsNullOrWhiteSpace(message.IncidentKey)
        ? $"alert:{message.Title}\u001f{message.Level}"
        : $"key:{message.IncidentKey}";
}

using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public interface IIncidentMatcher
{
    Task<Incident?> FindActiveIncidentAsync(ReportedMessage message, CancellationToken cancellationToken = default);
}

public sealed class IncidentMatcher(TemplateDbContext db) : IIncidentMatcher
{
    public Task<Incident?> FindActiveIncidentAsync(ReportedMessage message, CancellationToken cancellationToken = default)
    {
        var active = db.Incidents.Where(incident => incident.ApplicationId == message.ApplicationId &&
            incident.Status != Incident.Resolved);
        if (!string.IsNullOrWhiteSpace(message.IncidentKey))
            return active.OrderByDescending(incident => incident.TriggeredAtUtc)
                .FirstOrDefaultAsync(incident => incident.IncidentKey == message.IncidentKey, cancellationToken);
        return active.OrderByDescending(incident => incident.TriggeredAtUtc).FirstOrDefaultAsync(incident =>
            incident.IncidentKey == null && incident.AlertTitle == message.Title && incident.AlertLevel == message.Level,
            cancellationToken);
    }
}

using System.Linq.Expressions;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

// One policy snapshot per operation; consumers do not interpret severity codes.
public sealed class IncidentSeverityPolicy(IEnumerable<SeverityRule> rules)
{
    private readonly Dictionary<string, SeverityRule> _rules = rules.ToDictionary(rule => rule.Level);

    public bool Allows(string? level, string? channelType) =>
        level != null && _rules.TryGetValue(level, out var rule) && rule.Allows(channelType);

    public bool Allows(PushDelivery delivery)
    {
        // Explicit channel tests have no incident severity and remain available to channel managers.
        if (delivery.IncidentId == null && delivery.IncidentEventId == null && delivery.Incident == null && delivery.IncidentEvent == null) return true;
        var channelType = delivery.OnCallEmail != null ? "email" : delivery.Channel?.Type;
        var currentLevel = delivery.Incident?.Level ?? delivery.IncidentEvent?.Incident.Level;
        var eventLevel = delivery.IncidentEvent?.NewLevel ?? currentLevel;
        return Allows(currentLevel, channelType) && Allows(eventLevel, channelType);
    }

    public Expression<Func<Incident, bool>> VisibleIncidents
    {
        get
        {
            var levels = _rules.Values.Where(rule => rule.ShowInLists).Select(rule => rule.Level).ToArray();
            return incident => levels.Contains(incident.Level);
        }
    }
}

public sealed class SeverityPolicyStore(TemplateDbContext db)
{
    public async Task<IncidentSeverityPolicy> LoadAsync(CancellationToken cancellationToken = default) =>
        new(await db.SeverityRules.AsNoTracking().ToListAsync(cancellationToken));
}

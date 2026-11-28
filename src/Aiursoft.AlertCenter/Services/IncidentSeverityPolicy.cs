using System.Linq.Expressions;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public sealed class SmsEventPolicy
{
    public const string NewIncidentKey = "SmsEventPolicy_NewIncident";
    public const string EscalatedKey = "SmsEventPolicy_Escalated";
    public const string MitigatedKey = "SmsEventPolicy_Mitigated";
    public const string ResolvedKey = "SmsEventPolicy_Resolved";
    public const string ReopenedKey = "SmsEventPolicy_Reopened";

    public static readonly string[] AllKeys =
    [
        NewIncidentKey, EscalatedKey, MitigatedKey, ResolvedKey, ReopenedKey
    ];

    public bool NewIncident { get; set; } = true;
    public bool Escalated { get; set; } = true;
    public bool Mitigated { get; set; } = true;
    public bool Resolved { get; set; } = true;
    public bool Reopened { get; set; } = true;

    public bool Allows(string? incidentEventType) => incidentEventType switch
    {
        null => NewIncident,
        IncidentTimelineEntry.LevelChanged => Escalated,
        IncidentTimelineEntry.Mitigated => Mitigated,
        IncidentTimelineEntry.Resolved => Resolved,
        IncidentTimelineEntry.Reopened => Reopened,
        _ => false
    };

    public Dictionary<string, string> ToSettingsMap() => new()
    {
        [NewIncidentKey] = NewIncident.ToString(),
        [EscalatedKey] = Escalated.ToString(),
        [MitigatedKey] = Mitigated.ToString(),
        [ResolvedKey] = Resolved.ToString(),
        [ReopenedKey] = Reopened.ToString()
    };

    public static SmsEventPolicy FromSettingsMap(IReadOnlyDictionary<string, string?> values)
    {
        bool Read(string key) => !values.TryGetValue(key, out var raw) || !bool.TryParse(raw, out var parsed) || parsed;
        return new SmsEventPolicy
        {
            NewIncident = Read(NewIncidentKey),
            Escalated = Read(EscalatedKey),
            Mitigated = Read(MitigatedKey),
            Resolved = Read(ResolvedKey),
            Reopened = Read(ReopenedKey)
        };
    }
}

// One policy snapshot per operation; consumers do not interpret severity codes.
public sealed class IncidentSeverityPolicy(IEnumerable<SeverityRule> rules, SmsEventPolicy? smsEvents = null)
{
    private readonly Dictionary<string, SeverityRule> _rules = rules.ToDictionary(rule => rule.Level);

    public SmsEventPolicy SmsEvents { get; } = smsEvents ?? new SmsEventPolicy();

    public bool Allows(string? level, string? channelType, string? incidentEventType = null) =>
        level != null &&
        _rules.TryGetValue(level, out var rule) &&
        rule.Allows(channelType) &&
        (channelType != "sms" || SmsEvents.Allows(incidentEventType));

    public bool Allows(PushDelivery delivery)
    {
        // Explicit channel tests have no incident severity and remain available to channel managers.
        if (delivery.IncidentId == null && delivery.IncidentEventId == null && delivery.Incident == null && delivery.IncidentEvent == null) return true;
        var channelType = delivery.OnCallEmail?.ChannelType ?? delivery.Channel?.Type;
        var currentLevel = delivery.Incident?.Level ?? delivery.IncidentEvent?.Incident.Level;
        var eventLevel = delivery.IncidentEvent?.NewLevel ?? currentLevel;
        var eventType = delivery.IncidentEvent?.Type;
        return Allows(currentLevel, channelType, eventType) && Allows(eventLevel, channelType, eventType);
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
    public async Task<IncidentSeverityPolicy> LoadAsync(CancellationToken cancellationToken = default)
    {
        var rules = await db.SeverityRules.AsNoTracking().ToListAsync(cancellationToken);
        var smsEvents = await LoadSmsEventsAsync(cancellationToken);
        return new(rules, smsEvents);
    }

    public async Task<SmsEventPolicy> LoadSmsEventsAsync(CancellationToken cancellationToken = default)
    {
        var entries = await db.GlobalSettings.AsNoTracking()
            .Where(setting => SmsEventPolicy.AllKeys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);
        return SmsEventPolicy.FromSettingsMap(entries);
    }

    public async Task StageSmsEventsAsync(SmsEventPolicy smsEvents, CancellationToken cancellationToken = default)
    {
        var existing = await db.GlobalSettings
            .Where(setting => SmsEventPolicy.AllKeys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, cancellationToken);
        foreach (var (key, value) in smsEvents.ToSettingsMap())
        {
            if (existing.TryGetValue(key, out var row))
            {
                row.Value = value;
            }
            else
            {
                db.GlobalSettings.Add(new GlobalSetting { Key = key, Value = value });
            }
        }
    }
}

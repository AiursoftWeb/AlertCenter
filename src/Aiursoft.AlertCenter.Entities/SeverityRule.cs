using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class SeverityRule
{
    [Key, MaxLength(4)]
    public string Level { get; set; } = string.Empty;
    public bool Email { get; set; }
    public bool Sms { get; set; }
    public bool Webhook { get; set; }
    public bool InApp { get; set; }
    public bool ShowInLists { get; set; }

    public bool Allows(string? channelType) => channelType switch
    {
        "email" => Email,
        "sms" => Sms,
        "webhook" => Webhook,
        "inapp" => InApp,
        _ => false
    };

    public static SeverityRule[] Defaults() =>
    [
        new() { Level = "sev0", Email = true, Sms = true, Webhook = true, InApp = true, ShowInLists = true },
        new() { Level = "sev1", Email = true, Sms = true, Webhook = true, InApp = true, ShowInLists = true },
        new() { Level = "sev2", Email = true, Sms = true, Webhook = true, InApp = true, ShowInLists = true },
        new() { Level = "sev3", Email = true, Webhook = true, InApp = true, ShowInLists = true },
        new() { Level = "sev4", ShowInLists = true }
    ];
}

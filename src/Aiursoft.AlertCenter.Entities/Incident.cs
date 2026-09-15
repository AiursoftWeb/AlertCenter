using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class Incident
{
    public const string Triggered = "triggered";
    public const string Acknowledged = "acknowledged";
    public const string Resolved = "resolved";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    public SourceApplication Application { get; set; } = null!;
    [Required, MaxLength(200)] public string Title { get; set; } = string.Empty;
    [MaxLength(200)] public string? IncidentKey { get; set; }
    [Required, MaxLength(200)] public string AlertTitle { get; set; } = string.Empty;
    [Required, MaxLength(4)] public string AlertLevel { get; set; } = "sev3";
    [Required, MaxLength(20)] public string Status { get; set; } = Triggered;
    public DateTime TriggeredAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? AcknowledgedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public ICollection<ReportedMessage> Messages { get; set; } = [];
    public ICollection<IncidentTimelineEntry> TimelineEntries { get; set; } = [];
}

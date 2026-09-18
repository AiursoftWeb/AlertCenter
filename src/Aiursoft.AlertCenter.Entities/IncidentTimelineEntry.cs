using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class IncidentTimelineEntry
{
    public const string Triggered = "triggered";
    public const string Acknowledged = "acknowledged";
    public const string Updated = "updated";
    public const string Resolved = "resolved";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public Incident Incident { get; set; } = null!;
    [Required, MaxLength(20)] public string Type { get; set; } = Triggered;
    [MaxLength(4000)] public string? Note { get; set; }
    [MaxLength(450)] public string? ActorId { get; set; }
    public User? Actor { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

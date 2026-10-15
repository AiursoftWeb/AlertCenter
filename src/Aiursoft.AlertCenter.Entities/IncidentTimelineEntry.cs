using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Aiursoft.AlertCenter.Entities;

public class IncidentTimelineEntry
{
    public const string Created = "created";
    public const string Acked = "acked";
    public const string OwnershipChanged = "ownership_changed";
    public const string Mitigated = "mitigated";
    public const string Reopened = "reopened";
    public const string LevelChanged = "level_changed";
    public const string Updated = "updated";
    public const string Resolved = "resolved";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public Incident Incident { get; set; } = null!;
    [Required, MaxLength(20)] public string Type { get; set; } = Created;
    [MaxLength(4000)] public string? Note { get; set; }
    [MaxLength(450)] public string? ActorId { get; set; }
    public User? Actor { get; set; }
    [MaxLength(450)] public string? ActorIdSnapshot { get; set; }
    [MaxLength(30)] public string? ActorName { get; set; }
    [NotMapped] public string ActorDisplayName => ActorName ?? Actor?.DisplayName ??
        (Type == Created && ActorId == null && ActorIdSnapshot == null ? "系统" : "已删除用户");
    [MaxLength(20)] public string? PreviousStatus { get; set; }
    [MaxLength(20)] public string? NewStatus { get; set; }
    [MaxLength(450)] public string? PreviousOwnerId { get; set; }
    [MaxLength(450)] public string? NewOwnerId { get; set; }
    [MaxLength(30)] public string? PreviousOwnerName { get; set; }
    [MaxLength(30)] public string? NewOwnerName { get; set; }
    [MaxLength(4)] public string? PreviousLevel { get; set; }
    [MaxLength(4)] public string? NewLevel { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

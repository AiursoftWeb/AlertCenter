using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class OnCallCoverageDebt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public OnCallGroup Group { get; set; } = null!;
    public Guid OriginOverrideId { get; set; }
    public OnCallShiftOverride OriginOverride { get; set; } = null!;
    [Required, MaxLength(450)] public string OwingUserId { get; set; } = string.Empty;
    public User OwingUser { get; set; } = null!;
    [Required, MaxLength(450)] public string CoveringUserId { get; set; } = string.Empty;
    public User CoveringUser { get; set; } = null!;
    public int DurationMinutes { get; set; }
    public Guid? SettlementOverrideId { get; set; }
    public OnCallShiftOverride? SettlementOverride { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SettledAtUtc { get; set; }
}

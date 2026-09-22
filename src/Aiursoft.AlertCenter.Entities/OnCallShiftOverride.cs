using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class OnCallShiftOverride
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public OnCallGroup Group { get; set; } = null!;
    public Guid OriginalShiftId { get; set; }
    public OnCallShift OriginalShift { get; set; } = null!;
    [Required, MaxLength(450)] public string CoveringUserId { get; set; } = string.Empty;
    public User CoveringUser { get; set; } = null!;
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    [Required, MaxLength(1000)] public string Reason { get; set; } = string.Empty;
    [MaxLength(450)] public string? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

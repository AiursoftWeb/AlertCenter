using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class OnCallShiftTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public OnCallGroup Group { get; set; } = null!;
    [Required, MaxLength(100)] public string Name { get; set; } = string.Empty;
    public int StartMinuteOfDay { get; set; }
    public int DurationMinutes { get; set; } = 480;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

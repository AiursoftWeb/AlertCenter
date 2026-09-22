using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class OnCallShift
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public OnCallGroup Group { get; set; } = null!;
    public Guid? TemplateId { get; set; }
    public OnCallShiftTemplate? Template { get; set; }
    [Required, MaxLength(450)] public string UserId { get; set; } = string.Empty;
    public User User { get; set; } = null!;
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public DateTime PublishedAtUtc { get; set; } = DateTime.UtcNow;
}

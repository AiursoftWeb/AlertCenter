using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class OnCallShiftTemplateMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TemplateId { get; set; }
    public OnCallShiftTemplate Template { get; set; } = null!;
    [Required, MaxLength(450)] public string UserId { get; set; } = string.Empty;
    public User User { get; set; } = null!;
    public int RotationOrder { get; set; }
}

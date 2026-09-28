using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class OnCallRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public OnCallGroup Group { get; set; } = null!;
    [Required, MaxLength(100)] public string Name { get; set; } = string.Empty;
    public int ShiftHours { get; set; } = 8;
    public int RestHours { get; set; } = 16;
    public DateTime AnchorUtc { get; set; }
    public ICollection<OnCallRuleMember> Members { get; set; } = [];
}

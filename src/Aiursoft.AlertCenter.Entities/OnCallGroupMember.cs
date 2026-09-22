using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class OnCallGroupMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public OnCallGroup Group { get; set; } = null!;
    [Required, MaxLength(450)] public string UserId { get; set; } = string.Empty;
    public User User { get; set; } = null!;
    public DateTime JoinedAtUtc { get; set; } = DateTime.UtcNow;
}

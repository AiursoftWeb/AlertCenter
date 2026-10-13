namespace Aiursoft.AlertCenter.Entities;

public class OnCallGroupMember
{
    public Guid GroupId { get; set; }
    public OnCallGroup Group { get; set; } = null!;
    public string UserId { get; set; } = string.Empty;
    public User User { get; set; } = null!;
    public int RotationOrder { get; set; }
}

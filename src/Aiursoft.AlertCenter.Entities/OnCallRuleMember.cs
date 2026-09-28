namespace Aiursoft.AlertCenter.Entities;

public class OnCallRuleMember
{
    public Guid RuleId { get; set; }
    public OnCallRule Rule { get; set; } = null!;
    public string UserId { get; set; } = string.Empty;
    public User User { get; set; } = null!;
    public int RotationOrder { get; set; }
}

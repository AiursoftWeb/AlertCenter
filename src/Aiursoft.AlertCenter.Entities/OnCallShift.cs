namespace Aiursoft.AlertCenter.Entities;

public class OnCallShift
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RuleId { get; set; }
    public OnCallRule Rule { get; set; } = null!;
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public ICollection<OnCallShiftMember> Members { get; set; } = [];
}

public class OnCallShiftMember
{
    public Guid ShiftId { get; set; }
    public OnCallShift Shift { get; set; } = null!;
    public string UserId { get; set; } = string.Empty;
    public User User { get; set; } = null!;
}

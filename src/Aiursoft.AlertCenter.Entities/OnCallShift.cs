namespace Aiursoft.AlertCenter.Entities;

public class OnCallShift
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public OnCallGroup Group { get; set; } = null!;
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public int RestHours { get; set; } = 16;
    public ICollection<OnCallShiftMember> Members { get; set; } = [];
}

public class OnCallShiftMember
{
    public Guid ShiftId { get; set; }
    public OnCallShift Shift { get; set; } = null!;
    public string UserId { get; set; } = string.Empty;
    public User User { get; set; } = null!;
}

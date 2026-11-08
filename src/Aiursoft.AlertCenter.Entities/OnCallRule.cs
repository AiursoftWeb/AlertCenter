using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class OnCallRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(100)] public string Name { get; set; } = string.Empty;
    public int ShiftHours { get; set; } = 8;
    public int RestHours { get; set; } = 16;
    public bool BalanceShiftTimes { get; set; }
    public int MinimumStaff { get; set; } = 1;
    public int? TargetStaff { get; set; }
    public int? ShiftMinutes { get; set; }
    public bool SwapAlternateRounds { get; set; }
    public int StaffPerShift { get; set; } = 1;
    public ICollection<OnCallHandoverPoint> HandoverPoints { get; set; } = [];
}

public class OnCallHandoverPoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RuleId { get; set; }
    public OnCallRule Rule { get; set; } = null!;
    public DayOfWeek DayOfWeek { get; set; }
    public int MinuteOfDay { get; set; }
}

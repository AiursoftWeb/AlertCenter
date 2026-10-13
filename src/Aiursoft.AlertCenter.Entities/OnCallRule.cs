using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class OnCallRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(100)] public string Name { get; set; } = string.Empty;
    public int ShiftHours { get; set; } = 8;
    public int RestHours { get; set; } = 16;
    public bool BalanceShiftTimes { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class OnCallGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(100)] public string Name { get; set; } = string.Empty;
    [Required, MaxLength(100)] public string TimeZoneId { get; set; } = "UTC";
    [MaxLength(8000)] public string? EmailCcRecipients { get; set; }
    public Guid? RuleId { get; set; }
    public OnCallRule? Rule { get; set; }
    public DateTime AnchorUtc { get; set; } = DateTime.UtcNow.Date;
    public DayOfWeek WeekStartsOn { get; set; } = DayOfWeek.Monday;
    public Guid ScheduleVersion { get; set; } = Guid.NewGuid();
    public double? MinimumRestHours { get; set; }
    public double? MaximumContinuousHours { get; set; }
    public int? MinimumShiftStaff { get; set; }
    public bool AutoExtend { get; set; }
    public DateTime? HandoverCheckedThroughUtc { get; set; }
    public ICollection<OnCallFallbackRecipient> FallbackRecipients { get; set; } = [];
    public ICollection<OnCallGroupMember> Members { get; set; } = [];
}

public class OnCallFallbackRecipient
{
    public Guid GroupId { get; set; }
    public OnCallGroup Group { get; set; } = null!;
    public string UserId { get; set; } = string.Empty;
    public User User { get; set; } = null!;
}

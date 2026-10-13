using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class OnCallGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(100)] public string Name { get; set; } = string.Empty;
    [Required, MaxLength(100)] public string TimeZoneId { get; set; } = "UTC";
    public Guid? RuleId { get; set; }
    public OnCallRule? Rule { get; set; }
    public DateTime AnchorUtc { get; set; } = DateTime.UtcNow.Date;
    public Guid ScheduleVersion { get; set; } = Guid.NewGuid();
    public ICollection<OnCallGroupMember> Members { get; set; } = [];
}

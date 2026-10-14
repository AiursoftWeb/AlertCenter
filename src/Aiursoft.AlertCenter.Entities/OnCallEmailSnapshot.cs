using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class OnCallEmailSnapshot
{
    public Guid DeliveryId { get; set; }
    public PushDelivery Delivery { get; set; } = null!;
    public Guid GroupId { get; set; }
    [MaxLength(100)] public string GroupName { get; set; } = string.Empty;
    public Guid? ShiftId { get; set; }
    public DateTime? StartsAtUtc { get; set; }
    public DateTime? EndsAtUtc { get; set; }
    public DateTime NotificationAtUtc { get; set; }
    [MaxLength(8000)] public string? CcRecipients { get; set; }
    [MaxLength(2000)] public string? ValidationError { get; set; }
    public ICollection<OnCallEmailRecipient> Recipients { get; set; } = [];
}

public class OnCallEmailRecipient
{
    public Guid DeliveryId { get; set; }
    public OnCallEmailSnapshot Snapshot { get; set; } = null!;
    [MaxLength(450)] public string UserId { get; set; } = string.Empty;
    [MaxLength(30)] public string DisplayName { get; set; } = string.Empty;
    [MaxLength(320)] public string? Email { get; set; }
}

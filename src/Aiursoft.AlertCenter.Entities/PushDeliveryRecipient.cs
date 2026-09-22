using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class PushDeliveryRecipient
{
    public Guid DeliveryId { get; set; }
    public PushDelivery Delivery { get; set; } = null!;
    [MaxLength(255)] public string UserId { get; set; } = string.Empty;
    public User User { get; set; } = null!;
    [MaxLength(100)] public string? DisplayName { get; set; }
    [MaxLength(320)] public string? Email { get; set; }
}

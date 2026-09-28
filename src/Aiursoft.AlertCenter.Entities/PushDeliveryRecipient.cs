namespace Aiursoft.AlertCenter.Entities;

public class PushDeliveryRecipient
{
    public Guid DeliveryId { get; set; }
    public PushDelivery Delivery { get; set; } = null!;
    public string UserId { get; set; } = string.Empty;
    public User User { get; set; } = null!;
}

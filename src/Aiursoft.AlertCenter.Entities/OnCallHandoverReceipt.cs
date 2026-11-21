namespace Aiursoft.AlertCenter.Entities;

public class OnCallHandoverReceipt
{
    public Guid ShiftId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public DateTime ProcessedAtUtc { get; set; }
}

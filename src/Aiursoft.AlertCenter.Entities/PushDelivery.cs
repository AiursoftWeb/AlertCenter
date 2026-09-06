using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class PushDelivery
{
    public const string Pending = "pending";
    public const string Sending = "sending";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChannelId { get; set; }
    public PushChannel Channel { get; set; } = null!;
    public Guid? MessageId { get; set; }
    public ReportedMessage? Message { get; set; }
    [MaxLength(20)] public string Status { get; set; } = Pending;
    public int Attempts { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? AttemptedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    [MaxLength(2000)] public string? Error { get; set; }
}

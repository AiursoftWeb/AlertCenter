using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class PushDelivery
{
    public const string Pending = "pending";
    public const string Sending = "sending";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string DefaultTestContent = "This is a test message for the push channel.";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ChannelId { get; set; }
    public PushChannel? Channel { get; set; }
    public OnCallEmailSnapshot? OnCallEmail { get; set; }
    public Guid? IncidentId { get; set; }
    public Incident? Incident { get; set; }
    public Guid? IncidentEventId { get; set; }
    public IncidentTimelineEntry? IncidentEvent { get; set; }
    [MaxLength(4000)] public string? TestContent { get; set; }
    [MaxLength(20)] public string Status { get; set; } = Pending;
    public int Attempts { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? AttemptedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    [MaxLength(2000)] public string? Error { get; set; }
}

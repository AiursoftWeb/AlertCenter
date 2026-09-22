using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class OnCallHandoverRequest
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Declined = "declined";
    public const string Cancelled = "cancelled";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ShiftId { get; set; }
    public OnCallShift Shift { get; set; } = null!;
    [Required, MaxLength(450)] public string RequestedByUserId { get; set; } = string.Empty;
    public User RequestedByUser { get; set; } = null!;
    [Required, MaxLength(450)] public string ReceivingUserId { get; set; } = string.Empty;
    public User ReceivingUser { get; set; } = null!;
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    [Required, MaxLength(20)] public string Status { get; set; } = Pending;
    [MaxLength(1000)] public string? Note { get; set; }
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RespondedAtUtc { get; set; }
    public Guid? OverrideId { get; set; }
    public OnCallShiftOverride? Override { get; set; }
}

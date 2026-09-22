using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class OnCallLeaveRequest
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public OnCallGroup Group { get; set; } = null!;
    [Required, MaxLength(450)] public string UserId { get; set; } = string.Empty;
    public User User { get; set; } = null!;
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    [Required, MaxLength(1000)] public string Reason { get; set; } = string.Empty;
    [Required, MaxLength(20)] public string Status { get; set; } = Pending;
    [MaxLength(450)] public string? ReviewedByUserId { get; set; }
    public User? ReviewedByUser { get; set; }
    public DateTime SubmittedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAtUtc { get; set; }
}

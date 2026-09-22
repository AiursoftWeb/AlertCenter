using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class OnCallGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(100)] public string Name { get; set; } = string.Empty;
    [Required, MaxLength(100)] public string TimeZoneId { get; set; } = "UTC";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

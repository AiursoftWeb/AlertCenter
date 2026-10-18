using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class SourceApplication
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(100)] public string Name { get; set; } = string.Empty;
    [MaxLength(450)] public string? OwnerId { get; set; }
    public User? Owner { get; set; }
    public bool IsEnabled { get; set; } = true;
    public Guid? OnCallGroupId { get; set; }
    public OnCallGroup? OnCallGroup { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

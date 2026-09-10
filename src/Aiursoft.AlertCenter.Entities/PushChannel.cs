using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class PushChannel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(100)] public string Name { get; set; } = string.Empty;
    [MaxLength(40)] public string Type { get; set; } = string.Empty;
    [MaxLength(2048)] public string? Endpoint { get; set; }
    public string? WebhookHeadersProtected { get; set; }
    public string? WebhookPayloadTemplate { get; set; }
    [MaxLength(8000)] public string? EmailRecipients { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

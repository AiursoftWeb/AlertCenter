using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class ApplicationApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    public SourceApplication Application { get; set; } = null!;
    [Required, MaxLength(100)] public string Name { get; set; } = string.Empty;
    [MaxLength(512)] public string SecretHash { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public bool CanQueryIncidents { get; set; }
    public bool CanCreateIncidents { get; set; }
    public bool CanResolveIncidents { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAtUtc { get; set; }
    public long TotalCalls { get; set; }
}

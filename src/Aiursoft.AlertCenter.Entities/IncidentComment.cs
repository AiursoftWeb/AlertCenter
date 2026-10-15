using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class IncidentComment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public Incident Incident { get; set; } = null!;
    [MaxLength(450)] public string? AuthorId { get; set; }
    public User? Author { get; set; }
    [Required, MaxLength(30)] public string AuthorName { get; set; } = string.Empty;
    [Required, MaxLength(4000)] public string Markdown { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAtUtc { get; set; }
    [MaxLength(450)] public string? DeletedById { get; set; }
    public User? DeletedBy { get; set; }
    [MaxLength(30)] public string? DeletedByName { get; set; }
}

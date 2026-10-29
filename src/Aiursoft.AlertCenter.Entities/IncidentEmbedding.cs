using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class IncidentEmbedding
{
    public Guid IncidentId { get; set; }
    public Incident Incident { get; set; } = null!;
    [MaxLength(64)] public string ModelKey { get; set; } = string.Empty;
    [MaxLength(64)] public string ContentHash { get; set; } = string.Empty;
    public byte[] Vector { get; set; } = [];
    public DateTime GeneratedAtUtc { get; set; }
}

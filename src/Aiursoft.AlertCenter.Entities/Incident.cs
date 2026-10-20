using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Aiursoft.AlertCenter.Entities;

public class Incident
{
    public const string New = "new";
    // Legacy status retained for old filter links and data migration only.
    public const string Acked = "acked";
    public const string Mitigated = "mitigated";
    public const string Resolved = "resolved";

    public Guid Id { get; set; } = Guid.NewGuid();
    // A stable conversation reference; collisions are allowed. Identity and routing use Id.
    [NotMapped]
    public string ShortId => (BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(Id.ToByteArray())) % 1_000_000)
        .ToString("D6", CultureInfo.InvariantCulture);

    public Guid ReportId { get; set; } = Guid.NewGuid();
    [MaxLength(4000)] public string? Content { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public string? ContextJson { get; set; }
    public string? MetadataJson { get; set; }
    public Guid ApplicationId { get; set; }
    public SourceApplication Application { get; set; } = null!;
    [Required, MaxLength(200)] public string Title { get; set; } = string.Empty;
    [Required, MaxLength(4)] public string Level { get; set; } = "sev3";
    [Required, MaxLength(20)] public string Status { get; set; } = New;
    [MaxLength(450)] public string? OwnerId { get; set; }
    public User? Owner { get; set; }
    [MaxLength(30)] public string? OwnerDisplayName { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public DateTime? AcknowledgedAtUtc { get; set; }
    [NotMapped] public bool IsAcknowledged => AcknowledgedAtUtc.HasValue;
    public DateTime? MitigatedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public ICollection<IncidentTimelineEntry> TimelineEntries { get; set; } = [];
}

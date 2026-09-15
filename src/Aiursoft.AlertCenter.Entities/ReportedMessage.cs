using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class ReportedMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    public SourceApplication Application { get; set; } = null!;
    public Guid? IncidentId { get; set; }
    public Incident? Incident { get; set; }
    public bool IsArchived { get; set; }
    public Guid ReportId { get; set; }
    [MaxLength(200)] public string? IncidentKey { get; set; }
    [MaxLength(200)] public string Title { get; set; } = string.Empty;
    [MaxLength(4000)] public string? Content { get; set; }
    [MaxLength(4)] public string Level { get; set; } = "sev3";
    public DateTime OccurredAtUtc { get; set; }
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public string? ContextJson { get; set; }
}

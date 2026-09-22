using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class AlertRoutingFailure
{
    public const string MissingOnCallGroup = "missing-oncall-group";
    public const string MissingOce = "missing-oce";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    public SourceApplication Application { get; set; } = null!;
    public Guid MessageId { get; set; }
    public ReportedMessage Message { get; set; } = null!;
    [Required, MaxLength(50)] public string ReasonCode { get; set; } = string.Empty;
    [Required, MaxLength(1000)] public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

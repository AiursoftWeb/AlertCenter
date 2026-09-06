using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json.Linq;

namespace Aiursoft.AlertCenter.Models;

public class ReportMessageRequest
{
    public Guid ReportId { get; set; }
    [Required, StringLength(200)] public string Title { get; set; } = string.Empty;
    [StringLength(4000)] public string? Content { get; set; }
    [Required, RegularExpression("^sev[1-4]$")] public string Level { get; set; } = "sev3";
    public DateTimeOffset OccurredAtUtc { get; set; }
    public JObject? Context { get; set; }
}

using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json.Linq;

namespace Aiursoft.AlertCenter.SDK.Models;

public sealed class CreateIncidentRequest : IValidatableObject
{
    public Guid ReportId { get; set; }
    [Required, StringLength(200)] public string Title { get; set; } = string.Empty;
    [StringLength(4000)] public string? Content { get; set; }
    [Required, RegularExpression("^sev[0-4]$")] public string Level { get; set; } = "sev3";
    public DateTimeOffset OccurredAtUtc { get; set; }
    public JObject? Context { get; set; }
    [JsonConverter(typeof(IncidentMetadataConverter))]
    public JObject? Metadata { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Metadata != null && IncidentMetadata.Validate(Metadata) is { } error)
            yield return new ValidationResult(error, [nameof(Metadata)]);
        if (ReportId == Guid.Empty)
            yield return new ValidationResult("A non-empty report ID is required.", [nameof(ReportId)]);
        if (OccurredAtUtc == default)
            yield return new ValidationResult("The occurrence time is required.", [nameof(OccurredAtUtc)]);
    }
}

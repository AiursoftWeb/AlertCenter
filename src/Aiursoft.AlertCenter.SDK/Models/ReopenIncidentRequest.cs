using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.SDK.Models;

public sealed class ReopenIncidentRequest : IValidatableObject
{
    public Guid Version { get; set; }
    [Required, StringLength(4000)] public string Reason { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Version == Guid.Empty)
            yield return new ValidationResult("The incident version from a query is required.", [nameof(Version)]);
    }
}

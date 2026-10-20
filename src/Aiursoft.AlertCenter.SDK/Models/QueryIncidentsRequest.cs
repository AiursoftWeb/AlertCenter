using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.SDK.Models;

public sealed class QueryIncidentsRequest : IValidatableObject
{
    [Range(1, 1000000)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 50;
    public bool ActiveOnly { get; set; }
    /// <summary>Include open incidents and only incidents recovered at or after this UTC cutoff.</summary>
    public DateTimeOffset? RecoveredSinceUtc { get; set; }
    [StringLength(128, MinimumLength = 1)] public string? MetadataKey { get; set; }
    [DisplayFormat(ConvertEmptyStringToNull = false), StringLength(4096)] public string? MetadataValue { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if ((MetadataKey == null) != (MetadataValue == null))
            yield return new ValidationResult("MetadataKey and MetadataValue must be supplied together.", [nameof(MetadataKey), nameof(MetadataValue)]);
    }
}

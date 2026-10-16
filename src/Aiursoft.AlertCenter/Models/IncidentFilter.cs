using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Models;

public class IncidentFilter : IValidatableObject
{
    [StringLength(200, ErrorMessage = "Keywords cannot exceed 200 characters.")]
    public string? Keyword { get; set; }
    public Guid? ApplicationId { get; set; }
    [RegularExpression("^(new|acked|mitigated|resolved|all)$", ErrorMessage = "Please select a valid incident status.")]
    public string? Status { get; set; }
    [RegularExpression("^sev[0-4]$", ErrorMessage = "Please select a valid alert level.")]
    public string? Level { get; set; }
    public DateOnly? FromDate { get; set; }
    public DateOnly? ToDate { get; set; }

    public void Normalize()
    {
        Keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim();
        Status ??= "all";
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var localizer = validationContext.GetService(typeof(IStringLocalizer<IncidentFilter>)) as IStringLocalizer<IncidentFilter>;
        if (FromDate.HasValue && ToDate.HasValue && FromDate.Value > ToDate.Value)
            yield return new ValidationResult(localizer is null ? "The start date cannot be later than the end date." : localizer["The start date cannot be later than the end date."].Value, [nameof(FromDate), nameof(ToDate)]);
    }
}

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Models;

public class AlertFilter : IValidatableObject
{
    [StringLength(200, ErrorMessage = "Keywords cannot exceed 200 characters.")]
    public string? Keyword { get; set; }
    public Guid? ApplicationId { get; set; }
    [RegularExpression("^sev[0-4]$", ErrorMessage = "Please select a valid alert level.")]
    public string? Level { get; set; }
    public DateOnly? FromDate { get; set; }
    public DateOnly? ToDate { get; set; }
    public int Page { get; set; } = 1;

    public void Normalize()
    {
        Keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim();
        Page = Math.Clamp(Page, 1, 1000000);
    }

    public Dictionary<string, string?> ToRouteValues(int? page = null)
    {
        var values = new Dictionary<string, string?>();
        if (!string.IsNullOrEmpty(Keyword)) values["keyword"] = Keyword;
        if (ApplicationId.HasValue) values["applicationId"] = ApplicationId.Value.ToString();
        if (!string.IsNullOrEmpty(Level)) values["level"] = Level;
        if (FromDate.HasValue) values["fromDate"] = FromDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (ToDate.HasValue) values["toDate"] = ToDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if ((page ?? Page) > 1) values["page"] = (page ?? Page).ToString(CultureInfo.InvariantCulture);
        return values;
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var localizer = validationContext.GetService(typeof(IStringLocalizer<AlertFilter>)) as IStringLocalizer<AlertFilter>;
        if (FromDate.HasValue && ToDate.HasValue && FromDate.Value > ToDate.Value)
            yield return new ValidationResult(localizer is null ? "The start date cannot be later than the end date." : localizer["The start date cannot be later than the end date."].Value, [nameof(FromDate), nameof(ToDate)]);
    }
}

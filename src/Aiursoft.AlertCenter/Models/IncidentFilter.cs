using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Models;

public class IncidentFilter : IValidatableObject
{
    public int Page { get; set; } = 1;
    [StringLength(200, ErrorMessage = "Keywords cannot exceed 200 characters.")]
    public string? Keyword { get; set; }
    public Guid? ApplicationId { get; set; }
    [RegularExpression("^(new|acked|mitigated|resolved|all)$", ErrorMessage = "Please select a valid incident status.")]
    public string? Status { get; set; }
    [RegularExpression("^sev[0-4]$", ErrorMessage = "Please select a valid incident level.")]
    public string? Level { get; set; }
    public DateOnly? FromDate { get; set; }
    public DateOnly? ToDate { get; set; }

    public void Normalize()
    {
        Keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim();
        Page = Math.Clamp(Page, 1, 1000000);
        Status ??= "all";
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
        if (Status is not (null or "all")) values["status"] = Status;
        return values;
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var localizer = validationContext.GetService(typeof(IStringLocalizer<IncidentFilter>)) as IStringLocalizer<IncidentFilter>;
        if (FromDate.HasValue && ToDate.HasValue && FromDate.Value > ToDate.Value)
            yield return new ValidationResult(localizer is null ? "The start date cannot be later than the end date." : localizer["The start date cannot be later than the end date."].Value, [nameof(FromDate), nameof(ToDate)]);
    }
}

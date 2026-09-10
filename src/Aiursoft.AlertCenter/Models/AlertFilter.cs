using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Aiursoft.AlertCenter.Models;

public class AlertFilter : IValidatableObject
{
    [StringLength(200, ErrorMessage = "关键词不能超过 200 个字符。")]
    public string? Keyword { get; set; }
    public Guid? ApplicationId { get; set; }
    [RegularExpression("^sev[1-4]$", ErrorMessage = "请选择有效的告警级别。")]
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
        if (FromDate.HasValue && ToDate.HasValue && FromDate.Value > ToDate.Value)
            yield return new ValidationResult("开始日期不能晚于结束日期。", [nameof(FromDate), nameof(ToDate)]);
    }
}

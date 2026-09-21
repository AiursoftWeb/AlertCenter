using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Models;

public class IncidentFilter : IValidatableObject
{
    [StringLength(200, ErrorMessage = "关键词不能超过 200 个字符。")]
    public string? Keyword { get; set; }
    public Guid? ApplicationId { get; set; }
    [RegularExpression("^(triggered|acknowledged|resolved|all)$", ErrorMessage = "请选择有效的事故状态。")]
    public string? Status { get; set; }
    [RegularExpression("^sev[0-4]$", ErrorMessage = "请选择有效的告警级别。")]
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
        if (FromDate.HasValue && ToDate.HasValue && FromDate.Value > ToDate.Value)
            yield return new ValidationResult("开始日期不能晚于结束日期。", [nameof(FromDate), nameof(ToDate)]);
    }
}

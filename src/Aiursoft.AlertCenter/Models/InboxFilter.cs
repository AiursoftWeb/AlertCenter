using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Aiursoft.AlertCenter.Models;

public class InboxFilter : IValidatableObject
{
    [StringLength(200, ErrorMessage = "关键词不能超过 200 个字符。")]
    public string? Keyword { get; set; }
    [RegularExpression("^(unread|read|all)$", ErrorMessage = "请选择有效的阅读状态。")]
    public string? ReadStatus { get; set; }
    public Guid? ApplicationId { get; set; }
    public Guid? ChannelId { get; set; }
    [RegularExpression("^sev[1-4]$", ErrorMessage = "请选择有效的告警级别。")]
    public string? Level { get; set; }
    public DateOnly? FromDate { get; set; }
    public DateOnly? ToDate { get; set; }
    public int Page { get; set; } = 1;
    public bool? UnreadOnly { get; set; }

    public void Normalize()
    {
        Keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim();
        // 保留旧链接的 unreadOnly 参数；新链接统一使用阅读状态。
        ReadStatus ??= UnreadOnly == false ? "all" : "unread";
        Page = Math.Clamp(Page, 1, 1000000);
    }

    public Dictionary<string, string?> ToRouteValues(int? page = null)
    {
        var values = new Dictionary<string, string?>();
        if (!string.IsNullOrEmpty(Keyword)) values["keyword"] = Keyword;
        if (ReadStatus is "read" or "all") values["readStatus"] = ReadStatus;
        if (ApplicationId.HasValue) values["applicationId"] = ApplicationId.Value.ToString();
        if (ChannelId.HasValue) values["channelId"] = ChannelId.Value.ToString();
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

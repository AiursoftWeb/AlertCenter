using Aiursoft.AlertCenter.Entities;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Configuration;

public sealed class SmsTemplateSettings
{
    public string NewIncident { get; set; } = string.Empty;
    public string Escalated { get; set; } = string.Empty;
    public string Mitigated { get; set; } = string.Empty;
    public string Resolved { get; set; } = string.Empty;
    public string Reopened { get; set; } = string.Empty;
}

public sealed class SmsSettings
{
    public string SecretId { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string SmsSdkAppId { get; set; } = string.Empty;
    public string SignName { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public SmsTemplateSettings Templates { get; set; } = new();
    public string Region { get; set; } = "ap-guangzhou";
    public string Endpoint { get; set; } = "sms.tencentcloudapi.com";

    public string ResolveTemplateId(string? incidentEventType)
    {
        var specific = incidentEventType switch
        {
            IncidentTimelineEntry.LevelChanged => Templates.Escalated,
            IncidentTimelineEntry.Mitigated => Templates.Mitigated,
            IncidentTimelineEntry.Resolved => Templates.Resolved,
            IncidentTimelineEntry.Reopened => Templates.Reopened,
            _ => Templates.NewIncident
        };
        if (!string.IsNullOrWhiteSpace(specific)) return specific.Trim();
        return TemplateId.Trim();
    }

    public bool TryValidate(out string? error, string? templateId = null, IStringLocalizer? localizer = null)
    {
        error = null;
        var effectiveTemplateId = string.IsNullOrWhiteSpace(templateId) ? ResolveTemplateId(null) : templateId.Trim();
        if (string.IsNullOrWhiteSpace(SecretId) ||
            string.IsNullOrWhiteSpace(SecretKey) ||
            string.IsNullOrWhiteSpace(SmsSdkAppId) ||
            string.IsNullOrWhiteSpace(SignName) ||
            string.IsNullOrWhiteSpace(effectiveTemplateId) ||
            string.IsNullOrWhiteSpace(Region))
        {
            error = localizer is null
                ? "Please configure Sms:SecretId, Sms:SecretKey, Sms:SmsSdkAppId, Sms:SignName, Sms:Templates, and Sms:Region."
                : localizer["Please configure Sms:SecretId, Sms:SecretKey, Sms:SmsSdkAppId, Sms:SignName, Sms:Templates, and Sms:Region."].Value;
            return false;
        }

        return true;
    }
}

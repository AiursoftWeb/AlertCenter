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

    public bool AreAllConfigured() =>
        !string.IsNullOrWhiteSpace(NewIncident) &&
        !string.IsNullOrWhiteSpace(Escalated) &&
        !string.IsNullOrWhiteSpace(Mitigated) &&
        !string.IsNullOrWhiteSpace(Resolved) &&
        !string.IsNullOrWhiteSpace(Reopened);
}

public sealed class SmsSettings
{
    public string SecretId { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string SmsSdkAppId { get; set; } = string.Empty;
    public string SignName { get; set; } = string.Empty;
    public SmsTemplateSettings? Templates { get; set; } = new();
    public string Region { get; set; } = "ap-guangzhou";
    public string Endpoint { get; set; } = "sms.tencentcloudapi.com";

    public string ResolveTemplateId(string? incidentEventType) => (incidentEventType switch
    {
        IncidentTimelineEntry.LevelChanged => Templates?.Escalated,
        IncidentTimelineEntry.Mitigated => Templates?.Mitigated,
        IncidentTimelineEntry.Resolved => Templates?.Resolved,
        IncidentTimelineEntry.Reopened => Templates?.Reopened,
        _ => Templates?.NewIncident
    })?.Trim() ?? string.Empty;

    public bool TryValidate(out string? error, IStringLocalizer? localizer = null)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(SecretId) ||
            string.IsNullOrWhiteSpace(SecretKey) ||
            string.IsNullOrWhiteSpace(SmsSdkAppId) ||
            string.IsNullOrWhiteSpace(SignName) ||
            string.IsNullOrWhiteSpace(Region) ||
            Templates is null ||
            !Templates.AreAllConfigured())
        {
            error = localizer is null
                ? "Please configure Sms:SecretId, Sms:SecretKey, Sms:SmsSdkAppId, Sms:SignName, Sms:Region, and all five template IDs under Sms:Templates (NewIncident, Escalated, Mitigated, Resolved, Reopened)."
                : localizer["Please configure Sms:SecretId, Sms:SecretKey, Sms:SmsSdkAppId, Sms:SignName, Sms:Region, and all five template IDs under Sms:Templates (NewIncident, Escalated, Mitigated, Resolved, Reopened)."].Value;
            return false;
        }

        return true;
    }
}

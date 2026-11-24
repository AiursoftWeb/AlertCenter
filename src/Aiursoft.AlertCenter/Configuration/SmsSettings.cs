using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Configuration;

public sealed class SmsSettings
{
    public string SecretId { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string SmsSdkAppId { get; set; } = string.Empty;
    public string SignName { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public string Region { get; set; } = "ap-guangzhou";
    public string Endpoint { get; set; } = "sms.tencentcloudapi.com";

    public bool TryValidate(out string? error, IStringLocalizer? localizer = null)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(SecretId) ||
            string.IsNullOrWhiteSpace(SecretKey) ||
            string.IsNullOrWhiteSpace(SmsSdkAppId) ||
            string.IsNullOrWhiteSpace(SignName) ||
            string.IsNullOrWhiteSpace(TemplateId) ||
            string.IsNullOrWhiteSpace(Region))
        {
            error = localizer is null
                ? "Please configure Sms:SecretId, Sms:SecretKey, Sms:SmsSdkAppId, Sms:SignName, Sms:TemplateId, and Sms:Region."
                : localizer["Please configure Sms:SecretId, Sms:SecretKey, Sms:SmsSdkAppId, Sms:SignName, Sms:TemplateId, and Sms:Region."].Value;
            return false;
        }

        return true;
    }
}

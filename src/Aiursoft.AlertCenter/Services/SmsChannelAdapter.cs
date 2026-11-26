using System.Globalization;
using System.Text.RegularExpressions;
using Aiursoft.AlertCenter.Configuration;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using TencentCloud.Common;
using TencentCloud.Common.Profile;
using TencentCloud.Sms.V20210111.Models;

namespace Aiursoft.AlertCenter.Services;

public sealed record SmsMessage(IReadOnlyList<string> PhoneNumbers, string TemplateId, IReadOnlyList<string> TemplateParams);

public interface ISmsSender
{
    Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default);
}

public sealed class SmsDeliveryException(string message) : Exception(message);

public sealed class TencentCloudSmsSender(IOptions<SmsSettings> options) : ISmsSender
{
    // 腾讯云 SendSms 单次请求最多支持 200 个手机号。
    private const int MaxPhoneNumbersPerBatch = 200;

    // 显式使用 PascalCase 序列化，避免 AiurProtocol 全局设置的 JsonConvert.DefaultSettings 将腾讯云字段转为 camelCase。
    private static readonly JsonSerializerSettings TencentJsonSettings = new()
    {
        ContractResolver = new DefaultContractResolver(),
        NullValueHandling = NullValueHandling.Ignore
    };

    private readonly Lock _clientLock = new();
    private (string SecretId, string SecretKey, string Region, string Endpoint)? _cachedKey;
    private CommonClient? _cachedClient;

    private CommonClient GetOrCreateClient(SmsSettings settings)
    {
        var secretId = settings.SecretId.Trim();
        var secretKey = settings.SecretKey.Trim();
        var region = settings.Region.Trim();
        var rawEndpoint = string.IsNullOrWhiteSpace(settings.Endpoint) ? "sms.tencentcloudapi.com" : settings.Endpoint.Trim();
        var key = (secretId, secretKey, region, rawEndpoint);

        lock (_clientLock)
        {
            if (_cachedClient != null && _cachedKey == key)
            {
                return _cachedClient;
            }

            var credential = new Credential
            {
                SecretId = secretId,
                SecretKey = secretKey
            };

            var useHttp = rawEndpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
            var host = (useHttp
                ? rawEndpoint["http://".Length..]
                : rawEndpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? rawEndpoint["https://".Length..]
                    : rawEndpoint).TrimEnd('/');

            var clientProfile = new ClientProfile
            {
                HttpProfile = new HttpProfile
                {
                    Endpoint = host,
                    Protocol = useHttp ? HttpProfile.REQ_HTTP : HttpProfile.REQ_HTTPS,
                    Timeout = 10
                }
            };

            _cachedClient = new CommonClient("sms", "2021-01-11", credential, region, clientProfile);
            _cachedKey = key;
            return _cachedClient;
        }
    }

    public async Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.TryValidate(out var error))
        {
            throw new InvalidOperationException(error);
        }

        var client = GetOrCreateClient(settings);

        foreach (var batch in message.PhoneNumbers.Chunk(MaxPhoneNumbersPerBatch))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new SendSmsRequest
            {
                SmsSdkAppId = settings.SmsSdkAppId.Trim(),
                SignName = settings.SignName.Trim(),
                TemplateId = message.TemplateId.Trim(),
                TemplateParamSet = message.TemplateParams.ToArray(),
                PhoneNumberSet = batch
            };

            var payloadJson = JsonConvert.SerializeObject(request, TencentJsonSettings);
            var rawResponse = await client.CallAsync(new CommonRequest(payloadJson), "SendSms");
            var response = JsonConvert.DeserializeObject<JsonResponseModel<SendSmsResponse>>(rawResponse, TencentJsonSettings)?.Response;
            var statuses = response?.SendStatusSet;
            if (statuses is null || statuses.Length == 0)
            {
                throw new SmsDeliveryException("Tencent Cloud SMS returned an empty response without delivery statuses.");
            }

            var failed = statuses
                .Where(status => !string.Equals(status.Code, "Ok", StringComparison.OrdinalIgnoreCase))
                .Select(status => $"{SmsChannelAdapter.MaskPhoneNumber(status.PhoneNumber)}: {status.Code} ({status.Message})")
                .ToList();

            if (failed.Count > 0)
            {
                throw new SmsDeliveryException($"Tencent Cloud SMS rejected {failed.Count} recipient(s): {string.Join("; ", failed)}");
            }
        }
    }
}

public partial class SmsChannelAdapter(
    TemplateDbContext db,
    ISmsSender sender,
    IOptions<SmsSettings> options,
    IStringLocalizer<SmsChannelAdapter> localizer) : INotificationChannelAdapter
{
    // 腾讯云国内短信标准模板单个变量上限为 12 个字符。
    private const int MaxTemplateParamLength = 12;

    [GeneratedRegex(@"^\+[1-9]\d{6,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex E164Regex();

    public ChannelTypeDefinition Definition { get; } = new(
        "sms",
        localizer["SMS (Tencent Cloud)"].Value,
        localizer["Send incident notifications via Tencent Cloud SMS to the bound phone numbers of selected users and user groups."].Value,
        "smartphone",
        false);

    public static bool TryNormalizePhoneNumber(string? value, out string normalized, out string? error, IStringLocalizer? localizer = null)
    {
        normalized = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = localizer is null
                ? "Please enter a phone number."
                : localizer["Please enter a phone number."].Value;
            return false;
        }

        var compact = Regex.Replace(value.Trim(), @"[\s\-()]+", string.Empty);
        if (!E164Regex().IsMatch(compact))
        {
            error = localizer is null
                ? "Invalid phone number format. Use E.164 format with country code, e.g. +8613800138000."
                : localizer["Invalid phone number format. Use E.164 format with country code, e.g. +8613800138000."].Value;
            return false;
        }

        normalized = compact;
        return true;
    }

    public static string MaskPhoneNumber(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber)) return string.Empty;
        var trimmed = phoneNumber.Trim();
        if (trimmed.Length <= 4) return new string('*', trimmed.Length);
        if (trimmed.Length <= 7) return $"{trimmed[..2]}****{trimmed[^1..]}";
        if (trimmed.Length <= 10) return $"{trimmed[..3]}****{trimmed[^2..]}";
        return $"{trimmed[..4]}****{trimmed[^4..]}";
    }

    public async Task<NotificationDeliveryResult> DeliverAsync(PushDelivery delivery, CancellationToken cancellationToken = default)
    {
        if (delivery.IncidentEvent is { Type: not (IncidentTimelineEntry.Mitigated or IncidentTimelineEntry.Resolved or IncidentTimelineEntry.Reopened), IsEscalation: false })
        {
            return new(false, ErrorMessage: localizer["SMS notifications are only sent when an incident is created, escalated, mitigated, resolved or reopened."].Value);
        }

        List<string> phoneNumbers;
        if (delivery.OnCallEmail is { } snapshot)
        {
            if (snapshot.ValidationError != null) return new(false, ErrorMessage: snapshot.ValidationError);
            phoneNumbers = ExtractUniquePhoneNumbers(snapshot.Recipients.Select(engineer => engineer.PhoneNumber));
            if (phoneNumbers.Count == 0)
            {
                return new(false, ErrorMessage: localizer["The on-call SMS recipient snapshot has no valid phone numbers; cannot re-select the current OCE delivery."].Value);
            }
        }
        else
        {
            var recipientIds = await db.PushDeliveryRecipients
                .Where(recipient => recipient.DeliveryId == delivery.Id)
                .Select(recipient => recipient.UserId)
                .ToListAsync(cancellationToken);

            if (recipientIds.Count == 0)
            {
                return new(false, ErrorMessage: localizer["No users available to receive SMS notifications. Please configure recipient users or user groups."].Value);
            }

            var rawPhones = await db.Users.AsNoTracking()
                .Where(user => recipientIds.Contains(user.Id) && user.PhoneNumber != null && user.PhoneNumber != string.Empty)
                .Select(user => user.PhoneNumber!)
                .ToListAsync(cancellationToken);

            phoneNumbers = ExtractUniquePhoneNumbers(rawPhones);
            if (phoneNumbers.Count == 0)
            {
                return new(false, ErrorMessage: localizer["None of the target recipients have a valid phone number bound. Ask users to bind their phone number in Profile Settings."].Value);
            }
        }

        var templateId = options.Value.ResolveTemplateId(delivery.IncidentEvent?.Type);
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return new(false, ErrorMessage: localizer["Tencent Cloud SMS template ID is not configured for this notification type. Please check Sms:Templates settings."].Value);
        }

        var incident = delivery.Incident ?? delivery.IncidentEvent?.Incident;
        var applicationName = incident?.Application?.Name;
        if (string.IsNullOrWhiteSpace(applicationName) && incident != null)
        {
            applicationName = await db.SourceApplications.AsNoTracking()
                .Where(app => app.Id == incident.ApplicationId)
                .Select(app => app.Name)
                .SingleOrDefaultAsync(cancellationToken);
        }

        try
        {
            await sender.SendAsync(CreateMessage(delivery, phoneNumbers, templateId, applicationName), cancellationToken);
            return new(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new(false, ErrorMessage: localizer["SMS sending timed out. Some recipients may have received the message; please verify and retry."].Value);
        }
        catch (InvalidOperationException)
        {
            return new(false, ErrorMessage: localizer["Invalid Tencent Cloud SMS configuration. Please check Sms settings."].Value);
        }
        catch (SmsDeliveryException exception)
        {
            return new(false, ErrorMessage: exception.Message);
        }
        catch (TencentCloudSDKException exception) when (exception.InnerException is OperationCanceledException or TimeoutException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            return new(false, ErrorMessage: localizer["SMS sending timed out. Some recipients may have received the message; please verify and retry."].Value);
        }
        catch (TencentCloudSDKException exception)
        {
            return new(false, ErrorMessage: string.Format(
                CultureInfo.InvariantCulture,
                localizer["Tencent Cloud SMS API error: {0}"].Value,
                exception.Message));
        }
    }

    internal SmsMessage CreateMessage(PushDelivery delivery, IReadOnlyList<string> phoneNumbers, string templateId, string? applicationName = null)
    {
        var level = (delivery.Incident?.Level ?? delivery.IncidentEvent?.NewLevel ?? "TEST").ToUpperInvariant();
        var appLabel = string.IsNullOrWhiteSpace(applicationName) ? "AlertCenter" : applicationName;
        return new SmsMessage(phoneNumbers, templateId, [Truncate(Sanitize(appLabel), MaxTemplateParamLength), level]);
    }

    private static List<string> ExtractUniquePhoneNumbers(IEnumerable<string?> rawPhones)
    {
        var result = new List<string>();
        foreach (var rawPhone in rawPhones)
        {
            if (TryNormalizePhoneNumber(rawPhone, out var normalized, out _) &&
                !result.Contains(normalized, StringComparer.Ordinal))
            {
                result.Add(normalized);
            }
        }
        return result;
    }

    private static string Sanitize(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

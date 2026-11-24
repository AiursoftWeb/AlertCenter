using System.Globalization;
using System.Text.RegularExpressions;
using Aiursoft.AlertCenter.Configuration;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using TencentCloud.Common;
using TencentCloud.Common.Profile;
using TencentCloud.Sms.V20210111;
using TencentCloud.Sms.V20210111.Models;

namespace Aiursoft.AlertCenter.Services;

public sealed record SmsMessage(IReadOnlyList<string> PhoneNumbers, IReadOnlyList<string> TemplateParams);

public interface ISmsSender
{
    Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default);
}

public sealed class SmsDeliveryException(string message) : Exception(message);

public sealed class TencentCloudSmsSender(IOptions<SmsSettings> options) : ISmsSender
{
    // 腾讯云 SendSms 单次请求最多支持 200 个手机号。
    private const int MaxPhoneNumbersPerBatch = 200;

    public async Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.TryValidate(out var error))
        {
            throw new InvalidOperationException(error);
        }

        var credential = new Credential
        {
            SecretId = settings.SecretId,
            SecretKey = settings.SecretKey
        };

        var clientProfile = new ClientProfile
        {
            HttpProfile = new HttpProfile
            {
                Endpoint = string.IsNullOrWhiteSpace(settings.Endpoint) ? "sms.tencentcloudapi.com" : settings.Endpoint.Trim(),
                Timeout = 10
            }
        };

        var client = new SmsClient(credential, settings.Region.Trim(), clientProfile);

        foreach (var batch in message.PhoneNumbers.Chunk(MaxPhoneNumbersPerBatch))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new SendSmsRequest
            {
                SmsSdkAppId = settings.SmsSdkAppId.Trim(),
                SignName = settings.SignName.Trim(),
                TemplateId = settings.TemplateId.Trim(),
                TemplateParamSet = message.TemplateParams.ToArray(),
                PhoneNumberSet = batch
            };

            var response = await client.SendSms(request);
            var statuses = response?.SendStatusSet ?? [];
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
    IStringLocalizer<SmsChannelAdapter> localizer) : INotificationChannelAdapter
{
    private const int MaxTemplateParamLength = 60;

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
        if (trimmed.Length <= 7) return trimmed;
        return $"{trimmed[..4]}****{trimmed[^4..]}";
    }

    public async Task<NotificationDeliveryResult> DeliverAsync(PushDelivery delivery, CancellationToken cancellationToken = default)
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

        var phoneNumbers = new List<string>();
        foreach (var rawPhone in rawPhones)
        {
            if (TryNormalizePhoneNumber(rawPhone, out var normalized, out _) &&
                !phoneNumbers.Contains(normalized, StringComparer.Ordinal))
            {
                phoneNumbers.Add(normalized);
            }
        }

        if (phoneNumbers.Count == 0)
        {
            return new(false, ErrorMessage: localizer["None of the target recipients have a valid phone number bound. Ask users to bind their phone number in Profile Settings."].Value);
        }

        try
        {
            await sender.SendAsync(CreateMessage(delivery, phoneNumbers), cancellationToken);
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
        catch (TencentCloudSDKException exception)
        {
            return new(false, ErrorMessage: string.Format(
                CultureInfo.InvariantCulture,
                localizer["Tencent Cloud SMS API error: {0}"].Value,
                exception.Message));
        }
    }

    internal SmsMessage CreateMessage(PushDelivery delivery, IReadOnlyList<string> phoneNumbers)
    {
        var incident = delivery.Incident ?? delivery.IncidentEvent?.Incident;
        var level = (delivery.Incident?.Level ?? delivery.IncidentEvent?.NewLevel ?? "TEST").ToUpperInvariant();
        string summary;
        if (delivery.IncidentEvent is { } incidentEvent)
        {
            summary = incidentEvent.Type switch
            {
                IncidentTimelineEntry.LevelChanged => $"[{incidentEvent.PreviousLevel?.ToUpperInvariant()}->{incidentEvent.NewLevel?.ToUpperInvariant()}] {incidentEvent.Incident.Title}",
                IncidentTimelineEntry.Mitigated => $"[Mitigated] {incidentEvent.Incident.Title}",
                IncidentTimelineEntry.Resolved => $"[Resolved] {incidentEvent.Incident.Title}",
                IncidentTimelineEntry.Reopened => $"[Reopened] {incidentEvent.Incident.Title}",
                _ => incidentEvent.Incident.Title
            };
        }
        else if (incident != null)
        {
            summary = $"#{incident.ShortId} {incident.Title}";
        }
        else
        {
            summary = delivery.TestContent ?? PushDelivery.DefaultTestContent;
        }

        return new SmsMessage(phoneNumbers, [level, Truncate(Sanitize(summary), MaxTemplateParamLength)]);
    }

    private static string Sanitize(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
}

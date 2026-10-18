using System.Net;
using System.Net.Mail;
using System.Text;
using Aiursoft.AlertCenter.Configuration;
using Aiursoft.AlertCenter.Entities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public sealed record EmailMessage(IReadOnlyList<string> Recipients, IReadOnlyList<string> CcRecipients, string Subject, string Body, string? HtmlBody = null);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

public sealed class SmtpEmailSender(IOptions<EmailSettings> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.TryValidate(out var error)) throw new InvalidOperationException(error);
        using var mail = new MailMessage();
        mail.From = new MailAddress(settings.From);
        mail.Subject = message.Subject;
        mail.SubjectEncoding = Encoding.UTF8;
        mail.Body = message.Body;
        mail.BodyEncoding = Encoding.UTF8;
        mail.IsBodyHtml = false;
        if (message.HtmlBody != null)
            mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.HtmlBody, Encoding.UTF8, "text/html"));
        foreach (var recipient in message.Recipients) mail.To.Add(recipient);
        foreach (var recipient in message.CcRecipients) mail.CC.Add(recipient);
        using var client = new SmtpClient(settings.Host, settings.Port);
        client.EnableSsl = settings.RequireTls;
        client.UseDefaultCredentials = false;
        client.Timeout = 10_000;
        if (!string.IsNullOrWhiteSpace(settings.UserName))
            client.Credentials = new NetworkCredential(settings.UserName, settings.Password);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await client.SendMailAsync(mail, timeout.Token);
    }
}

public class EmailChannelAdapter(IEmailSender sender, IStringLocalizer<EmailChannelAdapter> localizer, IOptions<EmailSettings> options) : INotificationChannelAdapter
{
    private const int MaximumRecipients = 50;

    public ChannelTypeDefinition Definition { get; } = new("email", localizer["Email (SMTP)"].Value,
        localizer["Send incident notifications to specified email addresses via the global SMTP server."].Value, "mail", false);

    public static bool TryParseRecipient(string? value, out string recipient, out string? error, IStringLocalizer? localizer = null)
    {
        recipient = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = (localizer is null ? "Please enter the recipient email address." : localizer["Please enter the recipient email address."].Value);
            return false;
        }
        if (!TryParseAddress(value.Trim(), out recipient))
        {
            error = (localizer is null ? "Invalid recipient email format. Display names are not supported." : localizer["Invalid recipient email format. Display names are not supported."].Value);
            return false;
        }
        return true;
    }

    public static bool TryParseCcRecipients(string? value, out List<string> recipients, out string? error, IStringLocalizer? localizer = null)
    {
        recipients = [];
        error = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        foreach (var item in value.Split([';', ',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseAddress(item, out var address))
            {
                error = (localizer is null ? "Invalid CC email format. Display names are not supported." : localizer["Invalid CC email format. Display names are not supported."].Value);
                return false;
            }
            if (!recipients.Contains(address, StringComparer.OrdinalIgnoreCase)) recipients.Add(address);
        }
        if (recipients.Count > MaximumRecipients)
        {
            error = string.Format((localizer is null ? "Each email channel supports up to {0} CC recipients." : localizer["Each email channel supports up to {0} CC recipients."].Value), MaximumRecipients);
            return false;
        }
        return true;
    }

    private static bool TryParseAddress(string value, out string address)
    {
        address = string.Empty;
        if (value.Length > 320 || value.Any(char.IsControl)) return false;
        try
        {
            var parsedAddress = new MailAddress(value);
            if (!string.Equals(parsedAddress.Address, value, StringComparison.OrdinalIgnoreCase)) return false;
            address = parsedAddress.Address;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public async Task<NotificationDeliveryResult> DeliverAsync(PushDelivery delivery, CancellationToken cancellationToken = default)
    {
        if (delivery.IncidentEvent is { Type: not (IncidentTimelineEntry.Mitigated or IncidentTimelineEntry.Resolved) })
            return new(false, ErrorMessage: localizer["Email notifications are only sent when an incident is created, mitigated or resolved."].Value);
        List<string> recipients;
        List<string> ccRecipients;
        if (delivery.OnCallEmail is { } snapshot)
        {
            if (snapshot.ValidationError != null) return new(false, ErrorMessage: snapshot.ValidationError);
            if (snapshot.Recipients.Count == 0 || snapshot.Recipients.Any(engineer => !TryParseRecipient(engineer.Email, out _, out _, localizer: localizer)) ||
                !TryParseCcRecipients(snapshot.CcRecipients, out ccRecipients, out _, localizer: localizer))
                return new(false, ErrorMessage: localizer["The on-call email recipient snapshot is invalid; cannot re-select the current OCE delivery."].Value);
            recipients = snapshot.Recipients.Select(engineer => engineer.Email!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            ccRecipients.RemoveAll(address => recipients.Contains(address, StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            if (!TryParseRecipient(delivery.Channel?.EmailRecipient, out var recipient, out _, localizer: localizer) ||
                !TryParseCcRecipients(delivery.Channel?.EmailCcRecipients, out ccRecipients, out _, localizer: localizer) ||
                ccRecipients.Contains(recipient, StringComparer.OrdinalIgnoreCase))
                return new(false, ErrorMessage: localizer["Invalid recipient or CC configuration for the email channel. Please check the channel settings."].Value);
            recipients = [recipient];
        }
        try
        {
            await sender.SendAsync(CreateMessage(delivery, recipients, ccRecipients), cancellationToken);
            return new(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new(false, ErrorMessage: localizer["Email sending timed out. The mail server may have received the message; please verify and retry."].Value);
        }
        catch (InvalidOperationException)
        {
            return new(false, ErrorMessage: localizer["Invalid SMTP configuration. Please check Email settings."].Value);
        }
        catch (SmtpException)
        {
            return new(false, ErrorMessage: localizer["Email sending failed. Please check SMTP configuration, network, or mail server."].Value);
        }
        catch (TimeoutException)
        {
            return new(false, ErrorMessage: localizer["Email sending timed out. The mail server may have received the message; please verify and retry."].Value);
        }
    }

    private EmailMessage CreateMessage(PushDelivery delivery, IReadOnlyList<string> recipients, IReadOnlyList<string> ccRecipients)
    {
        var message = delivery.Incident;
        var incidentEvent = delivery.IncidentEvent;
        var title = incidentEvent?.Type switch
        {
            IncidentTimelineEntry.Mitigated => string.Format(localizer["Incident mitigated: {0}"].Value, incidentEvent.Incident.Title),
            IncidentTimelineEntry.Resolved => string.Format(localizer["Incident resolved: {0}"].Value, incidentEvent.Incident.Title),
            _ => message?.Title ?? localizer["Channel test notification"].Value
        };
        var level = message?.Level ?? incidentEvent?.NewLevel ?? "sev3";
        var subject = $"[AlertCenter][{level}] {SanitizeSubject(title)}";
        var incidentId = message?.Id ?? incidentEvent?.IncidentId;
        var baseUrl = options.Value.PublicBaseUrl;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var site) ||
            site.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(site.UserInfo) ||
            !string.IsNullOrEmpty(site.Query) || !string.IsNullOrEmpty(site.Fragment))
            throw new InvalidOperationException("Email:PublicBaseUrl must be an absolute HTTP(S) site URL.");
        var url = new Uri(new Uri(site.AbsoluteUri.TrimEnd('/') + "/"),
            incidentId.HasValue ? $"Incidents/Details/{incidentId.Value}" : "").AbsoluteUri;
        var body = new StringBuilder()
            .AppendLine(Introduction(delivery, localizer))
            .AppendLine()
            .AppendLine(string.Format(localizer["Title: {0}"].Value, title))
            .AppendLine(string.Format(localizer["Severity: {0}"].Value, level))
            .AppendLine(string.Format(localizer["Occurred at (UTC): {0:O}"].Value, message?.OccurredAtUtc ?? delivery.CreatedAtUtc))
            .AppendLine(string.Format(localizer["Delivery ID: {0}"].Value, delivery.Id));
        if (incidentEvent != null)
            body.AppendLine(string.Format(localizer["Incident ID: {0}"].Value, incidentEvent.IncidentId)).AppendLine(string.Format(localizer["Action time (UTC): {0:O}"].Value, incidentEvent.CreatedAtUtc))
                .AppendLine(string.Format(localizer["Reason: {0}"].Value, incidentEvent.Note ?? localizer["None"].Value));
        if (message?.Content is not null) body.AppendLine().AppendLine(localizer["Content: "].Value).AppendLine(message.Content);
        var context = IncidentContextPresentation.ScalarFields((message ?? incidentEvent?.Incident)?.ContextJson);
        if (context.Count > 0)
        {
            body.AppendLine().AppendLine(localizer["Context:"].Value);
            foreach (var field in context) body.AppendLine($"{field.Key}: {field.Value}");
        }
        if (delivery.OnCallEmail is { } snapshot)
        {
            body.AppendLine().AppendLine(string.Format(localizer["On-call group: {0}"].Value, snapshot.GroupName))
                .AppendLine(string.Format(localizer["OCE Query Time (UTC): {0:O}"].Value, snapshot.NotificationAtUtc))
                .AppendLine(string.Format(localizer["On-call time (UTC): {0:O} — {1:O}"].Value, snapshot.StartsAtUtc, snapshot.EndsAtUtc));
        }
        body.AppendLine().AppendLine(localizer["Open in AlertCenter"].Value).AppendLine(url);
        return new(recipients, ccRecipients, subject, body.ToString(),
            RenderHtml(delivery, title, level, url, localizer, context));
    }

    private static string Introduction(PushDelivery delivery, IStringLocalizer localizer) => delivery.IncidentEvent?.Type switch
    {
        IncidentTimelineEntry.Mitigated => localizer["The incident has been mitigated."].Value,
        IncidentTimelineEntry.Resolved => localizer["The incident has been resolved."].Value,
        _ => delivery.Incident != null ? localizer["A new incident has been reported."].Value
            : delivery.TestContent ?? localizer["This is a test message for the push channel."].Value
    };

    private static string SanitizeSubject(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    private static string RenderHtml(PushDelivery delivery, string title, string level, string url,
        IStringLocalizer localizer, IReadOnlyList<KeyValuePair<string, string>> context)
    {
        var incident = delivery.Incident ?? delivery.IncidentEvent?.Incident;
        var color = delivery.IncidentEvent?.Type switch
        {
            IncidentTimelineEntry.Mitigated => "#facc15",
            IncidentTimelineEntry.Resolved => "#15803d",
            _ => incident != null ? "#b42318" : "#175cd3"
        };
        var foreground = delivery.IncidentEvent?.Type == IncidentTimelineEntry.Mitigated ? "#422006" : "#ffffff";
        var linkColor = delivery.IncidentEvent?.Type == IncidentTimelineEntry.Mitigated ? "#854d0e" : color;
        var status = delivery.IncidentEvent?.Type switch
        {
            IncidentTimelineEntry.Mitigated => localizer["Mitigated"].Value,
            IncidentTimelineEntry.Resolved => localizer["Resolved"].Value,
            _ => incident != null ? localizer["New incident"].Value : localizer["Test notification"].Value
        };
        var introduction = Introduction(delivery, localizer);
        var sections = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(incident?.Content))
            sections.Append(Section(localizer["Incident details"].Value, incident.Content));
        if (delivery.IncidentEvent is { } entry)
            sections.Append(Section(localizer["Reason"].Value, entry.Note ?? localizer["None"].Value));
        if (delivery.OnCallEmail is { } snapshot)
        {
            var engineers = string.Join(", ", snapshot.Recipients.Select(person => person.DisplayName));
            sections.Append(Section(localizer["On-call team"].Value,
                $"{snapshot.GroupName}\n{engineers}\n{snapshot.StartsAtUtc:yyyy-MM-dd HH:mm} — {snapshot.EndsAtUtc:yyyy-MM-dd HH:mm} UTC"));
        }
        if (context.Count > 0)
        {
            sections.Append($"<h2 style=\"margin:26px 0 10px;font-size:14px;color:#102a43;\">{Encode(localizer["Context"].Value)}</h2>");
            sections.Append("<table width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" style=\"width:100%;table-layout:fixed;border-collapse:collapse;font-size:14px;line-height:1.6;\">");
            foreach (var field in context)
                sections.Append($"<tr><th scope=\"row\" style=\"width:35%;padding:12px;text-align:left;vertical-align:top;background-color:#f1f5f9;border:1px solid #e2e8f0;overflow-wrap:anywhere;word-break:break-word;\">{Encode(field.Key)}</th><td style=\"padding:12px;vertical-align:top;border:1px solid #e2e8f0;overflow-wrap:anywhere;word-break:break-word;white-space:pre-wrap;\">{Encode(field.Value)}</td></tr>");
            sections.Append("</table>");
        }
        var incidentId = incident?.Id;
        var reference = incidentId.HasValue
            ? $"{localizer["Incident ID"].Value}: {incidentId.Value}"
            : localizer["Channel test notification"].Value;
        var occurredAt = incident?.OccurredAtUtc ?? delivery.CreatedAtUtc;
        // Tables and inline styles also work when email clients strip stylesheets.
        // Encode every external value; only the markup in this renderer is trusted.
        return $$"""
            <!doctype html>
            <html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"></head>
            <body style="margin:0;padding:0;background-color:#f1f5f9;color:#172b4d;font-family:Arial,Helvetica,sans-serif;">
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background-color:#f1f5f9;"><tr><td align="center" style="padding:24px 12px;">
            <table role="presentation" width="640" cellspacing="0" cellpadding="0" style="width:100%;max-width:640px;background-color:{{foreground}};border:1px solid #dce3ed;border-radius:12px;">
            <tr><td style="padding:24px 28px;background-color:{{color}};border-radius:12px 12px 0 0;color:{{foreground}};font-size:20px;font-weight:bold;">AlertCenter · {{Encode(status)}}</td></tr>
            <tr><td style="padding:28px;">
                <span style="display:inline-block;padding:6px 12px;background-color:{{color}};border-radius:6px;color:{{foreground}};font-size:12px;font-weight:bold;">{{Encode(level.ToUpperInvariant())}}</span>
                <h1 style="margin:18px 0 12px;font-size:24px;line-height:1.35;color:#102a43;overflow-wrap:anywhere;word-break:break-word;">{{Encode(title)}}</h1>
                <p style="margin:0 0 22px;font-size:15px;line-height:1.6;color:#526579;">{{Encode(introduction)}}</p>
                <table role="presentation" cellspacing="0" cellpadding="0"><tr><td bgcolor="{{color}}" style="border-radius:6px;">
                    <a href="{{Encode(url)}}" style="display:inline-block;border:1px solid {{color}};padding:13px 22px;color:{{foreground}};text-decoration:none;font-size:15px;font-weight:bold;border-radius:6px;">{{Encode(localizer["Open in AlertCenter"].Value)}}</a>
                </td></tr></table>
                <p style="margin:20px 0 0;font-size:12px;line-height:1.6;color:#526579;">{{Encode(localizer["Occurred at (UTC)"].Value)}}: {{occurredAt:yyyy-MM-dd HH:mm:ss}}</p>
                {{sections}}
                <p style="margin:24px 0 8px;font-size:12px;line-height:1.6;color:#526579;">{{Encode(localizer["If the button does not work, open this link:"].Value)}}<br>
                    <a href="{{Encode(url)}}" style="color:{{linkColor}};overflow-wrap:anywhere;word-break:break-all;">{{Encode(url)}}</a>
                </p>
            </td></tr>
            <tr><td style="padding:18px 28px;background-color:#f8fafc;border-top:1px solid #e2e8f0;font-size:11px;line-height:1.8;color:#526579;overflow-wrap:anywhere;word-break:break-all;">
                {{Encode(reference)}}<br>{{Encode(localizer["Delivery ID"].Value)}}: {{delivery.Id}}
            </td></tr>
            </table>
            </td></tr></table>
            </body></html>
            """;
    }

    private static string Section(string heading, string text) => $$"""
        <h2 style="margin:26px 0 10px;font-size:14px;line-height:1.5;color:#102a43;">{{Encode(heading)}}</h2>
        <div style="padding:16px;background-color:#f8fafc;border:1px solid #e2e8f0;border-radius:6px;font-size:14px;line-height:1.7;color:#334155;overflow-wrap:anywhere;word-break:break-word;">{{Encode(text).Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "<br>")}}</div>
        """;

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

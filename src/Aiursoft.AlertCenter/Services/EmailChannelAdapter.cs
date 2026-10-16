using System.Net;
using System.Net.Mail;
using System.Text;
using Aiursoft.AlertCenter.Configuration;
using Aiursoft.AlertCenter.Entities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public sealed record EmailMessage(IReadOnlyList<string> Recipients, IReadOnlyList<string> CcRecipients, string Subject, string Body);

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

public class EmailChannelAdapter(IEmailSender sender, IStringLocalizer<EmailChannelAdapter> localizer) : INotificationChannelAdapter
{
    private const int MaximumRecipients = 50;

    public ChannelTypeDefinition Definition { get; } = new("email", localizer["Email (SMTP)"].Value,
        localizer["Send plain text alerts to specified email addresses via the global SMTP server."].Value, "mail", false);

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
            await sender.SendAsync(CreateMessage(delivery, recipients, ccRecipients, localizer: localizer), cancellationToken);
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

    private static EmailMessage CreateMessage(PushDelivery delivery, IReadOnlyList<string> recipients, IReadOnlyList<string> ccRecipients, IStringLocalizer? localizer = null)
    {
        var message = delivery.Message;
        var incidentEvent = delivery.IncidentEvent;
        var title = message?.Title ?? (incidentEvent != null ? string.Format((localizer is null ? "Incident reopened: {0}" : localizer["Incident reopened: {0}"].Value), incidentEvent.Incident.Title) : (localizer is null ? "Channel test notification" : localizer["Channel test notification"].Value));
        var level = message?.Level ?? incidentEvent?.NewLevel ?? "sev3";
        var subject = $"[AlertCenter][{level}] {SanitizeSubject(title)}";
        var body = new StringBuilder()
            .AppendLine(incidentEvent != null ? (localizer is null ? "The incident has been reopened. Please respond, on-call engineer." : localizer["The incident has been reopened. Please respond, on-call engineer."].Value) : message == null ? delivery.TestContent ?? (localizer is null ? "This is a test message for the push channel." : localizer["This is a test message for the push channel."].Value) : (localizer is null ? "A new alert has been detected." : localizer["A new alert has been detected."].Value))
            .AppendLine()
            .AppendLine(string.Format((localizer is null ? "Title: {0}" : localizer["Title: {0}"].Value), title))
            .AppendLine(string.Format((localizer is null ? "Severity: {0}" : localizer["Severity: {0}"].Value), level))
            .AppendLine(string.Format((localizer is null ? "Occurred at (UTC): {0:O}" : localizer["Occurred at (UTC): {0:O}"].Value), message?.OccurredAtUtc ?? delivery.CreatedAtUtc))
            .AppendLine(string.Format((localizer is null ? "Delivery ID: {0}" : localizer["Delivery ID: {0}"].Value), delivery.Id));
        if (incidentEvent != null)
            body.AppendLine(string.Format((localizer is null ? "Incident ID: {0}" : localizer["Incident ID: {0}"].Value), incidentEvent.IncidentId)).AppendLine(string.Format((localizer is null ? "Action time (UTC): {0:O}" : localizer["Action time (UTC): {0:O}"].Value), incidentEvent.CreatedAtUtc))
                .AppendLine(string.Format(localizer is null ? "Reopen note: {0}" : localizer["Reopen note: {0}"].Value, incidentEvent.Note ?? (localizer is null ? "None" : localizer["None"].Value)));
        if (message?.Content is not null) body.AppendLine().AppendLine((localizer is null ? "Content: " : localizer["Content: "].Value)).AppendLine(message.Content);
        if (!string.IsNullOrWhiteSpace(message?.ContextJson)) body.AppendLine().AppendLine((localizer is null ? "Context:" : localizer["Context:"].Value)).AppendLine(message.ContextJson);
        if (delivery.OnCallEmail is { } snapshot)
        {
            body.AppendLine().AppendLine(string.Format((localizer is null ? "On-call group: {0}" : localizer["On-call group: {0}"].Value), snapshot.GroupName))
                .AppendLine(string.Format((localizer is null ? "OCE Query Time (UTC): {0:O}" : localizer["OCE Query Time (UTC): {0:O}"].Value), snapshot.NotificationAtUtc))
                .AppendLine(string.Format((localizer is null ? "On-call time (UTC): {0:O} — {1:O}" : localizer["On-call time (UTC): {0:O} — {1:O}"].Value), snapshot.StartsAtUtc, snapshot.EndsAtUtc));
        }
        return new(recipients, ccRecipients, subject, body.ToString());
    }

    private static string SanitizeSubject(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
}

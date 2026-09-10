using System.Net;
using System.Net.Mail;
using System.Text;
using Aiursoft.AlertCenter.Configuration;
using Aiursoft.AlertCenter.Entities;
using Microsoft.Extensions.Options;

namespace Aiursoft.AlertCenter.Services;

public sealed record EmailMessage(IReadOnlyList<string> Recipients, string Subject, string Body);

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
        using var mail = new MailMessage
        {
            From = new MailAddress(settings.From),
            Subject = message.Subject,
            SubjectEncoding = Encoding.UTF8,
            Body = message.Body,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false
        };
        foreach (var recipient in message.Recipients) mail.To.Add(recipient);
        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.RequireTls,
            UseDefaultCredentials = false,
            Timeout = 10_000
        };
        if (!string.IsNullOrWhiteSpace(settings.UserName))
            client.Credentials = new NetworkCredential(settings.UserName, settings.Password);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await client.SendMailAsync(mail, timeout.Token);
    }
}

public class EmailChannelAdapter(IEmailSender sender) : INotificationChannelAdapter
{
    private const int MaximumRecipients = 50;

    public ChannelTypeDefinition Definition { get; } = new("email", "邮件（SMTP）",
        "通过全局 SMTP 服务器向指定邮箱发送纯文本告警。", "mail", false);

    public static bool TryParseRecipients(string? value, out List<string> recipients, out string? error)
    {
        recipients = [];
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "请至少填写一个收件人邮箱。";
            return false;
        }
        foreach (var item in value.Split([';', ',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (item.Length > 320 || item.Any(char.IsControl))
            {
                error = "收件人邮箱格式无效。";
                return false;
            }
            try
            {
                var address = new MailAddress(item);
                if (!string.Equals(address.Address, item, StringComparison.OrdinalIgnoreCase))
                {
                    error = "收件人必须是单独的邮箱地址，不支持显示名称。";
                    return false;
                }
                if (!recipients.Contains(address.Address, StringComparer.OrdinalIgnoreCase)) recipients.Add(address.Address);
            }
            catch (FormatException)
            {
                error = "收件人邮箱格式无效。";
                return false;
            }
        }
        if (recipients.Count == 0)
        {
            error = "请至少填写一个收件人邮箱。";
            return false;
        }
        if (recipients.Count > MaximumRecipients)
        {
            error = $"每个邮件渠道最多支持 {MaximumRecipients} 个收件人。";
            return false;
        }
        return true;
    }

    public async Task<NotificationDeliveryResult> DeliverAsync(PushDelivery delivery, CancellationToken cancellationToken = default)
    {
        if (!TryParseRecipients(delivery.Channel.EmailRecipients, out var recipients, out _))
            return new(false, ErrorMessage: "邮件渠道收件人配置无效，请检查渠道配置。");
        try
        {
            await sender.SendAsync(CreateMessage(delivery, recipients), cancellationToken);
            return new(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new(false, ErrorMessage: "邮件发送超时，邮件服务器可能已收到消息，请核实后重试。");
        }
        catch (InvalidOperationException)
        {
            return new(false, ErrorMessage: "SMTP 配置无效，请检查 Email 配置。");
        }
        catch (SmtpException)
        {
            return new(false, ErrorMessage: "邮件发送失败，请检查 SMTP 配置、网络或邮件服务器。");
        }
        catch (TimeoutException)
        {
            return new(false, ErrorMessage: "邮件发送超时，邮件服务器可能已收到消息，请核实后重试。");
        }
    }

    private static EmailMessage CreateMessage(PushDelivery delivery, IReadOnlyList<string> recipients)
    {
        var message = delivery.Message;
        var title = message?.Title ?? "渠道测试通知";
        var subject = $"[AlertCenter][{message?.Level ?? "sev3"}] {SanitizeSubject(title)}";
        var body = new StringBuilder()
            .AppendLine(message == null ? delivery.TestContent ?? PushDelivery.DefaultTestContent : "检测到一条新的告警。")
            .AppendLine()
            .AppendLine($"标题：{title}")
            .AppendLine($"级别：{message?.Level ?? "sev3"}")
            .AppendLine($"发生时间（UTC）：{message?.OccurredAtUtc ?? delivery.CreatedAtUtc:O}")
            .AppendLine($"投递 ID：{delivery.Id}");
        if (message?.Content is not null) body.AppendLine().AppendLine("内容：").AppendLine(message.Content);
        if (!string.IsNullOrWhiteSpace(message?.ContextJson)) body.AppendLine().AppendLine("上下文：").AppendLine(message.ContextJson);
        return new(recipients, subject, body.ToString());
    }

    private static string SanitizeSubject(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
}

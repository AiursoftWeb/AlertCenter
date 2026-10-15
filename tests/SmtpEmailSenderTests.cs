using System.Net;
using System.Net.Sockets;
using System.Text;
using Aiursoft.AlertCenter.Configuration;
using Aiursoft.AlertCenter.Services;
using Microsoft.Extensions.Options;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class SmtpEmailSenderTests
{
    [TestMethod]
    public async Task SendsAllToAndCcRecipientsInOneSmtpMessage()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var received = ReceiveMessage(listener, timeout.Token);
        var sender = new SmtpEmailSender(Options.Create(new EmailSettings
        {
            Host = "127.0.0.1", Port = port, From = "sender@example.com", RequireTls = false
        }));
        await sender.SendAsync(new EmailMessage(["alice@example.com", "bob@example.com"],
            ["team@example.com"], "OCE mail", "Alert payload"), timeout.Token);
        var (recipients, data) = await received;
        CollectionAssert.AreEquivalent(new[] { "alice@example.com", "bob@example.com", "team@example.com" }, recipients.ToArray());
        var parts = data.Split("\r\n\r\n", 2);
        Assert.Contains("To: alice@example.com, bob@example.com", parts[0]);
        Assert.Contains("Cc: team@example.com", parts[0]);
        Assert.Contains("Content-Transfer-Encoding: base64", parts[0]);
        Assert.AreEqual("Alert payload", Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])));
    }

    private static async Task<(List<string> Recipients, string Data)> ReceiveMessage(TcpListener listener, CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\r\n";
        writer.AutoFlush = true;
        await writer.WriteLineAsync("220 localhost SMTP test".AsMemory(), token);
        var recipients = new List<string>();
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (line.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase))
                await writer.WriteLineAsync("250 localhost".AsMemory(), token);
            else if (line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase))
                await writer.WriteLineAsync("250 OK".AsMemory(), token);
            else if (line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
            {
                recipients.Add(line[(line.IndexOf('<') + 1)..line.IndexOf('>')]);
                await writer.WriteLineAsync("250 OK".AsMemory(), token);
            }
            else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("354 End with a dot".AsMemory(), token);
                var data = new StringBuilder();
                while (await reader.ReadLineAsync(token) is { } payload && payload != ".") data.Append(payload).Append("\r\n");
                await writer.WriteLineAsync("250 Accepted".AsMemory(), token);
                return (recipients, data.ToString());
            }
            else throw new InvalidOperationException($"Unexpected SMTP command: {line}");
        }
        throw new InvalidOperationException("SMTP connection closed before DATA.");
    }
}

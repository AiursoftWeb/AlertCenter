using System.Net;
using System.Net.Sockets;
using System.Text;
using Aiursoft.AlertCenter.Configuration;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Microsoft.Extensions.Options;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class SmtpEmailSenderTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("<html><body><a href=\"https://alerts.example.com/Incidents/Details/123\">Open incident</a></body></html>")]
    public async Task SendsAllToAndCcRecipientsInOneSmtpMessage(string? html)
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
            ["team@example.com"], "OCE mail", "Alert payload", html), timeout.Token);
        var (recipients, data) = await received;
        CollectionAssert.AreEquivalent(new[] { "alice@example.com", "bob@example.com", "team@example.com" }, recipients.ToArray());
        var parts = data.Split("\r\n\r\n", 2);
        Assert.Contains("To: alice@example.com, bob@example.com", parts[0]);
        Assert.Contains("Cc: team@example.com", parts[0]);
        Assert.IsTrue(parts[0].Split("\r\n").Any(line => line.StartsWith("Message-ID: <", StringComparison.OrdinalIgnoreCase)),
            "Every SMTP message must include a Message-ID so overlapping To/CC deliveries can be deduplicated.");
        if (html == null)
        {
            Assert.Contains("Content-Transfer-Encoding: base64", parts[0]);
            Assert.AreEqual("Alert payload", Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])));
        }
        else
        {
            Assert.Contains("multipart/alternative", parts[0]);
            Assert.Contains("text/plain", parts[1]);
            Assert.Contains("text/html", parts[1]);
            var unfolded = data.Replace("\r\n", "");
            Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("Alert payload")), unfolded);
            Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(html)), unfolded);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IncidentNotificationsKeepTheirIdsOnRetryAndThreadLifecycleReplies(bool onCall)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var settings = Options.Create(new EmailSettings
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            From = "sender@example.com", RequireTls = false
        });
        var sender = new SmtpEmailSender(settings);
        var adapter = new EmailChannelAdapter(sender, TestLocalization.For<EmailChannelAdapter>(), settings);
        var channel = new PushChannel { EmailRecipient = "alice@example.com", EmailCcRecipients = "team@example.com" };
        var incident = new Incident { Title = "Service unavailable", Level = "sev3" };

        PushDelivery Delivery(Incident source, IncidentTimelineEntry? entry = null) => new()
        {
            Incident = entry == null ? source : null,
            IncidentId = entry == null ? source.Id : null,
            IncidentEvent = entry, IncidentEventId = entry?.Id,
            ChannelId = onCall ? null : channel.Id, Channel = onCall ? null : channel,
            OnCallEmail = onCall ? new OnCallEmailSnapshot
            {
                Recipients = [new OnCallEmailRecipient { Email = "alice@example.com", DisplayName = "Alice" }],
                CcRecipients = "team@example.com"
            } : null
        };

        async Task<Dictionary<string, string>> Send(PushDelivery delivery)
        {
            var received = ReceiveMessage(listener, timeout.Token);
            Assert.IsTrue((await adapter.DeliverAsync(delivery, timeout.Token)).Succeeded);
            var (recipients, data) = await received;
            CollectionAssert.AreEquivalent(new[] { "alice@example.com", "team@example.com" }, recipients.ToArray());
            return data.Split("\r\n\r\n", 2)[0].Split("\r\n")
                .Where(line => line.Contains(": "))
                .ToDictionary(line => line[..line.IndexOf(": ", StringComparison.Ordinal)],
                    line => line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..], StringComparer.OrdinalIgnoreCase);
        }

        var original = await Send(Delivery(incident));
        var rootId = original["Message-ID"];
        StringAssert.Matches(rootId, new System.Text.RegularExpressions.Regex(@"^<alertcenter\.[a-z0-9.]+@example\.com>$"));
        Assert.IsFalse(original.ContainsKey("In-Reply-To"));
        Assert.IsFalse(original.ContainsKey("References"));
        Assert.AreEqual(rootId, (await Send(Delivery(incident)))["Message-ID"]);
        var ids = new HashSet<string> { rootId };
        foreach (var type in new[] { IncidentTimelineEntry.Mitigated, IncidentTimelineEntry.Resolved, IncidentTimelineEntry.Reopened })
        {
            var entry = new IncidentTimelineEntry { Incident = incident, IncidentId = incident.Id, Type = type, NewLevel = "sev2" };
            var reply = await Send(Delivery(incident, entry));
            Assert.IsTrue(ids.Add(reply["Message-ID"]), "Different notifications must not suppress each other as duplicates.");
            Assert.AreEqual(rootId, reply["In-Reply-To"]);
            Assert.AreEqual(rootId, reply["References"]);
            Assert.AreEqual(reply["Message-ID"], (await Send(Delivery(incident, entry)))["Message-ID"]);
        }
        Assert.AreNotEqual(rootId, (await Send(Delivery(new Incident { Title = incident.Title })))["Message-ID"]);
        if (!onCall)
        {
            channel.Id = Guid.NewGuid();
            Assert.AreNotEqual(rootId, (await Send(Delivery(incident)))["Message-ID"],
                "Separate channels must not suppress each other's messages.");
        }
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

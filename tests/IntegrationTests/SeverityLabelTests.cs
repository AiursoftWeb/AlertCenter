using System.Net;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class IncidentPagesTests
{
    [TestMethod]
    public async Task Sev0UsesConsistentEnglishLabelAcrossAlertPages()
    {
        var key = await CreateApplication();
        var applicationId = Guid.Parse(key.Split('.')[0]);
        using var report = await Report(key, Guid.NewGuid(), level: "sev0");
        Assert.AreEqual(HttpStatusCode.Created, report.StatusCode);
        Guid incidentId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            incidentId = (await db.Incidents.SingleAsync()).Id;
            var channel = new PushChannel { Name = "Severity label channel", Type = "inapp" };
            db.PushChannels.Add(channel);
            var adminUserId = await db.Users.Where(user => user.UserName == "admin").Select(user => user.Id).SingleAsync();
            db.PushChannelBindingUsers.Add(new PushChannelBindingUser { ChannelId = channel.Id, UserId = adminUserId });
            await db.SaveChangesAsync();
        }

        foreach (var (url, expectedLabels) in new[]
        {
            ("/Dashboard/Index", new[] { "sev0 Critical (1)", "</span> Critical ·" }),
            ("/Incidents/Index?level=sev0", new[] { "sev0 · Critical (P0)" }),
            ($"/Incidents/Details/{incidentId}", new[] { "sev0" }),
            ("/Push/Inbox?level=sev0", new[] { "sev0 · Critical (P0)" }),
            ($"/Push/Bindings/{applicationId}", new[] { "Critical (P0)" }),
            ("/Applications/GettingStarted", new[] { "<code>sev0</code>(Critical, highest priority)" })
        })
        {
            var html = WebUtility.HtmlDecode(await Http.GetStringAsync(url));
            foreach (var label in expectedLabels)
            {
                Assert.Contains(label, html, url);
            }
            Assert.DoesNotContain("DEADLY", html, url);
        }
    }
}

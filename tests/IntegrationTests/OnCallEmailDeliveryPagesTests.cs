using System.Net;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class IncidentPagesTests
{
    [TestMethod]
    public async Task SilentDeliveryPagesExplainSuppressionWithoutOfferingRetry()
    {
        var key = await CreateApplication();
        var groupId = await CreateOnCallTeam("Quiet OCE");
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            (await db.SourceApplications.SingleAsync()).OnCallGroupId = groupId;
            await db.SaveChangesAsync();
        }
        using var response = await Report(key, Guid.NewGuid(), level: "sev4");
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Guid deliveryId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var delivery = await db.PushDeliveries.SingleAsync();
            Assert.AreEqual(PushDelivery.Suppressed, delivery.Status);
            deliveryId = delivery.Id;
        }
        var html = await Http.GetStringAsync($"/Push/Delivery/{deliveryId}");
        Assert.Contains("Suppressed", html);
        Assert.Contains("This notification was suppressed and will not be sent.", html);
        Assert.DoesNotContain("/Push/Retry", html);
        var records = await Http.GetStringAsync("/Push/Records?status=suppressed");
        Assert.Contains(deliveryId.ToString(), records);
    }

    [TestMethod]
    public async Task BuiltInDeliveryPagesShowSnapshotAndProtectEmailAddressesAndRetries()
    {
        var key = await CreateApplication();
        var applicationId = Guid.Parse(key.Split('.')[0]);
        var groupId = await CreateOnCallTeam("OCE Production");
        Guid deliveryId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            (await db.SourceApplications.SingleAsync()).OnCallGroupId = groupId;
            var message = new Incident
            {
                ApplicationId = applicationId, ReportId = Guid.NewGuid(), Title = "OCE response test", Level = "sev0"
            };
            var delivery = new PushDelivery
            {
                Incident = message, Status = PushDelivery.Failed, Error = "SMTP test failure", Attempts = 1,
                OnCallEmail = new OnCallEmailSnapshot
                {
                    GroupId = groupId, GroupName = "OCE Production", NotificationAtUtc = DateTime.UtcNow,
                    CcRecipients = "private-team@example.com", StartsAtUtc = DateTime.UtcNow.AddHours(-1), EndsAtUtc = DateTime.UtcNow.AddHours(7),
                    Recipients = [new OnCallEmailRecipient { UserId = "original-oce", DisplayName = "Original OCE", Email = "private-oce@example.com" }]
                }
            };
            db.PushDeliveries.Add(delivery);
            await db.SaveChangesAsync();
            deliveryId = delivery.Id;
        }
        var url = $"/Push/Delivery/{deliveryId}";
        var admin = WebUtility.HtmlDecode(await Http.GetStringAsync(url));
        Assert.Contains("Built-in On-call Email", admin);
        Assert.Contains("private-oce@example.com", admin);
        Assert.Contains("private-team@example.com", admin);
        Assert.Contains("/Push/Retry", admin);
        Assert.DoesNotContain("action=\"/Push/Edit\"", admin);
        var bindings = WebUtility.HtmlDecode(await Http.GetStringAsync($"/Push/Bindings/{applicationId}"));
        Assert.Contains("Auto-enabled · Cannot be disabled individually", bindings);
        Assert.DoesNotContain("private-oce@example.com", bindings);
        Assert.Contains("OCE Production", bindings);
        Assert.Contains("OCE response test", await Http.GetStringAsync($"/Push/Records?channelId={Guid.Empty}"));

        var (email, _) = await RegisterAndLoginAsync();
        var forbidden = await Http.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.Found, forbidden.StatusCode);
        Assert.Contains("Code403", forbidden.Headers.Location!.OriginalString);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var user = await db.Users.SingleAsync(item => item.Email == email);
            db.ApplicationUserAccesses.Add(new ApplicationUserAccess { CanViewIncidents = true, ApplicationId = applicationId, UserId = user.Id });
            await db.SaveChangesAsync();
        }
        Guid incidentId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            incidentId = (await db.Incidents.SingleAsync()).Id;
        }
        var incidentPage = WebUtility.HtmlDecode(await Http.GetStringAsync($"/Incidents/Details/{incidentId}"));
        Assert.Contains("Current On-Call OCE", incidentPage);
        Assert.Contains("OCE at incident occurrence", incidentPage);
        Assert.Contains("OCE Production · <span data-utc-time=", incidentPage);
        Assert.Contains("Original OCE", incidentPage);
        Assert.DoesNotContain("private-oce@example.com", incidentPage);
        var viewer = WebUtility.HtmlDecode(await Http.GetStringAsync(url));
        Assert.Contains("Original OCE", viewer);
        Assert.DoesNotContain("private-oce@example.com", viewer);
        Assert.DoesNotContain("private-team@example.com", viewer);
        Assert.DoesNotContain("action=\"/Push/Retry\"", viewer);
        Assert.Contains("OCE response test", await Http.GetStringAsync($"/Push/Records?channelId={Guid.Empty}"));
        using var deniedRetry = await PostForm("/Push/Retry", new() { ["id"] = deliveryId.ToString() }, "/Manage/ChangePassword");
        Assert.AreEqual(HttpStatusCode.Found, deniedRetry.StatusCode);
        Assert.Contains("Code403", deniedRetry.Headers.Location!.OriginalString);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            (await db.ApplicationUserAccesses.SingleAsync()).CanManageApplication = true;
            await db.SaveChangesAsync();
        }
        var manager = WebUtility.HtmlDecode(await Http.GetStringAsync(url));
        Assert.Contains("/Push/Retry", manager);
        Assert.DoesNotContain("private-oce@example.com", manager);
        Assert.DoesNotContain("private-team@example.com", manager);
        using var missingToken = await PostForm("/Push/Retry", new() { ["id"] = deliveryId.ToString() }, includeToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingToken.StatusCode);
        AssertRedirect(await PostForm("/Push/Retry", new() { ["id"] = deliveryId.ToString() }, url), url);
        using var scopeAfter = Server!.Services.CreateScope();
        var dbAfter = scopeAfter.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var snapshot = await dbAfter.OnCallEmailSnapshots.Include(item => item.Recipients).SingleAsync();
        Assert.AreEqual("private-oce@example.com", snapshot.Recipients.Single().Email);
        Assert.AreEqual("private-team@example.com", snapshot.CcRecipients);
    }

    [TestMethod]
    public async Task ConcurrentReportRetriesQueueOnlyOneOceSnapshotAndPreserveCustomRouting()
    {
        var key = await CreateApplication();
        var applicationId = Guid.Parse(key.Split('.')[0]);
        var groupId = await CreateOnCallTeam("Concurrent team");
        Guid customId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var user = await db.Users.FirstAsync();
            (await db.SourceApplications.SingleAsync()).OnCallGroupId = groupId;
            db.OnCallShifts.Add(new OnCallShift
            {
                GroupId = groupId, StartsAtUtc = DateTime.UtcNow.AddHours(-1), EndsAtUtc = DateTime.UtcNow.AddHours(7),
                Members = [new OnCallShiftMember { UserId = user.Id }]
            });
            var channel = new PushChannel { Name = "Additional", Type = "email", EmailRecipient = "extra@example.com" };
            customId = channel.Id;
            db.ApplicationPushChannels.Add(new ApplicationPushChannel { ApplicationId = applicationId, Channel = channel });
            await db.SaveChangesAsync();
        }
        var reportId = Guid.NewGuid();
        var retries = await Task.WhenAll(Report(key, reportId), Report(key, reportId));
        CollectionAssert.AreEquivalent(new[] { HttpStatusCode.Created, HttpStatusCode.Accepted }, retries.Select(response => response.StatusCode).ToArray());
        foreach (var response in retries) response.Dispose();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual(1, await db.Incidents.CountAsync());
            Assert.AreEqual(1, await db.OnCallEmailSnapshots.CountAsync());
            Assert.AreEqual(2, await db.PushDeliveries.CountAsync());
            Assert.AreEqual(1, await db.PushDeliveries.CountAsync(delivery => delivery.ChannelId == customId));
        }
        var distinctReports = await Task.WhenAll(Report(key, Guid.NewGuid()), Report(key, Guid.NewGuid()));
        Assert.IsTrue(distinctReports.All(response => response.StatusCode == HttpStatusCode.Created));
        foreach (var response in distinctReports) response.Dispose();
        using var finalScope = Server!.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(3, await finalDb.Incidents.CountAsync());
        Assert.AreEqual(3, await finalDb.OnCallEmailSnapshots.CountAsync());
        Assert.AreEqual(6, await finalDb.PushDeliveries.CountAsync());
    }

    [TestMethod]
    public async Task ReportWithoutCoverageHasVisibleFailureAndCannotRetryToNewOce()
    {
        var key = await CreateApplication();
        var id = Guid.Parse(key.Split('.')[0]);
        var team = await CreateOnCallTeam("Unstaffed");
        AssertRedirect(await PostForm("/Applications/SaveOnCall", new()
        {
            ["ApplicationId"] = id.ToString(), ["OnCallGroupId"] = team.ToString()
        }, $"/Applications/OnCall/{id}"), $"/Applications/OnCall/{id}");
        using var report = await Report(key, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Created, report.StatusCode);
        Guid deliveryId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            deliveryId = (await db.PushDeliveries.SingleAsync()).Id;
        }
        var url = $"/Push/Delivery/{deliveryId}";
        var html = WebUtility.HtmlDecode(await Http.GetStringAsync(url));
        Assert.Contains("no schedule coverage", html);
        Assert.Contains("disabled", html);
        using var retry = await PostForm("/Push/Retry", new() { ["id"] = deliveryId.ToString() }, url);
        Assert.AreEqual(HttpStatusCode.Conflict, retry.StatusCode);
    }
}

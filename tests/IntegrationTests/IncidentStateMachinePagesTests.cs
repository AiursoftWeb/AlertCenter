using System.Net;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class AlertDemoTests
{
    private async Task<Dictionary<string, string>> IncidentForm(Guid id, string? note = null, string? level = null)
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var incident = await db.Incidents.SingleAsync(item => item.Id == id);
        var values = new Dictionary<string, string> { ["IncidentId"] = id.ToString(), ["Version"] = incident.Version.ToString() };
        if (note != null) values["Note"] = note;
        if (level != null) values["Level"] = level;
        return values;
    }

    [TestMethod]
    public async Task IncidentPagesEnforceStateControlsReasonsAntiforgeryAndReopenDeliveryHistory()
    {
        var key = await CreateApplication();
        var applicationId = Guid.Parse(key.Split('.')[0]);
        using var report = await Report(key, Guid.NewGuid());
        Guid id;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            id = (await db.Incidents.SingleAsync()).Id;
        }
        var url = $"/Incidents/Details/{id}";
        var page = await Http.GetStringAsync(url);
        Assert.Contains("action=\"/Incidents/Acknowledge\"", page);
        Assert.DoesNotContain("action=\"/Incidents/Resolve\"", page);
        Assert.DoesNotContain("action=\"/Incidents/Mitigate\"", page);
        Assert.Contains("name=\"version\"", page);
        using var noToken = await PostForm("/Incidents/Acknowledge", await IncidentForm(id), includeToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, noToken.StatusCode);
        var stale = await IncidentForm(id);
        using var missingVersion = await PostForm("/Incidents/Acknowledge", new() { ["IncidentId"] = id.ToString() }, url);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingVersion.StatusCode);
        AssertRedirect(await PostForm("/Incidents/Acknowledge", await IncidentForm(id), url), url);
        using var repeated = await PostForm("/Incidents/Acknowledge", stale, url);
        Assert.AreEqual(HttpStatusCode.Conflict, repeated.StatusCode);
        page = await Http.GetStringAsync(url);
        Assert.Contains("action=\"/Incidents/TakeOwnership\"", page);
        Assert.Contains("action=\"/Incidents/Mitigate\"", page);
        Assert.DoesNotContain("action=\"/Incidents/Resolve\"", page);
        using var noReason = await PostForm("/Incidents/Mitigate", await IncidentForm(id, "   "), url);
        Assert.AreEqual(HttpStatusCode.BadRequest, noReason.StatusCode);
        using var invalidLevel = await PostForm("/Incidents/ChangeLevel", await IncidentForm(id, "Reason", "sev5"), url);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidLevel.StatusCode);
        AssertRedirect(await PostForm("/Incidents/ChangeLevel", await IncidentForm(id, "<script>unsafe</script>", "sev0"), url), url);
        page = await Http.GetStringAsync(url);
        Assert.Contains("&lt;script&gt;unsafe&lt;/script&gt;", page);
        Assert.DoesNotContain("<script>unsafe</script>", page);
        AssertRedirect(await PostForm("/Incidents/Mitigate", await IncidentForm(id, "Traffic shifted"), url), url);
        page = await Http.GetStringAsync(url);
        Assert.Contains("action=\"/Incidents/Resolve\"", page);
        Assert.Contains("action=\"/Incidents/Reopen\"", page);
        Assert.DoesNotContain("action=\"/Incidents/TakeOwnership\"", page);
        AssertRedirect(await PostForm("/Incidents/Resolve", await IncidentForm(id, "Root cause fixed"), url), url);
        page = await Http.GetStringAsync(url);
        Assert.DoesNotContain("action=\"/Incidents/ChangeLevel\"", page);
        Assert.Contains("action=\"/Incidents/Reopen\"", page);
        var team = await CreateOnCallTeam("Reopened team");
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            (await db.SourceApplications.SingleAsync()).OnCallGroupId = team;
            await db.SaveChangesAsync();
        }
        AssertRedirect(await PostForm("/Incidents/Reopen", await IncidentForm(id, "Failure returned"), url), url);
        Guid deliveryId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var incident = await db.Incidents.SingleAsync();
            Assert.AreEqual(Incident.New, incident.Status);
            Assert.IsNull(incident.OwnerId);
            Assert.AreEqual("sev0", incident.AlertLevel);
            Assert.AreEqual(1, await db.ReportedMessages.CountAsync());
            deliveryId = (await db.PushDeliveries.SingleAsync()).Id;
        }
        var deliveryPage = WebUtility.HtmlDecode(await Http.GetStringAsync($"/Push/Delivery/{deliveryId}"));
        Assert.Contains("事故重开", deliveryPage);
        Assert.Contains("Reopened team", deliveryPage);
        Assert.Contains("Failure returned", deliveryPage);
        Assert.Contains("事故重开", WebUtility.HtmlDecode(await Http.GetStringAsync("/Push/Records")));
        using var invalidRetry = await PostForm("/Push/Retry", new() { ["id"] = deliveryId.ToString() }, $"/Push/Delivery/{deliveryId}");
        Assert.AreEqual(HttpStatusCode.Conflict, invalidRetry.StatusCode);
        foreach (var state in new[] { "new", "acked", "mitigated", "resolved" })
        {
            var list = await Http.GetStringAsync($"/Incidents/Index?status={state}");
            Assert.AreEqual(state == "new", list.Contains("Order timeout"));
        }
        AssertRedirect(await PostForm("/Alerts/DeleteApplication", new()
        {
            ["id"] = applicationId.ToString(), ["confirmationName"] = "Demo Orders"
        }, "/Alerts/Applications"), "/Alerts/Applications");
        using var finalScope = Server!.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(0, await finalDb.PushDeliveries.CountAsync());
        Assert.AreEqual(0, await finalDb.OnCallEmailSnapshots.CountAsync());
    }

    [TestMethod]
    public async Task CompetingAcknowledgementsCreateExactlyOneOwnerAndAudit()
    {
        var key = await CreateApplication();
        using var report = await Report(key, Guid.NewGuid());
        Guid id;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            id = (await db.Incidents.SingleAsync()).Id;
        }
        var url = $"/Incidents/Details/{id}";
        var request = await IncidentForm(id);
        request["__RequestVerificationToken"] = await GetAntiCsrfToken(url);
        var outcomes = await Task.WhenAll(PostForm("/Incidents/Acknowledge", new(request), url),
            PostForm("/Incidents/Acknowledge", new(request), url));
        CollectionAssert.AreEquivalent(new[] { HttpStatusCode.Found, HttpStatusCode.Conflict }, outcomes.Select(response => response.StatusCode).ToArray());
        foreach (var response in outcomes) response.Dispose();
        using var finalScope = Server!.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var incident = await finalDb.Incidents.SingleAsync();
        Assert.AreEqual(Incident.Acked, incident.Status);
        Assert.IsNotNull(incident.OwnerId);
        Assert.AreEqual(1, await finalDb.IncidentTimelineEntries.CountAsync(entry => entry.Type == IncidentTimelineEntry.Acked));
    }

    [TestMethod]
    public async Task OcesAndOwnersSeeOnlyRelevantIncidentsWithoutApplicationManagementRights()
    {
        var key = await CreateApplication();
        var applicationId = Guid.Parse(key.Split('.')[0]);
        using var report = await Report(key, Guid.NewGuid());
        var (email, _) = await RegisterAndLoginAsync();
        Guid incidentId;
        string userId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var user = await db.Users.SingleAsync(item => item.Email == email);
            userId = user.Id;
            var incident = await db.Incidents.SingleAsync();
            incidentId = incident.Id;
            var group = new OnCallGroup { Name = "Current OCE" };
            db.OnCallShifts.Add(new OnCallShift
            {
                Group = group, StartsAtUtc = DateTime.UtcNow.AddHours(-1), EndsAtUtc = DateTime.UtcNow.AddHours(7),
                Members = [new OnCallShiftMember { UserId = user.Id }]
            });
            (await db.SourceApplications.SingleAsync()).OnCallGroup = group;
            await db.SaveChangesAsync();
        }
        var url = $"/Incidents/Details/{incidentId}";
        Assert.Contains("Order timeout", await Http.GetStringAsync("/Incidents/Index"));
        AssertRedirect(await PostForm("/Incidents/Acknowledge", await IncidentForm(incidentId), url), url);
        using var deniedManage = await Http.GetAsync($"/Alerts/OnCall/{applicationId}");
        Assert.AreEqual(HttpStatusCode.Found, deniedManage.StatusCode);
        Assert.Contains("Code403", deniedManage.Headers.Location!.OriginalString);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            (await db.OnCallShifts.SingleAsync()).EndsAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        Assert.AreEqual(HttpStatusCode.OK, (await Http.GetAsync(url)).StatusCode);
        AssertRedirect(await PostForm("/Incidents/Mitigate", await IncidentForm(incidentId, "Owner mitigation"), url), url);
        await RegisterAndLoginAsync();
        using var denied = await Http.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.Found, denied.StatusCode);
        Assert.Contains("Code403", denied.Headers.Location!.OriginalString);
        using var forbidden = await PostForm("/Incidents/Resolve", await IncidentForm(incidentId, "Not allowed"), "/Manage/ChangePassword");
        Assert.AreEqual(HttpStatusCode.Found, forbidden.StatusCode);
        Assert.Contains("Code403", forbidden.Headers.Location!.OriginalString);
        using var scopeAfter = Server!.Services.CreateScope();
        var dbAfter = scopeAfter.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(userId, (await dbAfter.Incidents.SingleAsync()).OwnerId);
    }
}

using System.Net;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class IncidentPagesTests
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
    [DataRow("sev0", true)]
    [DataRow("sev1", true)]
    [DataRow("sev2", true)]
    [DataRow("sev3", true)]
    [DataRow("sev4", true)]
    [DataRow("sev2", false)]
    [DataRow("sev3", false)]
    [DataRow("sev4", false)]
    public async Task MitigationCanResolveLowerSeverityIncidentsWithTheSameReason(string level, bool resolveImmediately)
    {
        var key = await CreateApplication();
        using var report = await Report(key, Guid.NewGuid());
        Guid id;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var incident = await db.Incidents.SingleAsync();
            incident.Level = level;
            await db.SaveChangesAsync();
            id = incident.Id;
        }
        var url = $"/Incidents/Details/{id}";
        AssertRedirect(await PostForm("/Incidents/Acknowledge", await IncidentForm(id), url), url);
        var eligible = level is "sev2" or "sev3" or "sev4";
        var page = await Http.GetStringAsync(url);
        Assert.AreEqual(eligible, page.Contains("id=\"resolve-immediately\""));
        Assert.DoesNotContain("checked", page.Split("id=\"resolve-immediately\"").Last().Split('>').First());
        var form = await IncidentForm(id, "  Fixed the underlying cause.  ");
        form["ResolveImmediately"] = resolveImmediately.ToString();
        using var response = await PostForm("/Incidents/Mitigate", form, url);
        if (!eligible)
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        else
        {
            AssertRedirect(response, url);
            using var stale = await PostForm("/Incidents/Mitigate", form, url);
            Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
        }
        using var after = Server!.Services.CreateScope();
        var database = after.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var saved = await database.Incidents.SingleAsync();
        var events = await database.IncidentTimelineEntries.Where(entry => entry.Type == IncidentTimelineEntry.Mitigated || entry.Type == IncidentTimelineEntry.Resolved)
            .OrderBy(entry => entry.CreatedAtUtc).ToListAsync();
        Assert.AreEqual(!eligible ? Incident.New : resolveImmediately ? Incident.Resolved : Incident.Mitigated, saved.Status);
        Assert.AreEqual(!eligible ? 0 : resolveImmediately ? 2 : 1, events.Count);
        if (!eligible) return;
        Assert.AreEqual(IncidentTimelineEntry.Mitigated, events[0].Type);
        Assert.AreEqual(Incident.New, events[0].PreviousStatus);
        Assert.AreEqual(Incident.Mitigated, events[0].NewStatus);
        Assert.AreEqual("Fixed the underlying cause.", events[0].Note);
        Assert.IsNotNull(saved.MitigatedAtUtc);
        if (resolveImmediately)
        {
            Assert.AreEqual(IncidentTimelineEntry.Resolved, events[1].Type);
            Assert.AreEqual(Incident.Mitigated, events[1].PreviousStatus);
            Assert.AreEqual(Incident.Resolved, events[1].NewStatus);
            Assert.AreEqual(events[0].Note, events[1].Note);
            Assert.AreEqual(events[0].ActorId, events[1].ActorId);
            Assert.IsNotNull(saved.ResolvedAtUtc);
        }
        else Assert.IsNull(saved.ResolvedAtUtc);
    }

    [TestMethod]
    public async Task IncidentPagesEnforceStateControlsAndLifecycleDeliveryHistory()
    {
        var key = await CreateApplication();
        var applicationId = Guid.Parse(key.Split('.')[0]);
        var team = await CreateOnCallTeam("Lifecycle team");
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            (await db.SourceApplications.SingleAsync()).OnCallGroupId = team;
            await db.SaveChangesAsync();
        }
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
        Assert.Contains("action=\"/Incidents/Acknowledge\"", page);
        Assert.DoesNotContain("action=\"/Incidents/TakeOwnership\"", page);
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
        AssertRedirect(await PostForm("/Incidents/Reopen", await IncidentForm(id, "Failure returned"), url), url);
        Guid deliveryId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var incident = await db.Incidents.SingleAsync();
            Assert.AreEqual(Incident.New, incident.Status);
            Assert.IsNotNull(incident.OwnerId);
            Assert.IsTrue(incident.IsAcknowledged);
            Assert.AreEqual("sev0", incident.Level);
            Assert.AreEqual(1, await db.Incidents.CountAsync());
            Assert.AreEqual(3, await db.PushDeliveries.CountAsync());
            deliveryId = await db.PushDeliveries.Where(d => d.IncidentEvent != null && d.IncidentEvent.Type == IncidentTimelineEntry.Resolved).Select(d => d.Id).SingleAsync();
        }
        var deliveryPage = WebUtility.HtmlDecode(await Http.GetStringAsync($"/Push/Delivery/{deliveryId}"));
        Assert.Contains("Incident resolved", deliveryPage);
        Assert.Contains("Lifecycle team", deliveryPage);
        Assert.Contains("Root cause fixed", deliveryPage);
        var records = WebUtility.HtmlDecode(await Http.GetStringAsync("/Push/Records"));
        Assert.Contains("Incident mitigated", records);
        Assert.Contains("Incident resolved", records);
        Assert.DoesNotContain("Incident reopened:", records);
        foreach (var state in new[] { "new", "acked", "mitigated", "resolved" })
        {
            var list = await Http.GetStringAsync($"/Incidents/Index?status={state}");
            Assert.AreEqual(state is "new" or "acked", list.Contains("Order timeout"));
        }
        AssertRedirect(await PostForm("/Applications/DeleteApplication", new()
        {
            ["id"] = applicationId.ToString(), ["confirmationName"] = "Demo Orders"
        }, "/Applications/Applications"), "/Applications/Applications");
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
        Assert.AreEqual(Incident.New, incident.Status);
        Assert.IsTrue(incident.IsAcknowledged);
        Assert.IsNotNull(incident.OwnerId);
        Assert.AreEqual(1, await finalDb.IncidentTimelineEntries.CountAsync(entry => entry.Type == IncidentTimelineEntry.Acked));
    }

    [TestMethod]
    public async Task OnCallAssignmentsRequireExplicitApplicationPermissions()
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
        AssertForbidden(await Http.GetAsync(url));
        AssertForbidden(await PostForm("/Incidents/Acknowledge", await IncidentForm(incidentId), "/Manage/ChangePassword"));
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.ApplicationUserAccesses.Add(new ApplicationUserAccess { ApplicationId = applicationId, UserId = userId, CanViewIncidents = true, CanUpdateIncidents = true });
            await db.SaveChangesAsync();
        }
        Assert.Contains("Order timeout", await Http.GetStringAsync("/Incidents/Index"));
        AssertRedirect(await PostForm("/Incidents/Acknowledge", await IncidentForm(incidentId), url), url);
        using var deniedManage = await Http.GetAsync($"/Applications/OnCall/{applicationId}");
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
    [TestMethod]
    [DataRow(Incident.New)]
    [DataRow(Incident.Mitigated)]
    [DataRow(Incident.Resolved)]
    public async Task AcknowledgementCanBeRepeatedAndFilteredIndependentlyOfProgress(string state)
    {
        var key = await CreateApplication();
        using var report = await Report(key, Guid.NewGuid(), title: "Repeatable acknowledgement");
        Guid id;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var incident = await db.Incidents.SingleAsync();
            incident.Status = state;
            await db.SaveChangesAsync();
            id = incident.Id;
        }
        var url = $"/Incidents/Details/{id}";
        foreach (var action in new[] { "Acknowledge", "Acknowledge", "TakeOwnership" })
        {
            var page = await Http.GetStringAsync(url);
            Assert.Contains("action=\"/Incidents/Acknowledge\"", page);
            AssertRedirect(await PostForm($"/Incidents/{action}", await IncidentForm(id), url), url);
        }
        Assert.Contains("data-acknowledged=\"true\"", await Http.GetStringAsync(url));
        Assert.Contains("Repeatable acknowledgement", await Http.GetStringAsync($"/Incidents?status={state}&acknowledged=true"));
        Assert.DoesNotContain("Repeatable acknowledgement", await Http.GetStringAsync($"/Incidents?status={state}&acknowledged=false"));
        Assert.Contains("Repeatable acknowledgement", await Http.GetStringAsync("/Incidents?status=acked"));
        using var finalScope = Server!.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var saved = await finalDb.Incidents.SingleAsync();
        Assert.AreEqual(state, saved.Status);
        Assert.IsTrue(saved.IsAcknowledged);
        Assert.IsNotNull(saved.OwnerId);
        Assert.AreEqual(3, await finalDb.IncidentTimelineEntries.CountAsync(entry => entry.Type == IncidentTimelineEntry.Acked));
        await using var services = ReportingClient(key);
        var client = services.GetRequiredService<Aiursoft.AlertCenter.SDK.AlertCenterApiClient>();
        var details = (await client.GetIncidentAsync(id)).Incident;
        Assert.AreEqual(state, details.Status);
        Assert.IsTrue(details.IsAcknowledged);
        Assert.AreEqual(saved.OwnerId, details.OwnerId);
        Assert.AreEqual(new DateTimeOffset(DateTime.SpecifyKind(saved.AcknowledgedAtUtc!.Value, DateTimeKind.Utc)), details.AcknowledgedAtUtc);
    }

}

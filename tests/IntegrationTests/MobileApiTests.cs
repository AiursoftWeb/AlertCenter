using System.Net;
using System.Net.Http.Headers;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.SDK.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class IncidentPagesTests
{
    private async Task<string> MobileLogin(string email = "admin@default.com", string password = "Admin@123456!")
    {
        using var response = await Http.PostAsJsonAsync("/api/v1/auth/local/login", new LocalLoginRequest
        { EmailOrUserName = email, Password = password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LocalAuthenticationResponse>())!.AccessToken;
    }

    private async Task<T> MobileCall<T>(string token, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await Http.SendAsync(request);
        var result = await response.Content.ReadFromJsonAsync<T>();
        Assert.IsNotNull(result);
        return result;
    }

    private async Task<Incident> MobileIncident(string level = "sev3")
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var admin = await db.Users.SingleAsync(item => item.Email == "admin@default.com");
        var incident = new Incident
        {
            Application = new SourceApplication { Name = "Mobile service", OwnerId = admin.Id },
            Title = "Mobile incident", Level = level, OccurredAtUtc = DateTime.UtcNow
        };
        db.Incidents.Add(incident);
        await db.SaveChangesAsync();
        return incident;
    }

    [TestMethod]
    public async Task MobileRequiresBearerAndKeepsConfigurationPublicWithoutSecrets()
    {
        await LoginAsAdmin();
        using var anonymous = await Http.GetAsync("/api/v1/mobile/incidents");
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.AreEqual(Code.WrongKey, (await anonymous.Content.ReadFromJsonAsync<AiurResponse>())!.Code);
        using var invalid = new HttpRequestMessage(HttpMethod.Get, "/api/v1/mobile/me");
        invalid.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "local.invalid");
        using var denied = await Http.SendAsync(invalid);
        Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var wrongPassword = await Http.PostAsJsonAsync("/api/v1/auth/local/login", new LocalLoginRequest
        { EmailOrUserName = "admin@default.com", Password = "WrongPassword123!" });
        Assert.AreEqual(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        var configuration = await Http.GetFromJsonAsync<MobileConfigurationResponse>("/api/v1/config");
        Assert.IsNotNull(configuration);
        Assert.AreEqual("Local", configuration.AuthenticationMode);
        Assert.AreEqual(string.Empty, configuration.Authority);
        Assert.AreEqual(string.Empty, configuration.ClientId);
        var token = await MobileLogin();
        var me = await MobileCall<MobileUserResponse>(token, HttpMethod.Get, "/api/v1/mobile/me");
        Assert.AreEqual(Code.ResultShown, me.Code);
        Assert.IsFalse(string.IsNullOrWhiteSpace(me.DisplayName));
    }

    [TestMethod]
    public async Task MobileFiltersMineSeverityAndPaginationAndHidesOtherApplications()
    {
        var incident = await MobileIncident();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var admin = await db.Users.SingleAsync(item => item.Email == "admin@default.com");
            for (var i = 0; i < 24; i++) db.Incidents.Add(new Incident
            {
                ApplicationId = incident.ApplicationId, Title = $"Queue {i:D2}", Level = "sev3",
                OwnerId = admin.Id, OccurredAtUtc = DateTime.UtcNow
            });
            db.Incidents.Add(new Incident { ApplicationId = incident.ApplicationId, Title = "Silent", Level = "sev4" });
            db.Incidents.Add(new Incident { Application = new SourceApplication { Name = "Private" }, Title = "Hidden", Level = "sev0" });
            await db.SaveChangesAsync();
        }
        var token = await MobileLogin();
        var page1 = await MobileCall<MobileIncidentsResponse>(token, HttpMethod.Get, "/api/v1/mobile/incidents");
        Assert.AreEqual(20, page1.Incidents.Count);
        Assert.IsTrue(page1.HasNextPage);
        Assert.IsTrue(page1.Incidents.All(item => item.Level != "sev4" && item.Title != "Hidden"));
        var page2 = await MobileCall<MobileIncidentsResponse>(token, HttpMethod.Get, "/api/v1/mobile/incidents?page=2");
        Assert.AreEqual(5, page2.Incidents.Count);
        Assert.IsFalse(page2.HasNextPage);
        Assert.AreEqual(25, page1.Incidents.Concat(page2.Incidents).Select(item => item.Id).Distinct().Count());
        var mine = await MobileCall<MobileIncidentsResponse>(token, HttpMethod.Get, "/api/v1/mobile/incidents?onlyMine=true&keyword=Queue");
        Assert.AreEqual(20, mine.Incidents.Count);
        var silent = await MobileCall<MobileIncidentsResponse>(token, HttpMethod.Get, "/api/v1/mobile/incidents?level=sev4");
        Assert.AreEqual("Silent", silent.Incidents.Single().Title);
        var bad = await MobileCall<AiurResponse>(token, HttpMethod.Get, "/api/v1/mobile/incidents?level=sev9");
        Assert.AreEqual(Code.InvalidInput, bad.Code);
    }

    [TestMethod]
    public async Task MobileWorkflowPreservesVersionsAcknowledgementOwnerAndTimeline()
    {
        var incident = await MobileIncident();
        var token = await MobileLogin();
        var path = $"/api/v1/mobile/incidents/{incident.Id}";
        var before = await MobileCall<MobileIncidentResponse>(token, HttpMethod.Get, path);
        Assert.IsTrue(before.Incident.CanManage);
        var premature = await MobileCall<AiurResponse>(token, HttpMethod.Post, path + "/mitigate",
            new MobileIncidentAction { Version = before.Incident.Version, Note = "Recovered" });
        Assert.AreEqual(Code.InvalidInput, premature.Code);
        var ack = await MobileCall<AiurResponse>(token, HttpMethod.Post, path + "/acknowledge",
            new MobileIncidentAction { Version = before.Incident.Version });
        Assert.AreEqual(Code.JobDone, ack.Code);
        var after = await MobileCall<MobileIncidentResponse>(token, HttpMethod.Get, path);
        Assert.AreEqual("new", after.Incident.Status);
        Assert.IsNotNull(after.Incident.AcknowledgedAtUtc);
        Assert.IsFalse(string.IsNullOrEmpty(after.Incident.OwnerName));
        Assert.AreNotEqual(before.Incident.Version, after.Incident.Version);
        var stale = await MobileCall<AiurResponse>(token, HttpMethod.Post, path + "/resolve",
            new MobileIncidentAction { Version = before.Incident.Version, Note = "Resolved" });
        Assert.AreEqual(Code.Conflict, stale.Code);
        foreach (var action in new[] { "severity", "take-ownership", "mitigate", "resolve", "reopen" })
        {
            after = await MobileCall<MobileIncidentResponse>(token, HttpMethod.Get, path);
            var changed = await MobileCall<AiurResponse>(token, HttpMethod.Post, path + "/" + action,
                new MobileIncidentAction { Version = after.Incident.Version, Note = "Reviewed by engineer", Level = action == "severity" ? "sev2" : null });
            Assert.AreEqual(Code.JobDone, changed.Code, action);
        }
        var comment = await MobileCall<AiurResponse>(token, HttpMethod.Post, path + "/comments", new MobileCommentRequest { Markdown = "**Verified** recovery" });
        Assert.AreEqual(Code.JobDone, comment.Code);
        after = await MobileCall<MobileIncidentResponse>(token, HttpMethod.Get, path);
        Assert.AreEqual("new", after.Incident.Status);
        Assert.AreEqual("sev2", after.Incident.Level);
        Assert.IsNotNull(after.Incident.AcknowledgedAtUtc);
        Assert.IsNull(after.Incident.MitigatedAtUtc);
        Assert.IsNull(after.Incident.ResolvedAtUtc);
        Assert.IsTrue(after.Activities.Any(item => item.Type == "comment" && item.Note == "**Verified** recovery"));
        CollectionAssert.IsSubsetOf(new[] { "acked", "level_changed", "mitigated", "resolved", "reopened" }, after.Activities.Select(item => item.Type).ToArray());
    }

    [TestMethod]
    public async Task MobileReadOnlyAndAssignmentDoNotGrantIncidentHandling()
    {
        var incident = await MobileIncident();
        string readerId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = new User { UserName = "reader", Email = "reader@example.com", DisplayName = "Reader" };
            Assert.IsTrue((await manager.CreateAsync(user, "ReaderPassword123!")).Succeeded);
            readerId = user.Id;
            db.ApplicationUserAccesses.Add(new ApplicationUserAccess { ApplicationId = incident.ApplicationId, UserId = user.Id, CanViewIncidents = true });
            var row = await db.Incidents.SingleAsync(item => item.Id == incident.Id);
            row.OwnerId = user.Id;
            await db.SaveChangesAsync();
        }
        var token = await MobileLogin("reader@example.com", "ReaderPassword123!");
        var path = $"/api/v1/mobile/incidents/{incident.Id}";
        var detail = await MobileCall<MobileIncidentResponse>(token, HttpMethod.Get, path);
        Assert.AreEqual(Code.ResultShown, detail.Code);
        Assert.IsFalse(detail.Incident.CanManage);
        var forbidden = await MobileCall<AiurResponse>(token, HttpMethod.Post, path + "/acknowledge", new MobileIncidentAction { Version = detail.Incident.Version });
        Assert.AreEqual(Code.Unauthorized, forbidden.Code);
        var comment = await MobileCall<AiurResponse>(token, HttpMethod.Post, path + "/comments", new MobileCommentRequest { Markdown = "Cannot write" });
        Assert.AreEqual(Code.Unauthorized, comment.Code);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.ApplicationUserAccesses.Remove(await db.ApplicationUserAccesses.SingleAsync(item => item.UserId == readerId));
            await db.SaveChangesAsync();
        }
        var denied = await MobileCall<AiurResponse>(token, HttpMethod.Get, path);
        Assert.AreEqual(Code.Unauthorized, denied.Code);
        var mine = await MobileCall<MobileIncidentsResponse>(token, HttpMethod.Get, "/api/v1/mobile/incidents?onlyMine=true");
        Assert.AreEqual(0, mine.Incidents.Count);
    }

    [TestMethod]
    public async Task MobileCreatesIncidentsWithoutGrantingViewAndRejectsDisabledApplications()
    {
        var incident = await MobileIncident();
        string creatorId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = new User { UserName = "creator", Email = "creator@example.com", DisplayName = "Creator" };
            Assert.IsTrue((await manager.CreateAsync(user, "CreatorPassword123!")).Succeeded);
            creatorId = user.Id;
            db.ApplicationUserAccesses.Add(new ApplicationUserAccess { ApplicationId = incident.ApplicationId, UserId = creatorId, CanCreateIncidents = true });
            await db.SaveChangesAsync();
        }
        var token = await MobileLogin("creator@example.com", "CreatorPassword123!");
        var apps = await MobileCall<MobileApplicationsResponse>(token, HttpMethod.Get, "/api/v1/mobile/applications");
        Assert.IsTrue(apps.Applications.Single().CanCreate);
        Assert.IsFalse(apps.Applications.Single().CanView);
        var request = new MobileCreateIncidentRequest { ApplicationId = incident.ApplicationId, Title = "  Created on phone  ", Level = "sev4" };
        var created = await MobileCall<MobileCreatedIncidentResponse>(token, HttpMethod.Post, "/api/v1/mobile/incidents", request);
        Assert.AreEqual(Code.JobDone, created.Code);
        Assert.IsFalse(created.CanView);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual("Created on phone", (await db.Incidents.SingleAsync(item => item.Id == created.IncidentId)).Title);
            Assert.AreEqual(creatorId, (await db.IncidentTimelineEntries.SingleAsync(item => item.IncidentId == created.IncidentId)).ActorId);
            var app = await db.SourceApplications.SingleAsync(item => item.Id == incident.ApplicationId);
            app.IsEnabled = false;
            await db.SaveChangesAsync();
        }
        var disabled = await MobileCall<AiurResponse>(token, HttpMethod.Post, "/api/v1/mobile/incidents", request);
        Assert.AreEqual(Code.InvalidInput, disabled.Code);
    }
}

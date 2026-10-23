using System.Net;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using System.Text.RegularExpressions;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class IncidentPagesTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApplicationIncidentCountsSeparateStatesAndLinkToTheMatchingQueue(bool disabled)
    {
        await LoginAsAdmin();
        Guid applicationId;
        Guid emptyApplicationId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var ownerId = await db.Users.Where(user => user.Email == "admin@default.com").Select(user => user.Id).SingleAsync();
            var application = new SourceApplication { Name = "Counted application", OwnerId = ownerId, IsEnabled = !disabled };
            var empty = new SourceApplication { Name = "Empty application", OwnerId = ownerId };
            applicationId = application.Id;
            emptyApplicationId = empty.Id;
            db.AddRange(application, empty);
            db.Incidents.AddRange(
                new Incident { Application = application, Title = "Unacknowledged active incident" },
                new Incident { Application = application, Title = "Acknowledged active incident", AcknowledgedAtUtc = DateTime.UtcNow },
                new Incident { Application = application, Title = "Mitigated incident", Status = Incident.Mitigated },
                new Incident { Application = application, Title = "Resolved incident", Status = Incident.Resolved });
            await db.SaveChangesAsync();
        }
        var html = await Http.GetStringAsync("/Applications/Applications");
        var row = Regex.Matches(html, "<tr>.*?</tr>", RegexOptions.Singleline)
            .Single(match => match.Value.Contains(applicationId.ToString())).Value;
        var emptyRow = Regex.Matches(html, "<tr>.*?</tr>", RegexOptions.Singleline)
            .Single(match => match.Value.Contains(emptyApplicationId.ToString())).Value;
        Assert.Contains("Active: 2", row);
        Assert.Contains("Mitigated: 1", row);
        Assert.Contains("Active: 0", emptyRow);
        Assert.Contains("Mitigated: 0", emptyRow);
        foreach (var (label, expectedTitle, excludedTitle) in new[]
        {
            ("Active: 2", "Acknowledged active incident", "Mitigated incident"),
            ("Mitigated: 1", "Mitigated incident", "Acknowledged active incident")
        })
        {
            var link = Regex.Match(row, "<a[^>]*href=\"([^\"]+)\"[^>]*>\\s*" + label + "\\s*</a>", RegexOptions.Singleline);
            Assert.IsTrue(link.Success);
            var queue = await Http.GetStringAsync(WebUtility.HtmlDecode(link.Groups[1].Value));
            Assert.Contains(expectedTitle, queue);
            Assert.DoesNotContain(excludedTitle, queue);
            Assert.DoesNotContain("Resolved incident", queue);
        }
        var filtered = await Http.GetStringAsync("/Applications/Applications?keyword=Counted");
        Assert.Contains("Active: 2", filtered);
        Assert.DoesNotContain("Empty application", filtered);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApplicationIncidentCountsRequireViewPermissionRatherThanAnotherApplicationGrant(bool roleGrant)
    {
        var key = await CreateApplication();
        var readableId = Guid.Parse(key.Split('.')[0]);
        var (email, _) = await RegisterAndLoginAsync();
        Guid createOnlyId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var userId = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
            var readable = await db.SourceApplications.SingleAsync();
            readable.Name = "Readable counts";
            var createOnly = new SourceApplication { Name = "Create-only counts", OwnerId = readable.OwnerId };
            var hidden = new SourceApplication { Name = "Private counts", OwnerId = readable.OwnerId };
            createOnlyId = createOnly.Id;
            db.AddRange(createOnly, hidden);
            foreach (var application in new[] { readable, createOnly, hidden })
                db.Incidents.Add(new Incident { Application = application, Title = "Private incident information" });
            if (roleGrant)
            {
                var role = new IdentityRole("Incident count readers");
                db.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = role.Id });
                db.ApplicationRoleAccesses.Add(new ApplicationRoleAccess { Application = readable, Role = role, CanViewIncidents = true });
                db.ApplicationRoleAccesses.Add(new ApplicationRoleAccess { Application = createOnly, Role = role, CanCreateIncidents = true });
            }
            else
            {
                db.ApplicationUserAccesses.Add(new ApplicationUserAccess { Application = readable, UserId = userId, CanViewIncidents = true });
                db.ApplicationUserAccesses.Add(new ApplicationUserAccess { Application = createOnly, UserId = userId, CanCreateIncidents = true });
            }
            await db.SaveChangesAsync();
        }
        var html = await Http.GetStringAsync("/Applications/Applications");
        var readableRow = Regex.Matches(html, "<tr>.*?</tr>", RegexOptions.Singleline)
            .Single(match => match.Value.Contains(readableId.ToString())).Value;
        var createOnlyRow = Regex.Matches(html, "<tr>.*?</tr>", RegexOptions.Singleline)
            .Single(match => match.Value.Contains(createOnlyId.ToString())).Value;
        Assert.Contains("Active: 1", readableRow);
        Assert.Contains("Mitigated: 0", readableRow);
        Assert.Contains("No permission to view incidents", createOnlyRow);
        Assert.DoesNotContain("Active:", createOnlyRow);
        Assert.DoesNotContain("Mitigated:", createOnlyRow);
        Assert.DoesNotContain("Private counts", html);
        Assert.DoesNotContain("Private incident information", html);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DashboardCreationUsesOnlyEnabledApplicationsWithExplicitCreateAccess(bool roleGrant)
    {
        var key = await CreateApplication();
        var applicationId = Guid.Parse(key.Split('.')[0]);
        var (email, _) = await RegisterAndLoginAsync();
        Guid privateId;
        Guid disabledId;
        string userId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            userId = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
            var application = await db.SourceApplications.SingleAsync();
            application.Name = "Allowed create-only application";
            var other = new SourceApplication { Name = "Private application", OwnerId = application.OwnerId };
            var disabled = new SourceApplication { Name = "Disabled application", OwnerId = application.OwnerId, IsEnabled = false };
            db.AddRange(other, disabled);
            privateId = other.Id;
            disabledId = disabled.Id;
            db.ApplicationUserAccesses.Add(new ApplicationUserAccess { Application = disabled, UserId = userId, CanCreateIncidents = true });
            db.ApplicationUserAccesses.Add(new ApplicationUserAccess { Application = other, UserId = userId, CanViewIncidents = true });
            if (roleGrant)
            {
                var role = new IdentityRole("Incident reporters");
                db.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = role.Id });
                db.ApplicationRoleAccesses.Add(new ApplicationRoleAccess { Application = application, Role = role, CanCreateIncidents = true });
            }
            else
                db.ApplicationUserAccesses.Add(new ApplicationUserAccess { Application = application, UserId = userId, CanCreateIncidents = true });
            var channel = new PushChannel { Name = "Reporter inbox", Type = "inapp", IsEnabled = true };
            db.ApplicationPushChannels.Add(new ApplicationPushChannel { Application = application, Channel = channel });
            db.PushChannelUsers.Add(new PushChannelUser { Channel = channel, UserId = userId });
            await db.SaveChangesAsync();
        }
        var dashboard = await Http.GetStringAsync("/Dashboard");
        Assert.Contains("id=\"new-alert\"", dashboard);
        Assert.Contains("href=\"/Incidents/Create\"", dashboard);
        var page = await Http.GetStringAsync("/Incidents/Create");
        Assert.Contains("Allowed create-only application", page);
        Assert.DoesNotContain("Private application", page);
        Assert.DoesNotContain("Disabled application", page);
        AssertForbidden(await Http.GetAsync($"/Incidents/Create?applicationId={privateId}"));
        AssertForbidden(await PostForm("/Incidents/Create", new() { ["ApplicationId"] = privateId.ToString(), ["Title"] = "Forged report" }, "/Incidents/Create"));
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PostForm("/Incidents/Create", new() { ["ApplicationId"] = disabledId.ToString(), ["Title"] = "Disabled report" }, "/Incidents/Create")).StatusCode);
        var invalid = await PostForm("/Incidents/Create", new() { ["ApplicationId"] = applicationId.ToString(), ["Title"] = "", ["Level"] = "sev9" }, "/Incidents/Create");
        Assert.AreEqual(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains("Allowed create-only application", await invalid.Content.ReadAsStringAsync());
        var missing = await PostForm("/Incidents/Create", new() { ["Title"] = "Missing app" }, "/Incidents/Create");
        Assert.AreEqual(HttpStatusCode.OK, missing.StatusCode);
        Assert.Contains("Please select an application.", await missing.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PostForm("/Incidents/Create", new() { ["ApplicationId"] = applicationId.ToString(), ["Title"] = "No CSRF" }, includeToken: false)).StatusCode);
        AssertRedirect(await PostForm("/Incidents/Create", new()
        {
            ["ApplicationId"] = applicationId.ToString(), ["Title"] = "Manual report", ["Level"] = "sev2", ["Content"] = "<script>unsafe</script>"
        }, "/Incidents/Create"), "/Applications/Applications");
        Guid incidentId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var incident = await db.Incidents.SingleAsync();
            incidentId = incident.Id;
            Assert.AreEqual(applicationId, incident.ApplicationId);
            Assert.AreEqual("sev2", incident.Level);
            Assert.AreEqual(userId, (await db.IncidentTimelineEntries.SingleAsync()).ActorId);
            Assert.AreEqual(1, await db.PushDeliveries.CountAsync(delivery => delivery.IncidentId == incidentId));
            Assert.AreEqual(1, await db.PushDeliveryRecipients.CountAsync(recipient => recipient.UserId == userId));
            db.ApplicationUserAccesses.RemoveRange(db.ApplicationUserAccesses.Where(access => access.UserId == userId));
            db.UserRoles.RemoveRange(db.UserRoles.Where(link => link.UserId == userId));
            await db.SaveChangesAsync();
        }
        AssertForbidden(await Http.GetAsync($"/Incidents/Details/{incidentId}"));
        AssertForbidden(await PostForm("/Incidents/Create", new() { ["ApplicationId"] = applicationId.ToString(), ["Title"] = "Revoked report" }, "/Manage/ChangePassword"));
        Assert.Contains("new-alert-unavailable", await Http.GetStringAsync("/Dashboard"));
        page = await Http.GetStringAsync("/Incidents/Create");
        Assert.Contains("No enabled applications are available.", page);
        Assert.DoesNotContain("Allowed create-only application", page);
    }

    [TestMethod]
    public async Task ApplicationManagerCanBindAnyPublicTeamButCannotEditItOrAnotherApplication()
    {
        var key = await CreateApplication();
        var applicationId = Guid.Parse(key.Split('.')[0]);
        var (email, _) = await RegisterAndLoginAsync();
        Guid teamId;
        Guid otherId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var userId = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
            var team = new OnCallGroup { Name = "Public shared on-call team" };
            var other = new SourceApplication { Name = "Another customer's application" };
            db.AddRange(team, other);
            teamId = team.Id;
            otherId = other.Id;
            db.ApplicationUserAccesses.Add(new ApplicationUserAccess { ApplicationId = applicationId, UserId = userId, CanManageApplication = true });
            await db.SaveChangesAsync();
        }
        var url = $"/Applications/OnCall/{applicationId}";
        Assert.Contains("Public shared on-call team", await Http.GetStringAsync(url));
        AssertRedirect(await PostForm("/Applications/SaveOnCall", new()
        {
            ["ApplicationId"] = applicationId.ToString(), ["OnCallGroupId"] = teamId.ToString()
        }, url), url);
        AssertForbidden(await Http.GetAsync($"/OnCall/GroupEdit/{teamId}"));
        AssertForbidden(await PostForm("/Applications/SaveOnCall", new()
        {
            ["ApplicationId"] = otherId.ToString(), ["OnCallGroupId"] = teamId.ToString()
        }, url));
        using var after = Server!.Services.CreateScope();
        var verify = after.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(teamId, (await verify.SourceApplications.SingleAsync(application => application.Id == applicationId)).OnCallGroupId);
        Assert.IsNull((await verify.SourceApplications.SingleAsync(application => application.Id == otherId)).OnCallGroupId);
    }

    [TestMethod]
    public async Task OwnerCanGrantIndividualOperationsAndRevocationTakesEffectImmediately()
    {
        var key = await CreateApplication();
        var applicationId = Guid.Parse(key.Split('.')[0]);
        var (email, password) = await RegisterAndLoginAsync();
        string recipient;
        using (var scope = Server!.Services.CreateScope())
            recipient = await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
        await LoginAsAdmin();
        var url = $"/Applications/Access/{applicationId}";
        var html = await Http.GetStringAsync(url);
        foreach (var field in new[] { "CanViewIncidents", "CanCreateIncidents", "CanUpdateIncidents", "CanManageApiKeys", "CanDeleteIncidents", "CanManageApplication" })
            Assert.Contains($"name=\"{field}\"", html);
        AssertRedirect(await PostForm("/Applications/SaveAccess", new()
        {
            ["ApplicationId"] = applicationId.ToString(), ["UserId"] = recipient,
            ["CanViewIncidents"] = "true", ["CanCreateIncidents"] = "true"
        }, url), url);
        await PostForm("/Account/Login", new() { ["EmailOrUserName"] = email, ["Password"] = password });
        var createUrl = $"/Incidents/Create?applicationId={applicationId}";
        Assert.AreEqual(HttpStatusCode.OK, (await Http.GetAsync(createUrl)).StatusCode);
        var created = await PostForm("/Incidents/Create", new()
        {
            ["ApplicationId"] = applicationId.ToString(), ["Title"] = "Human report", ["Level"] = "sev2", ["Content"] = "Investigation"
        }, createUrl);
        Assert.AreEqual(HttpStatusCode.Found, created.StatusCode);
        Assert.Contains("/Incidents/Details/", created.Headers.Location!.OriginalString);
        Guid incidentId;
        Guid notificationId;
        Guid version;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var incident = await db.Incidents.SingleAsync();
            incidentId = incident.Id; version = incident.Version;
            Assert.AreEqual("sev2", incident.Level);
            Assert.AreEqual(recipient, (await db.IncidentTimelineEntries.SingleAsync()).ActorId);
            var notification = new InAppNotification
            {
                UserId = recipient, Delivery = new PushDelivery { Incident = incident, Status = PushDelivery.Succeeded }
            };
            db.InAppNotifications.Add(notification);
            await db.SaveChangesAsync();
            notificationId = notification.Id;
        }
        Assert.Contains("Human report", await Http.GetStringAsync("/Push/Inbox"));
        Assert.AreEqual(HttpStatusCode.OK, (await Http.GetAsync($"/Push/Notification/{notificationId}")).StatusCode);
        var details = $"/Incidents/Details/{incidentId}";
        AssertForbidden(await PostForm("/Incidents/Acknowledge", new() { ["IncidentId"] = incidentId.ToString(), ["Version"] = version.ToString() }, "/Manage/ChangePassword"));
        AssertForbidden(await PostForm("/Incidents/Delete", new() { ["id"] = incidentId.ToString(), ["version"] = version.ToString(), ["confirmationTitle"] = "Human report" }, "/Manage/ChangePassword"));
        AssertForbidden(await PostForm("/ApplicationApiKeys/Create", new() { ["ApplicationId"] = applicationId.ToString() }, "/Manage/ChangePassword"));
        AssertForbidden(await Http.GetAsync(url));
        await LoginAsAdmin();
        AssertRedirect(await PostForm("/Applications/RemoveAccess", new() { ["id"] = applicationId.ToString(), ["userId"] = recipient }, url), url);
        await PostForm("/Account/Login", new() { ["EmailOrUserName"] = email, ["Password"] = password });
        AssertForbidden(await Http.GetAsync(details));
        AssertForbidden(await Http.GetAsync(createUrl));
        AssertForbidden(await Http.GetAsync($"/Push/Notification/{notificationId}"));
        Assert.DoesNotContain("Human report", await Http.GetStringAsync("/Push/Inbox"));
    }

    [TestMethod]
    public async Task SharingRejectsMixedRecipientsAndDelegatesCannotEscalateOrTransferOwnership()
    {
        var key = await CreateApplication();
        var id = Guid.Parse(key.Split('.')[0]);
        var (email, _) = await RegisterAndLoginAsync();
        string recipient;
        string role;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            recipient = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
            role = await db.Roles.Select(item => item.Id).FirstAsync();
            db.ApplicationUserAccesses.Add(new ApplicationUserAccess { ApplicationId = id, UserId = recipient, CanManageApplication = true });
            await db.SaveChangesAsync();
        }
        var url = $"/Applications/Access/{id}";
        Assert.AreEqual(HttpStatusCode.OK, (await Http.GetAsync(url)).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PostForm("/Applications/SaveAccess", new()
        {
            ["ApplicationId"] = id.ToString(), ["UserId"] = recipient, ["RoleId"] = role
        }, url)).StatusCode);
        AssertForbidden(await PostForm("/Applications/SaveAccess", new()
        {
            ["ApplicationId"] = id.ToString(), ["UserId"] = recipient, ["CanManageApiKeys"] = "true"
        }, url));
        AssertForbidden(await PostForm("/Applications/TransferOwnership", new() { ["id"] = id.ToString(), ["ownerId"] = recipient }, url));
        AssertForbidden(await PostForm("/Applications/DeleteApplication", new() { ["id"] = id.ToString(), ["confirmationName"] = "Demo Orders" }, url));
        await LoginAsAdmin();
        AssertRedirect(await PostForm("/Applications/TransferOwnership", new() { ["id"] = id.ToString(), ["ownerId"] = recipient }, url), "/Applications/Applications");
        AssertForbidden(await Http.GetAsync(url));
    }

    [TestMethod]
    public async Task PermanentIncidentDeletionRequiresItsOwnGrantExactConfirmationAndCurrentVersion()
    {
        var key = await CreateApplication();
        using var report = await Report(key, Guid.NewGuid(), title: "Delete only this incident");
        Guid id;
        Guid version;
        using (var scope = Server!.Services.CreateScope())
        {
            var incident = await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().Incidents.SingleAsync();
            id = incident.Id; version = incident.Version;
        }
        var url = $"/Incidents/Details/{id}";
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PostForm("/Incidents/Delete", new()
        {
            ["id"] = id.ToString(), ["version"] = version.ToString(), ["confirmationTitle"] = "wrong"
        }, url)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, (await PostForm("/Incidents/Delete", new()
        {
            ["id"] = id.ToString(), ["version"] = Guid.NewGuid().ToString(), ["confirmationTitle"] = "Delete only this incident"
        }, url)).StatusCode);
        AssertRedirect(await PostForm("/Incidents/Delete", new()
        {
            ["id"] = id.ToString(), ["version"] = version.ToString(), ["confirmationTitle"] = "Delete only this incident"
        }, url), "/Incidents");
        using var after = Server!.Services.CreateScope();
        var db = after.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(0, await db.Incidents.CountAsync());
        Assert.AreEqual(0, await db.IncidentTimelineEntries.CountAsync());
        Assert.AreEqual(1, await db.SourceApplications.CountAsync());
    }
}

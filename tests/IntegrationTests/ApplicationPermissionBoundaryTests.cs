using Aiursoft.AlertCenter.Authorization;
using System.Security.Claims;
using System.Net;
using Aiursoft.AlertCenter.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class AlertDemoTests
{
    [TestMethod]
    [DataRow("none", false)]
    [DataRow("none", true)]
    [DataRow("applications", false)]
    [DataRow("on-call", false)]
    [DataRow("push", false)]
    [DataRow("all", false)]
    public async Task PlatformCapabilitiesFollowPermissionsInsteadOfTheAdministratorRole(string capability, bool administratorRole)
    {
        var (email, password) = await RegisterAndLoginAsync();
        using (var scope = Server!.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = (await users.FindByEmailAsync(email))!;
            if (administratorRole) Assert.IsTrue((await users.AddToRoleAsync(user, "Administrators")).Succeeded);
            var keys = capability switch
            {
                "applications" => new[] { AppPermissionNames.CanManageApplications },
                "on-call" => new[] { AppPermissionNames.CanManageOnCall },
                "push" => new[] { AppPermissionNames.CanManagePush },
                "all" => AppPermissions.GetAllPermissions().Select(permission => permission.Key).ToArray(),
                _ => []
            };
            // Keep this role-only case free of role claims, including the normally seeded claims.
            if (administratorRole)
            {
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                var role = (await roles.FindByNameAsync("Administrators"))!;
                foreach (var claim in await roles.GetClaimsAsync(role))
                    Assert.IsTrue((await roles.RemoveClaimAsync(role, claim)).Succeeded);
            }
            Assert.IsTrue((await users.AddClaimsAsync(user, keys.Select(key => new Claim(AppPermissions.Type, key)))).Succeeded);
        }
        using var signOut = await Http.GetAsync("/Account/LogOff");
        using var login = await PostForm("/Account/Login", new()
        {
            ["EmailOrUserName"] = email, ["Password"] = password
        });
        Assert.AreEqual(HttpStatusCode.Found, login.StatusCode);
        var canManageApplications = capability is "applications" or "all";
        var canManageOnCall = capability is "on-call" or "all";
        var canManagePush = capability is "push" or "all";
        using var groupCreate = await Http.GetAsync("/OnCall/GroupCreate");
        if (canManageOnCall) Assert.AreEqual(HttpStatusCode.OK, groupCreate.StatusCode);
        else AssertForbidden(groupCreate);
        using var pushSettings = await Http.GetAsync("/Push/Index");
        if (canManagePush) Assert.AreEqual(HttpStatusCode.OK, pushSettings.StatusCode);
        else AssertForbidden(pushSettings);
        using var create = await PostForm("/Alerts/CreateApplication", new() { ["Name"] = "Permission-managed app" }, "/Manage/ChangePassword");
        if (canManageApplications) AssertRedirect(create, "/Alerts/Applications");
        else AssertForbidden(create);
        using var verifyScope = Server.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(canManageApplications ? 1 : 0, await db.SourceApplications.CountAsync());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task ApplicationGrantsCannotEscalatePrivilegesOrManageAnotherApplication(bool roleGrant, bool manage)
    {
        var ownKey = await CreateApplication();
        var otherKey = await CreateApplication();
        var ownId = Guid.Parse(ownKey.Split('.')[0]);
        var otherId = Guid.Parse(otherKey.Split('.')[0]);
        using var ownReport = await Report(ownKey, Guid.NewGuid(), title: "Allowed application alert");
        using var otherReport = await Report(otherKey, Guid.NewGuid(), title: "Private other application alert");
        Assert.AreEqual(HttpStatusCode.Created, ownReport.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, otherReport.StatusCode);
        var (email, _) = await RegisterAndLoginAsync();
        string userId;
        Guid otherMessageId;
        Dictionary<Guid, string> hashes;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            userId = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
            await AddBoundaryApplicationGrant(db, ownId, userId, roleGrant, manage);
            (await db.SourceApplications.SingleAsync(application => application.Id == ownId)).Name = "Allowed application";
            (await db.SourceApplications.SingleAsync(application => application.Id == otherId)).Name = "Private other application";
            await db.SaveChangesAsync();
            hashes = await db.SourceApplications.ToDictionaryAsync(application => application.Id, application => application.SecretHash);
            otherMessageId = await db.ReportedMessages.Where(message => message.ApplicationId == otherId).Select(message => message.Id).SingleAsync();
        }

        var applications = await Http.GetStringAsync("/Alerts/Applications");
        Assert.Contains("Allowed application", applications);
        Assert.DoesNotContain("Private other application", applications);
        var alerts = await Http.GetStringAsync("/Alerts");
        Assert.Contains("Allowed application alert", alerts);
        Assert.DoesNotContain("Private other application alert", alerts);
        AssertForbidden(await Http.GetAsync($"/Alerts?applicationId={otherId}"));
        AssertForbidden(await Http.GetAsync($"/Alerts/Details/{otherMessageId}"));

        foreach (var id in manage ? new[] { otherId } : new[] { ownId, otherId })
        {
            AssertForbidden(await PostForm("/Alerts/ResetApiKey", new() { ["id"] = id.ToString() }, "/Manage/ChangePassword"));
            AssertForbidden(await PostForm("/Alerts/SetEnabled", new()
            {
                ["id"] = id.ToString(), ["enabled"] = "false"
            }, "/Manage/ChangePassword"));
            AssertForbidden(await PostForm("/Alerts/DeleteApplication", new()
            {
                ["id"] = id.ToString(), ["confirmationName"] = id == ownId ? "Allowed application" : "Private other application"
            }, "/Manage/ChangePassword"));
        }
        foreach (var id in new[] { ownId, otherId })
        {
            AssertForbidden(await Http.GetAsync($"/Alerts/Access/{id}"));
            AssertForbidden(await PostForm("/Alerts/SaveAccess", new()
            {
                ["ApplicationId"] = id.ToString(), ["ManageUserIds"] = userId
            }, "/Manage/ChangePassword"));
        }
        AssertForbidden(await PostForm("/Alerts/CreateApplication", new() { ["Name"] = "Unauthorized application" }, "/Manage/ChangePassword"));

        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var saved = await db.SourceApplications.ToListAsync();
            Assert.AreEqual(2, saved.Count);
            Assert.IsTrue(saved.All(application => application.IsEnabled && application.SecretHash == hashes[application.Id]));
            Assert.AreEqual(2, await db.ReportedMessages.CountAsync());
            Assert.AreEqual(2, await db.Incidents.CountAsync());
            Assert.AreEqual(roleGrant ? 0 : 1, await db.ApplicationUserAccesses.CountAsync());
            Assert.AreEqual(roleGrant ? 1 : 0, await db.ApplicationRoleAccesses.CountAsync());
            Assert.AreEqual(manage, roleGrant
                ? (await db.ApplicationRoleAccesses.SingleAsync()).CanManage
                : (await db.ApplicationUserAccesses.SingleAsync()).CanManage);
        }

        if (manage)
        {
            var newKey = await ResetApplicationKey(ownId);
            using var rejectedOldKey = await Report(ownKey, Guid.NewGuid());
            using var acceptedNewKey = await Report(newKey, Guid.NewGuid());
            Assert.AreEqual(HttpStatusCode.Unauthorized, rejectedOldKey.StatusCode);
            Assert.AreEqual(HttpStatusCode.Created, acceptedNewKey.StatusCode);
        }
        else
        {
            using var acceptedOwnKey = await Report(ownKey, Guid.NewGuid());
            Assert.AreEqual(HttpStatusCode.Created, acceptedOwnKey.StatusCode);
        }
        using var acceptedOtherKey = await Report(otherKey, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Created, acceptedOtherKey.StatusCode);
        using var finalScope = Server!.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(hashes[otherId], await finalDb.SourceApplications.Where(application => application.Id == otherId)
            .Select(application => application.SecretHash).SingleAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApplicationGrantRevocationTakesEffectWithTheExistingLoginCookie(bool roleGrant)
    {
        var key = await CreateApplication();
        var otherKey = await CreateApplication();
        var id = Guid.Parse(key.Split('.')[0]);
        var otherId = Guid.Parse(otherKey.Split('.')[0]);
        using var report = await Report(key, Guid.NewGuid(), title: "Revoked application alert");
        using var otherReport = await Report(otherKey, Guid.NewGuid(), title: "Retained application alert");
        Assert.AreEqual(HttpStatusCode.Created, report.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, otherReport.StatusCode);
        var (email, _) = await RegisterAndLoginAsync();
        Guid messageId;
        Guid incidentId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var userId = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
            await AddBoundaryApplicationGrant(db, id, userId, roleGrant, true);
            db.ApplicationUserAccesses.Add(new ApplicationUserAccess { ApplicationId = otherId, UserId = userId });
            await db.SaveChangesAsync();
            messageId = await db.ReportedMessages.Where(message => message.ApplicationId == id).Select(message => message.Id).SingleAsync();
            incidentId = await db.Incidents.Where(incident => incident.ApplicationId == id).Select(incident => incident.Id).SingleAsync();
        }
        foreach (var url in new[] { $"/Alerts/Details/{messageId}", $"/Incidents/Details/{incidentId}", $"/Alerts/OnCall/{id}" })
        {
            using var allowed = await Http.GetAsync(url);
            Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
        }
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            if (roleGrant)
            {
                var roleId = await db.ApplicationRoleAccesses.Where(access => access.ApplicationId == id)
                    .Select(access => access.RoleId).SingleAsync();
                db.UserRoles.Remove(await db.UserRoles.SingleAsync(link => link.RoleId == roleId));
            }
            else
                db.ApplicationUserAccesses.Remove(await db.ApplicationUserAccesses.SingleAsync(access => access.ApplicationId == id));
            await db.SaveChangesAsync();
        }

        foreach (var url in new[] { $"/Alerts/Details/{messageId}", $"/Incidents/Details/{incidentId}", $"/Alerts/OnCall/{id}", $"/Alerts?applicationId={id}" })
            AssertForbidden(await Http.GetAsync(url));
        AssertForbidden(await PostForm("/Alerts/ResetApiKey", new() { ["id"] = id.ToString() }, "/Manage/ChangePassword"));
        var alerts = await Http.GetStringAsync("/Alerts");
        Assert.DoesNotContain("Revoked application alert", alerts);
        Assert.Contains("Retained application alert", alerts);
        var incidents = await Http.GetStringAsync("/Incidents");
        Assert.DoesNotContain("Revoked application alert", incidents);
        Assert.Contains("Retained application alert", incidents);
        using var stillValidKey = await Report(key, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Created, stillValidKey.StatusCode);
    }

    [TestMethod]
    public async Task IncidentOwnershipGrantsAccessOnlyToThatIncidentAndItsAssociatedAlerts()
    {
        var key = await CreateApplication();
        var applicationId = Guid.Parse(key.Split('.')[0]);
        using var firstReport = await Report(key, Guid.NewGuid(), title: "Owned incident marker");
        using var secondReport = await Report(key, Guid.NewGuid(), title: "Unrelated incident marker");
        Assert.AreEqual(HttpStatusCode.Created, firstReport.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, secondReport.StatusCode);
        var (email, _) = await RegisterAndLoginAsync();
        Guid ownedIncidentId;
        Guid ownedMessageId;
        Guid otherIncidentId;
        Guid otherMessageId;
        Guid unassociatedId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var user = await db.Users.SingleAsync(user => user.Email == email);
            var owned = await db.Incidents.SingleAsync(incident => incident.Title == "Owned incident marker");
            owned.OwnerId = user.Id;
            owned.OwnerDisplayName = user.DisplayName;
            owned.Status = Incident.Acked;
            ownedIncidentId = owned.Id;
            otherIncidentId = await db.Incidents.Where(incident => incident.Id != owned.Id).Select(incident => incident.Id).SingleAsync();
            ownedMessageId = await db.ReportedMessages.Where(message => message.IncidentId == owned.Id).Select(message => message.Id).SingleAsync();
            otherMessageId = await db.ReportedMessages.Where(message => message.IncidentId == otherIncidentId).Select(message => message.Id).SingleAsync();
            var unassociated = new ReportedMessage
            {
                ApplicationId = applicationId, ReportId = Guid.NewGuid(), Title = "Unassociated private marker", Level = "sev1"
            };
            db.ReportedMessages.Add(unassociated);
            await db.SaveChangesAsync();
            unassociatedId = unassociated.Id;
        }
        foreach (var url in new[] { $"/Incidents/Details/{ownedIncidentId}", $"/Alerts/Details/{ownedMessageId}" })
        {
            using var allowed = await Http.GetAsync(url);
            Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
        }
        foreach (var url in new[] { $"/Incidents/Details/{otherIncidentId}", $"/Alerts/Details/{otherMessageId}", $"/Alerts/Details/{unassociatedId}" })
            AssertForbidden(await Http.GetAsync(url));
        var incidents = await Http.GetStringAsync("/Incidents");
        Assert.Contains("Owned incident marker", incidents);
        Assert.DoesNotContain("Unrelated incident marker", incidents);
        Assert.DoesNotContain("Unrelated incident marker", await Http.GetStringAsync("/Incidents?keyword=Unrelated"));
        AssertForbidden(await PostForm("/Alerts/SetEnabled", new()
        {
            ["id"] = applicationId.ToString(), ["enabled"] = "false"
        }, "/Manage/ChangePassword"));
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var owned = await db.Incidents.SingleAsync(incident => incident.Id == ownedIncidentId);
            owned.OwnerId = await db.Users.Where(user => user.UserName == "admin").Select(user => user.Id).SingleAsync();
            owned.OwnerDisplayName = "Administrator";
            await db.SaveChangesAsync();
        }
        AssertForbidden(await Http.GetAsync($"/Incidents/Details/{ownedIncidentId}"));
        AssertForbidden(await Http.GetAsync($"/Alerts/Details/{ownedMessageId}"));
        Assert.DoesNotContain("Owned incident marker", await Http.GetStringAsync("/Incidents"));
    }

    private static async Task AddBoundaryApplicationGrant(TemplateDbContext db, Guid applicationId, string userId, bool roleGrant, bool manage)
    {
        if (roleGrant)
        {
            var role = new IdentityRole("Boundary application role");
            db.Roles.Add(role);
            db.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = role.Id });
            db.ApplicationRoleAccesses.Add(new ApplicationRoleAccess { ApplicationId = applicationId, RoleId = role.Id, CanManage = manage });
        }
        else
            db.ApplicationUserAccesses.Add(new ApplicationUserAccess { ApplicationId = applicationId, UserId = userId, CanManage = manage });
        await db.SaveChangesAsync();
    }
}

using System.Net;
using Aiursoft.AlertCenter.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class AlertDemoTests
{
    private async Task<Guid> CreateOnCallTeam(string name)
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var group = new OnCallGroup { Name = name, TimeZoneId = "Asia/Shanghai" };
        db.OnCallGroups.Add(group);
        await db.SaveChangesAsync();
        return group.Id;
    }

    private async Task<Guid?> ApplicationTeam(Guid applicationId)
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        return await db.SourceApplications.Where(application => application.Id == applicationId)
            .Select(application => application.OnCallGroupId).SingleAsync();
    }

    [TestMethod]
    public async Task ApplicationOnCallBindingIsOptionalAndCanBeChangedOrRemoved()
    {
        var key = await CreateApplication();
        var id = Guid.Parse(key.Split('.')[0]);
        var first = await CreateOnCallTeam("Primary OCE");
        var second = await CreateOnCallTeam("Backup OCE");
        var settingsUrl = $"/Alerts/OnCall/{id}";
        Assert.IsNull(await ApplicationTeam(id));
        var settings = await Http.GetStringAsync(settingsUrl);
        Assert.Contains("name=\"OnCallGroupId\"", settings);
        Assert.Contains("Primary OCE (Asia/Shanghai)", settings);
        Assert.Contains("Backup OCE (Asia/Shanghai)", settings);
        Assert.Contains("/Alerts/SaveOnCall", settings);

        foreach (var groupId in new[] { first.ToString(), second.ToString(), string.Empty })
        {
            AssertRedirect(await PostForm("/Alerts/SaveOnCall", new()
            {
                ["ApplicationId"] = id.ToString(), ["OnCallGroupId"] = groupId
            }, settingsUrl), settingsUrl);
            Assert.AreEqual(string.IsNullOrEmpty(groupId) ? null : Guid.Parse(groupId), await ApplicationTeam(id));
            var saved = WebUtility.HtmlDecode(await Http.GetStringAsync(settingsUrl));
            Assert.Contains(string.IsNullOrEmpty(groupId) ? "已解除值班组绑定" : "值班组已更新", saved);
            if (!string.IsNullOrEmpty(groupId)) Assert.Contains($"<option value=\"{groupId}\" selected=\"selected\">", saved);
            var applications = await Http.GetStringAsync("/Alerts/Applications");
            Assert.Contains(settingsUrl, applications);
            if (!string.IsNullOrEmpty(groupId)) Assert.Contains($"/OnCall/GroupDetails/{groupId}", applications);
            using var report = await Report(key, Guid.NewGuid());
            Assert.AreEqual(HttpStatusCode.Created, report.StatusCode);
        }
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(3, await db.ReportedMessages.CountAsync());
        Assert.AreEqual(1, await db.Incidents.CountAsync());
        Assert.AreEqual(2, await db.PushDeliveries.CountAsync(delivery => delivery.OnCallEmail != null && delivery.Status == PushDelivery.Failed));
        Assert.AreEqual(0, await db.OnCallShifts.CountAsync());
    }

    [TestMethod]
    public async Task ApplicationCreationAcceptsOptionalTeamAndRejectsUnknownTeams()
    {
        await LoginAsAdmin();
        var groupId = await CreateOnCallTeam("Shared team");
        var applications = await Http.GetStringAsync("/Alerts/Applications");
        Assert.Contains("id=\"create-oncall-group\"", applications);
        Assert.Contains(groupId.ToString(), applications);
        foreach (var name in new[] { "Orders", "Billing" })
        {
            AssertRedirect(await PostForm("/Alerts/CreateApplication", new()
            {
                ["Name"] = name, ["OnCallGroupId"] = groupId.ToString()
            }, "/Alerts/Applications"), "/Alerts/Applications");
        }
        foreach (var group in new[] { Guid.NewGuid().ToString(), Guid.Empty.ToString(), "invalid-guid" })
        {
            using var response = await PostForm("/Alerts/CreateApplication", new()
            {
                ["Name"] = "Rejected", ["OnCallGroupId"] = group
            }, "/Alerts/Applications");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("validation-summary-errors", html);
            Assert.Contains("value=\"Rejected\"", html);
        }
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var saved = await db.SourceApplications.ToListAsync();
        Assert.AreEqual(2, saved.Count);
        Assert.IsTrue(saved.All(application => application.OnCallGroupId == groupId));
    }

    [TestMethod]
    public async Task InvalidBindingPreservesExistingTeamAndRequiresAntiforgeryToken()
    {
        var key = await CreateApplication();
        var id = Guid.Parse(key.Split('.')[0]);
        var groupId = await CreateOnCallTeam("Keep team");
        var settingsUrl = $"/Alerts/OnCall/{id}";
        AssertRedirect(await PostForm("/Alerts/SaveOnCall", new()
        {
            ["ApplicationId"] = id.ToString(), ["OnCallGroupId"] = groupId.ToString()
        }, settingsUrl), settingsUrl);
        foreach (var group in new[] { Guid.NewGuid().ToString(), Guid.Empty.ToString(), "invalid-guid" })
        {
            using var invalid = await PostForm("/Alerts/SaveOnCall", new()
            {
                ["ApplicationId"] = id.ToString(), ["OnCallGroupId"] = group
            }, settingsUrl);
            Assert.AreEqual(HttpStatusCode.OK, invalid.StatusCode);
            Assert.Contains("validation-summary-errors", await invalid.Content.ReadAsStringAsync());
            Assert.AreEqual(groupId, await ApplicationTeam(id));
        }
        using var missingToken = await PostForm("/Alerts/SaveOnCall", new()
        {
            ["ApplicationId"] = id.ToString(), ["OnCallGroupId"] = string.Empty
        }, includeToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingToken.StatusCode);
        using var get = await Http.GetAsync($"/Alerts/SaveOnCall?ApplicationId={id}");
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        using var missingApplication = await PostForm("/Alerts/SaveOnCall", new()
        {
            ["ApplicationId"] = Guid.NewGuid().ToString(), ["OnCallGroupId"] = groupId.ToString()
        }, settingsUrl);
        Assert.AreEqual(HttpStatusCode.NotFound, missingApplication.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync($"/Alerts/OnCall/{Guid.NewGuid()}")).StatusCode);
        Assert.AreEqual(groupId, await ApplicationTeam(id));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApplicationOnCallSettingsRequireManageAccessForUsersAndRoles(bool roleGrant)
    {
        var key = await CreateApplication();
        var id = Guid.Parse(key.Split('.')[0]);
        var otherKey = await CreateApplication();
        var otherId = Guid.Parse(otherKey.Split('.')[0]);
        var groupId = await CreateOnCallTeam("Operations");
        var (email, _) = await RegisterAndLoginAsync();
        var settingsUrl = $"/Alerts/OnCall/{id}";
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var user = await db.Users.SingleAsync(item => item.Email == email);
            if (roleGrant)
            {
                var role = new IdentityRole("OnCallApplicationViewers");
                db.Roles.Add(role);
                db.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = role.Id });
                db.ApplicationRoleAccesses.Add(new ApplicationRoleAccess { ApplicationId = id, RoleId = role.Id });
            }
            else
                db.ApplicationUserAccesses.Add(new ApplicationUserAccess { ApplicationId = id, UserId = user.Id });
            await db.SaveChangesAsync();
        }
        var applications = await Http.GetStringAsync("/Alerts/Applications");
        Assert.DoesNotContain(settingsUrl, applications);
        Assert.DoesNotContain("create-oncall-group", applications);
        AssertForbidden(await Http.GetAsync(settingsUrl));
        AssertForbidden(await PostForm("/Alerts/SaveOnCall", new()
        {
            ["ApplicationId"] = id.ToString(), ["OnCallGroupId"] = groupId.ToString()
        }, "/Alerts/Applications"));
        Assert.IsNull(await ApplicationTeam(id));
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            if (roleGrant) (await db.ApplicationRoleAccesses.SingleAsync(item => item.ApplicationId == id)).CanManage = true;
            else (await db.ApplicationUserAccesses.SingleAsync(item => item.ApplicationId == id)).CanManage = true;
            await db.SaveChangesAsync();
        }
        Assert.Contains(settingsUrl, await Http.GetStringAsync("/Alerts/Applications"));
        Assert.AreEqual(HttpStatusCode.OK, (await Http.GetAsync(settingsUrl)).StatusCode);
        AssertRedirect(await PostForm("/Alerts/SaveOnCall", new()
        {
            ["ApplicationId"] = id.ToString(), ["OnCallGroupId"] = groupId.ToString()
        }, settingsUrl), settingsUrl);
        Assert.AreEqual(groupId, await ApplicationTeam(id));
        AssertForbidden(await PostForm("/Alerts/SaveOnCall", new()
        {
            ["ApplicationId"] = otherId.ToString(), ["OnCallGroupId"] = groupId.ToString()
        }, settingsUrl));
        Assert.IsNull(await ApplicationTeam(otherId));
        using var scopeAfter = Server!.Services.CreateScope();
        var dbAfter = scopeAfter.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(0, await dbAfter.OnCallGroupMembers.CountAsync());
    }

    private static void AssertForbidden(HttpResponseMessage response)
    {
        using var forbidden = response;
        Assert.AreEqual(HttpStatusCode.Found, forbidden.StatusCode);
        Assert.Contains("Code403", forbidden.Headers.Location!.OriginalString);
    }

    [TestMethod]
    public async Task BoundTeamCannotBeDeletedUntilAllApplicationsUnbind()
    {
        var key = await CreateApplication();
        var id = Guid.Parse(key.Split('.')[0]);
        var groupId = await CreateOnCallTeam("Protected team");
        AssertRedirect(await PostForm("/Alerts/SaveOnCall", new()
        {
            ["ApplicationId"] = id.ToString(), ["OnCallGroupId"] = groupId.ToString()
        }, $"/Alerts/OnCall/{id}"), $"/Alerts/OnCall/{id}");
        Guid otherId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var other = new SourceApplication { Name = "Other bound application", OnCallGroupId = groupId };
            db.SourceApplications.Add(other);
            await db.SaveChangesAsync();
            otherId = other.Id;
        }
        var detailsUrl = $"/OnCall/GroupDetails/{groupId}";
        var details = WebUtility.HtmlDecode(await Http.GetStringAsync(detailsUrl));
        Assert.Contains("已绑定接入应用", details);
        Assert.DoesNotContain("action=\"/OnCall/DeleteGroup\"", details);
        using var blocked = await PostForm("/OnCall/DeleteGroup", new() { ["id"] = groupId.ToString() }, $"/Alerts/OnCall/{id}");
        Assert.AreEqual(HttpStatusCode.Conflict, blocked.StatusCode);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                db.OnCallGroups.Where(group => group.Id == groupId).ExecuteDeleteAsync());
            Assert.IsTrue(await db.OnCallGroups.AnyAsync(group => group.Id == groupId));
            Assert.AreEqual(2, await db.SourceApplications.CountAsync(application => application.OnCallGroupId == groupId));
        }
        AssertRedirect(await PostForm("/Alerts/SaveOnCall", new()
        {
            ["ApplicationId"] = id.ToString(), ["OnCallGroupId"] = string.Empty
        }, $"/Alerts/OnCall/{id}"), $"/Alerts/OnCall/{id}");
        using var stillBound = await PostForm("/OnCall/DeleteGroup", new() { ["id"] = groupId.ToString() }, $"/Alerts/OnCall/{id}");
        Assert.AreEqual(HttpStatusCode.Conflict, stillBound.StatusCode);
        AssertRedirect(await PostForm("/Alerts/DeleteApplication", new()
        {
            ["id"] = otherId.ToString(), ["confirmationName"] = "Other bound application"
        }, "/Alerts/Applications"), "/Alerts/Applications");
        Assert.AreEqual(HttpStatusCode.OK, (await Http.GetAsync(detailsUrl)).StatusCode);
        AssertRedirect(await PostForm("/OnCall/DeleteGroup", new() { ["id"] = groupId.ToString() }, detailsUrl), "/OnCall/Groups");
        Assert.IsNull(await ApplicationTeam(id));
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync(detailsUrl)).StatusCode);
    }
}

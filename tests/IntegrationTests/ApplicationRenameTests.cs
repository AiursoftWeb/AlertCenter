using System.Net;
using Aiursoft.AlertCenter.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class IncidentPagesTests
{
    [TestMethod]
    public async Task ApplicationRenamePreservesIdentityHistoryKeysAndSettings()
    {
        var key = await CreateApplication();
        var id = Guid.Parse(key.Split('.')[0]);
        using var report = await Report(key, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Created, report.StatusCode);
        var team = await CreateOnCallTeam("Retained team");
        Guid incidentId, version, keyId, channelId;
        string secretHash, ownerId;
        DateTime createdAt;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var application = await db.SourceApplications.SingleAsync();
            ownerId = application.OwnerId!;
            createdAt = application.CreatedAtUtc;
            application.OnCallGroupId = team;
            application.IsEnabled = false;
            var incident = await db.Incidents.SingleAsync();
            incidentId = incident.Id; version = incident.Version;
            var apiKey = await db.ApplicationApiKeys.SingleAsync();
            keyId = apiKey.Id; secretHash = apiKey.SecretHash;
            var channel = new PushChannel { Name = "Retained channel", Type = "bark", IsEnabled = false, Endpoint = "https://example.com/push" };
            channelId = channel.Id;
            db.ApplicationPushChannels.Add(new ApplicationPushChannel { ApplicationId = id, Channel = channel });
            db.ApplicationUserAccesses.Add(new ApplicationUserAccess { ApplicationId = id, UserId = ownerId, CanViewIncidents = true });
            await db.SaveChangesAsync();
        }
        var url = $"/Applications/Rename/{id}";
        Assert.Contains(url, await Http.GetStringAsync("/Applications/Applications"));
        Assert.Contains("value=\"Demo Orders\"", await Http.GetStringAsync(url));
        AssertRedirect(await PostForm(url, new() { ["Id"] = id.ToString(), ["Name"] = "  Orders <new> & 服务  " }, url), "/Applications/Applications");
        var html = await Http.GetStringAsync("/Applications/Applications");
        Assert.Contains("Application renamed.", html);
        Assert.Contains("Orders &lt;new&gt; &amp;", html);
        Assert.DoesNotContain("Orders <new>", html);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var application = await db.SourceApplications.SingleAsync();
            Assert.AreEqual(id, application.Id);
            Assert.AreEqual("Orders <new> & 服务", application.Name);
            Assert.AreEqual(ownerId, application.OwnerId);
            Assert.AreEqual(createdAt, application.CreatedAtUtc);
            Assert.AreEqual(team, application.OnCallGroupId);
            Assert.IsFalse(application.IsEnabled);
            var apiKey = await db.ApplicationApiKeys.SingleAsync();
            Assert.AreEqual(keyId, apiKey.Id);
            Assert.AreEqual(secretHash, apiKey.SecretHash);
            var incident = await db.Incidents.SingleAsync();
            Assert.AreEqual(incidentId, incident.Id);
            Assert.AreEqual(version, incident.Version);
            Assert.AreEqual(id, incident.ApplicationId);
            Assert.AreEqual(channelId, (await db.ApplicationPushChannels.SingleAsync()).ChannelId);
            Assert.IsTrue((await db.ApplicationUserAccesses.SingleAsync()).CanViewIncidents);
            application.IsEnabled = true;
            await db.SaveChangesAsync();
        }
        using var afterRename = await Report(key, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Created, afterRename.StatusCode);
    }

    [TestMethod]
    public async Task ApplicationRenameValidatesNameAndAntiforgeryAndAllowsCorrection()
    {
        var key = await CreateApplication();
        var id = Guid.Parse(key.Split('.')[0]);
        var url = $"/Applications/Rename/{id}";
        foreach (var name in new[] { "", "   ", new string('a', 101) })
        {
            using var invalid = await PostForm(url, new() { ["Id"] = id.ToString(), ["Name"] = name }, url);
            Assert.AreEqual(HttpStatusCode.OK, invalid.StatusCode);
            Assert.Contains("validation-summary-errors", await invalid.Content.ReadAsStringAsync());
            using var scope = Server!.Services.CreateScope();
            Assert.AreEqual("Demo Orders", (await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().SourceApplications.SingleAsync()).Name);
        }
        using var missingToken = await PostForm(url, new() { ["Id"] = id.ToString(), ["Name"] = "Rejected" }, includeToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingToken.StatusCode);
        var missing = $"/Applications/Rename/{Guid.NewGuid()}";
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync(missing)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await PostForm(missing, new() { ["Name"] = "Missing" }, url)).StatusCode);
        var corrected = new string('a', 100);
        AssertRedirect(await PostForm(url, new() { ["Id"] = id.ToString(), ["Name"] = corrected }, url), "/Applications/Applications");
        using var finalScope = Server!.Services.CreateScope();
        Assert.AreEqual(corrected, (await finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>().SourceApplications.SingleAsync()).Name);
    }

    [TestMethod]
    public async Task ApplicationRenameFeedbackAllowsInvalidInputToBeCorrected()
    {
        var id = Guid.Parse((await CreateApplication()).Split('.')[0]);
        var url = $"/Applications/Rename/{id}";
        Http.DefaultRequestHeaders.Add("X-AlertCenter-Dialog", "true");
        using var invalid = await PostForm(url, new() { ["Id"] = id.ToString(), ["Name"] = " " }, url);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("Enter an application name.", await invalid.Content.ReadAsStringAsync());
        using var corrected = await PostForm(url, new() { ["Id"] = id.ToString(), ["Name"] = "Corrected name" }, url);
        Assert.AreEqual(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Contains("\"redirectUrl\":\"/Applications/Applications\"", await corrected.Content.ReadAsStringAsync());
        using var scope = Server!.Services.CreateScope();
        Assert.AreEqual("Corrected name", (await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().SourceApplications.SingleAsync()).Name);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApplicationRenameRequiresApplicationManagementForUserAndRoleGrants(bool roleGrant)
    {
        var key = await CreateApplication();
        var id = Guid.Parse(key.Split('.')[0]);
        var other = Guid.Parse((await CreateApplication()).Split('.')[0]);
        var (email, _) = await RegisterAndLoginAsync();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var user = await db.Users.SingleAsync(item => item.Email == email);
            if (roleGrant)
            {
                var role = new IdentityRole("Rename delegates");
                db.Roles.Add(role);
                db.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = role.Id });
                db.ApplicationRoleAccesses.Add(new ApplicationRoleAccess { ApplicationId = id, RoleId = role.Id, CanViewIncidents = true, CanManageApiKeys = true });
            }
            else db.ApplicationUserAccesses.Add(new ApplicationUserAccess { ApplicationId = id, UserId = user.Id, CanViewIncidents = true, CanManageApiKeys = true });
            await db.SaveChangesAsync();
        }
        var url = $"/Applications/Rename/{id}";
        Assert.DoesNotContain(url, await Http.GetStringAsync("/Applications/Applications"));
        AssertForbidden(await Http.GetAsync(url));
        AssertForbidden(await PostForm(url, new() { ["Id"] = id.ToString(), ["Name"] = "Rejected" }, "/Applications/Applications"));
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual("Demo Orders", (await db.SourceApplications.SingleAsync(item => item.Id == id)).Name);
            if (roleGrant) (await db.ApplicationRoleAccesses.SingleAsync()).CanManageApplication = true;
            else (await db.ApplicationUserAccesses.SingleAsync()).CanManageApplication = true;
            await db.SaveChangesAsync();
        }
        Assert.Contains(url, await Http.GetStringAsync("/Applications/Applications"));
        Assert.AreEqual(HttpStatusCode.OK, (await Http.GetAsync(url)).StatusCode);
        AssertRedirect(await PostForm(url, new() { ["Id"] = id.ToString(), ["Name"] = "Delegated rename" }, url), "/Applications/Applications");
        AssertForbidden(await Http.GetAsync($"/Applications/Rename/{other}"));
        AssertForbidden(await PostForm("/Applications/Rename", new() { ["Id"] = other.ToString(), ["Name"] = "Cross-application attempt" }, url));
        using var finalScope = Server!.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual("Delegated rename", (await finalDb.SourceApplications.SingleAsync(item => item.Id == id)).Name);
        Assert.AreEqual("Demo Orders", (await finalDb.SourceApplications.SingleAsync(item => item.Id == other)).Name);
    }
}

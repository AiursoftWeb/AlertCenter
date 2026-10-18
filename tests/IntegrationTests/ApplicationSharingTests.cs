using System.Net;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class IncidentPagesTests
{
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
        Guid version;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var incident = await db.Incidents.SingleAsync();
            incidentId = incident.Id; version = incident.Version;
            Assert.AreEqual("sev2", incident.Level);
            Assert.AreEqual(recipient, (await db.IncidentTimelineEntries.SingleAsync()).ActorId);
        }
        var details = $"/Incidents/Details/{incidentId}";
        AssertForbidden(await PostForm("/Incidents/Acknowledge", new() { ["IncidentId"] = incidentId.ToString(), ["Version"] = version.ToString() }, "/Manage/ChangePassword"));
        AssertForbidden(await PostForm("/Incidents/Delete", new() { ["id"] = incidentId.ToString(), ["version"] = version.ToString(), ["confirmationTitle"] = "Human report" }, "/Manage/ChangePassword"));
        AssertForbidden(await PostForm("/Applications/ResetApiKey", new() { ["id"] = applicationId.ToString() }, "/Manage/ChangePassword"));
        AssertForbidden(await Http.GetAsync(url));
        await LoginAsAdmin();
        AssertRedirect(await PostForm("/Applications/RemoveAccess", new() { ["id"] = applicationId.ToString(), ["userId"] = recipient }, url), url);
        await PostForm("/Account/Login", new() { ["EmailOrUserName"] = email, ["Password"] = password });
        AssertForbidden(await Http.GetAsync(details));
        AssertForbidden(await Http.GetAsync(createUrl));
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

using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class IncidentPagesTests
{
    private static readonly HttpClient AnonymousClient = new(
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

    private async Task<string> CreateApplicationKey(Guid applicationId, bool query = true, bool create = true, bool resolve = true, bool severity = false, bool mitigate = false)
    {
        var url = $"/ApplicationApiKeys?applicationId={applicationId}";
        using var response = await PostForm("/ApplicationApiKeys/Create", new()
        {
            ["ApplicationId"] = applicationId.ToString(), ["Name"] = "Integration key", ["IsEnabled"] = "true",
            ["CanQueryIncidents"] = query.ToString(), ["CanCreateIncidents"] = create.ToString(), ["CanResolveIncidents"] = resolve.ToString(), ["CanChangeIncidentSeverity"] = severity.ToString(), ["CanMitigateIncidents"] = mitigate.ToString()
        }, $"/ApplicationApiKeys/Create?applicationId={applicationId}");
        AssertRedirect(response, url);
        var html = await Http.GetStringAsync(response.Headers.Location);
        var key = Regex.Match(html, "value=\"([a-fA-F0-9-]{36}\\.[a-fA-F0-9-]{36}\\.[A-F0-9]{64})\"").Groups[1].Value;
        Assert.IsFalse(string.IsNullOrEmpty(key));
        Assert.DoesNotContain(key, await Http.GetStringAsync(url));
        return key;
    }

    private async Task<string> ReplaceApplicationKey(Guid applicationId)
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var key = await db.ApplicationApiKeys.SingleAsync(item => item.ApplicationId == applicationId);
        using var deleted = await PostForm("/ApplicationApiKeys/Delete", new()
        {
            ["applicationId"] = applicationId.ToString(), ["id"] = key.Id.ToString(), ["confirmationName"] = key.Name
        }, $"/ApplicationApiKeys?applicationId={applicationId}");
        Assert.AreEqual(HttpStatusCode.Redirect, deleted.StatusCode);
        return await CreateApplicationKey(applicationId);
    }

    [TestMethod]
    public async Task ApplicationsHaveIndependentKeysWithPersistentUsageAndRevocation()
    {
        var first = await CreateApplication();
        var applicationId = Guid.Parse(first.Split('.')[0]);
        var second = await CreateApplicationKey(applicationId);
        using var report = await Report(first, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Created, report.StatusCode);
        using var duplicate = await Report(second, Guid.Parse(JObject.Parse(await report.Content.ReadAsStringAsync()).Value<string>("ReportId")!));
        Assert.AreEqual(HttpStatusCode.Accepted, duplicate.StatusCode);
        var url = $"/ApplicationApiKeys?applicationId={applicationId}";
        using var missingCsrf = await PostForm("/ApplicationApiKeys/Delete", new() { ["applicationId"] = applicationId.ToString(), ["id"] = first.Split('.')[1], ["confirmationName"] = "Integration key" }, includeToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        using var wrongName = await PostForm("/ApplicationApiKeys/Delete", new() { ["applicationId"] = applicationId.ToString(), ["id"] = first.Split('.')[1], ["confirmationName"] = "wrong" }, url);
        Assert.AreEqual(HttpStatusCode.BadRequest, wrongName.StatusCode);
        using var deleted = await PostForm("/ApplicationApiKeys/Delete", new() { ["applicationId"] = applicationId.ToString(), ["id"] = first.Split('.')[1], ["confirmationName"] = "Integration key" }, url);
        Assert.AreEqual(HttpStatusCode.Redirect, deleted.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Report(first, Guid.NewGuid())).StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, (await Report(second, Guid.NewGuid())).StatusCode);
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var key = await db.ApplicationApiKeys.SingleAsync();
        Assert.AreEqual(applicationId, key.ApplicationId);
        Assert.AreEqual(2L, key.TotalCalls);
        Assert.IsNotNull(key.LastUsedAtUtc);
        Assert.AreNotEqual(second.Split('.')[2], key.SecretHash);
        Assert.AreEqual(2, await db.Incidents.CountAsync());
    }

    [TestMethod]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    [DataRow(false, false, false)]
    public async Task ApiKeyScopesAreEnforcedByEachProtocolEndpoint(bool query, bool create, bool resolve)
    {
        var full = await CreateApplication();
        var applicationId = Guid.Parse(full.Split('.')[0]);
        using var reported = await Report(full, Guid.NewGuid());
        var id = Guid.Parse(JObject.Parse(await reported.Content.ReadAsStringAsync()).Value<string>("IncidentId")!);
        Guid version;
        using (var scope = Server!.Services.CreateScope())
            version = (await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().Incidents.SingleAsync()).Version;
        var limited = await CreateApplicationKey(applicationId, query, create, resolve);
        using var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/incidents");
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", limited);
        using var queried = await Http.SendAsync(get);
        Assert.AreEqual(query ? 2 : -8, JObject.Parse(await queried.Content.ReadAsStringAsync()).Value<int>("Code"));
        using var created = await Report(limited, Guid.NewGuid());
        Assert.AreEqual(create ? 2 : -8, JObject.Parse(await created.Content.ReadAsStringAsync()).Value<int>("Code"));
        using var close = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/incidents/{id}/resolve");
        close.Content = JsonContent.Create(new { Version = version, Reason = "Scope test" });
        close.Headers.Authorization = new AuthenticationHeaderValue("Bearer", limited);
        using var closed = await Http.SendAsync(close);
        Assert.AreEqual(resolve ? 2 : -8, JObject.Parse(await closed.Content.ReadAsStringAsync()).Value<int>("Code"));
    }

    [TestMethod]
    public async Task KeyEditingAndApplicationDisableTakeEffectImmediately()
    {
        var key = await CreateApplication();
        var app = key.Split('.')[0];
        var url = $"/ApplicationApiKeys?applicationId={app}";
        using var edited = await PostForm("/ApplicationApiKeys/Edit", new()
        {
            ["ApplicationId"] = app, ["Id"] = key.Split('.')[1], ["Name"] = "Renamed integration", ["IsEnabled"] = "false", ["CanCreateIncidents"] = "true"
        }, url);
        Assert.AreEqual(HttpStatusCode.Redirect, edited.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Report(key, Guid.NewGuid())).StatusCode);
        Assert.Contains("Renamed integration", await Http.GetStringAsync(url));
        var other = await CreateApplicationKey(Guid.Parse(app));
        using var disabled = await PostForm("/Applications/SetEnabled", new() { ["id"] = app, ["enabled"] = "false" }, "/Applications/Applications");
        Assert.AreEqual(HttpStatusCode.Redirect, disabled.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Report(other, Guid.NewGuid())).StatusCode);
    }
    [TestMethod]
    public async Task KeyManagementIsScopedToApplicationsAndRequiresExplicitPermission()
    {
        var first = await CreateApplication();
        var second = await CreateApplication();
        var app = first.Split('.')[0];
        var other = second.Split('.')[0];
        var url = $"/ApplicationApiKeys?applicationId={app}";
        using var anonymous = await AnonymousClient.GetAsync(new Uri(Http.BaseAddress!, url));
        Assert.AreEqual(HttpStatusCode.Redirect, anonymous.StatusCode);
        using var wrongApplication = await PostForm("/ApplicationApiKeys/Edit", new()
        {
            ["ApplicationId"] = app, ["Id"] = second.Split('.')[1], ["Name"] = "Unauthorized edit"
        }, url);
        Assert.AreEqual(HttpStatusCode.NotFound, wrongApplication.StatusCode);
        using var forged = new HttpRequestMessage(HttpMethod.Get, "/api/v1/incidents");
        forged.Headers.Authorization = new AuthenticationHeaderValue("Bearer", app + "." + second.Split('.')[1] + "." + second.Split('.')[2]);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Http.SendAsync(forged)).StatusCode);
        var (email, _) = await RegisterAndLoginAsync();
        AssertForbidden(await Http.GetAsync(url));
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var user = await db.Users.SingleAsync(item => item.Email == email);
            db.ApplicationUserAccesses.Add(new ApplicationUserAccess { ApplicationId = Guid.Parse(app), UserId = user.Id, CanManageApiKeys = true });
            await db.SaveChangesAsync();
        }
        Assert.AreEqual(HttpStatusCode.OK, (await Http.GetAsync(url)).StatusCode);
        AssertForbidden(await Http.GetAsync($"/ApplicationApiKeys?applicationId={other}"));
        AssertForbidden(await PostForm("/ApplicationApiKeys/Create", new()
        {
            ["ApplicationId"] = app, ["Name"] = "Privilege escalation", ["CanCreateIncidents"] = "true"
        }, "/Manage/ChangePassword"));
        AssertForbidden(await PostForm("/ApplicationApiKeys/Create", new()
        {
            ["ApplicationId"] = app, ["Name"] = "Mitigation privilege escalation", ["CanMitigateIncidents"] = "true"
        }, "/Manage/ChangePassword"));
        var emptyScope = await CreateApplicationKey(Guid.Parse(app), false, false, false);
        Assert.AreEqual(-8, JObject.Parse(await (await Report(emptyScope, Guid.NewGuid())).Content.ReadAsStringAsync()).Value<int>("Code"));
        using var deleted = await PostForm("/ApplicationApiKeys/Delete", new()
        {
            ["applicationId"] = app, ["id"] = first.Split('.')[1], ["confirmationName"] = "Integration key"
        }, url);
        Assert.AreEqual(HttpStatusCode.Redirect, deleted.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Report(first, Guid.NewGuid())).StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, (await Report(second, Guid.NewGuid())).StatusCode);
    }

    [TestMethod]
    public async Task ConcurrentCallsDoNotLoseUsageCountsAndInvalidKeysAreNotCounted()
    {
        var key = await CreateApplication();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Report(key, Guid.NewGuid())));
        foreach (var response in responses)
        {
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            response.Dispose();
        }
        using var oldFormat = await Report(key.Split('.')[0] + "." + key.Split('.')[2], Guid.NewGuid());
        using var badSecret = await Report(key + "wrong", Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Unauthorized, oldFormat.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, badSecret.StatusCode);
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(8L, (await db.ApplicationApiKeys.SingleAsync()).TotalCalls);
    }

}

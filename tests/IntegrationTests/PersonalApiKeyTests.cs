using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.Client;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.SDK.Models;
using Aiursoft.CommandFramework;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using TestResult = Aiursoft.CommandFramework.Models.TestResult;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public sealed class PersonalApiKeyTests : TestBase
{
    private const string KeyPage = "/PersonalApiKeys";
    private const string Api = "/api/v1/personal/incidents";

    private async Task<(Guid Id, string Key)> CreateKey(bool canHandle = false)
    {
        using var response = await PostForm(KeyPage + "/Create", KeyForm(canHandle), KeyPage);
        response.EnsureSuccessStatusCode();
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        var html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "acpat_([a-f0-9]{32})\\.[A-F0-9]{64}");
        Assert.IsTrue(match.Success, "The form did not issue a personal key.");
        return (Guid.ParseExact(match.Groups[1].Value, "N"), match.Value);
    }

    private static Dictionary<string, string> KeyForm(bool canHandle = false) => new()
    {
        ["Input.Name"] = "Terminal", ["Input.LifetimeDays"] = "30", ["Input.CanHandleIncidents"] = canHandle ? "true" : "false"
    };

    private Task<TestResult> RunCli(string key, string command, params string[] args) => new NestedCommandApp()
        .WithFeature(new PersonalHandler())
        .TestRunAsync(["personal", command, "--server", Http.BaseAddress!.ToString(), "--api-key", key, "--json", .. args]);

    private static JObject Json(TestResult result)
    {
        Assert.AreEqual(0, result.ProgramReturn, result.StdErr);
        return JObject.Parse(result.StdOut);
    }

    private async Task<HttpResponseMessage> Bearer(string key, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body != null) request.Content = JsonContent.Create(body);
        return await Http.SendAsync(request);
    }

    [TestMethod]
    public async Task PersonalCliCanHandleIncidentsAcrossAuthorizedApplicationsWithoutBypassingScopeOrVersion()
    {
        var (email, _) = await RegisterAndLoginAsync();
        Incident mine, shared, readOnly, hidden;
        string accountId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            accountId = (await db.Users.SingleAsync(user => user.Email == email)).Id;
            mine = NewIncident("Owned", "sev2", accountId);
            shared = NewIncident("Shared", "sev2");
            readOnly = NewIncident("Read only", "sev4");
            hidden = NewIncident("Private", "sev2");
            var resolved = NewIncident("Old", "sev2", accountId);
            resolved.Status = Incident.Resolved;
            db.Incidents.AddRange(mine, shared, readOnly, hidden, resolved);
            db.ApplicationUserAccesses.AddRange(
                new ApplicationUserAccess { UserId = accountId, ApplicationId = shared.Application.Id, CanViewIncidents = true, CanUpdateIncidents = true },
                new ApplicationUserAccess { UserId = accountId, ApplicationId = readOnly.Application.Id, CanViewIncidents = true });
            (await db.SeverityRules.SingleAsync(rule => rule.Level == "sev4")).ShowInLists = false;
            await db.SaveChangesAsync();
        }
        var (_, key) = await CreateKey(canHandle: true);
        var (_, reader) = await CreateKey();
        var active = ReadIds(Json(await RunCli(key, "list", "--level", "sev2")));
        CollectionAssert.AreEquivalent(new[] { mine.Id, shared.Id }, active);
        var visible = ReadIds(Json(await RunCli(key, "list", "--all-visible")));
        CollectionAssert.AreEquivalent(new[] { mine.Id, shared.Id, readOnly.Id }, visible);
        Assert.AreEqual(4, ReadIds(Json(await RunCli(key, "list", "--all-visible", "--status", "all"))).Length);
        Assert.AreEqual(0, ReadIds(Json(await RunCli(key, "list", "--mine"))).Length);
        Assert.AreNotEqual(0, (await RunCli(key, "get", "--id", hidden.Id.ToString())).ProgramReturn);
        var sharedId = shared.Id.ToString();
        var initial = Json(await RunCli(reader, "get", "--id", sharedId))["incident"]!;
        Assert.IsFalse(initial.Value<bool>("canManage"));
        var version = initial.Value<string>("version")!;
        var denied = await RunCli(reader, "ack", "--id", sharedId, "--version", version);
        Assert.AreNotEqual(0, denied.ProgramReturn);
        Assert.Contains("Unauthorized", denied.StdErr);
        var premature = await RunCli(key, "resolve", "--id", sharedId, "--version", version, "--reason", "Recovered");
        Assert.AreNotEqual(0, premature.ProgramReturn);
        Json(await RunCli(key, "ack", "--id", sharedId, "--version", version));
        var stale = await RunCli(key, "mitigate", "--id", sharedId, "--version", version, "--reason", "Rolled back");
        Assert.AreNotEqual(0, stale.ProgramReturn);
        Assert.Contains("Conflict", stale.StdErr);
        version = Json(await RunCli(key, "get", "--id", sharedId))["incident"]!.Value<string>("version")!;
        CollectionAssert.AreEqual(new[] { shared.Id }, ReadIds(Json(await RunCli(key, "list", "--mine"))));
        Json(await RunCli(key, "mitigate", "--id", sharedId, "--version", version, "--reason", "Rolled back"));
        version = Json(await RunCli(key, "get", "--id", sharedId))["incident"]!.Value<string>("version")!;
        Json(await RunCli(key, "resolve", "--id", sharedId, "--version", version, "--reason", "Verified recovery"));
        var resolvedDetails = Json(await RunCli(key, "get", "--id", sharedId));
        Assert.AreEqual("resolved", resolvedDetails["incident"]!.Value<string>("status"));
        Assert.IsNotNull(resolvedDetails["incident"]!.Value<string>("ownerName"));
        Assert.AreEqual(3, resolvedDetails["activities"]!.Count());

        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var permission = await db.ApplicationUserAccesses.SingleAsync(item => item.ApplicationId == shared.ApplicationId && item.UserId == accountId);
            db.ApplicationUserAccesses.Remove(permission);
            await db.SaveChangesAsync();
        }
        Assert.AreNotEqual(0, (await RunCli(key, "get", "--id", sharedId)).ProgramReturn);
        using var noWrite = await Bearer(key, HttpMethod.Post, $"{Api}/{sharedId}/acknowledge",
            new MobileIncidentAction { Version = Guid.Parse(resolvedDetails["incident"]!.Value<string>("version")!) });
        Assert.AreEqual(Code.Unauthorized, (await noWrite.Content.ReadFromJsonAsync<AiurResponse>())!.Code);
    }

    [TestMethod]
    public async Task PersonalKeysAreShownOnceAndCannotBeManagedByOtherAccountsOrUsedOutsidePersonalApi()
    {
        var (ownerEmail, ownerPassword) = await RegisterAndLoginAsync();
        using var noCsrf = await PostForm(KeyPage + "/Create", KeyForm(), KeyPage, includeToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        var (id, key) = await CreateKey();
        using var keyPage = await Http.GetAsync(KeyPage);
        var html = await keyPage.Content.ReadAsStringAsync();
        Assert.Contains("href=\"/PersonalApiKeys", html);
        Assert.DoesNotContain(key, html);
        using var cookieOnly = await Http.GetAsync(Api);
        Assert.AreEqual(HttpStatusCode.Unauthorized, cookieOnly.StatusCode);
        using var wrongSecret = await Bearer(key[..^1] + (key[^1] == 'A' ? 'B' : 'A'), HttpMethod.Get, Api);
        Assert.AreEqual(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
        using var mobile = await Bearer(key, HttpMethod.Get, "/api/v1/mobile/incidents");
        Assert.AreEqual(HttpStatusCode.Unauthorized, mobile.StatusCode);
        using var integration = await Bearer(key, HttpMethod.Get, "/api/v1/incidents");
        Assert.AreEqual(HttpStatusCode.Unauthorized, integration.StatusCode);
        using var local = await Http.PostAsJsonAsync("/api/v1/auth/local/login", new LocalLoginRequest
            { EmailOrUserName = ownerEmail, Password = ownerPassword });
        local.EnsureSuccessStatusCode();
        var auth = (await local.Content.ReadFromJsonAsync<LocalAuthenticationResponse>())!;
        using var localInPersonal = await Bearer(auth.AccessToken, HttpMethod.Get, Api);
        Assert.AreEqual(HttpStatusCode.Unauthorized, localInPersonal.StatusCode);

        await RegisterAndLoginAsync();
        using var other = await PostForm(KeyPage + "/Revoke", new() { ["id"] = id.ToString() }, KeyPage);
        Assert.AreEqual(HttpStatusCode.NotFound, other.StatusCode);
        using var stillValid = await Bearer(key, HttpMethod.Get, Api);
        Assert.AreEqual(HttpStatusCode.OK, stillValid.StatusCode);

        using var login = await PostForm("/Account/Login", new()
            { ["EmailOrUserName"] = ownerEmail, ["Password"] = ownerPassword });
        Assert.AreEqual(HttpStatusCode.Found, login.StatusCode);
        using var noCsrfRevoke = await PostForm(KeyPage + "/Revoke", new() { ["id"] = id.ToString() }, KeyPage, includeToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, noCsrfRevoke.StatusCode);
        using var revoked = await PostForm(KeyPage + "/Revoke", new() { ["id"] = id.ToString() }, KeyPage);
        Assert.AreEqual(HttpStatusCode.Found, revoked.StatusCode);
        using var after = await Bearer(key, HttpMethod.Get, Api);
        Assert.AreEqual(HttpStatusCode.Unauthorized, after.StatusCode);

        var (expiredId, expiredKey) = await CreateKey();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var record = await db.PersonalApiKeys.SingleAsync(item => item.Id == expiredId);
            Assert.HasCount(32, record.SecretHash);
            record.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        using var expired = await Bearer(expiredKey, HttpMethod.Get, Api);
        Assert.AreEqual(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    private static Incident NewIncident(string app, string level, string? owner = null) => new()
    {
        Application = new SourceApplication { Name = app, OwnerId = owner }, Title = $"Incident in {app}", Level = level
    };

    private static Guid[] ReadIds(JObject list) => list["incidents"]!.Select(item => Guid.Parse(item.Value<string>("id")!)).ToArray();
}

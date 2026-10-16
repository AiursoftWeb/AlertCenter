using System.Net;
using System.Text.RegularExpressions;
using Aiursoft.AlertCenter.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class AlertDemoTests
{
    private static readonly HttpClient AnonymousClient = new(
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

    private async Task<string> ResetApplicationKey(Guid applicationId)
    {
        using var reset = await PostForm("/Alerts/ResetApiKey", new()
        {
            ["id"] = applicationId.ToString()
        }, "/Alerts/Applications");
        AssertRedirect(reset, "/Alerts/Applications");
        Assert.IsTrue(reset.Headers.CacheControl?.NoStore);
        using var response = await Http.GetAsync(reset.Headers.Location);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        var html = await response.Content.ReadAsStringAsync();
        var key = Regex.Match(html, "value=\"([a-fA-F0-9-]{36}\\.[A-F0-9]{64})\"").Groups[1].Value;
        Assert.IsFalse(string.IsNullOrEmpty(key));
        Assert.AreEqual(applicationId, Guid.Parse(key.Split('.')[0]));
        Assert.Contains("API Key has been reset", WebUtility.HtmlDecode(html));
        Assert.Contains("old key has been invalidated immediately", WebUtility.HtmlDecode(html));
        Assert.Contains("id=\"copy-key\"", html);
        Assert.Contains("key is displayed only once", WebUtility.HtmlDecode(html));
        Assert.DoesNotContain(key, await reset.Content.ReadAsStringAsync());
        return key;
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ResetApiKeyPreservesDataAndReplacesCredential(bool enabled)
    {
        var oldKey = await CreateApplication();
        var otherKey = await CreateApplication();
        var applicationId = Guid.Parse(oldKey.Split('.')[0]);
        var otherId = Guid.Parse(otherKey.Split('.')[0]);
        var reportId = Guid.NewGuid();
        using var report = await Report(oldKey, reportId);
        Assert.AreEqual(HttpStatusCode.Created, report.StatusCode);
        var receipt = JObject.Parse(await report.Content.ReadAsStringAsync());
        var messageId = Guid.Parse(receipt.Value<string>("id")!);
        var channel = new PushChannel { Name = "Preserved inbox", Type = "inapp" };
        var delivery = new PushDelivery { Channel = channel, MessageId = messageId, Status = PushDelivery.Succeeded, Attempts = 1 };
        var readAt = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);
        var notification = new InAppNotification { Delivery = delivery, ReadAtUtc = readAt };
        string oldHash;
        string otherHash;
        DateTime createdAt;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var application = await db.SourceApplications.SingleAsync(item => item.Id == applicationId);
            oldHash = application.SecretHash;
            createdAt = application.CreatedAtUtc;
            application.IsEnabled = enabled;
            otherHash = await db.SourceApplications.Where(item => item.Id == otherId).Select(item => item.SecretHash).SingleAsync();
            notification.UserId = await db.Users.Where(user => user.Email == "admin@default.com").Select(user => user.Id).SingleAsync();
            db.InAppNotifications.Add(notification);
            db.ApplicationPushChannels.Add(new ApplicationPushChannel
            {
                ApplicationId = applicationId, Channel = channel,
                ReceiveSev0 = true, ReceiveSev1 = true, ReceiveSev2 = false, ReceiveSev3 = false, ReceiveSev4 = true
            });
            await db.SaveChangesAsync();
        }

        var newKey = await ResetApplicationKey(applicationId);
        Assert.AreNotEqual(oldKey, newKey);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual(2, await db.SourceApplications.CountAsync());
            var application = await db.SourceApplications.SingleAsync(item => item.Id == applicationId);
            Assert.AreEqual("Demo Orders", application.Name);
            Assert.AreEqual(createdAt, application.CreatedAtUtc);
            Assert.AreEqual(enabled, application.IsEnabled);
            Assert.AreNotEqual(oldHash, application.SecretHash);
            Assert.AreNotEqual(newKey.Split('.')[1], application.SecretHash);
            var hasher = new PasswordHasher<SourceApplication>();
            Assert.AreEqual(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(application, application.SecretHash, oldKey.Split('.')[1]));
            Assert.AreEqual(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(application, application.SecretHash, newKey.Split('.')[1]));
            Assert.AreEqual(otherHash, await db.SourceApplications.Where(item => item.Id == otherId).Select(item => item.SecretHash).SingleAsync());
            Assert.AreEqual(messageId, (await db.ReportedMessages.SingleAsync()).Id);
            Assert.AreEqual(channel.Id, (await db.PushChannels.SingleAsync()).Id);
            var binding = await db.ApplicationPushChannels.SingleAsync();
            Assert.AreEqual(applicationId, binding.ApplicationId);
            Assert.AreEqual(channel.Id, binding.ChannelId);
            Assert.IsTrue(binding.ReceiveSev0 && binding.ReceiveSev1 && binding.ReceiveSev4);
            Assert.IsFalse(binding.ReceiveSev2 || binding.ReceiveSev3);
            var savedDelivery = await db.PushDeliveries.SingleAsync();
            Assert.AreEqual(delivery.Id, savedDelivery.Id);
            Assert.AreEqual(PushDelivery.Succeeded, savedDelivery.Status);
            Assert.AreEqual(1, savedDelivery.Attempts);
            var savedNotification = await db.InAppNotifications.SingleAsync();
            Assert.AreEqual(notification.Id, savedNotification.Id);
            Assert.AreEqual(readAt, savedNotification.ReadAtUtc);
        }

        using var rejectedOld = await Report(oldKey, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Unauthorized, rejectedOld.StatusCode);
        if (!enabled)
        {
            using var rejectedNew = await Report(newKey, Guid.NewGuid());
            Assert.AreEqual(HttpStatusCode.Unauthorized, rejectedNew.StatusCode);
            using var enable = await PostForm("/Alerts/SetEnabled", new()
            {
                ["id"] = applicationId.ToString(), ["enabled"] = "true"
            }, "/Alerts/Applications");
            AssertRedirect(enable, "/Alerts/Applications");
        }
        using var duplicate = await Report(newKey, reportId);
        Assert.AreEqual(HttpStatusCode.OK, duplicate.StatusCode);
        var duplicateReceipt = JObject.Parse(await duplicate.Content.ReadAsStringAsync());
        Assert.IsTrue(duplicateReceipt.Value<bool>("duplicate"));
        Assert.AreEqual(messageId, Guid.Parse(duplicateReceipt.Value<string>("id")!));
        using var accepted = await Report(newKey, Guid.NewGuid(), level: "sev3");
        Assert.AreEqual(HttpStatusCode.Created, accepted.StatusCode);
        using var otherAccepted = await Report(otherKey, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Created, otherAccepted.StatusCode);
        using var finalScope = Server!.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(3, await finalDb.ReportedMessages.CountAsync());
        Assert.AreEqual(1, await finalDb.PushDeliveries.CountAsync());
        Assert.AreEqual(1, await finalDb.InAppNotifications.CountAsync());
    }

    [TestMethod]
    public async Task ResetApiKeyShowsKeyOnceAndRefreshDoesNotResetAgain()
    {
        var oldKey = await CreateApplication();
        var applicationId = Guid.Parse(oldKey.Split('.')[0]);
        var html = await Http.GetStringAsync("/Alerts/Applications");
        Assert.Contains("data-bs-target=\"#reset-api-key\"", html);
        Assert.Contains($"data-application-id=\"{applicationId}\"", html);
        var form = Regex.Match(html, "<form[^>]*id=\"reset-api-key-form\"[^>]*>[\\s\\S]*?</form>").Value;
        Assert.Contains("action=\"/Alerts/ResetApiKey\"", form);
        Assert.Contains("method=\"post\"", form);
        Assert.Contains("name=\"__RequestVerificationToken\"", form);
        Assert.Contains("id=\"confirm-reset-api-key\" disabled", form);
        Assert.Contains("old key will become invalid immediately", WebUtility.HtmlDecode(form));

        var newKey = await ResetApplicationKey(applicationId);
        for (var refresh = 0; refresh < 3; refresh++)
        {
            using var response = await Http.GetAsync("/Alerts/Applications");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.CacheControl?.NoStore);
            html = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(oldKey, html);
            Assert.DoesNotContain(newKey, html);
            Assert.DoesNotContain("id=\"api-key\"", html);
        }
        using var accepted = await Report(newKey, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Created, accepted.StatusCode);
        var nextKey = await ResetApplicationKey(applicationId);
        Assert.AreNotEqual(newKey, nextKey);
        using var rejected = await Report(newKey, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using var nextAccepted = await Report(nextKey, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Created, nextAccepted.StatusCode);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not-a-guid")]
    [DataRow("00000000-0000-0000-0000-000000000000")]
    public async Task ResetApiKeyRejectsInvalidIds(string? id)
    {
        var key = await CreateApplication();
        var data = new Dictionary<string, string>();
        if (id != null) data["id"] = id;
        using var rejected = await PostForm("/Alerts/ResetApiKey", data, "/Alerts/Applications");
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.DoesNotContain("id=\"api-key\"", await Http.GetStringAsync("/Alerts/Applications"));
        using var accepted = await Report(key, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Created, accepted.StatusCode);
    }

    [TestMethod]
    public async Task ResetApiKeyRejectsUnknownApplication()
    {
        var key = await CreateApplication();
        using var rejected = await PostForm("/Alerts/ResetApiKey", new()
        {
            ["id"] = Guid.NewGuid().ToString()
        }, "/Alerts/Applications");
        Assert.AreEqual(HttpStatusCode.NotFound, rejected.StatusCode);
        Assert.DoesNotContain("id=\"api-key\"", await Http.GetStringAsync("/Alerts/Applications"));
        using var accepted = await Report(key, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Created, accepted.StatusCode);
    }

    [TestMethod]
    public async Task ResetApiKeyRejectsGetAndMissingAntiforgeryToken()
    {
        var key = await CreateApplication();
        var applicationId = key.Split('.')[0];
        using var get = await Http.GetAsync($"/Alerts/ResetApiKey?id={applicationId}");
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        using var missingToken = await PostForm("/Alerts/ResetApiKey", new()
        {
            ["id"] = applicationId
        }, includeToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingToken.StatusCode);
        using var accepted = await Report(key, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Created, accepted.StatusCode);
    }

    [TestMethod]
    public async Task ResetApiKeyRequiresAdministrator()
    {
        var key = await CreateApplication();
        var applicationId = key.Split('.')[0];
        using var anonymousResult = await AnonymousClient.PostAsync(new Uri(Http.BaseAddress!, "/Alerts/ResetApiKey"), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = applicationId
        }));
        Assert.AreEqual(HttpStatusCode.Found, anonymousResult.StatusCode);
        Assert.Contains("/Account/Login", anonymousResult.Headers.Location!.OriginalString);

        await RegisterAndLoginAsync();
        using var forbidden = await PostForm("/Alerts/ResetApiKey", new()
        {
            ["id"] = applicationId
        }, "/");
        Assert.AreEqual(HttpStatusCode.Found, forbidden.StatusCode);
        Assert.Contains("Code403", forbidden.Headers.Location!.OriginalString);
        using var accepted = await Report(key, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Created, accepted.StatusCode);
    }
}

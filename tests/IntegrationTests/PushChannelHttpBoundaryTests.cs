using System.Net;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class IncidentPagesTests
{
    [TestMethod]
    [DataRow("webhook")]
    [DataRow("email")]
    public async Task ChannelBindingPermissionDoesNotGrantConfigurationSecretsOrChannelManagement(string type)
    {
        var key = await CreateApplication();
        var applicationId = Guid.Parse(key.Split('.')[0]);
        using var report = await Report(key, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Created, report.StatusCode);
        var (email, _) = await RegisterAndLoginAsync();
        const string endpoint = "https://receiver.example/private-channel-secret-token";
        const string recipient = "private-recipient@example.com";
        const string cc = "private-copy@example.com";
        Guid channelId;
        Guid deliveryId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var userId = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
            var channel = new PushChannel
            {
                Name = "Limited binding channel", Type = type,
                Endpoint = type == "webhook" ? endpoint : null,
                EmailRecipient = type == "email" ? recipient : string.Empty,
                EmailCcRecipients = type == "email" ? cc : string.Empty
            };
            var delivery = new PushDelivery
            {
                Channel = channel, IncidentId = await db.Incidents.Select(message => message.Id).SingleAsync(),
                Status = PushDelivery.Succeeded, Attempts = 1
            };
            db.PushDeliveries.Add(delivery);
            db.ApplicationUserAccesses.Add(new ApplicationUserAccess { ApplicationId = applicationId, UserId = userId, CanManage = true });
            db.PushChannelBindingUsers.Add(new PushChannelBindingUser { Channel = channel, UserId = userId });
            db.ApplicationPushChannels.Add(new ApplicationPushChannel
            {
                ApplicationId = applicationId, Channel = channel,
                ReceiveSev0 = false, ReceiveSev1 = true, ReceiveSev2 = false, ReceiveSev3 = false, ReceiveSev4 = false
            });
            await db.SaveChangesAsync();
            channelId = channel.Id;
            deliveryId = delivery.Id;
        }

        foreach (var url in new[] { $"/Push/Bindings/{applicationId}", $"/Push/Delivery/{deliveryId}", "/Push/Records" })
        {
            using var allowed = await Http.GetAsync(url);
            Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
            var html = await allowed.Content.ReadAsStringAsync();
            Assert.Contains("Limited binding channel", html);
            Assert.DoesNotContain(endpoint, html);
            Assert.DoesNotContain("private-channel-secret-token", html);
            Assert.DoesNotContain(recipient, html);
            Assert.DoesNotContain(cc, html);
        }
        foreach (var url in new[] { $"/Push/Edit/{channelId}", $"/Push/Create?type={type}", "/Push/SearchRecipients?query=a", "/Push" })
            AssertForbidden(await Http.GetAsync(url));
        AssertForbidden(await PostForm("/Push/Save", new()
        {
            ["Id"] = channelId.ToString(), ["Name"] = "Unauthorized change", ["Type"] = type,
            ["Endpoint"] = "https://attacker.example/", ["EmailRecipient"] = "attacker@example.com"
        }, "/Manage/ChangePassword"));
        foreach (var action in new[] { "Test", "Delete", "Retry" })
            AssertForbidden(await PostForm($"/Push/{action}", new()
            {
                ["id"] = (action == "Retry" ? deliveryId : channelId).ToString()
            }, "/Manage/ChangePassword"));
        AssertForbidden(await PostForm("/Push/SetEnabled", new()
        {
            ["id"] = channelId.ToString(), ["enabled"] = "false"
        }, "/Manage/ChangePassword"));

        var (channelOnlyEmail, _) = await RegisterAndLoginAsync();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var userId = await db.Users.Where(user => user.Email == channelOnlyEmail).Select(user => user.Id).SingleAsync();
            db.PushChannelBindingUsers.Add(new PushChannelBindingUser { ChannelId = channelId, UserId = userId });
            await db.SaveChangesAsync();
        }
        AssertForbidden(await Http.GetAsync($"/Push/Bindings/{applicationId}"));
        AssertForbidden(await Http.GetAsync($"/Push/Delivery/{deliveryId}"));
        foreach (var url in new[] { "/Push/Records", $"/Push/Records?channelId={channelId}" })
        {
            var channelOnlyRecords = await Http.GetStringAsync(url);
            var selected = url.Contains('?') ? " selected=\"selected\"" : string.Empty;
            Assert.Contains($"<option value=\"{channelId}\"{selected}>Limited binding channel</option>", channelOnlyRecords);
            Assert.DoesNotContain($"/Push/Delivery/{deliveryId}", channelOnlyRecords);
            Assert.DoesNotContain("Order timeout", channelOnlyRecords);
            Assert.DoesNotContain(endpoint, channelOnlyRecords);
            Assert.DoesNotContain(recipient, channelOnlyRecords);
            Assert.DoesNotContain(cc, channelOnlyRecords);
        }
        AssertForbidden(await PostForm("/Push/SaveBindings", new()
        {
            ["id"] = applicationId.ToString(), ["Sev0ChannelIds"] = channelId.ToString()
        }, "/Manage/ChangePassword"));

        using var finalScope = Server!.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var saved = await finalDb.PushChannels.SingleAsync();
        Assert.AreEqual(channelId, saved.Id);
        Assert.AreEqual("Limited binding channel", saved.Name);
        Assert.IsTrue(saved.IsEnabled);
        Assert.AreEqual(type == "webhook" ? endpoint : null, saved.Endpoint);
        Assert.AreEqual(type == "email" ? recipient : string.Empty, saved.EmailRecipient);
        Assert.AreEqual(type == "email" ? cc : string.Empty, saved.EmailCcRecipients);
        var savedDelivery = await finalDb.PushDeliveries.SingleAsync();
        Assert.AreEqual(deliveryId, savedDelivery.Id);
        Assert.AreEqual(PushDelivery.Succeeded, savedDelivery.Status);
        Assert.AreEqual(1, savedDelivery.Attempts);
        var binding = await finalDb.ApplicationPushChannels.SingleAsync();
        Assert.IsTrue(binding.ReceiveSev1);
        Assert.IsFalse(binding.ReceiveSev0 || binding.ReceiveSev2 || binding.ReceiveSev3 || binding.ReceiveSev4);
    }
}

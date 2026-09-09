using System.Net;
using Aiursoft.AlertCenter.Authorization;
using Aiursoft.AlertCenter.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class NotificationRecipientsTests : TestBase
{
    [TestMethod]
    public async Task InvalidRecipientsRedisplaySelectionsWithoutChangingSavedConfiguration()
    {
        await LoginAsAdmin();
        string userId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            userId = await db.Users.Select(user => user.Id).FirstAsync();
        }
        AssertRedirect(await PostForm("/Push/Save", new()
        {
            ["Name"] = "Targeted", ["Type"] = "inapp", ["RecipientUserIds"] = userId
        }, "/Push/Create?type=inapp"), "/Push");
        Guid channelId;
        using (var scope = Server!.Services.CreateScope())
            channelId = await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().PushChannels.Where(channel => channel.Name == "Targeted").Select(channel => channel.Id).SingleAsync();
        foreach (var targets in new[] { new Dictionary<string, string>(), new() { ["RecipientUserIds"] = "missing-user" }, new() { ["RecipientUserIds"] = userId, ["RecipientRoleIds"] = "missing-role" } })
        {
            targets["Id"] = channelId.ToString();
            targets["Name"] = "Must not save";
            targets["Type"] = "inapp";
            var response = await PostForm("/Push/Save", targets, $"/Push/Edit/{channelId}");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("validation-summary-errors", html);
            Assert.Contains(userId, html);
            using var scope = Server!.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual("Targeted", (await db.PushChannels.SingleAsync(channel => channel.Id == channelId)).Name);
            Assert.AreEqual(userId, (await db.PushChannelUsers.SingleAsync(link => link.ChannelId == channelId)).UserId);
        }
    }

    [TestMethod]
    public async Task OrdinaryRecipientCanReadOwnNotificationsButCannotManageOrReadOthers()
    {
        var (email, _) = await RegisterAndLoginAsync();
        Guid ownId;
        Guid otherId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var user = await db.Users.SingleAsync(user => user.Email == email);
            var admin = await db.Users.SingleAsync(user => user.UserName == "admin");
            var channel = new PushChannel { Name = "Private channel", Type = "inapp" };
            var own = new InAppNotification
            {
                UserId = user.Id,
                Delivery = new PushDelivery
                {
                    Channel = channel,
                    Message = new ReportedMessage
                    {
                        Application = new SourceApplication { Name = "Private app" },
                        Title = "Recipient-only title", Content = "<script>private content</script>", Level = "sev3"
                    }
                }
            };
            var other = new InAppNotification { UserId = admin.Id, Delivery = new PushDelivery { Channel = channel } };
            db.InAppNotifications.AddRange(own, other);
            await db.SaveChangesAsync();
            ownId = own.Id;
            otherId = other.Id;
        }
        var inbox = await Http.GetStringAsync("/Push/Inbox");
        Assert.Contains($"/Push/Notification/{ownId}", inbox);
        Assert.DoesNotContain($"/Push/Notification/{otherId}", inbox);
        Assert.DoesNotContain("href=\"/Push/Create", inbox);
        var detail = await Http.GetStringAsync($"/Push/Notification/{ownId}");
        Assert.Contains("Recipient-only title", detail);
        Assert.DoesNotContain("<script>private content</script>", detail);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync($"/Push/Notification/{otherId}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await PostForm("/Push/MarkRead", new() { ["id"] = otherId.ToString() }, "/Push/Inbox")).StatusCode);
        AssertRedirect(await PostForm("/Push/MarkRead", new() { ["id"] = ownId.ToString() }, "/Push/Inbox"), "/Push/Inbox", exact: false);
        foreach (var url in new[] { "/Push", "/Push/Create?type=inapp", "/Push/Records", $"/Push/Delivery/{Guid.NewGuid()}" })
        {
            var response = await Http.GetAsync(url);
            Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
            Assert.Contains("/Error/Code403", response.Headers.Location!.ToString());
        }
        var denied = await PostForm("/Push/Save", new() { ["Name"] = "Forbidden", ["Type"] = "inapp" }, "/Push/Inbox");
        Assert.Contains("/Error/Code403", denied.Headers.Location!.ToString());
    }

    [TestMethod]
    public async Task PushPermissionCanBeDelegatedToANonAdministratorRole()
    {
        var (email, password) = await RegisterAndLoginAsync();
        string userId;
        using (var scope = Server!.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var role = new IdentityRole("Notification managers");
            Assert.IsTrue((await roles.CreateAsync(role)).Succeeded);
            Assert.IsTrue((await roles.AddClaimAsync(role, new Claim(AppPermissions.Type, AppPermissionNames.CanManagePush))).Succeeded);
            var user = (await users.FindByEmailAsync(email))!;
            userId = user.Id;
            Assert.IsTrue((await users.AddToRoleAsync(user, role.Name!)).Succeeded);
        }
        await Http.GetAsync("/Account/LogOff");
        await PostForm("/Account/Login", new() { ["EmailOrUserName"] = email, ["Password"] = password });
        var edit = await Http.GetStringAsync("/Push/Create?type=inapp");
        Assert.Contains("RecipientUserIds", edit);
        Assert.Contains("RecipientRoleIds", edit);
        AssertRedirect(await PostForm("/Push/Save", new()
        {
            ["Name"] = "Personal channel", ["Type"] = "inapp", ["RecipientUserIds"] = userId, ["IsEnabled"] = "true"
        }, "/Push/Create?type=inapp"), "/Push");
        using var finalScope = Server!.Services.CreateScope();
        var db = finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(userId, (await db.PushChannelUsers.SingleAsync(link => link.UserId == userId && link.Channel.Name == "Personal channel")).UserId);
    }
}

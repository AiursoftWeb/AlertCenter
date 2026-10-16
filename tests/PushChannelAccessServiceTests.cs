using System.Security.Claims;
using Aiursoft.AlertCenter.Authorization;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.InMemory;
using Aiursoft.AlertCenter.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class PushChannelAccessServiceTests
{
    [TestMethod]
    public async Task DirectAndRoleBindingAccessIsLimitedToGrantedChannels()
    {
        await using var db = await CreateContextAsync();
        var first = new PushChannel { Name = "First", Type = "email" };
        var second = new PushChannel { Name = "Second", Type = "email" };
        var user = User("user");
        var role = new IdentityRole("Operations");
        db.AddRange(first, second, user, role);
        db.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = role.Id });
        db.PushChannelBindingUsers.Add(new PushChannelBindingUser { Channel = first, UserId = user.Id });
        db.PushChannelBindingRoles.Add(new PushChannelBindingRole { Channel = second, RoleId = role.Id });
        await db.SaveChangesAsync();
        var access = new PushChannelAccessService(db);
        var principal = Principal(user.Id);

        Assert.IsTrue(await access.CanBindAsync(principal, first.Id));
        Assert.IsTrue(await access.CanBindAsync(principal, second.Id));
        CollectionAssert.AreEquivalent(new[] { first.Id, second.Id }, await access.GetBindableChannelIdsAsync(principal));
    }

    [TestMethod]
    public async Task PushPermissionGrantsGlobalBindingAccessButRoleNameAloneDoesNot()
    {
        await using var db = await CreateContextAsync();
        var channel = new PushChannel { Name = "Operations", Type = "email" };
        db.PushChannels.Add(channel);
        await db.SaveChangesAsync();
        var access = new PushChannelAccessService(db);
        var pushManager = Principal("manager", new Claim(AppPermissions.Type, AppPermissionNames.CanManagePush));
        var administrator = Principal("admin", new Claim(ClaimTypes.Role, "Administrators"));

        Assert.IsTrue(await access.CanBindAsync(pushManager, channel.Id));
        Assert.IsFalse(await access.CanBindAsync(administrator, channel.Id));
        CollectionAssert.AreEquivalent(new[] { channel.Id }, await access.GetBindableChannelIdsAsync(pushManager));
    }

    private static async Task<InMemoryContext> CreateContextAsync()
    {
        var db = new InMemoryContext(new DbContextOptionsBuilder<InMemoryContext>()
            .UseInMemoryDatabase($"push-channel-acl-{Guid.NewGuid():N}").Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static User User(string id) => new()
    {
        Id = id,
        UserName = id,
        NormalizedUserName = id.ToUpperInvariant(),
        DisplayName = id
    };

    private static ClaimsPrincipal Principal(string userId, params Claim[] claims) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, userId), .. claims], "test"));
}

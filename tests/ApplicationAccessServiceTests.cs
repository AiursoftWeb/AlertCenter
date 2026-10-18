using Aiursoft.AlertCenter.Authorization;
using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class ApplicationAccessServiceTests
{
    private SqliteContext _db = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.MigrateAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _db.DisposeAsync();

    private static ClaimsPrincipal Actor(string id) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));

    [TestMethod]
    public async Task OwnershipAndExplicitGrantsAreScopedAndPlatformPermissionDoesNotBypassThem()
    {
        var owner = new User { Id = "owner", UserName = "owner", DisplayName = "Owner" };
        var member = new User { Id = "member", UserName = "member", DisplayName = "Member" };
        var role = new IdentityRole("Engineers");
        var application = new SourceApplication { Name = "Orders", Owner = owner };
        var other = new SourceApplication { Name = "Private" };
        _db.AddRange(application, other, member, role);
        _db.UserRoles.Add(new IdentityUserRole<string> { UserId = member.Id, RoleId = role.Id });
        _db.ApplicationUserAccesses.Add(new ApplicationUserAccess { Application = application, User = member, CanViewIncidents = true });
        _db.ApplicationRoleAccesses.Add(new ApplicationRoleAccess { Application = application, Role = role, CanUpdateIncidents = true });
        await _db.SaveChangesAsync();
        var service = new ApplicationAccessService(_db);
        foreach (var permission in Enum.GetValues<ApplicationPermission>())
        {
            Assert.IsTrue(await service.CanAccessAsync(Actor(owner.Id), application.Id, permission));
            Assert.IsFalse(await service.CanAccessAsync(Actor(owner.Id), other.Id, permission));
        }
        Assert.IsTrue(await service.CanAccessAsync(Actor(member.Id), application.Id, ApplicationPermission.UpdateIncidents));
        Assert.IsFalse(await service.CanAccessAsync(Actor(member.Id), application.Id, ApplicationPermission.ManageApiKeys));
        var manager = Actor("platform-manager");
        ((ClaimsIdentity)manager.Identity!).AddClaim(new Claim(AppPermissions.Type, AppPermissionNames.CanManageApplications));
        ((ClaimsIdentity)manager.Identity!).AddClaim(new Claim(ClaimTypes.Role, "Administrators"));
        Assert.IsTrue(service.CanManageApplications(manager));
        Assert.IsFalse(await service.CanAccessAsync(manager, application.Id));
        Assert.AreEqual(0, (await service.GetAccessibleApplicationIdsAsync(manager)).Count);
        _db.UserRoles.Remove(await _db.UserRoles.SingleAsync());
        await _db.SaveChangesAsync();
        Assert.IsFalse(await service.CanAccessAsync(Actor(member.Id), application.Id, ApplicationPermission.UpdateIncidents));
        Assert.IsTrue(await service.CanAccessAsync(Actor(member.Id), application.Id));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EveryPermissionIsIndependentForUserAndRoleGrants(bool roleGrant)
    {
        var member = new User { Id = "member", UserName = "member", DisplayName = "Member" };
        var role = new IdentityRole("Operators");
        var application = new SourceApplication { Name = "App" };
        _db.AddRange(member, role, application);
        _db.UserRoles.Add(new IdentityUserRole<string> { UserId = member.Id, RoleId = role.Id });
        var direct = new ApplicationUserAccess { Application = application, User = member };
        var inherited = new ApplicationRoleAccess { Application = application, Role = role };
        object grant = roleGrant ? inherited : direct;
        _db.Add(grant);
        var service = new ApplicationAccessService(_db);
        foreach (var selected in Enum.GetValues<ApplicationPermission>())
        {
            foreach (var property in grant.GetType().GetProperties().Where(property => property.PropertyType == typeof(bool)))
                property.SetValue(grant, property.Name == "Can" + selected || property.Name == nameof(ApplicationUserAccess.CanViewIncidents));
            await _db.SaveChangesAsync();
            foreach (var permission in Enum.GetValues<ApplicationPermission>())
                Assert.AreEqual(permission == selected || permission == ApplicationPermission.ViewIncidents,
                    await service.CanAccessAsync(Actor(member.Id), application.Id, permission), permission.ToString());
        }
    }
}

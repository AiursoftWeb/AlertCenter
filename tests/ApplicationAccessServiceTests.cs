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

    [TestMethod]
    public async Task DirectAndRoleGrantsRemainScopedAndPlatformPermissionGrantsGlobalAccess()
    {
        var application = new SourceApplication { Name = "Orders" };
        var role = new IdentityRole("OnCall");
        var user = new User { Id = "on-call", UserName = "on-call", DisplayName = "On-call" };
        _db.AddRange(application, role, user);
        _db.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = role.Id });
        _db.ApplicationUserAccesses.Add(new ApplicationUserAccess { Application = application, User = user, CanManage = false });
        _db.ApplicationRoleAccesses.Add(new ApplicationRoleAccess { Application = application, Role = role, CanManage = true });
        await _db.SaveChangesAsync();
        var service = new ApplicationAccessService(_db);
        var onCall = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id)], "test"));
        var administrator = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Administrators")], "test"));

        Assert.IsTrue(await service.CanAccessAsync(onCall, application.Id));
        Assert.IsTrue(await service.CanAccessAsync(onCall, application.Id, requireManage: true));
        CollectionAssert.AreEquivalent(new[] { application.Id }, await service.GetAccessibleApplicationIdsAsync(onCall, true));
        Assert.IsFalse(await service.CanAccessAsync(administrator, application.Id, requireManage: true));
        var manager = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AppPermissions.Type, AppPermissionNames.CanManageApplications)], "test"));
        Assert.IsTrue(await service.CanAccessAsync(manager, application.Id, requireManage: true));
        CollectionAssert.AreEquivalent(new[] { application.Id }, await service.GetAccessibleApplicationIdsAsync(manager, true));
    }
}

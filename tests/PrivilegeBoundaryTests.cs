using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Aiursoft.AlertCenter.Authorization;
using Aiursoft.AlertCenter.Controllers;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models.RolesViewModels;
using Aiursoft.AlertCenter.Models.UsersViewModels;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class PrivilegeBoundaryTests
{
    private SqliteContext _db = null!;
    private RoleManager<IdentityRole> _roleManager = null!;
    private UserManager<User> _userManager = null!;
    private ServiceProvider _services = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddDbContext<SqliteContext>(options => options.UseSqlite("DataSource=:memory:"));
        services.AddIdentityCore<User>().AddRoles<IdentityRole>().AddEntityFrameworkStores<SqliteContext>().AddDefaultTokenProviders();
        _services = services.BuildServiceProvider();
        _db = _services.GetRequiredService<SqliteContext>();
        await _db.Database.OpenConnectionAsync();
        await _db.Database.MigrateAsync();
        _roleManager = _services.GetRequiredService<RoleManager<IdentityRole>>();
        _userManager = _services.GetRequiredService<UserManager<User>>();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _services.DisposeAsync();
    }

    [TestMethod]
    public async Task RoleAssignmentPermissionAppliesToAdministratorsAsInTemplate()
    {
        var administrators = await CreateRole("Administrators");
        var operators = await CreateRole("Operators");
        var target = await CreateUser("target");
        var administrator = await CreateUser("administrator");
        await _userManager.AddToRoleAsync(administrator, administrators.Name!);
        var controller = Users(operators, AppPermissionNames.CanAssignRoleToUser);

        var addAdministrator = await controller.ManageRoles(target.Id, new Models.UsersViewModels.EditViewModel
        {
            Id = target.Id, UserName = target.UserName!, Email = target.Email!, DisplayName = target.DisplayName,
            AvatarUrl = User.DefaultAvatarPath,
            AllRoles = [new UserRoleViewModel { RoleName = administrators.Name!, IsSelected = true }]
        });
        var modifyAdministrator = await controller.ManageRoles(administrator.Id, new Models.UsersViewModels.EditViewModel
        {
            Id = administrator.Id, UserName = administrator.UserName!, Email = administrator.Email!, DisplayName = administrator.DisplayName,
            AvatarUrl = User.DefaultAvatarPath,
            AllRoles = [new UserRoleViewModel { RoleName = operators.Name!, IsSelected = true }]
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(addAdministrator);
        Assert.IsInstanceOfType<RedirectToActionResult>(modifyAdministrator);
        Assert.IsTrue(await _userManager.IsInRoleAsync(target, administrators.Name!));
        Assert.IsFalse(await _userManager.IsInRoleAsync(administrator, administrators.Name!));
    }

    [TestMethod]
    public async Task UserManagementPermissionsApplyToAdministratorsAsInTemplate()
    {
        var administrators = await CreateRole("Administrators");
        var operators = await CreateRole("Operators");
        var administrator = await CreateUser("administrator");
        await _userManager.AddToRoleAsync(administrator, administrators.Name!);
        var controller = Users(operators, AppPermissionNames.CanEditUsers, AppPermissionNames.CanDeleteUsers);

        var edit = await controller.Edit(new Models.UsersViewModels.EditViewModel
        {
            Id = administrator.Id, UserName = administrator.UserName!, Email = "changed@example.test",
            DisplayName = administrator.DisplayName, AvatarUrl = User.DefaultAvatarPath, Password = "Changed-Password-123"
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(edit);
        var stored = await _userManager.FindByIdAsync(administrator.Id);
        Assert.IsNotNull(stored);
        Assert.AreEqual("changed@example.test", stored.Email);
        Assert.IsTrue(await _userManager.CheckPasswordAsync(stored, "Changed-Password-123"));
        Assert.IsInstanceOfType<RedirectToActionResult>(await controller.DeleteConfirmed(administrator.Id));
        Assert.IsNull(await _userManager.FindByIdAsync(administrator.Id));
    }

    [TestMethod]
    public async Task RoleEditingPermissionAllowsPermissionDelegationAsInTemplate()
    {
        var administrators = await CreateRole("Administrators");
        var operators = await CreateRole("Operators");
        var controller = Roles(operators, AppPermissionNames.CanEditRoles);

        var grantPermission = await controller.Edit(new Models.RolesViewModels.EditViewModel
        {
            Id = operators.Id, RoleName = operators.Name!,
            Claims = [new RoleClaimViewModel { Key = AppPermissionNames.CanManagePush, Name = "Push", Description = "Push", IsSelected = true }]
        });
        var editAdministrators = await controller.Edit(new Models.RolesViewModels.EditViewModel
        {
            Id = administrators.Id, RoleName = "Renamed administrators", Claims = []
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(grantPermission);
        Assert.IsInstanceOfType<RedirectToActionResult>(editAdministrators);
        Assert.IsTrue((await _roleManager.GetClaimsAsync(operators)).Any(claim => claim.Value == AppPermissionNames.CanManagePush));
        Assert.AreEqual("Renamed administrators", (await _roleManager.FindByIdAsync(administrators.Id))!.Name);
    }

    private async Task<IdentityRole> CreateRole(string name)
    {
        var role = new IdentityRole(name);
        Assert.IsTrue((await _roleManager.CreateAsync(role)).Succeeded);
        return role;
    }

    private async Task<User> CreateUser(string name)
    {
        var user = new User { UserName = name, Email = name + "@example.test", DisplayName = name };
        Assert.IsTrue((await _userManager.CreateAsync(user, "Initial-Password-123")).Succeeded);
        return user;
    }

    private UsersController Users(IdentityRole role, params string[] permissions) => new(_roleManager, _userManager, _db)
    {
        ControllerContext = ControllerContext(role, permissions)
    };

    private RolesController Roles(IdentityRole role, params string[] permissions) => new(_userManager, _db, _roleManager)
    {
        ControllerContext = ControllerContext(role, permissions)
    };

    private static ControllerContext ControllerContext(IdentityRole role, params string[] permissions) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Role, role.Name!), .. permissions.Select(permission => new Claim(AppPermissions.Type, permission))], "test"))
        }
    };
}

using System.Security.Claims;
using Aiursoft.AlertCenter.Controllers;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallControllerTests
{
    [TestMethod]
    public async Task AdminSavesAndEditsGeneratedRuleWithOrderedEmployees()
    {
        await using var db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        db.Users.AddRange(
            new User { Id = "a", UserName = "a", NormalizedUserName = "A", DisplayName = "Alice" },
            new User { Id = "b", UserName = "b", NormalizedUserName = "B", DisplayName = "Bob" },
            new User { Id = "c", UserName = "c", NormalizedUserName = "C", DisplayName = "Carol" });
        var group = new OnCallGroup { Name = "Operations" };
        db.OnCallGroups.Add(group);
        await db.SaveChangesAsync();
        var controller = new OnCallController(db, new OnCallScheduleGenerator())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Administrators")], "test"))
            } }
        };
        var anchor = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        var request = new SaveOnCallRuleRequest
        {
            GroupId = group.Id, Name = "Regular", AnchorUtc = anchor,
            UserIds = ["b", "a", "c"]
        };
        var created = Assert.IsInstanceOfType<RedirectToActionResult>(await controller.SaveRule(request, CancellationToken.None));
        Assert.AreEqual(nameof(OnCallController.Details), created.ActionName);
        db.ChangeTracker.Clear();
        var rule = await db.OnCallRules.Include(item => item.Members).SingleAsync();
        CollectionAssert.AreEqual(new[] { "b", "a", "c" },
            rule.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray());
        var missing = await controller.Details(Guid.NewGuid(), CancellationToken.None);
        Assert.IsInstanceOfType<NotFoundResult>(missing);
        request.Id = rule.Id;
        request.BalanceShiftTimes = true;
        request.UserIds = ["a", "b", "c"];
        var edited = Assert.IsInstanceOfType<RedirectToActionResult>(await controller.SaveRule(request, CancellationToken.None));
        Assert.AreEqual(nameof(OnCallController.Details), edited.ActionName);
        db.ChangeTracker.Clear();
        rule = await db.OnCallRules.Include(item => item.Members).SingleAsync();
        Assert.AreEqual(8, rule.ShiftHours);
        Assert.IsTrue(rule.BalanceShiftTimes);
        CollectionAssert.AreEqual(new[] { "a", "b", "c" },
            rule.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray());
    }

    [TestMethod]
    public async Task DeleteRuleRemovesPersistedScheduleInSqlite()
    {
        await using var db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        var user = new User { Id = "engineer", UserName = "engineer", NormalizedUserName = "ENGINEER", DisplayName = "Engineer" };
        var rule = new OnCallRule
        {
            Group = new OnCallGroup { Name = "Team", TimeZoneId = "UTC" }, Name = "Rotation", AnchorUtc = DateTime.UtcNow.Date,
            Members = [new OnCallRuleMember { UserId = user.Id, RotationOrder = 0 }]
        };
        db.Users.Add(user);
        db.OnCallShifts.Add(new OnCallShift
        {
            Rule = rule, StartsAtUtc = DateTime.UtcNow.Date, EndsAtUtc = DateTime.UtcNow.Date.AddHours(8),
            Members = [new OnCallShiftMember { UserId = user.Id }]
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new OnCallController(db, new OnCallScheduleGenerator());
        var result = await controller.DeleteRule(rule.Id, CancellationToken.None);
        Assert.AreEqual(nameof(OnCallController.Index), Assert.IsInstanceOfType<RedirectToActionResult>(result).ActionName);
        db.ChangeTracker.Clear();
        Assert.AreEqual(0, await db.OnCallRules.CountAsync());
        Assert.AreEqual(0, await db.OnCallRuleMembers.CountAsync());
        Assert.AreEqual(0, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(0, await db.OnCallShiftMembers.CountAsync());
        Assert.AreEqual(1, await db.OnCallGroups.CountAsync());
        Assert.AreEqual(1, await db.Users.CountAsync());
    }

    [TestMethod]
    public async Task SearchUsersFiltersByRoleAndPaginatesWithoutReturningEveryone()
    {
        await using var db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        var role = new IdentityRole { Id = "operators", Name = "Operators", NormalizedName = "OPERATORS" };
        db.Roles.Add(role);
        for (var i = 0; i < 25; i++)
        {
            var id = $"user-{i:D2}";
            db.Users.Add(new User { Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant(), DisplayName = id });
            db.UserRoles.Add(new IdentityUserRole<string> { RoleId = role.Id, UserId = id });
        }
        db.Users.Add(new User { Id = "outsider", UserName = "outsider", NormalizedUserName = "OUTSIDER", DisplayName = "outsider" });
        await db.SaveChangesAsync();
        var controller = new OnCallController(db, new OnCallScheduleGenerator());
        var empty = Assert.IsInstanceOfType<JsonResult>(await controller.SearchUsers(null, null, 1));
        Assert.AreEqual(0, ((IEnumerable<object>)empty.Value!.GetType().GetProperty("users")!.GetValue(empty.Value)!).Count());
        var first = Assert.IsInstanceOfType<JsonResult>(await controller.SearchUsers(null, role.Id, 1));
        var firstUsers = ((IEnumerable<object>)first.Value!.GetType().GetProperty("users")!.GetValue(first.Value)!).ToArray();
        Assert.AreEqual(20, firstUsers.Length);
        Assert.IsTrue((bool)first.Value.GetType().GetProperty("hasMore")!.GetValue(first.Value)!);
        Assert.IsFalse(firstUsers.Any(user => (string)user.GetType().GetProperty("id")!.GetValue(user)! == "outsider"));
        var second = Assert.IsInstanceOfType<JsonResult>(await controller.SearchUsers("user-00", role.Id, 2));
        Assert.AreEqual(5, ((IEnumerable<object>)second.Value!.GetType().GetProperty("users")!.GetValue(second.Value)!).Count());
        Assert.IsFalse((bool)second.Value.GetType().GetProperty("hasMore")!.GetValue(second.Value)!);
        var match = Assert.IsInstanceOfType<JsonResult>(await controller.SearchUsers("user-24", null, 1));
        Assert.AreEqual(1, ((IEnumerable<object>)match.Value!.GetType().GetProperty("users")!.GetValue(match.Value)!).Count());
    }

    [TestMethod]
    public async Task GroupCreationRejectsTimeZonesNotListedBySystem()
    {
        await using var db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        var controller = new OnCallController(db, new OnCallScheduleGenerator())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Administrators")], "test"))
            } }
        };
        Assert.IsInstanceOfType<RedirectToActionResult>(await controller.CreateGroup(
            new CreateOnCallGroupRequest { Name = "Operations", TimeZoneId = "UTC" }, CancellationToken.None));
        Assert.AreEqual("UTC", (await db.OnCallGroups.SingleAsync()).TimeZoneId);

        using var provider = new ServiceCollection().BuildServiceProvider();
        controller.HttpContext.RequestServices = provider;
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.CreateGroup(
            new CreateOnCallGroupRequest { Name = "Other", TimeZoneId = "Not/A-Time-Zone" }, CancellationToken.None));
        Assert.IsFalse(controller.ModelState.IsValid);
        Assert.AreEqual(1, await db.OnCallGroups.CountAsync());
    }

    [TestMethod]
    public async Task RejectsTamperedMemberIdsWithoutChangingRule()
    {
        await using var db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        db.Users.Add(new User { Id = "a", UserName = "a", NormalizedUserName = "A", DisplayName = "Alice" });
        var group = new OnCallGroup { Name = "Operations" };
        db.OnCallGroups.Add(group);
        await db.SaveChangesAsync();
        var controller = new OnCallController(db, new OnCallScheduleGenerator())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Administrators")], "test"))
            } }
        };
        var request = new SaveOnCallRuleRequest
        {
            GroupId = group.Id, Name = "Tampered", ShiftHours = 8, RestHours = 0,
            AnchorUtc = DateTime.UtcNow,
            UserIds = ["a", "not-in-staff"]
        };
        using var provider = new ServiceCollection().BuildServiceProvider();
        controller.HttpContext.RequestServices = provider;
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SaveRule(request, CancellationToken.None));
        Assert.IsFalse(controller.ModelState.IsValid);
        Assert.AreEqual(0, await db.OnCallRules.CountAsync());
    }
}

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
            Members = [new OnCallMemberInput { UserId = "a", Selected = true, RotationOrder = 2 },
                new OnCallMemberInput { UserId = "b", Selected = true, RotationOrder = 1 },
                new OnCallMemberInput { UserId = "c", Selected = true, RotationOrder = 3 }]
        };
        Assert.IsInstanceOfType<RedirectToActionResult>(await controller.SaveRule(request, CancellationToken.None));
        db.ChangeTracker.Clear();
        var rule = await db.OnCallRules.Include(item => item.Members).SingleAsync();
        CollectionAssert.AreEqual(new[] { "b", "a", "c" },
            rule.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray());
        request.Id = rule.Id;
        request.ShiftHours = 4;
        request.RestHours = 8;
        request.Members[0].RotationOrder = 1;
        request.Members[1].RotationOrder = 2;
        Assert.IsInstanceOfType<RedirectToActionResult>(await controller.SaveRule(request, CancellationToken.None));
        db.ChangeTracker.Clear();
        rule = await db.OnCallRules.Include(item => item.Members).SingleAsync();
        Assert.AreEqual(4, rule.ShiftHours);
        CollectionAssert.AreEqual(new[] { "a", "b", "c" },
            rule.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray());
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
            Members = [new OnCallMemberInput { UserId = "a", Selected = true, RotationOrder = 1 },
                new OnCallMemberInput { UserId = "not-in-staff", Selected = false, RotationOrder = 2 }]
        };
        using var provider = new ServiceCollection().BuildServiceProvider();
        controller.HttpContext.RequestServices = provider;
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SaveRule(request, CancellationToken.None));
        Assert.IsFalse(controller.ModelState.IsValid);
        Assert.AreEqual(0, await db.OnCallRules.CountAsync());
    }
}

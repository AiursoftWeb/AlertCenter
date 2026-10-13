using System.Security.Claims;
using Aiursoft.AlertCenter.Controllers;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallControllerTests
{
    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static async Task<SqliteContext> CreateDb()
    {
        var db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        db.Users.AddRange(new[] { "a", "b", "c" }.Select(id => new User
        {
            Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant(), DisplayName = id
        }));
        await db.SaveChangesAsync();
        return db;
    }

    private static OnCallController Controller(SqliteContext db) => new(db, new OnCallScheduleGenerator())
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Administrators")], "test"))
        } },
        TempData = new TempDataDictionary(new DefaultHttpContext(), new TestTempDataProvider())
    };

    private static CreateOnCallGroupRequest EditGroup(OnCallGroup group, params string[] userIds) => new()
    {
        Name = group.Name, TimeZoneId = group.TimeZoneId, RuleId = group.RuleId,
        AnchorUtc = group.AnchorUtc, Version = group.ScheduleVersion, UserIds = userIds.ToList()
    };

    [TestMethod]
    public async Task AdminCreatesReusableTemplateWithoutAnyTeamAndEditsIt()
    {
        await using var db = await CreateDb();
        var controller = Controller(db);
        var request = new SaveOnCallRuleRequest { Name = " Regular " };
        var created = Assert.IsInstanceOfType<RedirectToActionResult>(await controller.SaveRule(request, default));
        Assert.AreEqual(nameof(OnCallController.Details), created.ActionName);
        var rule = await db.OnCallRules.SingleAsync();
        Assert.AreEqual("Regular", rule.Name);
        Assert.AreEqual(0, await db.OnCallGroups.CountAsync());
        Assert.IsInstanceOfType<NotFoundResult>(await controller.Details(Guid.NewGuid(), default));
        request.Id = rule.Id;
        request.BalanceShiftTimes = true;
        request.RestHours = 24;
        var edited = Assert.IsInstanceOfType<RedirectToActionResult>(await controller.SaveRule(request, default));
        Assert.AreEqual(nameof(OnCallController.Details), edited.ActionName);
        db.ChangeTracker.Clear();
        rule = await db.OnCallRules.SingleAsync();
        Assert.AreEqual(8, rule.ShiftHours);
        Assert.AreEqual(24, rule.RestHours);
        Assert.IsTrue(rule.BalanceShiftTimes);
    }

    [TestMethod]
    public async Task EditingSharedTemplateInvalidatesEveryBoundTeamButPreservesSavedSnapshots()
    {
        await using var db = await CreateDb();
        var rule = new OnCallRule { Name = "Shared" };
        var first = new OnCallGroup { Name = "First", Rule = rule };
        var second = new OnCallGroup { Name = "Second", Rule = rule };
        var unbound = new OnCallGroup { Name = "Manual" };
        db.OnCallGroups.AddRange(first, second, unbound);
        var start = DateTime.UtcNow.Date.AddDays(-7);
        var shift = new OnCallShift
        {
            Group = first, StartsAtUtc = start, EndsAtUtc = start.AddHours(8), RestHours = 16,
            Members = [new OnCallShiftMember { UserId = "a" }]
        };
        db.OnCallShifts.Add(shift);
        await db.SaveChangesAsync();
        var versions = new[] { first.ScheduleVersion, second.ScheduleVersion, unbound.ScheduleVersion };
        Assert.IsInstanceOfType<RedirectToActionResult>(await Controller(db).SaveRule(new SaveOnCallRuleRequest
        {
            Id = rule.Id, Name = "Shared edited", RestHours = 48, BalanceShiftTimes = true
        }, default));
        db.ChangeTracker.Clear();
        var teams = await db.OnCallGroups.ToDictionaryAsync(group => group.Id);
        Assert.AreNotEqual(versions[0], teams[first.Id].ScheduleVersion);
        Assert.AreNotEqual(versions[1], teams[second.Id].ScheduleVersion);
        Assert.AreEqual(versions[2], teams[unbound.Id].ScheduleVersion);
        var saved = await db.OnCallShifts.Include(item => item.Members).SingleAsync();
        Assert.AreEqual(shift.Id, saved.Id);
        Assert.AreEqual(start, saved.StartsAtUtc);
        Assert.AreEqual(start.AddHours(8), saved.EndsAtUtc);
        Assert.AreEqual(16, saved.RestHours);
        CollectionAssert.AreEqual(new[] { "a" }, saved.Members.Select(member => member.UserId).ToArray());
    }

    [TestMethod]
    public async Task BoundTemplateCannotBeDeletedAndUnboundDeletionPreservesScheduleHistory()
    {
        await using var db = await CreateDb();
        var rule = new OnCallRule { Name = "Rotation" };
        var group = new OnCallGroup
        {
            Name = "Team", Rule = rule, Members = [new OnCallGroupMember { UserId = "a", RotationOrder = 0 }]
        };
        db.OnCallShifts.Add(new OnCallShift
        {
            Group = group, StartsAtUtc = DateTime.UtcNow.Date, EndsAtUtc = DateTime.UtcNow.Date.AddHours(8),
            Members = [new OnCallShiftMember { UserId = "a" }]
        });
        await db.SaveChangesAsync();
        var controller = Controller(db);
        Assert.IsInstanceOfType<ConflictObjectResult>(await controller.DeleteRule(rule.Id, default));
        group.Rule = null;
        group.RuleId = null;
        await db.SaveChangesAsync();
        Assert.AreEqual(nameof(OnCallController.Index),
            Assert.IsInstanceOfType<RedirectToActionResult>(await controller.DeleteRule(rule.Id, default)).ActionName);
        db.ChangeTracker.Clear();
        Assert.AreEqual(0, await db.OnCallRules.CountAsync());
        Assert.AreEqual(1, await db.OnCallGroupMembers.CountAsync());
        Assert.AreEqual(1, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(1, await db.OnCallShiftMembers.CountAsync());
        Assert.AreEqual(1, await db.OnCallGroups.CountAsync());
        Assert.AreEqual(3, await db.Users.CountAsync());
    }

    [TestMethod]
    public async Task DeletingUnsavedTeamRemovesMembersButNotItsReusableTemplate()
    {
        await using var db = await CreateDb();
        var rule = new OnCallRule { Name = "Reusable" };
        var group = new OnCallGroup
        {
            Name = "Disposable", Rule = rule, Members = [new OnCallGroupMember { UserId = "a", RotationOrder = 0 }]
        };
        db.OnCallGroups.Add(group);
        await db.SaveChangesAsync();
        Assert.AreEqual(nameof(OnCallController.Groups),
            Assert.IsInstanceOfType<RedirectToActionResult>(await Controller(db).DeleteGroup(group.Id, default)).ActionName);
        db.ChangeTracker.Clear();
        Assert.AreEqual(0, await db.OnCallGroups.CountAsync());
        Assert.AreEqual(0, await db.OnCallGroupMembers.CountAsync());
        Assert.AreEqual(rule.Id, (await db.OnCallRules.SingleAsync()).Id);
        Assert.AreEqual(3, await db.Users.CountAsync());
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
            } },
            TempData = new TempDataDictionary(new DefaultHttpContext(), new TestTempDataProvider())
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
    public async Task TeamMembershipCanBeEmptyOrBelowGenerationMinimumAndKeepsOrderedEmployees()
    {
        await using var db = await CreateDb();
        var controller = Controller(db);
        Assert.IsInstanceOfType<RedirectToActionResult>(await controller.CreateGroup(new CreateOnCallGroupRequest
        {
            Name = "Operations", UserIds = ["b", "a"]
        }, default));
        var group = await db.OnCallGroups.Include(item => item.Members).SingleAsync();
        CollectionAssert.AreEqual(new[] { "b", "a" },
            group.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray());
        var oldVersion = group.ScheduleVersion;
        Assert.IsInstanceOfType<RedirectToActionResult>(await controller.UpdateGroup(group.Id, EditGroup(group, "a"), default));
        Assert.AreNotEqual(oldVersion, group.ScheduleVersion);
        Assert.IsInstanceOfType<RedirectToActionResult>(await controller.UpdateGroup(group.Id, EditGroup(group), default));
        db.ChangeTracker.Clear();
        Assert.AreEqual(0, await db.OnCallGroupMembers.CountAsync());
        Assert.AreEqual(0, await db.OnCallRules.CountAsync());
    }

    [TestMethod]
    public async Task RemovingTeamMembersClearsOnlyFutureAssignmentsAndRetainsEmptySlotsAndHistory()
    {
        await using var db = await CreateDb();
        var now = DateTime.UtcNow;
        var group = new OnCallGroup
        {
            Name = "Operations", AnchorUtc = now.Date.AddDays(-14),
            Members = [new OnCallGroupMember { UserId = "a", RotationOrder = 0 },
                new OnCallGroupMember { UserId = "b", RotationOrder = 1 }]
        };
        var starts = new[] { now.AddDays(-2), now.AddHours(-1), now.AddDays(1), now.AddDays(2) };
        var shifts = starts.Select((start, index) => new OnCallShift
        {
            Group = group, StartsAtUtc = start, EndsAtUtc = start.AddHours(8), RestHours = 16,
            Members = index == 3 ? [new OnCallShiftMember { UserId = "a" }, new OnCallShiftMember { UserId = "b" }]
                : [new OnCallShiftMember { UserId = "a" }]
        }).ToArray();
        db.OnCallShifts.AddRange(shifts);
        await db.SaveChangesAsync();
        var oldVersion = group.ScheduleVersion;
        var staleRequest = EditGroup(group, "a");
        var controller = Controller(db);
        Assert.IsInstanceOfType<RedirectToActionResult>(await controller.UpdateGroup(group.Id, EditGroup(group, "b"), default));
        Assert.AreNotEqual(oldVersion, group.ScheduleVersion);
        Assert.IsInstanceOfType<ConflictObjectResult>(await controller.UpdateGroup(group.Id, staleRequest, default));
        db.ChangeTracker.Clear();
        var saved = await db.OnCallShifts.Include(item => item.Members).ToDictionaryAsync(shift => shift.Id);
        Assert.AreEqual(4, saved.Count);
        Assert.IsTrue(saved[shifts[0].Id].Members.Any(member => member.UserId == "a"));
        Assert.IsTrue(saved[shifts[1].Id].Members.Any(member => member.UserId == "a"));
        Assert.AreEqual(0, saved[shifts[2].Id].Members.Count);
        CollectionAssert.AreEqual(new[] { "b" }, saved[shifts[3].Id].Members.Select(member => member.UserId).ToArray());
        foreach (var shift in shifts)
        {
            Assert.AreEqual(shift.StartsAtUtc, saved[shift.Id].StartsAtUtc);
            Assert.AreEqual(shift.EndsAtUtc, saved[shift.Id].EndsAtUtc);
            Assert.AreEqual(16, saved[shift.Id].RestHours);
        }
        Assert.AreEqual("b", (await db.OnCallGroupMembers.SingleAsync()).UserId);
        Assert.IsInstanceOfType<ConflictObjectResult>(await controller.DeleteGroup(group.Id, default));
    }

    [TestMethod]
    public async Task RebindingTemplateWithSavedShiftsDoesNotMutateAssignmentsOrRestSnapshot()
    {
        await using var db = await CreateDb();
        var first = new OnCallRule { Name = "Original", RestHours = 16 };
        var second = new OnCallRule { Name = "Replacement", RestHours = 48 };
        var group = new OnCallGroup
        {
            Name = "Operations", Rule = first,
            Members = [new OnCallGroupMember { UserId = "a", RotationOrder = 0 }]
        };
        var start = group.AnchorUtc.AddDays(-7);
        var shift = new OnCallShift
        {
            Group = group, StartsAtUtc = start, EndsAtUtc = start.AddHours(8), RestHours = 16,
            Members = [new OnCallShiftMember { UserId = "a" }]
        };
        db.OnCallRules.Add(second);
        db.OnCallShifts.Add(shift);
        await db.SaveChangesAsync();
        var controller = Controller(db);
        var request = EditGroup(group, "a");
        request.RuleId = second.Id;
        var oldVersion = group.ScheduleVersion;
        Assert.IsInstanceOfType<RedirectToActionResult>(await controller.UpdateGroup(group.Id, request, default));
        db.ChangeTracker.Clear();
        var savedGroup = await db.OnCallGroups.SingleAsync();
        Assert.AreEqual(second.Id, savedGroup.RuleId);
        Assert.AreNotEqual(oldVersion, savedGroup.ScheduleVersion);
        var saved = await db.OnCallShifts.Include(item => item.Members).SingleAsync();
        Assert.AreEqual(shift.Id, saved.Id);
        Assert.AreEqual(start, saved.StartsAtUtc);
        Assert.AreEqual(start.AddHours(8), saved.EndsAtUtc);
        Assert.AreEqual(16, saved.RestHours);
        Assert.AreEqual("a", saved.Members.Single().UserId);

        request = EditGroup(savedGroup, "a");
        request.AnchorUtc = savedGroup.AnchorUtc.AddDays(7);
        using var provider = new ServiceCollection().BuildServiceProvider();
        controller.HttpContext.RequestServices = provider;
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.UpdateGroup(savedGroup.Id, request, default));
        Assert.IsFalse(controller.ModelState.IsValid);
        db.ChangeTracker.Clear();
        Assert.AreEqual(savedGroup.AnchorUtc, (await db.OnCallGroups.SingleAsync()).AnchorUtc);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("blank")]
    [DataRow("too-many")]
    public async Task RejectsTamperedMemberIdsWithoutChangingTeam(string invalid)
    {
        await using var db = await CreateDb();
        var group = new OnCallGroup
        {
            Name = "Operations", Members = [new OnCallGroupMember { UserId = "a", RotationOrder = 0 }]
        };
        db.OnCallGroups.Add(group);
        await db.SaveChangesAsync();
        var originalVersion = group.ScheduleVersion;
        if (invalid == "too-many")
        {
            db.Users.AddRange(Enumerable.Range(0, 201).Select(index => new User
            {
                Id = $"staff-{index}", UserName = $"staff-{index}", DisplayName = $"Staff {index}"
            }));
            await db.SaveChangesAsync();
        }
        var controller = Controller(db);
        var request = EditGroup(group);
        request.UserIds = invalid switch
        {
            "missing" => ["a", "not-in-staff"],
            "duplicate" => ["a", "a"],
            "blank" => [" "],
            _ => Enumerable.Range(0, 201).Select(index => $"staff-{index}").ToList()
        };
        request.Name = "Tampered";
        using var provider = new ServiceCollection().BuildServiceProvider();
        controller.HttpContext.RequestServices = provider;
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.UpdateGroup(group.Id, request, default));
        Assert.IsFalse(controller.ModelState.IsValid);
        db.ChangeTracker.Clear();
        var saved = await db.OnCallGroups.Include(item => item.Members).SingleAsync();
        Assert.AreEqual("Operations", saved.Name);
        Assert.AreEqual(originalVersion, saved.ScheduleVersion);
        Assert.AreEqual("a", saved.Members.Single().UserId);
    }

    [TestMethod]
    [DataRow(12, 16)]
    [DataRow(8, -1)]
    [DataRow(8, 169)]
    public async Task RejectsInvalidTemplateDurationAndRestWithoutSaving(int shiftHours, int restHours)
    {
        await using var db = await CreateDb();
        var controller = Controller(db);
        using var provider = new ServiceCollection().BuildServiceProvider();
        controller.HttpContext.RequestServices = provider;
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SaveRule(new SaveOnCallRuleRequest
        {
            Name = "Invalid", ShiftHours = shiftHours, RestHours = restHours
        }, default));
        Assert.IsFalse(controller.ModelState.IsValid);
        Assert.AreEqual(0, await db.OnCallRules.CountAsync());
    }
}

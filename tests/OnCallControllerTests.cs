using Aiursoft.AlertCenter.Authorization;
using System.Security.Claims;
using Aiursoft.AlertCenter.Controllers;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
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

    private static OnCallController Controller(SqliteContext db) => new(db, new OnCallScheduleGenerator(localizer:TestLocalization.For<OnCallScheduleGenerator>()), localizer: TestLocalization.For<OnCallController>(), localizerFactory: TestLocalization.Factory)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Administrators"), .. AppPermissions.GetAllPermissions().Select(permission => new Claim(AppPermissions.Type, permission.Key))], "test"))
        } },
        TempData = new TempDataDictionary(new DefaultHttpContext(), new TestTempDataProvider())
    };

    [TestMethod]
    public async Task UnsavedWeeklyPreviewContinuesLatestHistoricalAssignment()
    {
        await using var db = await CreateDb();
        var start = new DateTime(2026, 11, 9, 14, 0, 0, DateTimeKind.Utc);
        var group = new OnCallGroup { Name = "Weekly", TimeZoneId = "Asia/Shanghai", AnchorUtc = start.AddDays(-7),
            Rule = OnCallScheduleGeneratorTests.WeeklyRule(),
            Members = new[] { "a", "b", "c" }.Select((id, index) => new OnCallGroupMember { UserId = id, RotationOrder = index }).ToList() };
        db.OnCallShifts.AddRange(
            new() { Group = group, StartsAtUtc = start.AddDays(-7), EndsAtUtc = start.AddDays(-3), Members = [new() { UserId = "c" }] },
            new() { Group = group, StartsAtUtc = start.AddDays(-3), EndsAtUtc = start, Members = [new() { UserId = "a" }] });
        await db.SaveChangesAsync();
        var result = Assert.IsInstanceOfType<JsonResult>(await Controller(db).GetSchedule(group.Id, start, default));
        var json = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);
        Assert.IsFalse(json.GetProperty("saved").GetBoolean());
        Assert.AreEqual("b", json.GetProperty("slots")[0].GetProperty("userIds")[0].GetString());
        Assert.AreEqual(2, await db.OnCallShifts.CountAsync());
    }

    [TestMethod]
    public async Task SavedPreviewUsesDisplayNamesWithLegacyLargeThresholds()
    {
        await using var db = await CreateDb();
        (await db.Users.SingleAsync(user => user.Id == "a")).DisplayName = "Alice";
        var start = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
        var group = new OnCallGroup { Name = "Team", MinimumRestHours = 1e9, MaximumContinuousHours = 1,
            Members = [new() { UserId = "a" }] };
        db.OnCallShifts.AddRange(
            new() { Group = group, StartsAtUtc = start.AddHours(-2), EndsAtUtc = start.AddHours(-1), Members = [new() { UserId = "a" }] },
            new() { Group = group, StartsAtUtc = start, EndsAtUtc = start.AddHours(2), Members = [new() { UserId = "a" }] });
        await db.SaveChangesAsync();
        var result = Assert.IsInstanceOfType<JsonResult>(await Controller(db).GetSchedule(group.Id, start, default));
        var json = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);
        var warnings = json.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()!).ToList();
        foreach (var category in new[] { "insufficient rest", "continuous" })
            Assert.IsTrue(warnings.Any(warning => warning.StartsWith("Alice: ") && warning.Contains(category)));
        Assert.IsFalse(warnings.Any(warning => warning.StartsWith("a: ")));
        Assert.AreEqual(System.Text.Json.JsonValueKind.Null, json.GetProperty("generationError").ValueKind);
    }

    private static CreateOnCallGroupRequest EditGroup(OnCallGroup group, params string[] userIds) => new()
    {
        Name = group.Name, TimeZoneId = group.TimeZoneId, RuleId = group.RuleId,
        AnchorUtc = group.AnchorUtc, Version = group.ScheduleVersion, UserIds = userIds.ToList()
    };

    [TestMethod]
    public async Task FlexibleFormsPersistWeeklyPointsThresholdsFallbackAndWeekStart()
    {
        await using var db = await CreateDb();
        var controller = Controller(db);
        Assert.IsInstanceOfType<RedirectToActionResult>(await controller.SaveRule(new SaveOnCallRuleRequest
        {
            Name = "Weekday/weekend", SplitMode = "weekly", StaffPerShift = 2, SwapAlternateRounds = true,
            HandoverPoints = [new() { DayOfWeek = DayOfWeek.Monday, MinuteOfDay = 1320 }, new() { DayOfWeek = DayOfWeek.Friday, MinuteOfDay = 1320 }]
        }, default));
        var rule = await db.OnCallRules.Include(item => item.HandoverPoints).SingleAsync();
        Assert.AreEqual(2, rule.HandoverPoints.Count);
        Assert.AreEqual(2, rule.StaffPerShift);
        Assert.IsTrue(rule.SwapAlternateRounds);
        var created = await controller.CreateGroup(new CreateOnCallGroupRequest
        {
            Name = "Local team", TimeZoneId = "Asia/Shanghai", RuleId = rule.Id, UserIds = ["a", "b"],
            MinimumRestHours = 1.5, MaximumContinuousHours = 96, MinimumShiftStaff = 2,
            FallbackUserIds = ["c"], AutoExtend = true, WeekStartsOn = DayOfWeek.Friday
        }, default);
        Assert.IsInstanceOfType<RedirectToActionResult>(created);
        var group = await db.OnCallGroups.Include(item => item.FallbackRecipients).SingleAsync();
        Assert.AreEqual(1.5, group.MinimumRestHours);
        Assert.AreEqual(96, group.MaximumContinuousHours);
        Assert.AreEqual(2, group.MinimumShiftStaff);
        Assert.AreEqual("c", group.FallbackRecipients.Single().UserId);
        Assert.IsTrue(group.AutoExtend);
        Assert.AreEqual(DayOfWeek.Friday, group.WeekStartsOn);
        var start = OnCallScheduleService.WeekStart(group);
        Assert.AreEqual(DayOfWeek.Friday, TimeZoneInfo.ConvertTimeFromUtc(start, TimeZoneInfo.FindSystemTimeZoneById(group.TimeZoneId)).DayOfWeek);
        var update = EditGroup(group, "a", "b");
        update.WeekStartsOn = DayOfWeek.Sunday;
        update.FallbackSelectionSubmitted = true; update.AutoExtend = false;
        Assert.IsInstanceOfType<RedirectToActionResult>(await controller.UpdateGroup(group.Id, update, default));
        db.ChangeTracker.Clear();
        group = await db.OnCallGroups.Include(item => item.FallbackRecipients).SingleAsync();
        Assert.IsNull(group.MinimumRestHours);
        Assert.IsNull(group.MaximumContinuousHours);
        Assert.IsNull(group.MinimumShiftStaff);
        Assert.AreEqual(0, group.FallbackRecipients.Count);
        Assert.IsFalse(group.AutoExtend);
        Assert.AreEqual(DayOfWeek.Sunday, group.WeekStartsOn);
        Assert.IsInstanceOfType<RedirectToActionResult>(await controller.SaveRule(new SaveOnCallRuleRequest
        {
            Id = rule.Id, Name = rule.Name, SplitMode = "fixed", ShiftMinutes = 37, StaffPerShift = 1, SwapAlternateRounds = false
        }, default));
        db.ChangeTracker.Clear();
        rule = await db.OnCallRules.Include(item => item.HandoverPoints).SingleAsync();
        Assert.AreEqual(37, rule.ShiftMinutes);
        Assert.AreEqual(0, rule.HandoverPoints.Count);
        var sunday = new OnCallGroup { Name = "Sunday start", WeekStartsOn = DayOfWeek.Sunday };
        db.OnCallGroups.Add(sunday);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.AreEqual(DayOfWeek.Sunday, (await db.OnCallGroups.SingleAsync(item => item.Id == sunday.Id)).WeekStartsOn);
    }

    [TestMethod]
    [DataRow("Asia/Shanghai", "2026-11-09T22:00", "2026-11-09T14:00:00Z")]
    [DataRow("America/New_York", "2026-11-01T01:30", "2026-11-01T06:30:00Z")]
    public async Task GroupLocalAnchorTakesPriorityAndUsesTeamTimezone(string zone, string local, string expectedUtc)
    {
        await using var db = await CreateDb();
        var request = new CreateOnCallGroupRequest
        {
            Name = "Local", TimeZoneId = zone, AnchorLocal = local, AnchorUtc = null, AutoExtend = true
        };
        Assert.IsInstanceOfType<RedirectToActionResult>(await Controller(db).CreateGroup(request, default));
        var group = await db.OnCallGroups.SingleAsync();
        Assert.AreEqual(DateTime.Parse(expectedUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal), group.AnchorUtc);
        Assert.IsFalse(group.AutoExtend);
        var edit = EditGroup(group);
        edit.AnchorLocal = local;
        edit.AnchorUtc = DateTime.UtcNow;
        Assert.IsInstanceOfType<RedirectToActionResult>(await Controller(db).UpdateGroup(group.Id, edit, default));
        Assert.AreEqual(DateTime.Parse(expectedUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal), group.AnchorUtc);
    }

    [TestMethod]
    public async Task LegacyUtcAnchorRetainsUtcMeaningEvenWithDifferentTeamTimezone()
    {
        await using var db = await CreateDb();
        var anchor = new DateTime(2026, 11, 9, 22, 17, 39, DateTimeKind.Unspecified);
        Assert.IsInstanceOfType<RedirectToActionResult>(await Controller(db).CreateGroup(new CreateOnCallGroupRequest
        {
            Name = "Legacy", TimeZoneId = "Asia/Shanghai", AnchorUtc = anchor
        }, default));
        Assert.AreEqual(anchor, (await db.OnCallGroups.SingleAsync()).AnchorUtc);
    }

    [TestMethod]
    [DataRow("2026-03-08T02:30")]
    [DataRow("0001-01-01T00:00")]
    [DataRow("9999-12-31T23:59")]
    [DataRow("invalid")]
    public async Task LocalAnchorRejectsDstGapsMalformedAndBoundaryValues(string local)
    {
        await using var db = await CreateDb();
        var controller = Controller(db);
        using var provider = new ServiceCollection().BuildServiceProvider();
        controller.HttpContext.RequestServices = provider;
        var request = new CreateOnCallGroupRequest { Name = "Invalid", TimeZoneId = "America/New_York", AnchorLocal = local };
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.CreateGroup(request, default));
        Assert.IsFalse(controller.ModelState.IsValid);
        Assert.IsTrue(controller.ModelState["GroupInput.AnchorLocal"]!.Errors.Count > 0);
        Assert.AreEqual(local, request.AnchorLocal);
        Assert.AreEqual(0, await db.OnCallGroups.CountAsync());
    }

    [TestMethod]
    public async Task SavedAnchorSurvivesTimezoneChangesAndAmbiguousDaylightOccurrence()
    {
        await using var db = await CreateDb();
        var anchor = new DateTime(2026, 11, 1, 5, 30, 17, DateTimeKind.Utc);
        var group = new OnCallGroup { Name = "Locked", TimeZoneId = "America/New_York", AnchorUtc = anchor };
        db.OnCallShifts.Add(new OnCallShift { Group = group, StartsAtUtc = anchor, EndsAtUtc = anchor.AddHours(8) });
        await db.SaveChangesAsync();
        var request = EditGroup(group);
        request.AnchorLocal = "2026-11-01T01:30";
        request.AnchorUtc = null;
        Assert.IsInstanceOfType<RedirectToActionResult>(await Controller(db).UpdateGroup(group.Id, request, default));
        Assert.AreEqual(anchor, group.AnchorUtc);
        request = EditGroup(group);
        request.TimeZoneId = "Asia/Shanghai";
        request.AnchorLocal = "2026-11-01T13:30";
        Assert.IsInstanceOfType<RedirectToActionResult>(await Controller(db).UpdateGroup(group.Id, request, default));
        Assert.AreEqual(anchor, group.AnchorUtc);
        Assert.AreEqual("Asia/Shanghai", group.TimeZoneId);
        Assert.AreEqual(anchor, (await db.OnCallShifts.SingleAsync()).StartsAtUtc);
        request = EditGroup(group);
        request.AnchorLocal = "2026-11-02T13:30";
        var controller = Controller(db);
        using var provider = new ServiceCollection().BuildServiceProvider();
        controller.HttpContext.RequestServices = provider;
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.UpdateGroup(group.Id, request, default));
        Assert.IsFalse(controller.ModelState.IsValid);
        Assert.AreEqual("2026-11-01T13:30", request.AnchorLocal);
        db.ChangeTracker.Clear();
        Assert.AreEqual(anchor, (await db.OnCallGroups.SingleAsync()).AnchorUtc);
    }

    [TestMethod]
    public async Task UnalignedWeeklyAnchorSavesWithWarningAndUnbindingDisablesAutoExtend()
    {
        await using var db = await CreateDb();
        var rule = new OnCallRule
        {
            Name = "Weekly", HandoverPoints = [new() { DayOfWeek = DayOfWeek.Monday, MinuteOfDay = 1320 }]
        };
        db.OnCallRules.Add(rule);
        await db.SaveChangesAsync();
        var controller = Controller(db);
        Assert.IsInstanceOfType<RedirectToActionResult>(await controller.CreateGroup(new CreateOnCallGroupRequest
        {
            Name = "Partial", TimeZoneId = "Asia/Shanghai", RuleId = rule.Id, AnchorLocal = "2026-11-09T21:00", AutoExtend = true, UserIds = ["a"]
        }, default));
        var group = await db.OnCallGroups.SingleAsync();
        Assert.AreEqual(new DateTime(2026, 11, 9, 13, 0, 0, DateTimeKind.Utc), group.AnchorUtc);
        Assert.IsTrue(group.AutoExtend);
        Assert.Contains("first shift will be a partial segment", (string)controller.TempData["OnCallGroupWarning"]!);
        var update = EditGroup(group, "a");
        update.RuleId = null;
        Assert.IsInstanceOfType<RedirectToActionResult>(await Controller(db).UpdateGroup(group.Id, update, default));
        Assert.IsFalse(group.AutoExtend);
    }

    [TestMethod]
    public async Task AdminCreatesReusableTemplateWithoutAnyTeamAndEditsIt()
    {
        await using var db = await CreateDb();
        var controller = Controller(db);
        var request = new SaveOnCallRuleRequest { Name = " Regular " };
        Assert.AreEqual(1, request.MinimumStaff);
        Assert.AreEqual(1, request.TargetStaff);
        var created = Assert.IsInstanceOfType<RedirectToActionResult>(await controller.SaveRule(request, default));
        Assert.AreEqual(nameof(OnCallController.Details), created.ActionName);
        var rule = await db.OnCallRules.SingleAsync();
        Assert.AreEqual("Regular", rule.Name);
        Assert.AreEqual(0, await db.OnCallGroups.CountAsync());
        Assert.IsInstanceOfType<NotFoundResult>(await controller.Details(Guid.NewGuid(), default));
        request.Id = rule.Id;
        request.BalanceShiftTimes = true;
        request.RestHours = 24;
        request.MinimumStaff = 2;
        request.TargetStaff = 3;
        var edited = Assert.IsInstanceOfType<RedirectToActionResult>(await controller.SaveRule(request, default));
        Assert.AreEqual(nameof(OnCallController.Details), edited.ActionName);
        db.ChangeTracker.Clear();
        rule = await db.OnCallRules.SingleAsync();
        Assert.AreEqual(8, rule.ShiftHours);
        Assert.AreEqual(24, rule.RestHours);
        Assert.IsTrue(rule.BalanceShiftTimes);
        Assert.AreEqual(2, rule.MinimumStaff);
        Assert.AreEqual(3, rule.TargetStaff);
        request.TargetStaff = null;
        Assert.IsInstanceOfType<RedirectToActionResult>(await controller.SaveRule(request, default));
        db.ChangeTracker.Clear();
        Assert.IsNull((await db.OnCallRules.SingleAsync()).TargetStaff);
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
            Assert.IsInstanceOfType<RedirectToActionResult>(await Controller(db).DeleteGroup(group.Id, group.Name, default)).ActionName);
        db.ChangeTracker.Clear();
        Assert.AreEqual(0, await db.OnCallGroups.CountAsync());
        Assert.AreEqual(0, await db.OnCallGroupMembers.CountAsync());
        Assert.AreEqual(rule.Id, (await db.OnCallRules.SingleAsync()).Id);
        Assert.AreEqual(3, await db.Users.CountAsync());
    }

    [TestMethod]
    public async Task DeleteScheduledTeamRequiresExactNameAndRemovesOnlyTeamData()
    {
        await using var db = await CreateDb();
        var now = DateTime.UtcNow;
        var group = new OnCallGroup { Name = "Team <A>", AutoExtend = true, HandoverCheckedThroughUtc = now,
            Rule = new OnCallRule { Name = "Reusable" }, Members = [new() { UserId = "a" }],
            FallbackRecipients = [new() { UserId = "b" }] };
        var shifts = new[] { -2, 2 }.SelectMany(day => new[] { OnCallShiftSource.Manual, OnCallShiftSource.Generated }
            .Select(source => new OnCallShift { Group = group, StartsAtUtc = now.AddDays(day), EndsAtUtc = now.AddDays(day).AddHours(2),
                Source = source, Members = [new() { UserId = "a" }] })).ToArray();
        db.OnCallShifts.AddRange(shifts);
        db.AddRange(shifts.Select(shift => new OnCallHandoverReceipt { ShiftId = shift.Id, UserId = "a", ProcessedAtUtc = now }));
        var other = new OnCallShift { Group = new() { Name = "Other" }, StartsAtUtc = now, EndsAtUtc = now.AddHours(2) };
        db.OnCallShifts.Add(other);
        db.Add(new OnCallHandoverReceipt { ShiftId = other.Id, UserId = "a", ProcessedAtUtc = now });
        var snapshot = new OnCallEmailSnapshot { Delivery = new(), GroupId = group.Id, GroupName = group.Name,
            ShiftId = shifts[0].Id, CcRecipients = "team@example.com", Recipients = [new() { UserId = "a", DisplayName = "Original", Email = "a@example.com" }] };
        db.OnCallEmailSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        var controller = Controller(db);
        foreach (var name in new[] { null, "team <A>", "Team <A> " })
            Assert.IsInstanceOfType<BadRequestObjectResult>(await controller.DeleteGroup(group.Id, name, default));
        Assert.AreEqual(5, await db.OnCallShifts.CountAsync());
        Assert.IsInstanceOfType<RedirectToActionResult>(await controller.DeleteGroup(group.Id, group.Name, default));
        db.ChangeTracker.Clear();
        Assert.AreEqual(other.Id, (await db.OnCallShifts.SingleAsync()).Id);
        Assert.AreEqual(other.Id, (await db.Set<OnCallHandoverReceipt>().SingleAsync()).ShiftId);
        Assert.AreEqual(0, await db.OnCallShiftMembers.CountAsync());
        Assert.AreEqual(0, await db.OnCallGroupMembers.CountAsync());
        Assert.AreEqual(0, await db.Set<OnCallFallbackRecipient>().CountAsync());
        Assert.AreEqual(1, await db.OnCallRules.CountAsync());
        var retained = await db.OnCallEmailSnapshots.Include(item => item.Recipients).SingleAsync();
        Assert.AreEqual(group.Id, retained.GroupId);
        Assert.AreEqual("a@example.com", retained.Recipients.Single().Email);
        Assert.AreEqual("team@example.com", retained.CcRecipients);
        Assert.AreEqual(1, await db.PushDeliveries.CountAsync());
        await OnCallMaintenanceWorker.MaintainGroupAsync(db, TestLocalization.For<OnCallScheduleGenerator>(),
            TestLocalization.For<OnCallMaintenanceWorker>(), "https://example.com", group.Id, now, default);
        Assert.AreEqual(1, await db.OnCallShifts.CountAsync());
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
        var controller = new OnCallController(db, new OnCallScheduleGenerator(localizer:TestLocalization.For<OnCallScheduleGenerator>()), localizer: TestLocalization.For<OnCallController>(), localizerFactory: TestLocalization.Factory);
        var empty = Assert.IsInstanceOfType<JsonResult>(await controller.SearchUsers(null, null));
        Assert.AreEqual(0, ((IEnumerable<object>)empty.Value!.GetType().GetProperty("users")!.GetValue(empty.Value)!).Count());
        var first = Assert.IsInstanceOfType<JsonResult>(await controller.SearchUsers(null, role.Id));
        var firstUsers = ((IEnumerable<object>)first.Value!.GetType().GetProperty("users")!.GetValue(first.Value)!).ToArray();
        Assert.AreEqual(20, firstUsers.Length);
        Assert.IsTrue((bool)first.Value.GetType().GetProperty("hasMore")!.GetValue(first.Value)!);
        Assert.IsFalse(firstUsers.Any(user => (string)user.GetType().GetProperty("id")!.GetValue(user)! == "outsider"));
        var second = Assert.IsInstanceOfType<JsonResult>(await controller.SearchUsers("user-00", role.Id, 2));
        Assert.AreEqual(5, ((IEnumerable<object>)second.Value!.GetType().GetProperty("users")!.GetValue(second.Value)!).Count());
        Assert.IsFalse((bool)second.Value.GetType().GetProperty("hasMore")!.GetValue(second.Value)!);
        var match = Assert.IsInstanceOfType<JsonResult>(await controller.SearchUsers("user-24", null));
        Assert.AreEqual(1, ((IEnumerable<object>)match.Value!.GetType().GetProperty("users")!.GetValue(match.Value)!).Count());
    }

    [TestMethod]
    public async Task GroupCreationRejectsTimeZonesNotListedBySystem()
    {
        await using var db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        var controller = new OnCallController(db, new OnCallScheduleGenerator(localizer:TestLocalization.For<OnCallScheduleGenerator>()), localizer: TestLocalization.For<OnCallController>(), localizerFactory: TestLocalization.Factory)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Administrators"), .. AppPermissions.GetAllPermissions().Select(permission => new Claim(AppPermissions.Type, permission.Key))], "test"))
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
    public async Task RemovingTeamMembersPreservesAllAssignmentsAndHistory()
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
        Assert.AreEqual("a", saved[shifts[2].Id].Members.Single().UserId);
        CollectionAssert.AreEquivalent(new[] { "a", "b" }, saved[shifts[3].Id].Members.Select(member => member.UserId).ToArray());
        foreach (var shift in shifts)
        {
            Assert.AreEqual(shift.StartsAtUtc, saved[shift.Id].StartsAtUtc);
            Assert.AreEqual(shift.EndsAtUtc, saved[shift.Id].EndsAtUtc);
            Assert.AreEqual(16, saved[shift.Id].RestHours);
        }
        Assert.AreEqual("b", (await db.OnCallGroupMembers.SingleAsync()).UserId);
        Assert.IsInstanceOfType<RedirectToActionResult>(await controller.DeleteGroup(group.Id, group.Name, default));
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
    [DataRow(0, 1)]
    [DataRow(201, 201)]
    [DataRow(1, 0)]
    [DataRow(1, 201)]
    public async Task RejectsInvalidStaffingWithoutChangingExistingTemplate(int minimumStaff, int targetStaff)
    {
        await using var db = await CreateDb();
        var rule = new OnCallRule { Name = "Original", MinimumStaff = 2, TargetStaff = 3 };
        var group = new OnCallGroup { Name = "Bound", Rule = rule };
        db.OnCallGroups.Add(group);
        await db.SaveChangesAsync();
        var version = group.ScheduleVersion;
        var controller = Controller(db);
        using var provider = new ServiceCollection().BuildServiceProvider();
        controller.HttpContext.RequestServices = provider;
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SaveRule(new SaveOnCallRuleRequest
        {
            Id = rule.Id, Name = "Tampered", MinimumStaff = minimumStaff, TargetStaff = targetStaff
        }, default));
        Assert.IsFalse(controller.ModelState.IsValid);
        Assert.IsTrue(controller.ModelState.ContainsKey(minimumStaff is < 1 or > 200
            ? "RuleInput.MinimumStaff" : "RuleInput.TargetStaff"));
        db.ChangeTracker.Clear();
        var saved = await db.OnCallRules.SingleAsync();
        Assert.AreEqual("Original", saved.Name);
        Assert.AreEqual(2, saved.MinimumStaff);
        Assert.AreEqual(3, saved.TargetStaff);
        Assert.AreEqual(version, (await db.OnCallGroups.SingleAsync()).ScheduleVersion);
    }

    [TestMethod]
    [DataRow(0, 16)]
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

using System.Data.Common;
using System.Security.Claims;
using Aiursoft.AlertCenter.Controllers;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class RetryingTransactionTests
{
    [TestMethod]
    [DataRow("create-group", false)]
    [DataRow("update-group", false)]
    [DataRow("delete-group", false)]
    [DataRow("save-rule", false)]
    [DataRow("update-rule", false)]
    [DataRow("save-schedule", false)]
    [DataRow("delete-application", false)]
    [DataRow("create-group", true)]
    [DataRow("update-group", true)]
    [DataRow("delete-group", true)]
    [DataRow("save-rule", true)]
    [DataRow("update-rule", true)]
    [DataRow("save-schedule", true)]
    [DataRow("delete-application", true)]
    public async Task BusinessTransactionsCommitWithRetryingStrategy(string operation, bool failOnce)
    {
        var failure = new CommitFailure();
        await using var db = await CreateDb(failure);
        var group = await Seed(db);
        failure.Armed = failOnce;

        await Run(db, group, operation);

        Assert.AreEqual(failOnce, failure.Thrown);
        await Verify(db, group.Id, operation);
    }

    [TestMethod]
    [DataRow("create-group", 1)]
    [DataRow("create-group", 2)]
    [DataRow("update-group", 1)]
    [DataRow("update-group", 2)]
    [DataRow("delete-group", 1)]
    [DataRow("save-rule", 1)]
    [DataRow("update-rule", 1)]
    [DataRow("save-schedule", 1)]
    public async Task RetryAfterSavedChangesReloadsRolledBackState(string operation, int failOnSave)
    {
        var failure = new SaveFailure();
        await using var db = await CreateDb(failure);
        var group = await Seed(db);
        failure.Remaining = failOnSave;

        await Run(db, group, operation);

        Assert.IsTrue(failure.Thrown);
        await Verify(db, group.Id, operation);
    }

    [TestMethod]
    [DataRow("create-group")]
    [DataRow("save-rule")]
    public async Task LostCommitAcknowledgementDoesNotCreateDuplicateRecords(string operation)
    {
        var failure = new CommitFailure { AfterCommit = true };
        await using var db = await CreateDb(failure);
        var group = await Seed(db);
        failure.Armed = true;
        var controller = Controller(db);
        var result = operation == "create-group"
            ? await controller.CreateGroup(GroupRequest(group), default)
            : await controller.SaveRule(new SaveOnCallRuleRequest { Name = "New rule" }, default);

        Assert.IsInstanceOfType<ConflictObjectResult>(result);
        Assert.IsTrue(failure.Thrown);
        await Verify(db, group.Id, operation);
    }

    [TestMethod]
    public async Task PermanentFailureAfterFirstGroupSaveRollsBackMembershipReordering()
    {
        var failure = new SaveFailure { Permanent = true };
        await using var db = await CreateDb(failure);
        var group = await Seed(db);
        var originalVersion = group.ScheduleVersion;
        failure.Remaining = 1;
        var request = GroupRequest(group);
        request.UserIds!.Reverse();

        Assert.IsInstanceOfType<ConflictObjectResult>(await Controller(db).UpdateGroup(group.Id, request, default));

        db.ChangeTracker.Clear();
        var saved = await db.OnCallGroups.Include(item => item.Members).SingleAsync();
        Assert.AreEqual(originalVersion, saved.ScheduleVersion);
        CollectionAssert.AreEqual(new[] { "u0", "u1", "u2", "u3" }, saved.Members
            .OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray());
    }

    private static async Task<SqliteContext> CreateDb(IInterceptor failure)
    {
        var db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("Data Source=:memory:")
            .ReplaceService<IExecutionStrategyFactory, RetryFactory>().AddInterceptors(failure).Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        return db;
    }

    private static async Task<OnCallGroup> Seed(TemplateDbContext db)
    {
        db.Users.AddRange(Enumerable.Range(0, 4).Select(index => new User
            { Id = $"u{index}", UserName = $"staff{index}", DisplayName = $"Staff {index}" }));
        var group = new OnCallGroup
        {
            Name = "Original", AnchorUtc = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc),
            Rule = new OnCallRule { Name = "Original rule" },
            Members = Enumerable.Range(0, 4).Select(index => new OnCallGroupMember
                { UserId = $"u{index}", RotationOrder = index }).ToList()
        };
        db.OnCallGroups.Add(group);
        var application = new SourceApplication { Name = "Delete me" };
        db.SourceApplications.Add(application);
        db.ReportedMessages.Add(new ReportedMessage
        {
            ApplicationId = application.Id, ReportId = Guid.NewGuid(), Title = "Delete with application",
            OccurredAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return group;
    }

    private static CreateOnCallGroupRequest GroupRequest(OnCallGroup group) => new()
    {
        Name = "Updated", AnchorUtc = group.AnchorUtc, TimeZoneId = group.TimeZoneId,
        RuleId = group.RuleId, Version = group.ScheduleVersion, UserIds = ["u0", "u1", "u2", "u3"]
    };

    private static async Task Run(TemplateDbContext db, OnCallGroup group, string operation)
    {
        var controller = Controller(db);
        IActionResult result;
        switch (operation)
        {
            case "create-group":
                result = await controller.CreateGroup(GroupRequest(group), default);
                break;
            case "update-group":
                var request = GroupRequest(group);
                request.UserIds!.Reverse();
                result = await controller.UpdateGroup(group.Id, request, default);
                break;
            case "delete-group":
                result = await controller.DeleteGroup(group.Id, default);
                break;
            case "save-rule":
                result = await controller.SaveRule(new SaveOnCallRuleRequest { Name = "New rule" }, default);
                break;
            case "update-rule":
                result = await controller.SaveRule(new SaveOnCallRuleRequest
                    { Id = group.RuleId, Name = "Updated rule", RestHours = 24 }, default);
                break;
            case "save-schedule":
                var service = new OnCallScheduleService(db, new OnCallScheduleGenerator(TestLocalization.For<OnCallScheduleGenerator>()),
                    TestLocalization.For<OnCallScheduleService>(), TestLocalization.Factory);
                var errors = await service.Save(new SaveOnCallScheduleRequest
                {
                    GroupId = group.Id, Version = group.ScheduleVersion, StartUtc = group.AnchorUtc
                }, true, default);
                Assert.AreEqual(0, errors.Count, string.Join("; ", errors));
                return;
            default:
                var application = await db.SourceApplications.SingleAsync();
                var alerts = new AlertsController(db, new ApplicationAccessService(db), TestLocalization.For<AlertsController>(),
                    TestLocalization.Factory) { ControllerContext = controller.ControllerContext };
                result = await alerts.DeleteApplication(application.Id, application.Name, default);
                break;
        }
        Assert.IsInstanceOfType<RedirectToActionResult>(result);
    }

    private static async Task Verify(TemplateDbContext db, Guid groupId, string operation)
    {
        db.ChangeTracker.Clear();
        Assert.AreEqual(operation == "create-group" ? 2 : operation == "delete-group" ? 0 : 1, await db.OnCallGroups.CountAsync());
        Assert.AreEqual(operation == "save-rule" ? 2 : 1, await db.OnCallRules.CountAsync());
        Assert.AreEqual(operation == "delete-application" ? 0 : 1, await db.SourceApplications.CountAsync());
        Assert.AreEqual(operation == "save-schedule" ? 21 : 0, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(operation == "save-schedule" ? 28 : 0, await db.OnCallShiftMembers.CountAsync());
        Assert.AreEqual(operation == "delete-application" ? 0 : 1, await db.ReportedMessages.CountAsync());
        if (operation == "update-rule")
        {
            var rule = await db.OnCallRules.SingleAsync();
            Assert.AreEqual("Updated rule", rule.Name);
            Assert.AreEqual(24, rule.RestHours);
        }
        if (operation == "update-group")
        {
            var saved = await db.OnCallGroups.Include(item => item.Members).SingleAsync(item => item.Id == groupId);
            Assert.AreEqual("Updated", saved.Name);
            CollectionAssert.AreEqual(new[] { "u3", "u2", "u1", "u0" }, saved.Members
                .OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray());
        }
        Assert.AreEqual(operation == "create-group" ? 8 : operation == "delete-group" ? 0 : 4,
            await db.OnCallGroupMembers.CountAsync());
    }

    private static OnCallController Controller(TemplateDbContext db)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Administrators")], "test"))
        };
        return new OnCallController(db, new OnCallScheduleGenerator(TestLocalization.For<OnCallScheduleGenerator>()),
            TestLocalization.For<OnCallController>(), TestLocalization.Factory)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new TempDataProvider())
        };
    }

    private sealed class TempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class RetryFactory(ExecutionStrategyDependencies dependencies) : IExecutionStrategyFactory
    {
        public IExecutionStrategy Create() => new RetryStrategy(dependencies);
    }

    private sealed class RetryStrategy(ExecutionStrategyDependencies dependencies) : ExecutionStrategy(dependencies, 1, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is RetryFailure;
    }

    private sealed class RetryFailure : Exception;

    private sealed class CommitFailure : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool AfterCommit { get; init; }
        public bool Thrown { get; private set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Armed && !Thrown && !AfterCommit)
            {
                Thrown = true;
                throw new RetryFailure();
            }
            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Armed && !Thrown && AfterCommit)
            {
                Thrown = true;
                throw new RetryFailure();
            }
            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }

    private sealed class SaveFailure : SaveChangesInterceptor
    {
        public int Remaining { get; set; }
        public bool Thrown { get; private set; }
        public bool Permanent { get; init; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Remaining > 0 && --Remaining == 0)
            {
                Thrown = true;
                throw new DbUpdateException("Injected save failure.", Permanent ? new InvalidOperationException() : new RetryFailure());
            }
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }
}

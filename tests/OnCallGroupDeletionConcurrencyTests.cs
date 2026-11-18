using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Claims;
using Aiursoft.AlertCenter.Authorization;
using Aiursoft.AlertCenter.Configuration;
using Aiursoft.AlertCenter.Controllers;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.MySql;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallGroupDeletionConcurrencyTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    [DataRow("maintenance", false)]
    [DataRow("maintenance", true)]
    [DataRow("schedule", false)]
    [DataRow("schedule", true)]
    public async Task ConcurrentDeletionAndWriterSerializeWithoutRecreatingTeam(string writer, bool deleteFirst)
    {
        await using var database = new DatabaseFile();
        var group = await Seed(database);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var firstGate = new TransactionGate(blockCommit: true);
        var secondGate = new TransactionGate();
        var deleteCommands = new LockBeforeReadInterceptor();
        var writerCommands = new LockBeforeReadInterceptor();
        await using var deleteDb = database.Context(deleteFirst ? firstGate : secondGate, deleteCommands);
        await using var writerDb = database.Context(deleteFirst ? secondGate : firstGate, writerCommands);

        static async Task Delete(TemplateDbContext context, OnCallGroup team, CancellationToken cancellationToken)
        {
            var result = await Controller(context).DeleteGroup(team.Id, team.Name, cancellationToken);
            Assert.AreEqual(nameof(OnCallController.Groups),
                Assert.IsInstanceOfType<RedirectToActionResult>(result).ActionName);
        }

        static async Task Write(TemplateDbContext context, OnCallGroup team, string operation, bool deleted, CancellationToken cancellationToken)
        {
            if (operation == "maintenance")
            {
                await OnCallMaintenanceWorker.MaintainGroupAsync(context,
                    TestLocalization.For<OnCallScheduleGenerator>(), TestLocalization.For<OnCallMaintenanceWorker>(),
                    "https://example.com", team.Id, Now, cancellationToken);
                return;
            }
            var service = new OnCallScheduleService(context,
                new OnCallScheduleGenerator(TestLocalization.For<OnCallScheduleGenerator>()),
                TestLocalization.For<OnCallScheduleService>(), TestLocalization.Factory);
            var errors = await service.Save(new SaveOnCallScheduleRequest
            {
                GroupId = team.Id, Version = team.ScheduleVersion, StartUtc = Now,
                Slots = [new() { StartsAtUtc = Now, EndsAtUtc = Now.AddDays(1), UserIds = ["a"] }]
            }, false, cancellationToken);
            if (deleted)
                CollectionAssert.AreEqual(new[] { TestLocalization.For<OnCallScheduleService>()["On-call team does not exist."].Value }, errors);
            else
                Assert.AreEqual(0, errors.Count, string.Join("; ", errors));
        }

        // SQLite 的异步事务开始仍可能同步等待写锁，必须让两个操作运行在不同线程。
        var delete = RunBackground(deleteDb, context => Delete(context, group, token), token);
        var write = RunBackground(writerDb, context => Write(context, group, writer, deleteFirst, token), token);
        var first = deleteFirst ? delete() : write();
        Task second = Task.CompletedTask;
        try
        {
            await firstGate.Committing.Task.WaitAsync(token);
            if (!deleteFirst)
            {
                Assert.IsTrue(writerDb.ChangeTracker.Entries<OnCallShift>()
                    .Any(entry => entry.Entity.StartsAtUtc >= Now), "The writer must save a new shift before deletion competes.");
                if (writer == "maintenance")
                {
                    Assert.IsTrue(writerDb.ChangeTracker.Entries<OnCallHandoverReceipt>().Any(),
                        "Maintenance must create a handover receipt that deletion will explicitly remove.");
                    Assert.AreEqual(Now, writerDb.ChangeTracker.Entries<OnCallGroup>().Single().Entity.HandoverCheckedThroughUtc);
                }
            }

            second = deleteFirst ? write() : delete();
            await secondGate.Starting.Task.WaitAsync(token);
            await Task.WhenAny(secondGate.Started.Task, Task.Delay(200, token));
            Assert.IsFalse(secondGate.Started.Task.IsCompleted, "The competing transaction must wait for the uncommitted writer.");
            Assert.IsFalse(second.IsCompleted);
        }
        finally
        {
            firstGate.Release();
            await Task.WhenAll(first, second);
        }

        Assert.IsTrue(secondGate.Started.Task.IsCompletedSuccessfully);
        Assert.AreEqual(1, deleteCommands.LockCount);
        Assert.AreEqual(1, writerCommands.LockCount);
        await AssertDeleted(database, group);
    }

    [TestMethod]
    public async Task MaintenanceSkipsGroupDeletedAfterItsGroupListWasRead()
    {
        await using var database = new DatabaseFile();
        var group = await Seed(database);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var gate = new TransactionGate(blockStart: true);
        var services = new ServiceCollection();
        var contextFactory = ContextFactory(database, gate);
        services.AddScoped<TemplateDbContext>(_ => contextFactory());
        services.AddSingleton(TestLocalization.For<OnCallScheduleGenerator>());
        services.AddSingleton(TestLocalization.For<OnCallMaintenanceWorker>());
        services.AddSingleton(TestLocalization.Factory);
        services.Configure<EmailSettings>(settings => settings.PublicBaseUrl = "https://example.com");
        await using var provider = services.BuildServiceProvider();
        var logger = new MaintenanceLogger();
        using var worker = new OnCallMaintenanceWorker(provider.GetRequiredService<IServiceScopeFactory>(), logger);
        var maintenance = RunBackground(worker, instance => instance.ProcessAsync(Now, token), token)();
        try
        {
            // 每组事务开始前，ProcessAsync 已经完整读取了待维护的组 ID 列表。
            await gate.Starting.Task.WaitAsync(token);
            await using var deleteDb = database.Context();
            Assert.IsInstanceOfType<RedirectToActionResult>(await Controller(deleteDb).DeleteGroup(group.Id, group.Name, token));
        }
        finally
        {
            gate.Release();
            await maintenance;
        }

        Assert.IsTrue(gate.Started.Task.IsCompletedSuccessfully);
        Assert.IsFalse(gate.Committing.Task.IsCompleted, "A missing group must be skipped without attempting to save maintenance changes.");
        Assert.AreEqual(0, logger.Messages.Count, string.Join("; ", logger.Messages));
        await AssertDeleted(database, group);
    }

    [TestMethod]
    [DataRow("SQLite")]
    [DataRow("MySQL")]
    public void BothProviderModelsPreserveDeletionAndHistoricalDataRelationships(string provider)
    {
        using TemplateDbContext db = provider == "SQLite"
            ? new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("Data Source=:memory:").Options)
            : new MySqlContext(new DbContextOptionsBuilder<MySqlContext>()
                .UseMySql("Server=localhost;Database=metadata_only;Uid=unused;Pwd=unused;",
                    new MySqlServerVersion(new Version(8, 0, 31))).Options);
        var group = db.Model.FindEntityType(typeof(OnCallGroup))!;
        CollectionAssert.AreEquivalent(new[]
        {
            $"{nameof(SourceApplication)}.{nameof(SourceApplication.OnCallGroupId)}:{DeleteBehavior.Restrict}",
            $"{nameof(OnCallGroupMember)}.{nameof(OnCallGroupMember.GroupId)}:{DeleteBehavior.Cascade}",
            $"{nameof(OnCallFallbackRecipient)}.{nameof(OnCallFallbackRecipient.GroupId)}:{DeleteBehavior.Cascade}",
            $"{nameof(OnCallShift)}.{nameof(OnCallShift.GroupId)}:{DeleteBehavior.Cascade}"
        }, group.GetReferencingForeignKeys().Select(foreignKey =>
            $"{foreignKey.DeclaringEntityType.ClrType.Name}.{string.Join(',', foreignKey.Properties.Select(property => property.Name))}:{foreignKey.DeleteBehavior}").ToArray());
        Assert.AreEqual(DeleteBehavior.Restrict, group.GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(OnCallRule)).DeleteBehavior);

        var shiftForeignKey = db.Model.FindEntityType(typeof(OnCallShift))!.GetReferencingForeignKeys().Single();
        Assert.AreEqual(typeof(OnCallShiftMember), shiftForeignKey.DeclaringEntityType.ClrType);
        Assert.AreEqual(nameof(OnCallShiftMember.ShiftId), shiftForeignKey.Properties.Single().Name);
        Assert.AreEqual(DeleteBehavior.Cascade, shiftForeignKey.DeleteBehavior);

        var snapshot = db.Model.FindEntityType(typeof(OnCallEmailSnapshot))!;
        foreach (var property in new[] { nameof(OnCallEmailSnapshot.GroupId), nameof(OnCallEmailSnapshot.ShiftId) })
            Assert.IsFalse(snapshot.GetForeignKeys().Any(foreignKey => foreignKey.Properties.Any(item => item.Name == property)),
                $"{provider} historical {property} must not reference live scheduling data.");
        Assert.AreEqual(0, db.Model.FindEntityType(typeof(OnCallHandoverReceipt))!.GetForeignKeys().Count(),
            "Receipts have no foreign keys and must be explicitly removed by team deletion.");
        Assert.IsFalse(db.Database.HasPendingModelChanges(), "Deletion must not require a schema change.");
    }

    private static Func<Task> RunBackground<T>(T instance, Func<T, Task> action, CancellationToken token) =>
        () => Task.Run(() => action(instance), token);

    private static Func<SqliteContext> ContextFactory(DatabaseFile database, TransactionGate gate) => () => database.Context(gate);

    private static async Task<OnCallGroup> Seed(DatabaseFile database)
    {
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        db.Users.AddRange(new[] { "a", "b" }.Select(id => new User
        {
            Id = id, UserName = id, DisplayName = id, Email = $"{id}@example.com"
        }));
        var group = new OnCallGroup
        {
            Name = "Concurrent team", AnchorUtc = Now, AutoExtend = true,
            Rule = new OnCallRule { Name = "Reusable rule", ShiftHours = 24, RestHours = 0 },
            Members = [new() { UserId = "a", RotationOrder = 0 }, new() { UserId = "b", RotationOrder = 1 }],
            FallbackRecipients = [new() { UserId = "b" }]
        };
        var historicalShift = new OnCallShift
        {
            Group = group, StartsAtUtc = Now.AddDays(-1), EndsAtUtc = Now,
            Source = OnCallShiftSource.Manual, Members = [new() { UserId = "b" }]
        };
        db.OnCallShifts.Add(historicalShift);
        db.Add(new OnCallHandoverReceipt { ShiftId = historicalShift.Id, UserId = "b", ProcessedAtUtc = Now.AddDays(-1) });
        db.OnCallEmailSnapshots.Add(new OnCallEmailSnapshot
        {
            Delivery = new(), GroupId = group.Id, GroupName = group.Name, ShiftId = historicalShift.Id,
            Recipients = [new() { UserId = "b", DisplayName = "Historical engineer", Email = "b@example.com" }]
        });
        await db.SaveChangesAsync();
        return group;
    }

    private static async Task AssertDeleted(DatabaseFile database, OnCallGroup group)
    {
        await using var db = database.Context();
        Assert.AreEqual(0, await db.OnCallGroups.CountAsync());
        Assert.AreEqual(0, await db.OnCallGroupMembers.CountAsync());
        Assert.AreEqual(0, await db.Set<OnCallFallbackRecipient>().CountAsync());
        Assert.AreEqual(0, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(0, await db.OnCallShiftMembers.CountAsync());
        Assert.AreEqual(0, await db.Set<OnCallHandoverReceipt>().CountAsync());
        Assert.AreEqual(2, await db.Users.CountAsync());
        Assert.AreEqual(group.RuleId, (await db.OnCallRules.SingleAsync()).Id);
        Assert.AreEqual(1, await db.PushDeliveries.CountAsync());
        var snapshot = await db.OnCallEmailSnapshots.Include(item => item.Recipients).SingleAsync();
        Assert.AreEqual(group.Id, snapshot.GroupId);
        Assert.IsNotNull(snapshot.ShiftId);
        Assert.AreEqual("b@example.com", snapshot.Recipients.Single().Email);
    }

    private static OnCallController Controller(TemplateDbContext db)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "a"), new Claim(ClaimTypes.Role, "Administrators"),
                .. AppPermissions.GetAllPermissions().Select(permission => new Claim(AppPermissions.Type, permission.Key))
            ], "test"))
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

    private sealed class DatabaseFile : IAsyncDisposable
    {
        private readonly string path = Path.Combine(Directory.GetCurrentDirectory(), $"oncall-delete-{Guid.NewGuid():N}.db");

        public SqliteContext Context(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite(new SqliteConnectionStringBuilder
            {
                DataSource = path, Pooling = false, ForeignKeys = true, DefaultTimeout = 10
            }.ToString()).AddInterceptors(interceptors).Options);

        public ValueTask DisposeAsync()
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                File.Delete(path + suffix);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TransactionGate(bool blockStart = false, bool blockCommit = false) : DbTransactionInterceptor
    {
        public TaskCompletionSource Starting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Committing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => release.TrySetResult();

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            Starting.TrySetResult();
            if (blockStart) await release.Task.WaitAsync(cancellationToken);
            return result;
        }

        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection,
            TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Committing.TrySetResult();
            if (blockCommit) await release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class LockBeforeReadInterceptor : DbCommandInterceptor
    {
        public int LockCount { get; private set; }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"OnCallGroups\"", StringComparison.Ordinal)) LockCount++;
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) &&
                command.CommandText.Contains("FROM \"OnCallGroups\"", StringComparison.Ordinal))
                Assert.AreEqual(1, LockCount, "Deletion, maintenance and schedule saves must acquire the group write lock before reading it.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class MaintenanceLogger : ILogger<OnCallMaintenanceWorker>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
}

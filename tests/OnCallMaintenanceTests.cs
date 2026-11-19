using System.Data.Common;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallMaintenanceTests
{
    private SqliteContext db = null!;
    private OnCallGroup group = null!;
    private SaveFailure saveFailure = null!;
    private IncidentReads incidentReads = null!;
    private MaintenanceLogger logger = null!;
    private static readonly DateTime Now = new(2026, 9, 28, 14, 0, 0, DateTimeKind.Utc);

    [TestInitialize]
    public async Task Setup()
    {
        saveFailure = new();
        incidentReads = new();
        logger = new();
        db = new(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:")
            .AddInterceptors(saveFailure, incidentReads).Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        db.Users.AddRange(new[] { "a", "b", "c", "d" }.Select(id => new User
            { Id = id, UserName = id, DisplayName = id, Email = $"{id}@example.com" }));
        group = new() { Name = "Team", AnchorUtc = Now, TimeZoneId = "Asia/Shanghai", AutoExtend = true,
            Rule = OnCallScheduleGeneratorTests.WeeklyRule(),
            Members = new[] { "a", "b", "c" }.Select((id, index) => new OnCallGroupMember { UserId = id, RotationOrder = index }).ToList() };
        db.OnCallGroups.Add(group);
        await db.SaveChangesAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await db.DisposeAsync();

    private Task Maintain(DateTime? now = null) => OnCallMaintenanceWorker.MaintainGroupAsync(db,
        TestLocalization.For<OnCallScheduleGenerator>(), TestLocalization.For<OnCallMaintenanceWorker>(),
        "https://example.com/alerts", group.Id, now ?? Now, default, logger: logger);

    private async Task SaveSchedule(params OnCallSlotRequest[] slots)
    {
        var service = new OnCallScheduleService(db, new OnCallScheduleGenerator(TestLocalization.For<OnCallScheduleGenerator>()),
            TestLocalization.For<OnCallScheduleService>(), TestLocalization.Factory);
        var savedGroup = await db.OnCallGroups.SingleAsync(item => item.Id == group.Id);
        var errors = await service.Save(new() { GroupId = group.Id, Version = savedGroup.ScheduleVersion,
            StartUtc = Now.AddDays(-1), Slots = slots.ToList() }, false, default);
        Assert.AreEqual(0, errors.Count, string.Join(" ", errors));
    }

    private sealed class IncidentReads : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Incidents\"", StringComparison.OrdinalIgnoreCase) ||
                command.CommandText.Contains("JOIN \"Incidents\"", StringComparison.OrdinalIgnoreCase)) Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class SaveFailure : SaveChangesInterceptor
    {
        public Exception? Failure { get; set; }
        public bool AfterSave { get; set; }
        public int AddedShiftsAtFailure { get; private set; }
        public int AddedMembersAtFailure { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Failure == null) return base.SavingChangesAsync(eventData, result, cancellationToken);
            AddedShiftsAtFailure = eventData.Context!.ChangeTracker.Entries<OnCallShift>()
                .Count(entry => entry.State == EntityState.Added);
            AddedMembersAtFailure = eventData.Context.ChangeTracker.Entries<OnCallShiftMember>()
                .Count(entry => entry.State == EntityState.Added);
            if (!AfterSave) ThrowFailure();
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (AfterSave && Failure != null) ThrowFailure();
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }

        private void ThrowFailure()
        {
            var failure = Failure!;
            Failure = null;
            throw failure;
        }
    }

    private sealed class MaintenanceLogger : ILogger<OnCallMaintenanceWorker>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    [TestMethod]
    public async Task TeamMaintenanceFailureStillQueuesNextTeamsHandover()
    {
        group.AutoExtend = false;
        db.OnCallGroups.Add(new() { Name = "Other team", AnchorUtc = Now, TimeZoneId = "UTC", AutoExtend = false });
        await db.SaveChangesAsync();
        var groupIds = await db.OnCallGroups.AsNoTracking().Select(item => item.Id).ToListAsync();
        var failedGroupId = groupIds[0];
        var healthyGroupId = groupIds[1];
        var current = new OnCallShift { GroupId = healthyGroupId, StartsAtUtc = Now, EndsAtUtc = Now.AddDays(1),
            Members = [new() { UserId = "a" }] };
        db.OnCallShifts.Add(current);
        db.Incidents.Add(new() { Application = new() { Name = "App", OnCallGroupId = healthyGroupId }, Title = "Open outage" });
        await db.SaveChangesAsync();
        var failure = new InvalidOperationException("Injected team maintenance failure.");
        saveFailure.Failure = failure;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();
        services.AddOptions<Configuration.EmailSettings>();
        services.AddScoped<TemplateDbContext>(_ => new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite(db.Database.GetDbConnection()).AddInterceptors(saveFailure).Options));
        await using var provider = services.BuildServiceProvider();
        using var worker = new OnCallMaintenanceWorker(provider.GetRequiredService<IServiceScopeFactory>(), logger);

        await worker.ProcessAsync(Now);

        var delivery = await db.PushDeliveries.Include(item => item.OnCallEmail).ThenInclude(snapshot => snapshot!.Recipients).SingleAsync();
        Assert.AreEqual(PushDelivery.Pending, delivery.Status);
        Assert.AreEqual(healthyGroupId, delivery.OnCallEmail!.GroupId);
        Assert.AreEqual(current.Id, delivery.OnCallEmail.ShiftId);
        Assert.AreEqual("a", delivery.OnCallEmail.Recipients.Single().UserId);
        Assert.AreEqual(1, await db.Set<OnCallHandoverReceipt>().CountAsync());
        var error = logger.Entries.Single();
        Assert.AreEqual(LogLevel.Error, error.Level);
        Assert.Contains(failedGroupId.ToString(), error.Message);
        Assert.AreSame(failure, error.Exception);
    }

    [TestMethod]
    [DataRow(42)]
    [DataRow(10)]
    public async Task ExtensionFillsUnoccupiedDaysWithoutConsumingCoveredRotations(int manualStartDay)
    {
        db.RemoveRange(group.Rule!.HandoverPoints);
        group.Rule.HandoverPoints.Clear();
        group.Rule.ShiftHours = 24;
        var manual = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddDays(manualStartDay), EndsAtUtc = Now.AddDays(manualStartDay + 2),
            Source = OnCallShiftSource.Manual, Members = [new() { UserId = "b" }] };
        db.OnCallShifts.Add(manual);
        await db.SaveChangesAsync();
        var version = group.ScheduleVersion;

        await Maintain();

        var shifts = await db.OnCallShifts.Include(shift => shift.Members).OrderBy(shift => shift.StartsAtUtc).ToListAsync();
        var savedManual = shifts.Single(shift => shift.Source == OnCallShiftSource.Manual);
        Assert.AreEqual(manual.Id, savedManual.Id);
        Assert.AreEqual(manual.StartsAtUtc, savedManual.StartsAtUtc);
        Assert.AreEqual(manual.EndsAtUtc, savedManual.EndsAtUtc);
        Assert.AreEqual("b", savedManual.Members.Single().UserId);
        var generated = shifts.Where(shift => shift.Source == OnCallShiftSource.Generated).ToArray();
        var freeDays = Enumerable.Range(0, 28).Where(day => day < manualStartDay || day >= manualStartDay + 2).ToArray();
        CollectionAssert.AreEqual(freeDays.Select(day => Now.AddDays(day)).ToArray(), generated.Select(shift => shift.StartsAtUtc).ToArray());
        CollectionAssert.AreEqual(freeDays.Select(day => Now.AddDays(day + 1)).ToArray(), generated.Select(shift => shift.EndsAtUtc).ToArray());
        string[] rotation = ["a", "b", "c"];
        CollectionAssert.AreEqual(freeDays.Select((_, index) => rotation[index % rotation.Length]).ToArray(),
            generated.Select(shift => shift.Members.Single().UserId).ToArray());
        var savedGroup = await db.OnCallGroups.SingleAsync();
        Assert.AreNotEqual(version, savedGroup.ScheduleVersion);
        Assert.AreEqual(Now.AddDays(28), savedGroup.AutoExtendedThroughUtc);
    }

    [TestMethod]
    public async Task ExtensionDoesNotRefillDeletedShiftsAndOnlyAddsNewDay()
    {
        db.RemoveRange(group.Rule!.HandoverPoints);
        group.Rule.HandoverPoints.Clear();
        group.Rule.ShiftHours = 24;
        await db.SaveChangesAsync();
        await Maintain();
        var deleted = await db.OnCallShifts.SingleAsync(shift => shift.StartsAtUtc == Now.AddDays(4));
        db.Remove(deleted);
        await db.SaveChangesAsync();
        var original = await db.OnCallShifts.OrderBy(shift => shift.StartsAtUtc).Select(shift => shift.Id).ToArrayAsync();

        await Maintain(Now.AddDays(1));

        var shifts = await db.OnCallShifts.OrderBy(shift => shift.StartsAtUtc).ToArrayAsync();
        Assert.HasCount(original.Length + 1, shifts);
        CollectionAssert.AreEqual(original, shifts[..^1].Select(shift => shift.Id).ToArray());
        Assert.AreEqual(Now.AddDays(28), shifts[^1].StartsAtUtc);
        Assert.AreEqual(Now.AddDays(29), shifts[^1].EndsAtUtc);
        Assert.AreEqual(Now.AddDays(29), (await db.OnCallGroups.SingleAsync()).AutoExtendedThroughUtc);
    }

    [TestMethod]
    public async Task WeeklyShiftSplitAroundAnEmptyManualShiftSharesOneRotation()
    {
        var manual = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddMinutes(10), EndsAtUtc = Now.AddMinutes(20) };
        db.Add(manual);
        await db.SaveChangesAsync();
        var boundary = OnCallScheduleGenerator.NextHandover(group.Rule!.HandoverPoints,
            TimeZoneInfo.FindSystemTimeZoneById(group.TimeZoneId), Now);

        await Maintain();

        var first = await db.OnCallShifts.Include(shift => shift.Members).OrderBy(shift => shift.StartsAtUtc).Take(4).ToArrayAsync();
        Assert.AreEqual(Now, first[0].StartsAtUtc);
        Assert.AreEqual(manual.StartsAtUtc, first[0].EndsAtUtc);
        Assert.AreEqual(manual.Id, first[1].Id);
        Assert.IsEmpty(first[1].Members);
        Assert.AreEqual(manual.EndsAtUtc, first[2].StartsAtUtc);
        Assert.AreEqual(boundary, first[2].EndsAtUtc);
        Assert.AreEqual("a", first[0].Members.Single().UserId);
        Assert.AreEqual("a", first[2].Members.Single().UserId);
        Assert.AreEqual(boundary, first[3].StartsAtUtc);
        Assert.AreEqual("b", first[3].Members.Single().UserId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IrregularMaintenanceKeepsExtensionsOnTemplateBoundaries(bool weekly)
    {
        var rule = group.Rule!;
        rule.StaffPerShift = 2;
        if (!weekly)
        {
            db.RemoveRange(rule.HandoverPoints);
            rule.HandoverPoints.Clear();
            rule.ShiftHours = 8;
        }
        var start = Now.AddHours(20).AddMinutes(37);
        // 此手工班完全在首次续排窗口之外，但占用了最后一个模板班的中间。
        var manual = new OnCallShift { GroupId = group.Id,
            StartsAtUtc = start.AddDays(28).AddHours(1), EndsAtUtc = start.AddDays(28).AddHours(2) };
        db.Add(manual);
        await db.SaveChangesAsync();

        var ticks = Enumerable.Range(0, 120).Select(minute => start.AddMinutes(minute)).Concat(
            [start.AddHours(7), start.AddDays(1).AddHours(3).AddMinutes(13), start.AddDays(9)]);
        var templateEnds = Enumerable.Range(1, 42 * 3).Select(step => Now.AddHours(step * 8))
            .Where(boundary => !weekly || boundary.Hour == Now.Hour &&
                boundary.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Friday)
            .Where(boundary => boundary > start).ToArray();
        var processed = 0;
        foreach (var now in ticks)
        {
            var version = (await db.OnCallGroups.SingleAsync()).ScheduleVersion;
            await Maintain(now);
            var saved = await db.OnCallGroups.SingleAsync();
            var horizon = now.AddDays(28);
            var lastBoundary = Array.FindIndex(templateEnds, boundary => boundary >= horizon);
            Assert.IsTrue(lastBoundary >= 0);
            Assert.AreEqual(templateEnds[lastBoundary], saved.AutoExtendedThroughUtc);
            if (processed == lastBoundary + 1) Assert.AreEqual(version, saved.ScheduleVersion);
            processed = lastBoundary + 1;

            var expected = new List<(DateTime Start, DateTime End, long? Position)>();
            var from = start;
            for (var index = 0; index < processed; index++)
            {
                var to = templateEnds[index];
                var position = (long)index * rule.StaffPerShift;
                if (from < manual.StartsAtUtc && manual.EndsAtUtc < to)
                {
                    expected.Add((from, manual.StartsAtUtc, position));
                    expected.Add((manual.EndsAtUtc, to, position));
                }
                else expected.Add((from, to, position));
                from = to;
            }
            var actual = await db.OnCallShifts.Where(shift => shift.Source == OnCallShiftSource.Generated)
                .OrderBy(shift => shift.StartsAtUtc).ToArrayAsync();
            CollectionAssert.AreEqual(expected.ToArray(), actual.Select(shift =>
                (shift.StartsAtUtc, shift.EndsAtUtc, shift.RotationPosition)).ToArray());
        }
    }

    [TestMethod]
    public async Task FullyOccupiedHorizonStillAdvancesCursor()
    {
        var start = Now.AddMinutes(-1);
        var boundary = Now.AddDays(28);
        var manual = new OnCallShift { GroupId = group.Id, StartsAtUtc = start, EndsAtUtc = boundary };
        db.Add(manual);
        await db.SaveChangesAsync();
        await Maintain(start);
        Assert.AreEqual(boundary, (await db.OnCallGroups.SingleAsync()).AutoExtendedThroughUtc);
        Assert.AreEqual(manual.Id, (await db.OnCallShifts.SingleAsync()).Id);
        db.Remove(await db.OnCallShifts.SingleAsync());
        await db.SaveChangesAsync();
        await Maintain();
        Assert.IsFalse(await db.OnCallShifts.AnyAsync());
    }

    [TestMethod]
    public async Task MigrationPreservesLegacyShiftAsManualAndFirstExtensionStartsFromNow()
    {
        var shiftId = Guid.NewGuid();
        var start = Now.AddHours(1);
        var end = Now.AddHours(2);
        db.ChangeTracker.Clear();
        await db.Database.MigrateAsync("20261104142300_AddIncidentEmbedding");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO OnCallShifts (Id, GroupId, StartsAtUtc, EndsAtUtc, RestHours, MinimumStaff) VALUES ({shiftId}, {group.Id}, {start}, {end}, {0}, {1})");
        await db.Database.MigrateAsync();

        var migratedGroup = await db.OnCallGroups.SingleAsync();
        Assert.IsNull(migratedGroup.AutoExtendedThroughUtc);
        var manual = await db.OnCallShifts.SingleAsync();
        Assert.AreEqual(shiftId, manual.Id);
        Assert.AreEqual(OnCallShiftSource.Manual, manual.Source);
        Assert.AreEqual(start, manual.StartsAtUtc);
        Assert.AreEqual(end, manual.EndsAtUtc);

        migratedGroup.AutoExtend = true;
        var rule = OnCallScheduleGeneratorTests.WeeklyRule();
        db.OnCallRules.Add(rule);
        migratedGroup.Rule = rule;
        await db.SaveChangesAsync();
        await Maintain();
        var first = await db.OnCallShifts.Where(shift => shift.GroupId == group.Id)
            .OrderBy(shift => shift.StartsAtUtc).Take(3).ToArrayAsync();
        Assert.AreEqual(Now, first[0].StartsAtUtc);
        Assert.AreEqual(manual.StartsAtUtc, first[0].EndsAtUtc);
        Assert.AreEqual(manual.Id, first[1].Id);
        Assert.AreEqual(manual.EndsAtUtc, first[2].StartsAtUtc);
    }

    [TestMethod]
    public async Task PartialPeriodsAndFullStaffAlternateRoundsAppendWithoutOverlapAndPreserveLegacyData()
    {
        db.RemoveRange(group.Rule!.HandoverPoints);
        group.Rule.HandoverPoints.Clear();
        group.Rule.ShiftMinutes = 337;
        group.Rule.StaffPerShift = 4;
        group.Rule.SwapAlternateRounds = true;
        group.Members.Add(new() { UserId = "d", RotationOrder = 3 });
        var legacy = new[]
        {
            new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddHours(-2), EndsAtUtc = Now.AddMinutes(17), Members = [new() { UserId = "a" }] },
            new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddHours(-1), EndsAtUtc = Now.AddMinutes(7), Members = [new() { UserId = "a" }] }
        };
        db.OnCallShifts.AddRange(legacy);
        await db.SaveChangesAsync();
        await Maintain();
        await Maintain(Now.AddDays(1));
        var saved = await db.OnCallShifts.Include(shift => shift.Members).OrderBy(shift => shift.StartsAtUtc).ToListAsync();
        var additions = saved.Where(shift => !legacy.Any(original => original.Id == shift.Id)).ToList();
        Assert.AreEqual(Now.AddMinutes(17), additions[0].StartsAtUtc);
        Assert.IsTrue(additions.All(shift => shift.Members.Count == 4));
        Assert.AreEqual(0, OnCallScheduleValidator.OverlapErrors(additions, legacy).Count);
        Assert.AreEqual(1, OnCallScheduleValidator.OverlapErrors(saved, []).Count);
        foreach (var original in legacy)
        {
            var actual = saved.Single(shift => shift.Id == original.Id);
            Assert.AreEqual(original.StartsAtUtc, actual.StartsAtUtc);
            Assert.AreEqual(original.EndsAtUtc, actual.EndsAtUtc);
            Assert.AreEqual("a", actual.Members.Single().UserId);
        }
    }

    [TestMethod]
    public async Task RemovedLastAssigneeRestartsAtQueueHeadAndDisabledTeamsDoNotExtend()
    {
        db.OnCallShifts.Add(new() { GroupId = group.Id, StartsAtUtc = Now.AddHours(-1), EndsAtUtc = Now,
            Members = [new() { UserId = "d" }] });
        await db.SaveChangesAsync();
        await Maintain();
        var first = await db.OnCallShifts.Include(shift => shift.Members).Where(shift => shift.StartsAtUtc >= Now)
            .OrderBy(shift => shift.StartsAtUtc).FirstAsync();
        Assert.AreEqual("a", first.Members.Single().UserId);
        var count = await db.OnCallShifts.CountAsync();
        (await db.OnCallGroups.SingleAsync()).AutoExtend = false;
        await db.SaveChangesAsync();
        await Maintain(Now.AddDays(35));
        Assert.AreEqual(count, await db.OnCallShifts.CountAsync());
        var savedGroup = await db.OnCallGroups.SingleAsync();
        Assert.AreEqual(Now.AddDays(28), savedGroup.AutoExtendedThroughUtc);
        savedGroup.AutoExtend = true;
        await db.SaveChangesAsync();

        await Maintain(Now.AddDays(36));

        Assert.AreEqual(Now.AddDays(36), await db.OnCallShifts.Where(shift => shift.StartsAtUtc >= Now.AddDays(28))
            .MinAsync(shift => shift.StartsAtUtc));
        Assert.AreEqual(Now.AddDays(67), (await db.OnCallGroups.SingleAsync()).AutoExtendedThroughUtc);
    }

    [TestMethod]
    public async Task InvalidExtensionTemplateStillQueuesHandoverWithoutChangingSchedule()
    {
        group.Rule!.StaffPerShift = group.Members.Count + 1;
        var current = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now, EndsAtUtc = Now.AddDays(1),
            Members = [new() { UserId = "a" }] };
        db.OnCallShifts.Add(current);
        db.Incidents.Add(new() { Application = new() { Name = "App", OnCallGroupId = group.Id }, Title = "Open outage" });
        await db.SaveChangesAsync();
        var version = group.ScheduleVersion;

        await Maintain();
        db.ChangeTracker.Clear();

        var delivery = await db.PushDeliveries.Include(item => item.OnCallEmail).ThenInclude(snapshot => snapshot!.Recipients).SingleAsync();
        Assert.AreEqual("a", delivery.OnCallEmail!.Recipients.Single().UserId);
        Assert.AreEqual(current.Id, delivery.OnCallEmail.ShiftId);
        Assert.AreEqual(1, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(1, await db.OnCallShiftMembers.CountAsync());
        Assert.AreEqual(1, await db.Set<OnCallHandoverReceipt>().CountAsync());
        var savedGroup = await db.OnCallGroups.SingleAsync();
        Assert.AreEqual(version, savedGroup.ScheduleVersion);
        Assert.AreEqual(Now, savedGroup.HandoverCheckedThroughUtc);
        var error = logger.Entries.Single();
        Assert.AreEqual(LogLevel.Error, error.Level);
        Assert.Contains(group.Id.ToString(), error.Message);
        Assert.IsInstanceOfType<ArgumentException>(error.Exception);
    }

    [TestMethod]
    [DataRow(typeof(ArgumentException), false)]
    [DataRow(typeof(InvalidOperationException), false)]
    [DataRow(typeof(OverflowException), false)]
    [DataRow(typeof(TimeZoneNotFoundException), false)]
    [DataRow(typeof(InvalidTimeZoneException), false)]
    [DataRow(typeof(InvalidOperationException), true)]
    public async Task ExtensionSaveFailureDiscardsPartialChangesAndStillQueuesHandover(Type exceptionType, bool afterSave)
    {
        var current = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now, EndsAtUtc = Now.AddDays(1),
            Source = OnCallShiftSource.Manual, Members = [new() { UserId = "a" }] };
        db.OnCallShifts.Add(current);
        db.Incidents.Add(new() { Application = new() { Name = "App", OnCallGroupId = group.Id }, Title = "Open outage" });
        await db.SaveChangesAsync();
        var version = group.ScheduleVersion;
        var failure = (Exception)Activator.CreateInstance(exceptionType, "Injected extension failure.")!;
        saveFailure.Failure = failure;
        saveFailure.AfterSave = afterSave;

        await Maintain();

        Assert.IsTrue(saveFailure.AddedShiftsAtFailure > 0);
        Assert.IsTrue(saveFailure.AddedMembersAtFailure > 0);
        Assert.IsFalse(db.ChangeTracker.HasChanges());
        Assert.IsFalse(db.ChangeTracker.Entries<OnCallShift>().Any(entry => entry.Entity.Source == OnCallShiftSource.Generated));
        db.ChangeTracker.Clear();
        var saved = await db.OnCallShifts.Include(shift => shift.Members).SingleAsync();
        Assert.AreEqual(current.Id, saved.Id);
        Assert.AreEqual(current.StartsAtUtc, saved.StartsAtUtc);
        Assert.AreEqual(current.EndsAtUtc, saved.EndsAtUtc);
        Assert.AreEqual(OnCallShiftSource.Manual, saved.Source);
        Assert.AreEqual("a", saved.Members.Single().UserId);
        Assert.AreEqual(1, await db.OnCallShiftMembers.CountAsync());
        var savedGroup = await db.OnCallGroups.SingleAsync();
        Assert.AreEqual(version, savedGroup.ScheduleVersion);
        Assert.AreEqual(Now, savedGroup.HandoverCheckedThroughUtc);
        var delivery = await db.PushDeliveries.Include(item => item.OnCallEmail).ThenInclude(snapshot => snapshot!.Recipients).SingleAsync();
        Assert.AreEqual(PushDelivery.Pending, delivery.Status);
        Assert.AreEqual(current.Id, delivery.OnCallEmail!.ShiftId);
        Assert.AreEqual("a", delivery.OnCallEmail.Recipients.Single().UserId);
        Assert.AreEqual(1, await db.Set<OnCallHandoverReceipt>().CountAsync());
        var error = logger.Entries.Single();
        Assert.AreEqual(LogLevel.Error, error.Level);
        Assert.Contains(group.Id.ToString(), error.Message);
        Assert.AreSame(failure, error.Exception);
    }

    [TestMethod]
    public async Task HandoverSaveFailurePropagatesAndRollsBackReceiptsAndCheckpoint()
    {
        group.AutoExtend = false;
        db.OnCallShifts.Add(new() { GroupId = group.Id, StartsAtUtc = Now, EndsAtUtc = Now.AddDays(1),
            Members = [new() { UserId = "a" }] });
        db.Incidents.Add(new() { Application = new() { Name = "App", OnCallGroupId = group.Id }, Title = "Open outage" });
        await db.SaveChangesAsync();
        var failure = new InvalidOperationException("Injected handover failure.");
        saveFailure.Failure = failure;
        saveFailure.AfterSave = true;

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Maintain());
        Assert.AreSame(failure, thrown);
        db.ChangeTracker.Clear();
        Assert.AreEqual(0, await db.PushDeliveries.CountAsync());
        Assert.AreEqual(0, await db.Set<OnCallHandoverReceipt>().CountAsync());
        Assert.IsNull((await db.OnCallGroups.SingleAsync()).HandoverCheckedThroughUtc);
        Assert.AreEqual(0, logger.Entries.Count);
    }

    [TestMethod]
    public async Task HandoverQueuesOneSummaryForOnlyArrivingEngineersAndPersistsDeduplication()
    {
        group.AutoExtend = false;
        var previous = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddDays(-4), EndsAtUtc = Now,
            Members = [new() { UserId = "a" }, new() { UserId = "b" }] };
        var current = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now, EndsAtUtc = Now.AddDays(4),
            Members = [new() { UserId = "b" }, new() { UserId = "c" }] };
        db.OnCallShifts.AddRange(previous, current);
        var application = new SourceApplication { Name = "App", OnCallGroupId = group.Id };
        var open = new Incident { Application = application, Title = "Unresolved outage", Level = "sev1", Status = Incident.Mitigated,
            AcknowledgedAtUtc = Now.AddHours(-1), OwnerId = "a", OwnerDisplayName = "Original owner" };
        db.Incidents.AddRange(open, new() { Application = application, Title = "Resolved outage", Status = Incident.Resolved },
            new() { Application = new() { Name = "Unbound" }, Title = "Foreign outage" });
        await db.SaveChangesAsync();
        await Maintain();
        var delivery = await db.PushDeliveries.Include(item => item.OnCallEmail).ThenInclude(snapshot => snapshot!.Recipients).SingleAsync();
        Assert.AreEqual(PushDelivery.Pending, delivery.Status);
        Assert.AreEqual("c", delivery.OnCallEmail!.Recipients.Single().UserId);
        Assert.AreEqual(current.Id, delivery.OnCallEmail.ShiftId);
        var content = delivery.OnCallEmail.HandoverContent!;
        foreach (var text in new[] { $"You are now on call: {group.Name}", "1 total (SEV1 1), 0 unacknowledged",
            "App (1 incidents)", $"https://example.com/alerts/Incidents?ApplicationId={application.Id}",
            "https://example.com/alerts/OnCall/Mine", $"https://example.com/alerts/OnCall/ScheduleDetails/{group.Id}" })
            Assert.Contains(text, content);
        foreach (var text in new[] { "Unresolved outage", "Resolved outage", "Foreign outage", "Original owner", "/Incidents/Details/" })
            Assert.DoesNotContain(text, content);
        db.ChangeTracker.Clear();
        await Maintain();
        Assert.AreEqual(1, await db.PushDeliveries.CountAsync());
        Assert.AreEqual("a", (await db.Incidents.SingleAsync(incident => incident.Id == open.Id)).OwnerId);
        db.OnCallEmailSnapshots.Add(new() { Delivery = new(), GroupId = group.Id, HandoverKey = delivery.OnCallEmail.HandoverKey });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task HandoverSummarySortsLevelsAndApplicationsWithCounts()
    {
        AddSummaryShift();
        var zebra = new SourceApplication { Name = "Zebra", OnCallGroupId = group.Id };
        var beta = new SourceApplication { Name = "Beta", OnCallGroupId = group.Id };
        var alpha = new SourceApplication { Name = "Alpha", OnCallGroupId = group.Id };
        db.Incidents.AddRange(
            new() { Application = zebra, Level = "sev2" },
            new() { Application = zebra, Level = "sev3", AcknowledgedAtUtc = Now },
            new() { Application = beta, Level = "sev2" },
            new() { Application = alpha, Level = "sev1" });
        await db.SaveChangesAsync();
        await Maintain();

        var content = (await db.OnCallEmailSnapshots.SingleAsync()).HandoverContent!;
        Assert.Contains("4 total (SEV1 1 · SEV2 2 · SEV3 1), 3 unacknowledged", content);
        CollectionAssert.AreEqual(new[] { AppLine(zebra, 2), AppLine(alpha, 1), AppLine(beta, 1) }, ApplicationLines(content));
    }

    [TestMethod]
    public async Task HandoverSummaryListsOnlyFirstTwentyApplications()
    {
        AddSummaryShift();
        var applications = Enumerable.Range(1, 22)
            .Select(index => new SourceApplication { Name = $"App {index:D2}", OnCallGroupId = group.Id }).ToArray();
        db.Incidents.AddRange(applications.Reverse().Select(application => new Incident { Application = application, Level = "sev1" }));
        await db.SaveChangesAsync();
        await Maintain();

        var content = (await db.OnCallEmailSnapshots.SingleAsync()).HandoverContent!;
        Assert.Contains("22 total (SEV1 22), 22 unacknowledged", content);
        CollectionAssert.AreEqual(applications.Take(20).Select(application => AppLine(application, 1)).ToArray(), ApplicationLines(content));
        Assert.Contains("2 more applications", content);
    }

    [TestMethod]
    public async Task HandoverDoesNotReadIncidentsWhenNobodyNeedsAnEmail()
    {
        var current = AddSummaryShift();
        group.HandoverCheckedThroughUtc = Now.AddHours(-3);
        var expired = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddHours(-2), EndsAtUtc = Now,
            Members = [new() { UserId = "b" }] };
        current.Members.Add(new() { UserId = "b" });
        db.OnCallShifts.Add(expired);
        db.Add(new OnCallHandoverReceipt { ShiftId = current.Id, UserId = "a", ProcessedAtUtc = Now });
        db.Incidents.Add(new() { Application = new() { Name = "App", OnCallGroupId = group.Id } });
        await db.SaveChangesAsync();
        var readsBefore = incidentReads.Count;
        await Maintain();

        Assert.AreEqual(readsBefore, incidentReads.Count);
        Assert.AreEqual(0, await db.OnCallEmailSnapshots.CountAsync());
        Assert.AreEqual(3, await db.Set<OnCallHandoverReceipt>().CountAsync());
    }

    [TestMethod]
    public async Task HandoverSummaryShowsEachShiftInTeamTimeZone()
    {
        var first = AddSummaryShift();
        var second = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddHours(-1), EndsAtUtc = Now.AddHours(4),
            Members = [new() { UserId = "b" }] };
        db.OnCallShifts.Add(second);
        db.Incidents.Add(new() { Application = new() { Name = "App", OnCallGroupId = group.Id } });
        await db.SaveChangesAsync();
        var readsBefore = incidentReads.Count;
        await Maintain();

        var summaries = await db.OnCallEmailSnapshots.ToDictionaryAsync(snapshot => snapshot.ShiftId!.Value, snapshot => snapshot.HandoverContent!);
        Assert.AreEqual(2, summaries.Count);
        Assert.AreEqual(readsBefore + 1, incidentReads.Count);
        Assert.Contains("Shift: 2026-09-28 22:00 – 2026-09-29 02:00 (Asia/Shanghai)", summaries[first.Id]);
        Assert.Contains("Shift: 2026-09-28 21:00 – 2026-09-29 02:00 (Asia/Shanghai)", summaries[second.Id]);
    }

    private OnCallShift AddSummaryShift()
    {
        group.AutoExtend = false;
        var shift = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now, EndsAtUtc = Now.AddHours(4),
            Members = [new() { UserId = "a" }] };
        db.OnCallShifts.Add(shift);
        return shift;
    }

    private static string AppLine(SourceApplication app, int count) =>
        $"  {app.Name} ({count} incidents)  https://example.com/alerts/Incidents?ApplicationId={app.Id}";

    private static string[] ApplicationLines(string content) => content.Split(Environment.NewLine)
        .Where(line => line.Contains("/Incidents?ApplicationId=", StringComparison.Ordinal)).ToArray();

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MidShiftMemberChangesNotifyOnlyNewArrivals(bool replace)
    {
        group.AutoExtend = false;
        var current = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddHours(-1), EndsAtUtc = Now.AddHours(4),
            Members = [new() { UserId = "a" }] };
        db.OnCallShifts.Add(current);
        db.Incidents.Add(new() { Application = new() { Name = "App", OnCallGroupId = group.Id }, Title = "Open outage" });
        await db.SaveChangesAsync();
        await Maintain();

        await SaveSchedule(new OnCallSlotRequest { Id = current.Id, StartsAtUtc = current.StartsAtUtc, EndsAtUtc = current.EndsAtUtc,
            UserIds = replace ? ["b"] : ["a", "b"] });
        await Maintain(Now.AddMinutes(1));
        var deliveries = await db.PushDeliveries.Include(item => item.OnCallEmail).ThenInclude(snapshot => snapshot!.Recipients).ToListAsync();
        Assert.AreEqual(2, deliveries.Count);
        Assert.IsTrue(deliveries.All(delivery => delivery.OnCallEmail!.ShiftId == current.Id));
        CollectionAssert.AreEquivalent(new[] { "a", "b" }, deliveries.SelectMany(delivery => delivery.OnCallEmail!.Recipients.Select(person => person.UserId)).ToArray());
        var receipts = await db.Set<OnCallHandoverReceipt>().ToListAsync();
        Assert.AreEqual(replace ? 1 : 2, receipts.Count);
        Assert.AreEqual(Now.AddMinutes(1), receipts.Single(receipt => receipt.UserId == "b").ProcessedAtUtc);
        if (!replace) Assert.AreEqual(Now, receipts.Single(receipt => receipt.UserId == "a").ProcessedAtUtc);

        await Maintain(Now.AddMinutes(2));
        Assert.AreEqual(2, await db.PushDeliveries.CountAsync());
        Assert.AreEqual(receipts.Count, await db.Set<OnCallHandoverReceipt>().CountAsync());
    }

    [TestMethod]
    public async Task BackdatedOngoingShiftNotifiesMembersDespiteCheckpoint()
    {
        group.AutoExtend = false;
        db.Incidents.Add(new() { Application = new() { Name = "App", OnCallGroupId = group.Id }, Title = "Open outage" });
        await db.SaveChangesAsync();
        await Maintain();
        Assert.AreEqual(Now, (await db.OnCallGroups.SingleAsync()).HandoverCheckedThroughUtc);

        await SaveSchedule(new OnCallSlotRequest { StartsAtUtc = Now.AddMinutes(-7), EndsAtUtc = Now.AddHours(8), UserIds = ["b"] });
        var current = await db.OnCallShifts.SingleAsync();
        await Maintain(Now.AddMinutes(1));
        var delivery = await db.PushDeliveries.Include(item => item.OnCallEmail).ThenInclude(snapshot => snapshot!.Recipients).SingleAsync();
        Assert.AreEqual(current.Id, delivery.OnCallEmail!.ShiftId);
        Assert.AreEqual("b", delivery.OnCallEmail.Recipients.Single().UserId);
        var receipt = await db.Set<OnCallHandoverReceipt>().SingleAsync();
        Assert.AreEqual(current.Id, receipt.ShiftId);
        Assert.AreEqual("b", receipt.UserId);
        Assert.AreEqual(Now.AddMinutes(1), receipt.ProcessedAtUtc);

        await Maintain(Now.AddMinutes(2));
        Assert.AreEqual(1, await db.PushDeliveries.CountAsync());
        Assert.AreEqual(1, await db.Set<OnCallHandoverReceipt>().CountAsync());
    }

    [TestMethod]
    public async Task ReaddedMemberReceivesAnotherHandoverWithDistinctKey()
    {
        group.AutoExtend = false;
        var previous = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddHours(-3), EndsAtUtc = Now.AddHours(-2),
            Members = [new() { UserId = "b" }] };
        var current = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddHours(-1), EndsAtUtc = Now.AddHours(4),
            Members = [new() { UserId = "a" }, new() { UserId = "b" }] };
        db.OnCallShifts.AddRange(previous, current);
        db.Add(new OnCallHandoverReceipt { ShiftId = previous.Id, UserId = "b", ProcessedAtUtc = previous.StartsAtUtc });
        db.Incidents.Add(new() { Application = new() { Name = "App", OnCallGroupId = group.Id }, Title = "Open outage" });
        await db.SaveChangesAsync();
        await Maintain();
        Assert.AreEqual(2, await db.PushDeliveries.CountAsync());

        var previousSlot = new OnCallSlotRequest { Id = previous.Id, StartsAtUtc = previous.StartsAtUtc,
            EndsAtUtc = previous.EndsAtUtc, UserIds = ["b"] };
        var currentSlot = new OnCallSlotRequest { Id = current.Id, StartsAtUtc = current.StartsAtUtc,
            EndsAtUtc = current.EndsAtUtc, UserIds = ["a"] };
        await SaveSchedule(previousSlot, currentSlot);
        var retained = await db.Set<OnCallHandoverReceipt>().ToListAsync();
        Assert.AreEqual(2, retained.Count);
        Assert.AreEqual("b", retained.Single(receipt => receipt.ShiftId == previous.Id).UserId);
        Assert.AreEqual("a", retained.Single(receipt => receipt.ShiftId == current.Id).UserId);
        await Maintain(Now.AddMinutes(1));
        Assert.AreEqual(2, await db.PushDeliveries.CountAsync());

        currentSlot.UserIds.Add("b");
        await SaveSchedule(previousSlot, currentSlot);
        await Maintain(Now.AddMinutes(2));
        var deliveries = await db.PushDeliveries.Include(item => item.OnCallEmail).ThenInclude(snapshot => snapshot!.Recipients).ToListAsync();
        Assert.AreEqual(3, deliveries.Count);
        Assert.AreEqual(1, deliveries.Count(delivery => delivery.OnCallEmail!.Recipients.Single().UserId == "a"));
        var handovers = deliveries.Where(delivery => delivery.OnCallEmail!.Recipients.Single().UserId == "b").ToArray();
        Assert.AreEqual(2, handovers.Length);
        Assert.IsTrue(handovers.All(delivery => delivery.OnCallEmail!.ShiftId == current.Id));
        Assert.AreNotEqual(handovers[0].OnCallEmail!.HandoverKey, handovers[1].OnCallEmail!.HandoverKey);
        var readded = await db.Set<OnCallHandoverReceipt>().SingleAsync(receipt => receipt.ShiftId == current.Id && receipt.UserId == "b");
        Assert.AreEqual(Now.AddMinutes(2), readded.ProcessedAtUtc);

        await Maintain(Now.AddMinutes(3));
        Assert.AreEqual(3, await db.PushDeliveries.CountAsync());
        Assert.AreEqual(3, await db.Set<OnCallHandoverReceipt>().CountAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MidShiftContinuousMembersReceiveReceiptsWithoutEmail(bool parallel)
    {
        group.AutoExtend = false;
        var previous = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddHours(-2),
            EndsAtUtc = parallel ? Now.AddHours(1) : Now.AddHours(-1), Members = [new() { UserId = "b" }] };
        var current = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddHours(-1), EndsAtUtc = Now.AddHours(4),
            Members = [new() { UserId = "a" }] };
        db.OnCallShifts.AddRange(previous, current);
        db.Incidents.Add(new() { Application = new() { Name = "App", OnCallGroupId = group.Id }, Title = "Open outage" });
        await db.SaveChangesAsync();
        await Maintain();
        var deliveryCount = await db.PushDeliveries.CountAsync();
        db.OnCallShiftMembers.Add(new() { ShiftId = current.Id, UserId = "b" });
        await db.SaveChangesAsync();

        await Maintain(Now.AddMinutes(1));
        var receipt = await db.Set<OnCallHandoverReceipt>().SingleAsync(item => item.ShiftId == current.Id && item.UserId == "b");
        Assert.AreEqual(Now.AddMinutes(1), receipt.ProcessedAtUtc);
        Assert.AreEqual(deliveryCount, await db.PushDeliveries.CountAsync());
        await Maintain(Now.AddMinutes(2));
        Assert.AreEqual(deliveryCount, await db.PushDeliveries.CountAsync());
        Assert.AreEqual(parallel ? 3 : 2, await db.Set<OnCallHandoverReceipt>().CountAsync());
    }

    [TestMethod]
    public async Task PersistedCheckpointRecordsExpiredShortShiftsWithoutEmailAcrossRestart()
    {
        group.AutoExtend = false;
        var first = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now, EndsAtUtc = Now.AddMinutes(1), Members = [new() { UserId = "a" }] };
        var second = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddMinutes(1), EndsAtUtc = Now.AddMinutes(2), Members = [new() { UserId = "b" }] };
        db.OnCallShifts.AddRange(first, second);
        await db.SaveChangesAsync();
        await Maintain();
        db.Incidents.Add(new() { Application = new() { Name = "App", OnCallGroupId = group.Id }, Title = "Open outage" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await Maintain(Now.AddMinutes(3));
        Assert.AreEqual(0, await db.PushDeliveries.CountAsync());
        Assert.AreEqual(0, await db.OnCallEmailSnapshots.CountAsync());
        var receipts = await db.Set<OnCallHandoverReceipt>().ToListAsync();
        Assert.AreEqual(2, receipts.Count);
        Assert.AreEqual(Now, receipts.Single(receipt => receipt.ShiftId == first.Id).ProcessedAtUtc);
        var caughtUp = receipts.Single(receipt => receipt.ShiftId == second.Id);
        Assert.AreEqual("b", caughtUp.UserId);
        Assert.AreEqual(Now.AddMinutes(3), caughtUp.ProcessedAtUtc);
        await Maintain(Now.AddMinutes(4));
        Assert.AreEqual(0, await db.PushDeliveries.CountAsync());
        Assert.AreEqual(2, await db.Set<OnCallHandoverReceipt>().CountAsync());
        Assert.AreEqual(Now.AddMinutes(4), (await db.OnCallGroups.SingleAsync()).HandoverCheckedThroughUtc);
    }

    [TestMethod]
    public async Task CatchUpRecordsExpiredHandoversAndOnlyEmailsOngoingShifts()
    {
        group.AutoExtend = false;
        group.HandoverCheckedThroughUtc = Now.AddDays(-5);
        var expired = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddDays(-4), EndsAtUtc = Now.AddDays(-3),
            Members = [new() { UserId = "a" }] };
        var endingNow = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddDays(-3), EndsAtUtc = Now,
            Members = [new() { UserId = "b" }] };
        var ongoing = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddDays(-1), EndsAtUtc = Now.AddDays(1),
            Members = [new() { UserId = "a" }, new() { UserId = "c" }] };
        db.OnCallShifts.AddRange(expired, endingNow, ongoing);
        db.Incidents.Add(new() { Application = new() { Name = "App", OnCallGroupId = group.Id }, Title = "Open outage" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Maintain();
        db.ChangeTracker.Clear();

        var receipts = await db.Set<OnCallHandoverReceipt>().ToListAsync();
        Assert.AreEqual(4, receipts.Count);
        Assert.IsTrue(receipts.All(receipt => receipt.ProcessedAtUtc == Now));
        Assert.AreEqual("a", receipts.Single(receipt => receipt.ShiftId == expired.Id).UserId);
        Assert.AreEqual("b", receipts.Single(receipt => receipt.ShiftId == endingNow.Id).UserId);
        CollectionAssert.AreEquivalent(new[] { "a", "c" }, receipts.Where(receipt => receipt.ShiftId == ongoing.Id).Select(receipt => receipt.UserId).ToArray());
        var deliveries = await db.PushDeliveries.Include(item => item.OnCallEmail).ThenInclude(snapshot => snapshot!.Recipients).ToListAsync();
        Assert.AreEqual(2, deliveries.Count);
        Assert.IsTrue(deliveries.All(delivery => delivery.Status == PushDelivery.Pending && delivery.OnCallEmail!.ShiftId == ongoing.Id));
        Assert.IsTrue(deliveries.All(delivery => delivery.OnCallEmail!.NotificationAtUtc == ongoing.StartsAtUtc));
        CollectionAssert.AreEquivalent(new[] { "a", "c" }, deliveries.SelectMany(delivery => delivery.OnCallEmail!.Recipients.Select(person => person.UserId)).ToArray());
        Assert.AreEqual(Now, (await db.OnCallGroups.SingleAsync()).HandoverCheckedThroughUtc);
        await Maintain(Now.AddMinutes(1));
        Assert.AreEqual(4, await db.Set<OnCallHandoverReceipt>().CountAsync());
        Assert.AreEqual(2, await db.PushDeliveries.CountAsync());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ExpiredCatchUpRecordsEveryMemberWithoutUnresolvedIncidents(int applicationState)
    {
        group.AutoExtend = false;
        group.HandoverCheckedThroughUtc = Now.AddDays(-5);
        var previous = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddDays(-6), EndsAtUtc = Now.AddDays(-4),
            Members = [new() { UserId = "a" }] };
        var first = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddDays(-4), EndsAtUtc = Now.AddDays(-2),
            Members = [new() { UserId = "a" }, new() { UserId = "b" }] };
        var second = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddDays(-2), EndsAtUtc = Now,
            Members = [new() { UserId = "b" }, new() { UserId = "c" }] };
        db.OnCallShifts.AddRange(previous, first, second);
        if (applicationState > 0)
        {
            var application = new SourceApplication { Name = "App", OnCallGroupId = group.Id };
            db.SourceApplications.Add(application);
            if (applicationState == 2)
                db.Incidents.Add(new() { Application = application, Title = "Resolved outage", Status = Incident.Resolved });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Maintain();
        db.ChangeTracker.Clear();

        var receipts = await db.Set<OnCallHandoverReceipt>().ToListAsync();
        Assert.AreEqual(4, receipts.Count);
        Assert.IsTrue(receipts.All(receipt => receipt.ProcessedAtUtc == Now));
        CollectionAssert.AreEquivalent(new[] { "a", "b" }, receipts.Where(receipt => receipt.ShiftId == first.Id).Select(receipt => receipt.UserId).ToArray());
        CollectionAssert.AreEquivalent(new[] { "b", "c" }, receipts.Where(receipt => receipt.ShiftId == second.Id).Select(receipt => receipt.UserId).ToArray());
        Assert.AreEqual(0, await db.PushDeliveries.CountAsync());
        Assert.AreEqual(0, await db.OnCallEmailSnapshots.CountAsync());
        var savedGroup = await db.OnCallGroups.SingleAsync();
        Assert.AreEqual(Now, savedGroup.HandoverCheckedThroughUtc);
        savedGroup.HandoverCheckedThroughUtc = Now.AddDays(-5);
        await db.SaveChangesAsync();
        await Maintain(Now.AddMinutes(1));
        Assert.AreEqual(4, await db.Set<OnCallHandoverReceipt>().CountAsync());
        Assert.AreEqual(0, await db.PushDeliveries.CountAsync());
    }

    [TestMethod]
    public async Task SimultaneousOverlappingHandoversSendOneEmailPerArrivingPerson()
    {
        group.AutoExtend = false;
        db.OnCallShifts.AddRange(new OnCallShift { GroupId = group.Id, StartsAtUtc = Now, EndsAtUtc = Now.AddDays(1), Members = [new() { UserId = "a" }] },
            new OnCallShift { GroupId = group.Id, StartsAtUtc = Now, EndsAtUtc = Now.AddDays(2), Members = [new() { UserId = "a" }] });
        db.Incidents.Add(new() { Application = new() { Name = "App", OnCallGroupId = group.Id }, Title = "Open outage" });
        await db.SaveChangesAsync();
        await Maintain();
        Assert.AreEqual(1, await db.PushDeliveries.CountAsync());
        Assert.AreEqual(2, await db.Set<OnCallHandoverReceipt>().CountAsync());
        await Maintain();
        Assert.AreEqual(1, await db.PushDeliveries.CountAsync());
    }

    [TestMethod]
    public async Task NoUnresolvedIncidentsMeansNoHandoverEmail()
    {
        group.AutoExtend = false;
        db.OnCallShifts.Add(new() { GroupId = group.Id, StartsAtUtc = Now, EndsAtUtc = Now.AddDays(1), Members = [new() { UserId = "a" }] });
        db.Incidents.Add(new() { Application = new() { Name = "App", OnCallGroupId = group.Id }, Status = Incident.Resolved });
        await db.SaveChangesAsync();
        await Maintain();
        Assert.AreEqual(0, await db.PushDeliveries.CountAsync());
        Assert.AreEqual(1, await db.Set<OnCallHandoverReceipt>().CountAsync());
        db.Incidents.Add(new() { Application = new() { Name = "Later app", OnCallGroupId = group.Id }, Title = "Later incident" });
        await db.SaveChangesAsync();
        await Maintain(Now.AddMinutes(1));
        Assert.AreEqual(0, await db.PushDeliveries.CountAsync());
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task AlternateRoundPositionSurvivesExtensionBoundariesWithMultipleStaff(int staffPerShift)
    {
        var rule = group.Rule!;
        db.RemoveRange(rule.HandoverPoints);
        rule.HandoverPoints.Clear();
        rule.ShiftHours = 24;
        rule.StaffPerShift = staffPerShift;
        rule.SwapAlternateRounds = true;
        group.Members.Add(new() { UserId = "d", RotationOrder = 3 });
        await db.SaveChangesAsync();
        await Maintain();
        var original = await db.OnCallShifts.OrderBy(shift => shift.StartsAtUtc).Select(shift => shift.Id).ToArrayAsync();
        await Maintain(Now.AddDays(7));
        var saved = await db.OnCallShifts.Include(shift => shift.Members).OrderBy(shift => shift.StartsAtUtc).ToListAsync();
        var expected = new OnCallScheduleGenerator(TestLocalization.For<OnCallScheduleGenerator>())
            .Generate(rule, Now, ["a", "b", "c", "d"], Now, 35).GroupBy(shift => shift.StartsAtUtc).ToArray();
        Assert.AreEqual(35, saved.Count);
        CollectionAssert.AreEqual(original, saved.Take(original.Length).Select(shift => shift.Id).ToArray());
        for (var index = 0; index < saved.Count; index++)
            CollectionAssert.AreEquivalent(expected[index].Select(shift => shift.UserId).ToArray(), saved[index].Members.Select(member => member.UserId).ToArray());
    }
}

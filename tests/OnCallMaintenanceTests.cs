using Aiursoft.AlertCenter.Entities;
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
    private MaintenanceLogger logger = null!;
    private static readonly DateTime Now = new(2026, 9, 28, 14, 0, 0, DateTimeKind.Utc);

    [TestInitialize]
    public async Task Setup()
    {
        saveFailure = new();
        logger = new();
        db = new(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:")
            .AddInterceptors(saveFailure).Options);
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
    public async Task ExtensionOnlyAppendsAfterLatestManualShiftAndContinuesQueueAcrossReruns()
    {
        var manual = new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddDays(-1), EndsAtUtc = Now.AddDays(4),
            Source = OnCallShiftSource.Manual, Members = [new() { UserId = "b" }] };
        db.OnCallShifts.Add(manual);
        await db.SaveChangesAsync();
        var version = group.ScheduleVersion;
        await Maintain();
        var shifts = await db.OnCallShifts.Include(shift => shift.Members).OrderBy(shift => shift.StartsAtUtc).ToListAsync();
        Assert.AreEqual(manual.Id, shifts[0].Id);
        Assert.AreEqual(Now.AddDays(-1), shifts[0].StartsAtUtc);
        Assert.AreEqual(Now.AddDays(4), shifts[0].EndsAtUtc);
        Assert.AreEqual("b", shifts[0].Members.Single().UserId);
        Assert.AreEqual(OnCallShiftSource.Manual, shifts[0].Source);
        Assert.AreEqual("c", shifts[1].Members.Single().UserId);
        Assert.AreEqual(shifts[0].EndsAtUtc, shifts[1].StartsAtUtc);
        Assert.IsTrue(shifts[^1].EndsAtUtc >= Now.AddDays(28));
        Assert.IsTrue(shifts.Skip(1).All(shift => shift.Source == OnCallShiftSource.Generated));
        var ids = shifts.Select(shift => shift.Id).ToArray();
        Assert.AreNotEqual(version, (await db.OnCallGroups.SingleAsync()).ScheduleVersion);
        await Maintain();
        CollectionAssert.AreEqual(ids, await db.OnCallShifts.OrderBy(shift => shift.StartsAtUtc).Select(shift => shift.Id).ToArrayAsync());
        await Maintain(Now.AddDays(7));
        var extended = await db.OnCallShifts.Include(shift => shift.Members).OrderBy(shift => shift.StartsAtUtc).ToListAsync();
        CollectionAssert.AreEqual(ids, extended.Take(ids.Length).Select(shift => shift.Id).ToArray());
        Assert.IsTrue(extended[^1].EndsAtUtc >= Now.AddDays(35));
        var users = new[] { "a", "b", "c" };
        var previous = Array.IndexOf(users, shifts[^1].Members.Single().UserId);
        Assert.AreEqual(users[(previous + 1) % users.Length], extended[ids.Length].Members.Single().UserId);
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
        foreach (var text in new[] { "SEV1", "Unresolved outage", "mitigated", "ACK: True", "Original owner", $"https://example.com/alerts/Incidents/Details/{open.Id}" })
            Assert.Contains(text, content);
        Assert.DoesNotContain("Resolved outage", content);
        Assert.DoesNotContain("Foreign outage", content);
        db.ChangeTracker.Clear();
        await Maintain();
        Assert.AreEqual(1, await db.PushDeliveries.CountAsync());
        Assert.AreEqual("a", (await db.Incidents.SingleAsync(incident => incident.Id == open.Id)).OwnerId);
        db.OnCallEmailSnapshots.Add(new() { Delivery = new(), GroupId = group.Id, HandoverKey = delivery.OnCallEmail.HandoverKey });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
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

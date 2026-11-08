using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallMaintenanceTests
{
    private SqliteContext db = null!;
    private OnCallGroup group = null!;
    private static readonly DateTime Now = new(2026, 9, 28, 14, 0, 0, DateTimeKind.Utc);

    [TestInitialize]
    public async Task Setup()
    {
        db = new(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
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
        "https://example.com/alerts", group.Id, now ?? Now, default);

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
    public async Task PersistedCheckpointCatchesShortShiftsBetweenPollsAndAcrossRestart()
    {
        group.AutoExtend = false;
        db.OnCallShifts.AddRange(new OnCallShift { GroupId = group.Id, StartsAtUtc = Now, EndsAtUtc = Now.AddMinutes(1), Members = [new() { UserId = "a" }] },
            new OnCallShift { GroupId = group.Id, StartsAtUtc = Now.AddMinutes(1), EndsAtUtc = Now.AddMinutes(2), Members = [new() { UserId = "b" }] });
        await db.SaveChangesAsync();
        await Maintain();
        db.Incidents.Add(new() { Application = new() { Name = "App", OnCallGroupId = group.Id }, Title = "Open outage" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await Maintain(Now.AddMinutes(3));
        var snapshot = await db.OnCallEmailSnapshots.Include(item => item.Recipients).SingleAsync();
        Assert.AreEqual("b", snapshot.Recipients.Single().UserId);
        Assert.AreEqual(Now.AddMinutes(1), snapshot.NotificationAtUtc);
        await Maintain(Now.AddMinutes(4));
        Assert.AreEqual(1, await db.PushDeliveries.CountAsync());
        Assert.AreEqual(Now.AddMinutes(4), (await db.OnCallGroups.SingleAsync()).HandoverCheckedThroughUtc);
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

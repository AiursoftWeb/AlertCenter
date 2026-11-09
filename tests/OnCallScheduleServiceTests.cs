using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallScheduleServiceTests
{
    private SqliteContext db = null!;
    private OnCallScheduleService service = null!;
    private OnCallGroup group = null!;
    private static readonly DateTime Anchor = new(2026, 9, 28, 14, 0, 0, DateTimeKind.Utc);

    [TestInitialize]
    public async Task Setup()
    {
        db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        db.Users.AddRange(new[] { "a", "b", "c", "outsider" }.Select(id => new User { Id = id, UserName = id, DisplayName = id }));
        group = new OnCallGroup { Name = "Team", AnchorUtc = Anchor, TimeZoneId = "Asia/Shanghai",
            Rule = new() { Name = "Template" }, MinimumRestHours = 16, MaximumContinuousHours = 24, MinimumShiftStaff = 2,
            Members = new[] { "a", "b", "c" }.Select((id, index) => new OnCallGroupMember { UserId = id, RotationOrder = index }).ToList() };
        db.OnCallGroups.Add(group);
        await db.SaveChangesAsync();
        service = new(db, new OnCallScheduleGenerator(TestLocalization.For<OnCallScheduleGenerator>()),
            TestLocalization.For<OnCallScheduleService>(), TestLocalization.Factory);
    }

    [TestCleanup]
    public async Task Cleanup() => await db.DisposeAsync();

    private SaveOnCallScheduleRequest Request(params OnCallSlotRequest[] slots) => new()
        { GroupId = group.Id, Version = group.ScheduleVersion, StartUtc = Anchor, Slots = slots.ToList() };

    private static OnCallSlotRequest Slot(int minutes, int duration, params string[] ids) => new()
        { StartsAtUtc = Anchor.AddMinutes(minutes), EndsAtUtc = Anchor.AddMinutes(minutes + duration), UserIds = ids.ToList() };

    [TestMethod]
    public async Task SavesArbitraryMinutePrecisionDurationsEmptyShiftsOverlapsAndGapsAsWarnings()
    {
        var request = Request(Slot(17, 37, "a"), Slot(20, 7, "b", "c"), Slot(60, 96 * 60, "a"), Slot(6000, 30));
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        var saved = await db.OnCallShifts.OrderBy(shift => shift.StartsAtUtc).ToListAsync();
        Assert.AreEqual(4, saved.Count);
        Assert.AreEqual(Anchor.AddMinutes(17), saved[0].StartsAtUtc);
        Assert.AreEqual(Anchor.AddMinutes(54), saved[0].EndsAtUtc);
        Assert.IsTrue(saved.All(shift => shift.Source == OnCallShiftSource.Manual));
        foreach (var text in new[] { "gap", "overlapping", "no on-duty", "below", "insufficient rest", "continuous" })
            Assert.IsTrue(service.Warnings.Any(warning => warning.Contains(text)), text);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OverlapsRejectAtomicallyWhilePreviewRetainsAllSlotsAndDiagnostics(bool unassigned)
    {
        (await db.Users.SingleAsync(user => user.Id == "a")).DisplayName = "Alice";
        await db.SaveChangesAsync();
        var original = Slot(0, 60, "c");
        Assert.AreEqual(0, (await service.Save(Request(original), false, default)).Count);
        var version = group.ScheduleVersion;
        var users = unassigned ? Array.Empty<string>() : ["a"];
        var request = Request(Slot(17, 37, users), Slot(20, 7, users));
        var errors = await service.Save(request, false, default);
        Assert.AreEqual(1, errors.Count);
        foreach (var text in new[] { unassigned ? "Unassigned" : "Alice", "2026-09-28 14:17", "2026-09-28 14:54", "2026-09-28 14:20", "2026-09-28 14:27" })
            Assert.Contains(text, errors[0]);
        Assert.AreEqual(version, group.ScheduleVersion);
        Assert.AreEqual(Anchor, (await db.OnCallShifts.SingleAsync()).StartsAtUtc);
        await db.SaveChangesAsync();
        Assert.AreEqual(1, await db.OnCallShiftMembers.CountAsync(member => member.UserId == "c"));
        Assert.AreEqual(0, (await service.Save(request, false, default, preview: true)).Count);
        Assert.AreEqual(2, service.PreviewSlots.Count);
        Assert.AreEqual(request.Slots[0].StartsAtUtc, service.PreviewSlots[0].StartsAtUtc);
        CollectionAssert.AreEqual(errors, service.PreviewErrors);
        Assert.IsTrue(service.Warnings.Contains(errors[0]));
        Assert.IsTrue(service.TimelineWarnings.Any(warning => warning.Message == errors[0]));
        Assert.AreEqual(0, (await service.Save(Request(Slot(17, 37, users)), false, default, preview: true)).Count);
        Assert.AreEqual(0, service.PreviewErrors.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TouchingAssignmentsAndUnassignedShiftsAreAllowed(bool unassigned)
    {
        var users = unassigned ? Array.Empty<string>() : ["a", "b"];
        Assert.AreEqual(0, (await service.Save(Request(Slot(0, 60, users), Slot(60, 30, users)), false, default)).Count);
        Assert.AreEqual(2, await db.OnCallShifts.CountAsync());
    }

    [TestMethod]
    public async Task SharedMemberAndAdjacentWeekOverlapRejectWithoutWrites()
    {
        var request = Request(Slot(7 * 1440 - 30, 60, "a", "b"));
        request.AdjacentWeeks = [new() { StartUtc = Anchor.AddDays(7), Slots = [Slot(7 * 1440, 60, "b", "c")] }];
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("for b:")));
        Assert.AreEqual(0, await db.OnCallShifts.CountAsync());
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(1)]
    public async Task ChecksRetainedShiftsBeyondEitherReplacementBoundary(int direction)
    {
        var retained = new OnCallShift { GroupId = group.Id, StartsAtUtc = Anchor.AddHours(direction < 0 ? -2 : 2),
            EndsAtUtc = Anchor.AddHours(direction < 0 ? 0 : 4), Members = [new() { UserId = "a" }] };
        db.OnCallShifts.Add(retained);
        await db.SaveChangesAsync();
        var request = Request(Slot(direction < 0 ? -60 : 60, 120, "a"));
        request.EndUtc = Anchor.AddHours(2);
        Assert.AreEqual(1, (await service.Save(request, false, default)).Count);
        Assert.AreEqual(retained.Id, (await db.OnCallShifts.SingleAsync()).Id);
        request.Slots[0] = Slot(0, 120, "a");
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        Assert.AreEqual(2, await db.OnCallShifts.CountAsync());
    }

    [TestMethod]
    public async Task CrossTeamOverlapWithoutThresholdsOnlyWarns()
    {
        group.MinimumRestHours = null;
        group.MaximumContinuousHours = null;
        group.MinimumShiftStaff = null;
        var other = new OnCallShift { Group = new() { Name = "Other" }, StartsAtUtc = Anchor, EndsAtUtc = Anchor.AddHours(1),
            Members = [new() { UserId = "a" }] };
        db.OnCallShifts.Add(other);
        await db.SaveChangesAsync();
        Assert.AreEqual(0, (await service.Save(Request(Slot(0, 60, "a")), false, default)).Count);
        Assert.IsTrue(service.Warnings.Any(warning => warning.Contains("overlapping")));
        Assert.AreEqual(2, await db.OnCallShifts.CountAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GenerationClipsPartialPeriodsAndPreservesTouchingNeighbours(bool weekly)
    {
        if (weekly)
        {
            group.Rule!.HandoverPoints = OnCallScheduleGeneratorTests.WeeklyRule().HandoverPoints;
            db.AddRange(group.Rule.HandoverPoints);
        }
        var start = Anchor.AddMinutes(17);
        var end = Anchor.AddHours(9);
        var neighbours = new[]
        {
            new OnCallShift { GroupId = group.Id, StartsAtUtc = Anchor, EndsAtUtc = start, Members = [new() { UserId = "a" }] },
            new OnCallShift { GroupId = group.Id, StartsAtUtc = end, EndsAtUtc = end.AddHours(1), Members = [new() { UserId = "a" }] }
        };
        db.OnCallShifts.AddRange(neighbours);
        await db.SaveChangesAsync();
        var request = Request();
        request.StartUtc = start;
        request.EndUtc = end;
        Assert.AreEqual(0, (await service.Save(request, true, default)).Count);
        var saved = await db.OnCallShifts.Include(shift => shift.Members).OrderBy(shift => shift.StartsAtUtc).ToListAsync();
        Assert.AreEqual(neighbours[0].Id, saved[0].Id);
        Assert.AreEqual(neighbours[1].Id, saved[^1].Id);
        Assert.AreEqual(start, saved[1].StartsAtUtc);
        Assert.AreEqual(end, saved[^2].EndsAtUtc);
        Assert.AreEqual(0, OnCallScheduleValidator.OverlapErrors(saved, []).Count);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task InvalidEndRollsBackWholeSave(int duration)
    {
        var version = group.ScheduleVersion;
        Assert.IsTrue((await service.Save(Request(Slot(0, 60, "a"), Slot(60, duration, "b")), false, default)).Count > 0);
        Assert.AreEqual(0, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(version, group.ScheduleVersion);
    }

    [TestMethod]
    [DataRow("outsider")]
    [DataRow("missing")]
    public async Task RejectsNonMembersAndNonexistentUsersWithoutAnyWrites(string userId)
    {
        Assert.IsTrue((await service.Save(Request(Slot(0, 60, "a"), Slot(60, 60, userId)), false, default)).Count > 0);
        Assert.AreEqual(0, await db.OnCallShifts.CountAsync());
    }

    [TestMethod]
    public async Task RejectsConcurrentVersionAndPreservesSavedSchedule()
    {
        var request = Request(Slot(0, 30, "a"));
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.Save(request, false, default));
        Assert.AreEqual(1, await db.OnCallShifts.CountAsync());
    }

    [TestMethod]
    public async Task CrossTeamShortRestOnlyWarnsAndDoesNotChangeOtherTeams()
    {
        var other = new OnCallShift { Group = new() { Name = "Other" }, StartsAtUtc = Anchor.AddHours(-1), EndsAtUtc = Anchor,
            Members = [new() { UserId = "a" }] };
        db.OnCallShifts.Add(other);
        await db.SaveChangesAsync();
        Assert.AreEqual(0, (await service.Save(Request(Slot(1, 30, "a")), false, default)).Count);
        Assert.IsTrue(service.Warnings.Any(warning => warning.Contains("insufficient rest")));
        Assert.AreEqual(Anchor, (await db.OnCallShifts.SingleAsync(shift => shift.Id == other.Id)).EndsAtUtc);
    }

    [TestMethod]
    public async Task MultiweekEditsSaveAtomicallyAndDoNotEnforceRestOrCompleteGrids()
    {
        var request = Request(Slot(0, 60, "a"));
        request.AdjacentWeeks = [new() { StartUtc = Anchor.AddDays(7), Slots = [Slot(7 * 1440, 20, "a")] }];
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        Assert.AreEqual(2, await db.OnCallShifts.CountAsync());
        request.Version = group.ScheduleVersion;
        request.AdjacentWeeks[0].Slots[0].UserIds = ["missing"];
        Assert.IsTrue((await service.Save(request, false, default)).Count > 0);
        Assert.AreEqual(2, await db.OnCallShifts.CountAsync());
    }

    [TestMethod]
    public async Task DepartedMembersRemainOnOriginalShiftsWithWarningButCannotBeAddedElsewhere()
    {
        Assert.AreEqual(0, (await service.Save(Request(Slot(0, 60, "a")), false, default)).Count);
        var departing = group.Members.Single(member => member.UserId == "a");
        db.OnCallGroupMembers.Remove(departing);
        group.Members.Remove(departing);
        await db.SaveChangesAsync();
        Assert.AreEqual(0, (await service.Save(Request(Slot(0, 60, "a")), false, default)).Count);
        Assert.IsTrue(service.Warnings.Any(warning => warning.Contains("departed")));
        Assert.IsTrue((await service.Save(Request(Slot(0, 60, "a"), Slot(120, 30, "a")), false, default)).Count > 0);
        Assert.AreEqual(1, await db.OnCallShifts.CountAsync());
    }

    [TestMethod]
    public async Task GenerationRequiresReplacementConfirmationAndManualChangesMarkSource()
    {
        var request = Request();
        Assert.AreEqual(0, (await service.Save(request, true, default)).Count);
        Assert.AreEqual(21, await db.OnCallShifts.CountAsync());
        Assert.IsTrue(await db.OnCallShifts.AllAsync(shift => shift.Source == OnCallShiftSource.Generated));
        Assert.IsTrue((await service.Save(Request(), true, default)).Count > 0);
        var saved = await db.OnCallShifts.OrderBy(shift => shift.StartsAtUtc).FirstAsync();
        var edit = Request(Slot(0, 37, "b"));
        edit.Slots[0].Id = saved.Id;
        Assert.AreEqual(0, (await service.Save(edit, false, default)).Count);
        Assert.AreEqual(OnCallShiftSource.Manual, (await db.OnCallShifts.SingleAsync()).Source);
        var replace = Request();
        replace.ConfirmReplace = true;
        Assert.AreEqual(0, (await service.Save(replace, true, default)).Count);
        Assert.AreEqual(21, await db.OnCallShifts.CountAsync());
    }

    [TestMethod]
    public async Task WeeklyGenerationUsesTeamTimeZoneAndIncludesSharedMembers()
    {
        group.Rule!.HandoverPoints = OnCallScheduleGeneratorTests.WeeklyRule().HandoverPoints;
        db.AddRange(group.Rule.HandoverPoints);
        db.OnCallGroups.Add(new() { Name = "Shared", Members = [new() { UserId = "a" }] });
        await db.SaveChangesAsync();
        Assert.AreEqual(0, (await service.Save(Request(), true, default)).Count);
        var saved = await db.OnCallShifts.Include(shift => shift.Members).OrderBy(shift => shift.StartsAtUtc).ToListAsync();
        Assert.AreEqual(2, saved.Count);
        Assert.AreEqual(Anchor.AddDays(4), saved[0].EndsAtUtc);
        Assert.AreEqual("a", saved[0].Members.Single().UserId);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("[null]")]
    [DataRow("[{\"userIds\":null}]")]
    public async Task MalformedSlotsFailWithoutWrites(string json)
    {
        var request = Request();
        request.Slots = System.Text.Json.JsonSerializer.Deserialize<List<OnCallSlotRequest>>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.IsTrue((await service.Save(request, false, default)).Count > 0);
        Assert.AreEqual(0, await db.OnCallShifts.CountAsync());
    }

    [TestMethod]
    public void DefaultWeekUsesTeamLocalMondayAndExplicitRangesNeedNoAnchorAlignment()
    {
        Assert.AreEqual(Anchor.AddMinutes(17), OnCallScheduleService.WeekStart(group, Anchor.AddMinutes(17)));
        var local = TimeZoneInfo.ConvertTimeFromUtc(OnCallScheduleService.WeekStart(group), TimeZoneInfo.FindSystemTimeZoneById(group.TimeZoneId));
        Assert.AreEqual(DayOfWeek.Monday, local.DayOfWeek);
        Assert.AreEqual(TimeSpan.Zero, local.TimeOfDay);
    }
}

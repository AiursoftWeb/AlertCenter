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
    private OnCallRule rule = null!;
    private OnCallGroup group = null!;
    private static readonly DateTime Anchor = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

    [TestInitialize]
    public async Task Setup()
    {
        db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        db.Users.AddRange(Enumerable.Range(0, 4).Select(index => new User { Id = $"u{index}", UserName = $"staff{index}", DisplayName = $"Staff {index}" }));
        rule = new OnCallRule { Name = "Three shifts" };
        group = new OnCallGroup
        {
            Name = "Platform", Rule = rule, AnchorUtc = Anchor,
            Members = Enumerable.Range(0, 4).Select(index => new OnCallGroupMember { UserId = $"u{index}", RotationOrder = index }).ToList()
        };
        db.OnCallGroups.Add(group);
        await db.SaveChangesAsync();
        service = new OnCallScheduleService(db, new OnCallScheduleGenerator(localizer:TestLocalization.For<OnCallScheduleGenerator>()), localizer: TestLocalization.For<OnCallScheduleService>(), localizerFactory: TestLocalization.Factory);
    }

    [TestCleanup]
    public async Task Cleanup() => await db.DisposeAsync();

    private SaveOnCallScheduleRequest Request(DateTime? start = null) => new()
    {
        GroupId = group.Id, Version = group.ScheduleVersion, StartUtc = start ?? Anchor
    };

    private async Task<SaveOnCallScheduleRequest> EditRequest(DateTime? start = null)
    {
        var request = Request(start);
        var end = request.StartUtc.AddDays(7);
        request.Slots = await db.OnCallShifts.Where(shift => shift.GroupId == group.Id && shift.StartsAtUtc >= request.StartUtc && shift.StartsAtUtc < end)
            .OrderBy(shift => shift.StartsAtUtc).Select(shift => new OnCallSlotRequest
            {
                StartsAtUtc = shift.StartsAtUtc, UserIds = shift.Members.Select(member => member.UserId).ToList()
            }).ToListAsync();
        return request;
    }

    private static List<OnCallSlotRequest> ManualSlots(DateTime start) => Enumerable.Range(0, 21)
        .Select(index => new OnCallSlotRequest { StartsAtUtc = start.AddHours(index * 8), UserIds = [$"u{index % 3}"] }).ToList();

    private static OnCallScheduleWeekRequest AdjacentWeek(DateTime start) => new() { StartUtc = start, Slots = ManualSlots(start) };

    [TestMethod]
    public async Task SavesPreviousCurrentAndNextWeeksAtomicallyWithOneVersionChange()
    {
        for (var index = 0; index < 3; index++)
        {
            var seed = Request(Anchor.AddDays(index * 7));
            seed.Slots = ManualSlots(seed.StartUtc);
            Assert.AreEqual(0, (await service.Save(seed, false, default)).Count);
        }
        var originalIds = await db.OnCallShifts.OrderBy(shift => shift.StartsAtUtc).Select(shift => shift.Id).ToListAsync();
        var request = await EditRequest(Anchor.AddDays(7));
        var previous = await EditRequest();
        var next = await EditRequest(Anchor.AddDays(14));
        request.AdjacentWeeks =
        [
            new() { StartUtc = next.StartUtc, Slots = next.Slots },
            new() { StartUtc = previous.StartUtc, Slots = previous.Slots }
        ];
        foreach (var slot in request.Slots.Concat(request.AdjacentWeeks.SelectMany(week => week.Slots)))
            slot.UserIds = slot.UserIds.Select(id => id == "u0" ? "u1" : id == "u1" ? "u0" : id).ToList();
        var version = request.Version;

        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);

        Assert.AreNotEqual(version, group.ScheduleVersion);
        Assert.AreEqual(0, service.Warnings.Count);
        Assert.AreEqual(63, await db.OnCallShiftMembers.CountAsync());
        CollectionAssert.AreEqual(originalIds, await db.OnCallShifts.OrderBy(shift => shift.StartsAtUtc).Select(shift => shift.Id).ToListAsync());
        for (var index = 0; index < 3; index++)
        {
            var saved = await EditRequest(Anchor.AddDays(index * 7));
            CollectionAssert.AreEqual(new[] { "u1" }, saved.Slots[0].UserIds);
            CollectionAssert.AreEqual(new[] { "u0" }, saved.Slots[1].UserIds);
            Assert.AreEqual(group.ScheduleVersion, saved.Version);
        }
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.Save(request, false, default));
        Assert.AreEqual(63, await db.OnCallShiftMembers.CountAsync());
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(1)]
    public async Task CrossWeekRestFailureRollsBackEditsAndNewAdjacentWeeks(int direction)
    {
        var seed = Request(Anchor.AddDays(7));
        seed.Slots = ManualSlots(seed.StartUtc);
        Assert.AreEqual(0, (await service.Save(seed, false, default)).Count);
        var request = await EditRequest(seed.StartUtc);
        request.Slots[5].UserIds = ["u3"];
        var adjacent = AdjacentWeek(seed.StartUtc.AddDays(direction * 7));
        adjacent.Slots[direction < 0 ? 20 : 0].UserIds = [direction < 0 ? "u0" : "u2"];
        request.AdjacentWeeks = [adjacent];
        var version = group.ScheduleVersion;

        var errors = await service.Save(request, false, default);

        Assert.IsTrue(errors.Any(error => error.Contains("insufficient rest")));
        Assert.AreEqual(version, group.ScheduleVersion);
        Assert.AreEqual(0, service.Warnings.Count);
        Assert.AreEqual(21, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(21, await db.OnCallShiftMembers.CountAsync());
        CollectionAssert.AreEqual(new[] { "u2" }, (await EditRequest(seed.StartUtc)).Slots[5].UserIds);
        Assert.IsFalse(db.ChangeTracker.HasChanges());
    }

    [TestMethod]
    [DataRow("previous")]
    [DataRow("next")]
    [DataRow("other-team")]
    public async Task MultiweekSaveStillValidatesExternalAssignmentsOutsideEditedWeeks(string source)
    {
        var seed = Request(Anchor.AddDays(14));
        seed.Slots = ManualSlots(seed.StartUtc);
        Assert.AreEqual(0, (await service.Save(seed, false, default)).Count);
        var externalStart = source == "previous" ? Anchor.AddDays(7).AddHours(-8) :
            source == "next" ? Anchor.AddDays(28) : Anchor.AddDays(7);
        var externalGroup = source == "other-team" ? new OnCallGroup { Name = "Other", AnchorUtc = Anchor } : group;
        db.OnCallShifts.Add(new OnCallShift
        {
            Group = externalGroup, StartsAtUtc = externalStart, EndsAtUtc = externalStart.AddHours(8), RestHours = 16,
            Members = [new OnCallShiftMember { UserId = source == "next" ? "u2" : "u0" }]
        });
        await db.SaveChangesAsync();
        var request = await EditRequest(seed.StartUtc);
        request.Slots[5].UserIds = ["u3"];
        request.AdjacentWeeks = [AdjacentWeek(Anchor.AddDays(7)), AdjacentWeek(Anchor.AddDays(21))];
        var version = group.ScheduleVersion;

        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("insufficient rest")));

        Assert.AreEqual(version, group.ScheduleVersion);
        Assert.AreEqual(22, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(22, await db.OnCallShiftMembers.CountAsync());
        CollectionAssert.AreEqual(new[] { "u2" }, (await EditRequest(seed.StartUtc)).Slots[5].UserIds);
        Assert.IsFalse(db.ChangeTracker.HasChanges());
    }

    [TestMethod]
    [DataRow("partial")]
    [DataRow("rest")]
    [DataRow("minimum")]
    public async Task InvalidSavedAdjacentWeekCannotBeOverwrittenOrPartiallySaveMainWeek(string invalid)
    {
        var next = Request(Anchor.AddDays(7));
        next.Slots = ManualSlots(next.StartUtc);
        Assert.AreEqual(0, (await service.Save(next, false, default)).Count);
        var first = await db.OnCallShifts.OrderBy(shift => shift.StartsAtUtc).FirstAsync();
        switch (invalid)
        {
            case "partial": db.OnCallShifts.Remove(first); break;
            case "rest": first.RestHours = 0; break;
            case "minimum": first.MinimumStaff = 2; break;
        }
        await db.SaveChangesAsync();
        var count = await db.OnCallShifts.CountAsync();
        var request = Request();
        request.Slots = ManualSlots(request.StartUtc);
        request.AdjacentWeeks = [AdjacentWeek(next.StartUtc)];
        var version = group.ScheduleVersion;

        Assert.IsNotEmpty(await service.Save(request, false, default));

        Assert.AreEqual(version, group.ScheduleVersion);
        Assert.AreEqual(count, await db.OnCallShifts.CountAsync());
        Assert.IsFalse(await db.OnCallShifts.AnyAsync(shift => shift.StartsAtUtc < next.StartUtc));
        Assert.IsFalse(db.ChangeTracker.HasChanges());
    }

    [TestMethod]
    public async Task MultiweekSavePreservesEachSavedSnapshotAndUsesCurrentDefaultsForNewWeek()
    {
        rule.RestHours = 0;
        rule.MinimumStaff = 2;
        await db.SaveChangesAsync();
        var previous = Request();
        previous.Slots = ManualSlots(previous.StartUtc);
        Assert.AreEqual(0, (await service.Save(previous, false, default)).Count);
        rule.RestHours = 8;
        rule.MinimumStaff = 3;
        await db.SaveChangesAsync();
        var current = Request(Anchor.AddDays(7));
        current.Slots = ManualSlots(current.StartUtc);
        Assert.AreEqual(0, (await service.Save(current, false, default)).Count);
        rule.RestHours = 16;
        rule.MinimumStaff = 4;
        await db.SaveChangesAsync();
        var request = await EditRequest(current.StartUtc);
        request.AdjacentWeeks = [AdjacentWeek(Anchor), AdjacentWeek(Anchor.AddDays(14))];
        request.AdjacentWeeks[0].Slots[0].UserIds = ["u1"];

        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);

        Assert.AreEqual(63, service.Warnings.Count);
        for (var index = 0; index < 3; index++)
        {
            var start = Anchor.AddDays(index * 7);
            var end = start.AddDays(7);
            var slots = await db.OnCallShifts.Where(shift => shift.StartsAtUtc >= start && shift.StartsAtUtc < end).ToListAsync();
            Assert.AreEqual(21, slots.Count);
            Assert.IsTrue(slots.All(slot => slot.RestHours == index * 8 && slot.MinimumStaff == index + 2));
            Assert.IsTrue(service.Warnings.Skip(index * 21).Take(21).All(warning => warning.Contains($"minimum of {index + 2}")));
        }
    }

    [TestMethod]
    public async Task PeerWeekValidationUsesStricterHistoricalRestSnapshot()
    {
        rule.RestHours = 16;
        var seed = Request();
        seed.Slots = ManualSlots(seed.StartUtc);
        Assert.AreEqual(0, (await service.Save(seed, false, default)).Count);
        rule.RestHours = 0;
        await db.SaveChangesAsync();
        var request = Request(Anchor.AddDays(7));
        request.Slots = ManualSlots(request.StartUtc);
        request.Slots[0].UserIds = ["u2"];
        request.AdjacentWeeks = [AdjacentWeek(Anchor)];
        var version = group.ScheduleVersion;

        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("insufficient rest")));

        Assert.AreEqual(version, group.ScheduleVersion);
        Assert.AreEqual(21, await db.OnCallShifts.CountAsync());
        Assert.IsTrue(await db.OnCallShifts.AllAsync(shift => shift.RestHours == 16));
        Assert.IsFalse(db.ChangeTracker.HasChanges());
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("[null]")]
    [DataRow("[{},{},{}]")]
    [DataRow("[{}]")]
    [DataRow("[{\"StartUtc\":\"2026-10-05T00:00:00Z\"}]")]
    [DataRow("[{\"StartUtc\":\"2026-10-12T00:00:00Z\"},{\"StartUtc\":\"2026-10-12T00:00:00Z\"}]")]
    [DataRow("[{\"StartUtc\":\"2026-10-19T00:00:00Z\"}]")]
    [DataRow("[{\"StartUtc\":\"2026-10-12T08:00:00Z\"}]")]
    [DataRow("[{\"StartUtc\":\"2026-10-12T00:00:00Z\",\"Slots\":null}]")]
    [DataRow("[{\"StartUtc\":\"2026-10-12T00:00:00Z\",\"Slots\":[null]}]")]
    public async Task RejectsMalformedAdjacentRequestsWithoutAnyWrites(string adjacentJson)
    {
        var request = Request(Anchor.AddDays(7));
        request.Slots = ManualSlots(request.StartUtc);
        request.AdjacentWeeks = System.Text.Json.JsonSerializer.Deserialize<List<OnCallScheduleWeekRequest>>(adjacentJson)!;
        var version = group.ScheduleVersion;

        Assert.IsNotEmpty(await service.Save(request, false, default));

        Assert.AreEqual(version, group.ScheduleVersion);
        Assert.AreEqual(0, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(0, await db.OnCallShiftMembers.CountAsync());
        Assert.IsFalse(db.ChangeTracker.HasChanges());
    }

    [TestMethod]
    [DataRow("partial")]
    [DataRow("duplicate-slot")]
    [DataRow("null-slot")]
    [DataRow("null-users")]
    [DataRow("blank-user")]
    [DataRow("duplicate-user")]
    [DataRow("unknown-user")]
    [DataRow("local-slot")]
    [DataRow("local-week")]
    public async Task RejectsInvalidAdjacentSlotsWithoutWritingValidMainWeek(string invalid)
    {
        var request = Request();
        request.Slots = ManualSlots(request.StartUtc);
        var adjacent = AdjacentWeek(Anchor.AddDays(7));
        request.AdjacentWeeks = [adjacent];
        switch (invalid)
        {
            case "partial": adjacent.Slots.RemoveAt(0); break;
            case "duplicate-slot": adjacent.Slots[1].StartsAtUtc = adjacent.Slots[0].StartsAtUtc; break;
            case "null-slot": adjacent.Slots[0] = null!; break;
            case "null-users": adjacent.Slots[0].UserIds = null!; break;
            case "blank-user": adjacent.Slots[0].UserIds = [" "]; break;
            case "duplicate-user": adjacent.Slots[0].UserIds.Add("u0"); break;
            case "unknown-user": adjacent.Slots[0].UserIds = ["unknown"]; break;
            case "local-slot": adjacent.Slots[0].StartsAtUtc = DateTime.SpecifyKind(adjacent.StartUtc, DateTimeKind.Local); break;
            case "local-week": adjacent.StartUtc = DateTime.SpecifyKind(adjacent.StartUtc, DateTimeKind.Local); break;
        }
        var version = group.ScheduleVersion;

        Assert.IsNotEmpty(await service.Save(request, false, default));

        Assert.AreEqual(version, group.ScheduleVersion);
        Assert.AreEqual(0, await db.OnCallShifts.CountAsync());
        Assert.IsFalse(db.ChangeTracker.HasChanges());
    }

    [TestMethod]
    public async Task GenerationRejectsAdjacentEditsAndStaleManualRequestsCannotCreateWeeks()
    {
        var request = Request();
        request.Slots = ManualSlots(request.StartUtc);
        request.AdjacentWeeks = [AdjacentWeek(Anchor.AddDays(7))];
        var version = group.ScheduleVersion;
        Assert.IsTrue((await service.Save(request, true, default)).Any(error => error.Contains("Auto-generation")));
        Assert.AreEqual(version, group.ScheduleVersion);
        Assert.AreEqual(0, await db.OnCallShifts.CountAsync());
        request.Version = Guid.NewGuid();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.Save(request, false, default));
        Assert.AreEqual(version, group.ScheduleVersion);
        Assert.AreEqual(0, await db.OnCallShifts.CountAsync());
    }

    [TestMethod]
    public async Task MultiweekSaveOnlyRetainsDepartedMembersInTheirOwnOriginalSlots()
    {
        for (var index = 0; index < 2; index++)
            Assert.AreEqual(0, (await service.Save(Request(Anchor.AddDays(index * 7)), true, default)).Count);
        db.OnCallGroupMembers.Remove(group.Members.Single(member => member.UserId == "u3"));
        await db.SaveChangesAsync();
        var request = await EditRequest();
        var next = await EditRequest(Anchor.AddDays(7));
        request.AdjacentWeeks = [new() { StartUtc = next.StartUtc, Slots = next.Slots }];
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        request.Version = group.ScheduleVersion;
        var version = group.ScheduleVersion;
        var original = request.AdjacentWeeks[0].Slots.First(slot => slot.UserIds.Contains("u3"));
        var target = request.AdjacentWeeks[0].Slots.First(slot => !slot.UserIds.Contains("u3"));
        original.UserIds.Remove("u3");
        target.UserIds.Add("u3");

        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("left the team")));

        Assert.AreEqual(version, group.ScheduleVersion);
        Assert.AreEqual(56, await db.OnCallShiftMembers.CountAsync());
        Assert.IsFalse(db.ChangeTracker.HasChanges());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AutomaticGenerationExcludesSharedMembersEvenWhenOtherGroupHasNoSchedule(bool otherHasRule)
    {
        var other = new OnCallGroup
        {
            Name = "Other team", AnchorUtc = Anchor, Rule = otherHasRule ? rule : null,
            Members = [new OnCallGroupMember { UserId = "u0" }]
        };
        db.OnCallGroups.Add(other);
        await db.SaveChangesAsync();

        Assert.AreEqual(0, (await service.Save(Request(), true, default)).Count);

        var generated = await db.OnCallShifts.Where(shift => shift.GroupId == group.Id)
            .Include(shift => shift.Members).ToListAsync();
        Assert.AreEqual(21, generated.Count);
        Assert.IsTrue(generated.All(shift => shift.Members.Count == 1 && shift.Members.All(member => member.UserId != "u0")));
        CollectionAssert.AreEquivalent(new[] { "u1", "u2", "u3" },
            generated.SelectMany(shift => shift.Members).Select(member => member.UserId).Distinct().ToArray());
        Assert.IsFalse(await db.OnCallShifts.AnyAsync(shift => shift.GroupId == other.Id));

        var manual = Request();
        manual.Slots = ManualSlots(Anchor);
        Assert.AreEqual(0, (await service.Save(manual, false, default)).Count);
        Assert.AreEqual(7, await db.OnCallShiftMembers.CountAsync(member => member.Shift.GroupId == group.Id && member.UserId == "u0"));
    }

    [TestMethod]
    public async Task JoiningAnotherGroupPreservesSavedScheduleAndLeavingRestoresAutomaticEligibility()
    {
        Assert.AreEqual(0, (await service.Save(Request(), true, default)).Count);
        var original = await db.OnCallShifts.Where(shift => shift.GroupId == group.Id)
            .OrderBy(shift => shift.StartsAtUtc).Include(shift => shift.Members).ToListAsync();
        var originalIds = original.Select(shift => shift.Id).ToArray();
        var originalAssignments = original.Select(shift => string.Join(',', shift.Members.Select(member => member.UserId).Order())).ToArray();
        Assert.IsTrue(original.Any(shift => shift.Members.Any(member => member.UserId == "u3")));
        var other = new OnCallGroup
        {
            Name = "Manual team", AnchorUtc = Anchor,
            Members = [new OnCallGroupMember { UserId = "u3" }]
        };
        db.OnCallGroups.Add(other);
        await db.SaveChangesAsync();

        Assert.AreEqual(0, (await service.Save(await EditRequest(), false, default)).Count);
        Assert.AreEqual(0, (await service.Save(Request(Anchor.AddDays(7)), true, default)).Count);

        var saved = await db.OnCallShifts.Where(shift => shift.GroupId == group.Id && shift.StartsAtUtc < Anchor.AddDays(7))
            .OrderBy(shift => shift.StartsAtUtc).Include(shift => shift.Members).ToListAsync();
        CollectionAssert.AreEqual(originalIds, saved.Select(shift => shift.Id).ToArray());
        CollectionAssert.AreEqual(originalAssignments,
            saved.Select(shift => string.Join(',', shift.Members.Select(member => member.UserId).Order())).ToArray());
        Assert.IsFalse(await db.OnCallShiftMembers.AnyAsync(member => member.Shift.GroupId == group.Id &&
            member.Shift.StartsAtUtc >= Anchor.AddDays(7) && member.UserId == "u3"));

        db.OnCallGroupMembers.Remove(other.Members.Single());
        await db.SaveChangesAsync();
        Assert.AreEqual(0, (await service.Save(Request(Anchor.AddDays(14)), true, default)).Count);
        Assert.IsTrue(await db.OnCallShiftMembers.AnyAsync(member => member.Shift.GroupId == group.Id &&
            member.Shift.StartsAtUtc >= Anchor.AddDays(14) && member.UserId == "u3"));
    }

    [TestMethod]
    [DataRow(-8, "insufficient rest")]
    [DataRow(0, "overlaps")]
    public async Task SharedMembersRemainSubjectToCrossGroupManualRestAndOverlapValidation(int externalOffsetHours, string expectedError)
    {
        var other = new OnCallGroup
        {
            Name = "Other team", AnchorUtc = Anchor,
            Members = [new OnCallGroupMember { UserId = "u0" }]
        };
        db.OnCallGroups.Add(other);
        await db.SaveChangesAsync();
        var request = Request();
        request.Slots = ManualSlots(Anchor);
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        var external = new OnCallShift
        {
            Group = other, StartsAtUtc = Anchor.AddHours(externalOffsetHours), EndsAtUtc = Anchor.AddHours(externalOffsetHours + 8),
            RestHours = 16, Members = [new OnCallShiftMember { UserId = "u0" }]
        };
        db.OnCallShifts.Add(external);
        await db.SaveChangesAsync();
        request = await EditRequest();
        request.Slots[5].UserIds = ["u3"];
        var version = group.ScheduleVersion;

        var errors = await service.Save(request, false, default);

        Assert.IsTrue(errors.Any(error => error.Contains(expectedError)));
        Assert.AreEqual(version, group.ScheduleVersion);
        Assert.AreEqual(22, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(22, await db.OnCallShiftMembers.CountAsync());
        CollectionAssert.AreEqual(new[] { "u2" }, (await EditRequest()).Slots[5].UserIds);
        Assert.AreEqual("u0", (await db.OnCallShifts.Include(shift => shift.Members).SingleAsync(shift => shift.Id == external.Id)).Members.Single().UserId);
        Assert.IsFalse(db.ChangeTracker.HasChanges());
    }

    [TestMethod]
    public async Task SavesMultipleStaffAndManualChangesWithoutRegenerationOverwrite()
    {
        var originalVersion = group.ScheduleVersion;
        Assert.AreEqual(0, (await service.Save(Request(), true, default)).Count);
        Assert.AreEqual(21, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(28, await db.OnCallShiftMembers.CountAsync());
        Assert.AreNotEqual(originalVersion, group.ScheduleVersion);
        var request = await EditRequest();
        request.Slots[0].UserIds.Remove("u3");
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        db.ChangeTracker.Clear();
        group = await db.OnCallGroups.Include(item => item.Rule).Include(item => item.Members).SingleAsync();
        Assert.AreEqual(27, await db.OnCallShiftMembers.CountAsync());
        Assert.IsTrue((await service.Save(Request(), true, default)).Any(error => error.Contains("will not overwrite")));
        Assert.AreEqual(27, await db.OnCallShiftMembers.CountAsync());
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.Save(request, false, default));
    }

    [TestMethod]
    public async Task RejectsEmptyDuplicateAndRestViolationsAtomically()
    {
        await service.Save(Request(), true, default);
        var request = await EditRequest();
        var version = group.ScheduleVersion;
        request.Slots[0].UserIds.Clear();
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("Empty shift")));
        request = await EditRequest();
        request.Slots[1].UserIds.Add("u0");
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("insufficient rest")));
        request = await EditRequest();
        request.Slots[0].UserIds.Add("u0");
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("overlaps")));
        Assert.AreEqual(version, group.ScheduleVersion);
        Assert.AreEqual(28, await db.OnCallShiftMembers.CountAsync());
    }

    [TestMethod]
    [DataRow("\"Slots\":null")]
    [DataRow("\"Slots\":[null,{0}]")]
    [DataRow("\"Slots\":[{{\"StartsAtUtc\":\"2026-09-28T00:00:00Z\",\"UserIds\":null}},{0}]")]
    public async Task RejectsNullSlotPayloadsWithoutChangingSavedSchedule(string slotsJson)
    {
        await service.Save(Request(), true, default);
        var version = group.ScheduleVersion;
        var remainingSlots = string.Join(',', Enumerable.Range(1, 20).Select(index =>
            System.Text.Json.JsonSerializer.Serialize(new OnCallSlotRequest
            {
                StartsAtUtc = Anchor.AddHours(index * 8), UserIds = [$"u{index % 3}"]
            })));
        var payload = $"{{\"GroupId\":\"{group.Id}\",\"Version\":\"{version}\",\"StartUtc\":\"{Anchor:O}\"," +
            string.Format(slotsJson, remainingSlots) + "}";
        var request = System.Text.Json.JsonSerializer.Deserialize<SaveOnCallScheduleRequest>(payload);
        Assert.IsNotNull(request);

        var errors = await service.Save(request, false, default);

        Assert.IsTrue(errors.Any(error => error.Contains("All 21 shifts for this week must be submitted")));
        Assert.AreEqual(version, group.ScheduleVersion);
        Assert.AreEqual(21, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(28, await db.OnCallShiftMembers.CountAsync());
    }

    [TestMethod]
    public async Task ChecksBothAdjacentWeeksAndOtherTeams()
    {
        await service.Save(Request(), true, default);
        Assert.AreEqual(0, (await service.Save(Request(Anchor.AddDays(7)), true, default)).Count);
        var request = await EditRequest();
        request.Slots[^1].UserIds.Add("u0");
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("insufficient rest")));
        request = await EditRequest(Anchor.AddDays(7));
        request.Slots[0].UserIds.Add("u2");
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("insufficient rest")));

        var other = new OnCallGroup { Rule = rule, Name = "Other", AnchorUtc = Anchor };
        db.OnCallGroups.Add(other);
        db.OnCallShifts.Add(new OnCallShift
        {
            Group = other, StartsAtUtc = Anchor, EndsAtUtc = Anchor.AddHours(8),
            Members = [new OnCallShiftMember { UserId = "u0" }]
        });
        await db.SaveChangesAsync();
        Assert.IsTrue((await service.Save(await EditRequest(), false, default)).Any(error => error.Contains("overlaps")));
    }

    [TestMethod]
    public async Task CanCreateCompleteScheduleManuallyWithoutAutomaticGeneration()
    {
        var request = Request();
        request.Slots = Enumerable.Range(0, 21).Select(index => new OnCallSlotRequest
        {
            StartsAtUtc = Anchor.AddHours(index * 8), UserIds = [$"u{index % 3}"]
        }).ToList();
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        Assert.AreEqual(21, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(21, await db.OnCallShiftMembers.CountAsync());
    }

    [TestMethod]
    public async Task BelowMinimumManualScheduleSavesWarningsAndKeepsSnapshotAfterTemplateChanges()
    {
        rule.MinimumStaff = 2;
        rule.TargetStaff = 2;
        await db.SaveChangesAsync();
        var version = group.ScheduleVersion;
        Assert.IsTrue((await service.Save(Request(), true, default)).Any(error => error.Contains("at least 6 are required")));
        Assert.AreEqual(0, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(version, group.ScheduleVersion);

        var request = Request();
        request.Slots = Enumerable.Range(0, 21).Select(index => new OnCallSlotRequest
        {
            StartsAtUtc = Anchor.AddHours(index * 8), UserIds = [$"u{index % 3}"]
        }).ToList();
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        Assert.AreEqual(21, service.Warnings.Count);
        Assert.IsTrue(service.Warnings.All(warning => !string.IsNullOrWhiteSpace(warning)));
        Assert.AreNotEqual(version, group.ScheduleVersion);
        Assert.AreEqual(21, await db.OnCallShiftMembers.CountAsync());
        Assert.IsTrue(await db.OnCallShifts.AllAsync(shift => shift.MinimumStaff == 2));

        rule.MinimumStaff = 1;
        rule.TargetStaff = 1;
        await db.SaveChangesAsync();
        Assert.AreEqual(0, (await service.Save(await EditRequest(), false, default)).Count);
        Assert.AreEqual(21, service.Warnings.Count);
        var replacement = new OnCallRule { Name = "Replacement", MinimumStaff = 3, TargetStaff = 3 };
        db.OnCallRules.Add(replacement);
        await db.SaveChangesAsync();
        group.Rule = replacement;
        group.RuleId = replacement.Id;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        group = await db.OnCallGroups.Include(item => item.Rule).Include(item => item.Members).SingleAsync();
        Assert.AreEqual(0, (await service.Save(await EditRequest(), false, default)).Count);
        Assert.AreEqual(21, service.Warnings.Count);
        Assert.IsTrue(await db.OnCallShifts.AllAsync(shift => shift.MinimumStaff == 2));

        request = await EditRequest();
        version = group.ScheduleVersion;
        request.Slots[0].UserIds.Clear();
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("Empty shift")));
        Assert.AreEqual(0, service.Warnings.Count);
        Assert.AreEqual(version, group.ScheduleVersion);
        Assert.AreEqual(21, await db.OnCallShiftMembers.CountAsync());
    }

    [TestMethod]
    public async Task ManualScheduleWithoutTemplateUsesDefaultRestAndDoesNotRequireThreeMembers()
    {
        group.Rule = null;
        group.RuleId = null;
        await db.SaveChangesAsync();
        var request = Request();
        request.Slots = Enumerable.Range(0, 21).Select(index => new OnCallSlotRequest
        {
            StartsAtUtc = Anchor.AddHours(index * 8), UserIds = [$"u{index % 3}"]
        }).ToList();
        Assert.IsTrue((await service.Save(Request(), true, default)).Any(error => error.Contains("bind a scheduling rule")));
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        Assert.IsTrue(await db.OnCallShifts.AllAsync(shift => shift.RestHours == 16));

        db.OnCallGroupMembers.RemoveRange(group.Members.Where(member => member.UserId != "u0"));
        await db.SaveChangesAsync();
        Assert.AreEqual(1, await db.OnCallGroupMembers.CountAsync());
        Assert.AreEqual(0, (await service.Save(await EditRequest(), false, default)).Count);
        Assert.AreEqual(21, await db.OnCallShiftMembers.CountAsync());
    }

    [TestMethod]
    public async Task ManualCreationBelowGenerationMinimumUsesTemplateRestRatherThanMinimumStaff()
    {
        rule.RestHours = 0;
        db.OnCallGroupMembers.RemoveRange(group.Members.Where(member => member.UserId != "u0"));
        await db.SaveChangesAsync();
        Assert.IsTrue((await service.Save(Request(), true, default)).Any(error => error.Contains("at least 3 are required")));
        var request = Request();
        request.Slots = Enumerable.Range(0, 21).Select(index => new OnCallSlotRequest
        {
            StartsAtUtc = Anchor.AddHours(index * 8), UserIds = ["u0"]
        }).ToList();
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        Assert.AreEqual(21, await db.OnCallShiftMembers.CountAsync());
        Assert.IsTrue(await db.OnCallShifts.AllAsync(shift => shift.RestHours == 0));
    }

    [TestMethod]
    public async Task ManualCreationWithoutTemplateStillRequiresDefaultSixteenHoursRest()
    {
        group.Rule = null;
        group.RuleId = null;
        db.OnCallGroupMembers.RemoveRange(group.Members.Where(member => member.UserId != "u0"));
        await db.SaveChangesAsync();
        var request = Request();
        request.Slots = Enumerable.Range(0, 21).Select(index => new OnCallSlotRequest
        {
            StartsAtUtc = Anchor.AddHours(index * 8), UserIds = ["u0"]
        }).ToList();
        var version = group.ScheduleVersion;
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("insufficient rest")));
        Assert.AreEqual(0, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(version, group.ScheduleVersion);
    }

    [TestMethod]
    public async Task DepartedStaffCanOnlyBeRetainedInTheirOriginalSavedSlots()
    {
        Assert.AreEqual(0, (await service.Save(Request(), true, default)).Count);
        db.OnCallGroupMembers.Remove(group.Members.Single(member => member.UserId == "u3"));
        await db.SaveChangesAsync();
        Assert.AreEqual(0, (await service.Save(await EditRequest(), false, default)).Count);
        var request = await EditRequest();
        request.Slots[1].UserIds.Add("u3");
        var version = group.ScheduleVersion;
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("left the team")));
        request = await EditRequest();
        request.Slots[0].UserIds.Remove("u3");
        request.Slots[1].UserIds.Add("u3");
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("left the team")));
        Assert.AreEqual(version, group.ScheduleVersion);
        Assert.AreEqual(28, await db.OnCallShiftMembers.CountAsync());
        request = await EditRequest();
        request.Slots[0].UserIds.Remove("u3");
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        request = await EditRequest();
        request.Slots[0].UserIds.Add("u3");
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("left the team")));
        Assert.AreEqual(27, await db.OnCallShiftMembers.CountAsync());
    }

    [TestMethod]
    public async Task EditingSavedScheduleUsesRestSnapshotInsteadOfChangedTemplate()
    {
        await service.Save(Request(), true, default);
        rule.RestHours = 168;
        rule.BalanceShiftTimes = true;
        await db.SaveChangesAsync();
        var request = await EditRequest();
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        Assert.IsTrue(await db.OnCallShifts.AllAsync(shift => shift.RestHours == 16));
        Assert.AreEqual(28, await db.OnCallShiftMembers.CountAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CrossTeamRestUsesHistoricalSnapshotEvenIfTemplateIsUnboundOrEdited(bool unbind)
    {
        rule.RestHours = 0;
        var otherRule = new OnCallRule { Name = "Old policy", RestHours = 48 };
        var other = new OnCallGroup { Name = "Other", Rule = otherRule, AnchorUtc = Anchor };
        db.OnCallShifts.Add(new OnCallShift
        {
            Group = other, StartsAtUtc = Anchor.AddHours(-32), EndsAtUtc = Anchor.AddHours(-24), RestHours = 48,
            Members = [new OnCallShiftMember { UserId = "u0" }]
        });
        await db.SaveChangesAsync();
        if (unbind)
        {
            other.Rule = null;
            other.RuleId = null;
            await db.SaveChangesAsync();
            db.OnCallRules.Remove(otherRule);
        }
        else otherRule.RestHours = 0;
        await db.SaveChangesAsync();
        var request = Request();
        request.Slots = Enumerable.Range(0, 21).Select(index => new OnCallSlotRequest
        {
            StartsAtUtc = Anchor.AddHours(index * 8), UserIds = [$"u{index % 3}"]
        }).ToList();
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("insufficient rest")));
        Assert.AreEqual(1, await db.OnCallShifts.CountAsync());
        var existing = await service.Existing(group.Id, ["u0"], Anchor, default);
        Assert.AreEqual(48, existing.Single().RestHours);
        request.Slots[0].UserIds = ["u3"];
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        Assert.AreEqual(22, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(48, (await db.OnCallShifts.SingleAsync(shift => shift.GroupId == other.Id)).RestHours);
    }

    [TestMethod]
    public void WeekStartUsesTeamAnchorAndRejectsNonWeeklyOrLocalBoundaries()
    {
        Assert.AreEqual(Anchor.AddDays(7), OnCallScheduleService.WeekStart(group, Anchor.AddDays(7)));
        Assert.Throws<ArgumentException>(() => OnCallScheduleService.WeekStart(group, Anchor.AddHours(8)));
        Assert.Throws<ArgumentException>(() => OnCallScheduleService.WeekStart(group, Anchor.AddDays(-7)));
        Assert.Throws<ArgumentException>(() => OnCallScheduleService.WeekStart(group, DateTime.SpecifyKind(Anchor, DateTimeKind.Local)));
        var other = new OnCallGroup { AnchorUtc = Anchor.AddHours(4), Rule = rule };
        Assert.AreEqual(Anchor.AddHours(4), OnCallScheduleService.WeekStart(other, Anchor.AddHours(4)));
    }

    [TestMethod]
    public async Task CanSwapMultipleShiftsInOneSaveWithoutEnforcingBalancePreferences()
    {
        await service.Save(Request(), true, default);
        var request = await EditRequest();
        foreach (var slot in request.Slots)
            slot.UserIds = slot.UserIds.Select(id => id == "u0" ? "u1" : id == "u1" ? "u0" : id).ToList();
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        var saved = await EditRequest();
        Assert.IsTrue(saved.Slots[0].UserIds.Contains("u1"));
        Assert.IsFalse(saved.Slots[0].UserIds.Contains("u0"));
    }
}

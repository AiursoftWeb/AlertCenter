using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallScheduleClearingTests
{
    private SqliteContext db = null!;
    private OnCallGroup group = null!;
    private OnCallScheduleService service = null!;
    private DateTime now;

    [TestInitialize]
    public async Task Setup()
    {
        db = new(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        now = DateTime.UtcNow;
        db.Users.AddRange(new[] { "a", "b", "c" }.Select(id => new User { Id = id, UserName = id, DisplayName = id }));
        group = new() { Name = "Clear team", TimeZoneId = "Asia/Shanghai", AnchorUtc = now.AddDays(-7),
            Rule = new() { Name = "Hourly", ShiftMinutes = 60 },
            Members = new[] { "a", "b", "c" }.Select((id, index) => new OnCallGroupMember { UserId = id, RotationOrder = index }).ToList() };
        db.OnCallGroups.Add(group);
        await db.SaveChangesAsync();
        service = new(db, new OnCallScheduleGenerator(TestLocalization.For<OnCallScheduleGenerator>()),
            TestLocalization.For<OnCallScheduleService>(), TestLocalization.Factory);
    }

    [TestCleanup]
    public async Task Cleanup() => await db.DisposeAsync();

    private OnCallShift Shift(int startHours, int endHours, OnCallShiftSource source = OnCallShiftSource.Generated, string user = "a")
    {
        var shift = new OnCallShift { GroupId = group.Id, StartsAtUtc = now.AddHours(startHours), EndsAtUtc = now.AddHours(endHours),
            Source = source, Members = [new() { UserId = user }] };
        db.OnCallShifts.Add(shift);
        return shift;
    }

    private async Task<SaveOnCallScheduleRequest> Request(int start = 2, int end = 4, bool includeManual = false,
        bool includeHistory = false, string? action = null)
    {
        await db.SaveChangesAsync();
        return new() { GroupId = group.Id, Version = group.ScheduleVersion, StartUtc = now.AddDays(-4), EndUtc = now.AddDays(5),
            Slots = (await db.OnCallShifts.Include(shift => shift.Members).ToListAsync()).Select(OnCallScheduleClearing.Slot).ToList(),
            ClearRanges = [new() { StartUtc = now.AddHours(start), EndUtc = now.AddHours(end), IncludeManual = includeManual,
                IncludeHistory = includeHistory, AutoExtendAction = action }] };
    }

    private async Task ClearPreview(SaveOnCallScheduleRequest request)
    {
        var errors = await service.Save(request, false, default, preview: true, clear: true);
        Assert.AreEqual(0, errors.Count, string.Join("; ", errors));
        request.Slots = service.PreviewSlots;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RangeClearingPreservesOutsideFragmentsAndManualChoiceUntilSaved(bool includeManual)
    {
        var generated = Shift(1, 5);
        var manual = Shift(2, 4, OnCallShiftSource.Manual, "b");
        var request = await Request(includeManual: includeManual);
        var version = group.ScheduleVersion;
        await ClearPreview(request);
        Assert.AreEqual(2, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(version, group.ScheduleVersion);
        Assert.AreEqual(includeManual ? 2 : 3, request.Slots.Count);
        var fragments = request.Slots.Where(slot => slot.UserIds.Contains("a")).OrderBy(slot => slot.StartsAtUtc).ToArray();
        Assert.AreEqual(generated.Id, fragments[0].Id);
        Assert.IsNull(fragments[1].Id);
        Assert.AreEqual(now.AddHours(2), fragments[0].EndsAtUtc);
        Assert.AreEqual(now.AddHours(4), fragments[1].StartsAtUtc);
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        db.ChangeTracker.Clear();
        Assert.AreEqual(includeManual ? 2 : 3, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(!includeManual, await db.OnCallShifts.AnyAsync(shift => shift.Id == manual.Id));
        Assert.IsTrue(await db.OnCallShifts.Where(shift => shift.Id != manual.Id).AllAsync(shift => shift.Source == OnCallShiftSource.Generated));
    }

    [TestMethod]
    public async Task HistoricalShiftsRequireSeparateConfirmationAndOngoingAlwaysKeepsElapsedPart()
    {
        var historical = Shift(-6, -4);
        var ongoing = Shift(-2, 2);
        var request = await Request(-8);
        await ClearPreview(request);
        Assert.IsTrue(request.Slots.Any(slot => slot.Id == historical.Id));
        var cut = request.Slots.Single(slot => slot.Id == ongoing.Id);
        Assert.AreEqual(ongoing.StartsAtUtc, cut.StartsAtUtc);
        Assert.AreEqual(request.ClearRanges[0].ClearedAtUtc, cut.EndsAtUtc);
        Assert.IsTrue(cut.EndsAtUtc >= now && cut.EndsAtUtc <= DateTime.UtcNow);
        request = await Request(-8, includeHistory: true);
        Assert.IsTrue((await service.Save(request, false, default, true, true)).Count > 0);
        Assert.AreEqual(2, await db.OnCallShifts.CountAsync());
        request.ClearRanges[0].ConfirmHistory = true;
        await ClearPreview(request);
        Assert.AreEqual(ongoing.Id, request.Slots.Single().Id);
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        Assert.AreEqual(1, await db.OnCallShifts.CountAsync());
    }

    [TestMethod]
    public async Task EditedGeneratedDraftIsManualAndCannotBeClearedByGeneratedOnly()
    {
        Shift(2, 4);
        var request = await Request();
        request.Slots[0].UserIds = ["b"];
        request.Slots[0].Source = OnCallShiftSource.Generated;
        await ClearPreview(request);
        Assert.AreEqual(OnCallShiftSource.Manual, request.Slots.Single().Source);
        Assert.AreEqual(0, service.ReplacedSlots.Count);
    }

    [TestMethod]
    public async Task UnsavedGeneratedDraftsCanBeClearedButCannotClaimPersistedGeneratedSource()
    {
        var request = await Request();
        request.Slots.Add(new() { StartsAtUtc = now.AddHours(2), EndsAtUtc = now.AddHours(4), Source = OnCallShiftSource.Generated, UserIds = ["a"] });
        await ClearPreview(request);
        Assert.AreEqual(0, request.Slots.Count);
        Assert.AreEqual(0, await db.OnCallShifts.CountAsync());
        request.Slots.Add(new() { StartsAtUtc = now.AddHours(2), EndsAtUtc = now.AddHours(4), Source = OnCallShiftSource.Generated, UserIds = ["a"] });
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        Assert.AreEqual(OnCallShiftSource.Manual, (await db.OnCallShifts.SingleAsync()).Source);
    }

    [TestMethod]
    public async Task RepeatedClearsPreserveGeneratedFragmentsAndDepartedAssignments()
    {
        Shift(0, 8);
        await db.SaveChangesAsync();
        var departed = group.Members.Single(member => member.UserId == "a");
        db.OnCallGroupMembers.Remove(departed);
        group.Members.Remove(departed);
        var request = await Request();
        await ClearPreview(request);
        request.ClearRanges.Add(new() { StartUtc = now.AddHours(5), EndUtc = now.AddHours(6) });
        await ClearPreview(request);
        Assert.AreEqual(3, request.Slots.Count);
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        Assert.AreEqual(3, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(3, await db.OnCallShiftMembers.CountAsync());
    }

    [TestMethod]
    public async Task AutoExtensionChoiceAndVersionAreRequiredAndDisablingIsAtomic()
    {
        group.AutoExtend = true;
        Shift(2, 4);
        var request = await Request();
        Assert.IsTrue((await service.Save(request, false, default, true, true)).Count > 0);
        request.ClearRanges[0].AutoExtendAction = "disable";
        await ClearPreview(request);
        Assert.IsTrue(group.AutoExtend);
        var correctVersion = request.Version;
        request.Version = Guid.NewGuid();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.Save(request, false, default));
        Assert.IsTrue(group.AutoExtend);
        Assert.AreEqual(1, await db.OnCallShifts.CountAsync());
        request.Version = correctVersion;
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        db.ChangeTracker.Clear();
        Assert.IsFalse((await db.OnCallGroups.SingleAsync()).AutoExtend);
        Assert.AreEqual(0, await db.OnCallShifts.CountAsync());
    }

    [TestMethod]
    public async Task RegenerationFillsOnlyClearedGapsAndContinuesRotationWithoutOverwritingManualShifts()
    {
        group.AutoExtend = true;
        var first = Shift(0, 2);
        var cleared = Shift(2, 5, OnCallShiftSource.Generated, "b");
        var manual = Shift(3, 4, OnCallShiftSource.Manual, "c");
        var last = Shift(5, 8);
        var request = await Request(2, 5, action: "regenerate");
        await ClearPreview(request);
        Assert.AreEqual(3, request.Slots.Count);
        Assert.IsFalse(request.Slots.Any(slot => slot.Id == cleared.Id));
        Assert.AreEqual(4, await db.OnCallShifts.CountAsync());
        Assert.IsTrue((await service.Save(request, false, default)).Count > 0);
        request.ConfirmReplace = true;
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        db.ChangeTracker.Clear();
        var shifts = await db.OnCallShifts.Include(shift => shift.Members).OrderBy(shift => shift.StartsAtUtc).ToListAsync();
        Assert.AreEqual(5, shifts.Count);
        Assert.AreEqual(first.Id, shifts[0].Id);
        Assert.AreEqual("b", shifts[1].Members.Single().UserId);
        Assert.AreEqual(now.AddHours(2), shifts[1].StartsAtUtc);
        Assert.AreEqual(now.AddHours(3), shifts[1].EndsAtUtc);
        Assert.AreEqual(OnCallShiftSource.Generated, shifts[1].Source);
        Assert.AreEqual(manual.Id, shifts[2].Id);
        Assert.AreEqual(OnCallShiftSource.Manual, shifts[2].Source);
        Assert.AreEqual("a", shifts[3].Members.Single().UserId);
        Assert.AreEqual(last.Id, shifts[4].Id);
        Assert.IsTrue((await db.OnCallGroups.SingleAsync()).AutoExtend);
    }

    [TestMethod]
    public async Task RegenerationClipsPartialGapsAtBothRetainedBoundaries()
    {
        group.AutoExtend = true;
        var first = Shift(0, 2);
        Shift(2, 5, user: "b");
        var last = Shift(5, 8);
        var request = await Request(2, 5, action: "regenerate");
        request.ClearRanges[0].StartUtc = now.AddHours(2).AddMinutes(17);
        request.ClearRanges[0].EndUtc = now.AddHours(4).AddMinutes(43);
        await ClearPreview(request);
        request.ConfirmReplace = true;
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        var shifts = await db.OnCallShifts.Include(shift => shift.Members).OrderBy(shift => shift.StartsAtUtc).ToListAsync();
        Assert.AreEqual(first.Id, shifts[0].Id);
        Assert.AreEqual(last.Id, shifts[^1].Id);
        Assert.AreEqual(0, OnCallScheduleValidator.OverlapErrors(shifts, []).Count);
        Assert.IsTrue(shifts.Zip(shifts.Skip(1)).All(pair => pair.First.EndsAtUtc == pair.Second.StartsAtUtc));
    }

    [TestMethod]
    [DataRow("disable")]
    [DataRow("regenerate")]
    public async Task ClearAndRegenerateCannotSaveUnresolvedLegacyOverlaps(string action)
    {
        group.AutoExtend = true;
        var first = Shift(1, 5, OnCallShiftSource.Manual);
        Shift(2, 3, OnCallShiftSource.Manual);
        Shift(6, 8, user: "b");
        var request = await Request(6, 8, action: action);
        var version = group.ScheduleVersion;
        await ClearPreview(request);
        Assert.IsTrue(service.Warnings.Any(warning => warning.Contains("Cannot save overlapping")));
        request.ConfirmReplace = true;
        Assert.IsTrue((await service.Save(request, false, default)).Any(error => error.Contains("Cannot save overlapping")));
        Assert.AreEqual(version, group.ScheduleVersion);
        Assert.IsTrue(group.AutoExtend);
        Assert.AreEqual(3, await db.OnCallShifts.CountAsync());
        Assert.AreEqual(now.AddHours(5), (await db.OnCallShifts.SingleAsync(shift => shift.Id == first.Id)).EndsAtUtc);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MaintenanceResumesAtNowOrLastRetainedFutureEnd(bool futureRemains)
    {
        group.AutoExtend = true;
        var last = Shift(futureRemains ? 4 : -4, futureRemains ? 8 : -2, user: "b");
        await db.SaveChangesAsync();
        await OnCallMaintenanceWorker.MaintainGroupAsync(db, TestLocalization.For<OnCallScheduleGenerator>(),
            TestLocalization.For<OnCallMaintenanceWorker>(), "https://example.com", group.Id, now, default);
        var next = await db.OnCallShifts.Include(shift => shift.Members).Where(shift => shift.Id != last.Id)
            .OrderBy(shift => shift.StartsAtUtc).FirstAsync();
        Assert.AreEqual(futureRemains ? last.EndsAtUtc : now, next.StartsAtUtc);
        Assert.AreEqual("c", next.Members.Single().UserId);
    }

    [TestMethod]
    public async Task ClearingTailAndReenablingMaintenanceDoesNotBackfillHistory()
    {
        group.AutoExtend = true;
        var previous = Shift(-4, -2, user: "b");
        Shift(2, 8, user: "c");
        var request = await Request(0, 8, action: "disable");
        await ClearPreview(request);
        Assert.AreEqual(0, (await service.Save(request, false, default)).Count);
        Assert.AreEqual(previous.Id, (await db.OnCallShifts.SingleAsync()).Id);
        group.AutoExtend = true;
        await db.SaveChangesAsync();
        await OnCallMaintenanceWorker.MaintainGroupAsync(db, TestLocalization.For<OnCallScheduleGenerator>(),
            TestLocalization.For<OnCallMaintenanceWorker>(), "https://example.com", group.Id, now, default);
        var next = await db.OnCallShifts.Include(shift => shift.Members).Where(shift => shift.Id != previous.Id)
            .OrderBy(shift => shift.StartsAtUtc).FirstAsync();
        Assert.AreEqual(now, next.StartsAtUtc);
        Assert.AreEqual("c", next.Members.Single().UserId);
    }

    [TestMethod]
    public async Task InvalidActionOrRangeNeverMutatesSchedule()
    {
        Shift(2, 4);
        var request = await Request();
        request.ClearRanges[0].AutoExtendAction = "other";
        Assert.IsTrue((await service.Save(request, false, default, true, true)).Count > 0);
        request.ClearRanges[0].AutoExtendAction = null;
        request.ClearRanges[0].EndUtc = request.ClearRanges[0].StartUtc;
        Assert.IsTrue((await service.Save(request, false, default, true, true)).Count > 0);
        Assert.AreEqual(1, await db.OnCallShifts.CountAsync());
    }
}

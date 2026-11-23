using System.Net;
using System.Text.Json;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class OnCallScheduleResetTests : TestBase
{
    public TestContext TestContext { get; set; } = null!;

    private sealed class ResetClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<(Guid Id, Guid Version)> SeedGroup(bool withRule = true, bool withCoverageGap = false)
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var today = DateTime.UtcNow.Date;
        var users = Enumerable.Range(0, 4).Select(i => new User { UserName = $"reset-staff-{i}", DisplayName = $"Reset Staff {i}" }).ToArray();
        db.Users.AddRange(users);
        var group = new OnCallGroup
        {
            Name = "Reset team", AnchorUtc = today, TimeZoneId = "Asia/Shanghai",
            Rule = withRule ? new OnCallRule { Name = "Reset rule", ShiftMinutes = 480 } : null,
            AutoExtend = true, AutoExtendedThroughUtc = today.AddDays(60), HandoverCheckedThroughUtc = today.AddDays(-2),
            Members = users.Select((user, i) => new OnCallGroupMember { UserId = user.Id, RotationOrder = i }).ToList()
        };
        db.OnCallGroups.Add(group);
        var shifts = new[]
        {
            Shift(today.AddDays(-4), today.AddDays(-3), OnCallShiftSource.Generated),
            Shift(today.AddDays(-1), withCoverageGap ? today : today.AddDays(1), OnCallShiftSource.Generated),
            Shift(today.AddDays(2), today.AddDays(2).AddHours(8), OnCallShiftSource.Generated),
            Shift(today.AddDays(3), today.AddDays(3).AddHours(8), OnCallShiftSource.Manual)
        };
        db.OnCallShifts.AddRange(shifts);
        db.Set<OnCallHandoverReceipt>().AddRange(shifts.Select(shift => new OnCallHandoverReceipt
        {
            ShiftId = shift.Id, UserId = users[0].Id, ProcessedAtUtc = today
        }));
        await db.SaveChangesAsync();
        return (group.Id, group.ScheduleVersion);

        OnCallShift Shift(DateTime start, DateTime end, OnCallShiftSource source) => new()
        {
            GroupId = group.Id, StartsAtUtc = start, EndsAtUtc = end, Source = source,
            RotationPosition = source == OnCallShiftSource.Generated ? 1 : null,
            Members = [new() { UserId = users[0].Id }]
        };
    }

    private Task<HttpResponseMessage> Reset(Guid id, Guid version, bool preview, bool regenerate = false,
        bool includeManual = false, bool includeToken = true, string? planId = null, string? startUtc = null)
    {
        var form = new Dictionary<string, string>
        {
            ["id"] = id.ToString(), ["version"] = version.ToString(),
            ["preview"] = preview.ToString(), ["regenerate"] = regenerate.ToString(),
            ["includeManual"] = includeManual.ToString(), ["planId"] = planId ?? ""
        };
        if (startUtc != null) form["startUtc"] = startUtc;
        return PostForm("/OnCall/ResetSchedule", form, $"/OnCall/GroupDetails/{id}", includeToken);
    }

    private sealed record State(Guid Version, bool AutoExtend, DateTime? ExtensionCursor, DateTime? HandoverCursor,
        ShiftSnapshot[] Shifts, Guid[] Receipts);

    private async Task<State> ReadState(Guid id)
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var group = await db.OnCallGroups.SingleAsync(group => group.Id == id);
        var shifts = await db.OnCallShifts.Include(shift => shift.Members).Where(shift => shift.GroupId == id)
            .OrderBy(shift => shift.StartsAtUtc).ThenBy(shift => shift.Id).ToListAsync();
        var receipts = await db.Set<OnCallHandoverReceipt>().OrderBy(receipt => receipt.ShiftId).Select(receipt => receipt.ShiftId).ToArrayAsync();
        return new(group.ScheduleVersion, group.AutoExtend, group.AutoExtendedThroughUtc, group.HandoverCheckedThroughUtc,
            shifts.Select(shift => new ShiftSnapshot(shift.Id, shift.StartsAtUtc, shift.EndsAtUtc, shift.Source, shift.RotationPosition,
                string.Join(',', shift.Members.Select(member => member.UserId).Order()))).ToArray(), receipts);
    }

    private sealed record ShiftSnapshot(Guid Id, DateTime Start, DateTime End, OnCallShiftSource Source,
        long? RotationPosition, string Users);

    private async Task AssertUnchanged(Guid id, State before)
    {
        var after = await ReadState(id);
        Assert.AreEqual(before.Version, after.Version);
        Assert.AreEqual(before.AutoExtend, after.AutoExtend);
        Assert.AreEqual(before.ExtensionCursor, after.ExtensionCursor);
        Assert.AreEqual(before.HandoverCursor, after.HandoverCursor);
        CollectionAssert.AreEqual(before.Shifts, after.Shifts);
        CollectionAssert.AreEqual(before.Receipts, after.Receipts);
    }

    private static void AssertRetainedReceipts(State before, State after)
    {
        var survivingIds = after.Shifts.Select(shift => shift.Id).ToHashSet();
        var removedIds = before.Shifts.Select(shift => shift.Id).Where(id => !survivingIds.Contains(id)).ToHashSet();
        CollectionAssert.AreEqual(before.Receipts.Where(id => !removedIds.Contains(id)).ToArray(), after.Receipts);
        Assert.AreEqual(before.HandoverCursor, after.HandoverCursor);
    }

    private static async Task<JsonElement> ReadPlan(HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var field in new[] { "deletedCount", "generatedCount", "startUtc", "endUtc", "autoExtend", "version", "planId" })
            Assert.IsTrue(result.TryGetProperty(field, out _), $"Missing '{field}' in {result.GetRawText()}");
        return result;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ClearOnlyPreviewIsReadOnlyAndExecutePreservesHistoryAndOngoingShift(bool includeManual)
    {
        await LoginAsAdmin();
        var (id, version) = await SeedGroup();
        var before = await ReadState(id);
        using var preview = await Reset(id, version, preview: true, includeManual: includeManual);
        var plan = await ReadPlan(preview);
        Assert.AreEqual(includeManual ? 2 : 1, plan.GetProperty("deletedCount").GetInt32());
        Assert.AreEqual(0, plan.GetProperty("generatedCount").GetInt32());
        Assert.AreEqual(before.AutoExtend, plan.GetProperty("autoExtend").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, plan.GetProperty("startUtc").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, plan.GetProperty("endUtc").ValueKind);
        Assert.AreEqual(version, plan.GetProperty("version").GetGuid());
        await AssertUnchanged(id, before);

        using var missingPlan = await Reset(id, version, preview: false, includeManual: includeManual);
        Assert.AreEqual(HttpStatusCode.Conflict, missingPlan.StatusCode);
        await AssertUnchanged(id, before);

        var clearedAfter = DateTime.UtcNow;
        using var execute = await Reset(id, version, preview: false, includeManual: includeManual, planId: plan.GetProperty("planId").GetString());
        var result = await ReadPlan(execute);
        foreach (var field in new[] { "deletedCount", "generatedCount", "startUtc", "endUtc", "autoExtend" })
            Assert.AreEqual(plan.GetProperty(field).GetRawText(), result.GetProperty(field).GetRawText(), field);
        var after = await ReadState(id);
        Assert.AreNotEqual(version, after.Version);
        Assert.AreEqual(after.Version, result.GetProperty("version").GetGuid());
        Assert.AreEqual(before.AutoExtend, after.AutoExtend);
        Assert.IsTrue(after.ExtensionCursor >= clearedAfter && after.ExtensionCursor <= DateTime.UtcNow);
        AssertRetainedReceipts(before, after);
        CollectionAssert.AreEqual(includeManual ? before.Shifts[..2] : [.. before.Shifts[..2], before.Shifts[3]], after.Shifts);

        using var staleExecute = await Reset(id, version, preview: false, includeManual: includeManual, planId: plan.GetProperty("planId").GetString());
        Assert.AreEqual(HttpStatusCode.Conflict, staleExecute.StatusCode);
        await AssertUnchanged(id, after);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RegeneratePreviewMatchesPersistedReplacementWithoutOverlaps(bool includeManual)
    {
        await LoginAsAdmin();
        var (id, _) = await SeedGroup();
        string[] queue;
        Guid version;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.Include(group => group.Rule).Include(group => group.Members).SingleAsync(group => group.Id == id);
            group.Rule!.ShiftMinutes = 720;
            group.ScheduleVersion = version = Guid.NewGuid();
            queue = group.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray();
            await db.SaveChangesAsync();
        }
        var before = await ReadState(id);
        using var preview = await Reset(id, version, preview: true, regenerate: true, includeManual: includeManual);
        var plan = await ReadPlan(preview);
        Assert.AreEqual(version, plan.GetProperty("version").GetGuid());
        Assert.AreEqual(includeManual ? 2 : 1, plan.GetProperty("deletedCount").GetInt32());
        await AssertUnchanged(id, before);
        Assert.DoesNotContain("Schedule regenerated.", await Http.GetStringAsync($"/OnCall/GroupDetails/{id}"));

        using var execute = await Reset(id, version, preview: false, regenerate: true, includeManual: includeManual,
            planId: plan.GetProperty("planId").GetString(), startUtc: "2000-01-01T00:00:01Z");
        var result = await ReadPlan(execute);
        foreach (var field in new[] { "deletedCount", "generatedCount", "startUtc", "endUtc", "autoExtend" })
            Assert.AreEqual(plan.GetProperty(field).GetRawText(), result.GetProperty(field).GetRawText(), field);
        var after = await ReadState(id);
        Assert.AreNotEqual(version, after.Version);
        Assert.AreEqual(after.Version, result.GetProperty("version").GetGuid());
        Assert.IsTrue(after.AutoExtend);
        Assert.IsTrue(result.GetProperty("autoExtend").GetBoolean());
        var oldIds = before.Shifts.Select(shift => shift.Id).ToHashSet();
        CollectionAssert.AreEqual(includeManual ? before.Shifts[..2] : [.. before.Shifts[..2], before.Shifts[3]],
            after.Shifts.Where(shift => oldIds.Contains(shift.Id)).ToArray());
        var replacements = after.Shifts.Where(shift => !oldIds.Contains(shift.Id)).ToArray();
        Assert.IsNotEmpty(replacements);
        Assert.HasCount(result.GetProperty("generatedCount").GetInt32(), replacements);
        Assert.IsTrue(replacements.All(shift => shift.Source == OnCallShiftSource.Generated && shift.RotationPosition.HasValue));
        Assert.IsTrue(replacements.Take(4).All(shift => shift.End - shift.Start == TimeSpan.FromHours(12)));
        CollectionAssert.AreEquivalent(queue, replacements.Take(4).Select(shift => shift.Users).ToArray());
        Assert.IsTrue(replacements.All(shift => shift.Users == queue[(int)(shift.RotationPosition!.Value % queue.Length)]));
        CollectionAssert.AreEqual(replacements.Take(2).Select(shift => shift.Users).ToArray(),
            replacements.Skip(4).Take(2).Select(shift => shift.Users).ToArray());
        Assert.AreEqual(replacements[^1].End, after.ExtensionCursor);
        AssertRetainedReceipts(before, after);
        Assert.AreEqual(before.Shifts[1].End, replacements[0].Start);
        Assert.AreEqual(replacements[0].Start, result.GetProperty("startUtc").GetDateTime());
        Assert.AreEqual(replacements[^1].End, result.GetProperty("endUtc").GetDateTime());
        Assert.IsTrue(replacements[^1].End >= DateTime.UtcNow.Date.AddDays(28));
        Assert.IsTrue(after.Shifts.Zip(after.Shifts.Skip(1)).All(pair => pair.First.End <= pair.Second.Start));
        Assert.Contains("Schedule regenerated.", await Http.GetStringAsync($"/OnCall/GroupDetails/{id}"));
        Assert.DoesNotContain("Schedule regenerated.", await Http.GetStringAsync($"/OnCall/GroupDetails/{id}"));
    }

    [TestMethod]
    public async Task WeeklyRegenerationEndsAtTheFirstTeamLocalHandoverAtOrAfterFourWeeks()
    {
        await LoginAsAdmin();
        var (id, version) = await SeedGroup();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.Include(group => group.Rule).SingleAsync(group => group.Id == id);
            group.Rule!.ShiftMinutes = null;
            db.AddRange(OnCallScheduleGeneratorTests.WeeklyRule().HandoverPoints.Select(point =>
            {
                point.Rule = group.Rule;
                return point;
            }));
            await db.SaveChangesAsync();
        }
        var before = await ReadState(id);
        var earliest = DateTime.UtcNow.AddDays(28);
        using var preview = await Reset(id, version, preview: true, regenerate: true, includeManual: true);
        var previewPlan = await ReadPlan(preview);
        using var response = await Reset(id, version, preview: false, regenerate: true, includeManual: true,
            planId: previewPlan.GetProperty("planId").GetString());
        var plan = await ReadPlan(response);
        var latest = DateTime.UtcNow.AddDays(28);
        var start = plan.GetProperty("startUtc").GetDateTime();
        var end = plan.GetProperty("endUtc").GetDateTime();
        Assert.AreEqual(before.Shifts[1].End, start);
        Assert.IsTrue(end >= earliest && end < latest.AddDays(4));
        var localEnd = end.AddHours(8);
        Assert.IsTrue(localEnd.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Friday);
        Assert.AreEqual(TimeSpan.FromHours(22), localEnd.TimeOfDay);
        var previousHandover = end.AddDays(localEnd.DayOfWeek == DayOfWeek.Monday ? -3 : -4);
        Assert.IsTrue(previousHandover < latest);
        var after = await ReadState(id);
        Assert.AreEqual(end, after.ExtensionCursor);
        Assert.AreEqual(end, after.Shifts[^1].End);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RegenerationOutsideWindowPreservesExistingFutureShifts(bool preview)
    {
        await LoginAsAdmin();
        var (id, version) = await SeedGroup();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.SingleAsync(group => group.Id == id);
            group.AnchorUtc = DateTime.UtcNow.Date.AddDays(60);
            group.AutoExtend = false;
            var futureShifts = await db.OnCallShifts.Where(shift => shift.GroupId == id && shift.StartsAtUtc > DateTime.UtcNow).ToListAsync();
            foreach (var shift in futureShifts)
            {
                shift.StartsAtUtc = shift.StartsAtUtc.AddDays(60);
                shift.EndsAtUtc = shift.EndsAtUtc.AddDays(60);
            }
            await db.SaveChangesAsync();
        }
        var before = await ReadState(id);
        using var response = await Reset(id, version, preview, regenerate: true, includeManual: true);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("The next available shift starts outside the regeneration window.", result.GetProperty("errors")[0].GetString());
        await AssertUnchanged(id, before);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConfirmationRejectsPlanThatCrossesAShiftOrGenerationBoundary(bool regenerate)
    {
        var (id, version) = await SeedGroup();
        var before = await ReadState(id);
        var boundary = regenerate ? before.Shifts[2].Start.Date.AddHours(8).AddDays(-2) : before.Shifts[2].Start;
        var clock = new ResetClock(new DateTimeOffset(DateTime.SpecifyKind(boundary.AddSeconds(-1), DateTimeKind.Utc)));
        using var scope = Server!.Services.CreateScope();
        var service = ActivatorUtilities.CreateInstance<OnCallScheduleService>(scope.ServiceProvider, clock);
        var token = TestContext.CancellationToken;
        var preview = await service.ResetAsync(id, regenerate, false, true, version, null, token);
        Assert.IsNotNull(preview);
        clock.Now = clock.Now.AddSeconds(regenerate ? 2 : 1);

        await Assert.ThrowsExactlyAsync<OnCallScheduleService.ResetPlanChangedException>(
            () => service.ResetAsync(id, regenerate, false, false, version, preview.PlanId, token, preview.StartUtc));
        await AssertUnchanged(id, before);
        var refreshed = await service.ResetAsync(id, regenerate, false, true, version, null, token);
        Assert.IsNotNull(refreshed);
        if (regenerate)
            Assert.AreEqual(preview.GeneratedCount + 1, refreshed.GeneratedCount);
        else
            Assert.AreEqual(preview.DeletedCount - 1, refreshed.DeletedCount);
    }

    [TestMethod]
    public async Task RegenerationDuringACoverageGapCanConfirmThePreviewedStart()
    {
        var (id, version) = await SeedGroup();
        var before = await ReadState(id);
        var nextStart = DateTime.SpecifyKind(before.Shifts[1].End.AddHours(1), DateTimeKind.Utc);
        var clock = new ResetClock(new DateTimeOffset(nextStart.AddSeconds(-10)));
        using var scope = Server!.Services.CreateScope();
        var service = ActivatorUtilities.CreateInstance<OnCallScheduleService>(scope.ServiceProvider, clock);
        var token = TestContext.CancellationToken;
        var preview = await service.ResetAsync(id, true, false, true, version, null, token);
        Assert.IsNotNull(preview);
        Assert.AreEqual(nextStart, preview.StartUtc);
        clock.Now = clock.Now.AddSeconds(15);

        var result = await service.ResetAsync(id, true, false, false, version, preview.PlanId, token, preview.StartUtc);
        Assert.IsNotNull(result);
        Assert.AreEqual(preview.StartUtc, result.StartUtc);
        var after = await ReadState(id);
        Assert.AreEqual(nextStart, after.Shifts[2].Start);
    }

    [TestMethod]
    [DataRow(601, 0)]
    [DataRow(5, -60)]
    [DataRow(5, 60)]
    [DataRow(5, 1)]
    public async Task RegenerationDuringACoverageGapRejectsExpiredOrModifiedStarts(int secondsAfterStart, int startOffsetSeconds)
    {
        var (id, version) = await SeedGroup();
        var before = await ReadState(id);
        var nextStart = DateTime.SpecifyKind(before.Shifts[1].End.AddHours(1), DateTimeKind.Utc);
        var clock = new ResetClock(new DateTimeOffset(nextStart.AddSeconds(-10)));
        using var scope = Server!.Services.CreateScope();
        var service = ActivatorUtilities.CreateInstance<OnCallScheduleService>(scope.ServiceProvider, clock);
        var token = TestContext.CancellationToken;
        var preview = await service.ResetAsync(id, true, false, true, version, null, token);
        Assert.IsNotNull(preview);
        Assert.AreEqual(nextStart, preview.StartUtc);
        clock.Now = new DateTimeOffset(nextStart.AddSeconds(secondsAfterStart));

        await Assert.ThrowsExactlyAsync<OnCallScheduleService.ResetPlanChangedException>(
            () => service.ResetAsync(id, true, false, false, version, preview.PlanId, token, nextStart.AddSeconds(startOffsetSeconds)));
        await AssertUnchanged(id, before);
    }

    [TestMethod]
    public async Task GapResetBindsThePreviewedUtcStartFromTheForm()
    {
        await LoginAsAdmin();
        var (id, version) = await SeedGroup(withCoverageGap: true);
        using var preview = await Reset(id, version, preview: true, regenerate: true);
        var plan = await ReadPlan(preview);
        var start = plan.GetProperty("startUtc");
        Assert.AreEqual(DateTimeKind.Utc, start.GetDateTime().Kind);
        var planId = plan.GetProperty("planId").GetString();

        var modifiedStart = start.GetDateTime().AddSeconds(1).ToString("O");
        using var rejected = await Reset(id, version, preview: false, regenerate: true, planId: planId, startUtc: modifiedStart);
        Assert.AreEqual(HttpStatusCode.Conflict, rejected.StatusCode);

        using var confirmed = await Reset(id, version, preview: false, regenerate: true, planId: planId, startUtc: start.GetString());
        var result = await ReadPlan(confirmed);
        Assert.AreEqual(start.GetString(), result.GetProperty("startUtc").GetString());
        var after = await ReadState(id);
        Assert.AreEqual(start.GetDateTime(), after.Shifts[2].Start);
    }

    [TestMethod]
    public async Task ResetRequiresAntiforgeryAndManagementPermission()
    {
        var (id, version) = await SeedGroup();
        var before = await ReadState(id);
        await RegisterAndLoginAsync();
        using var denied = await Reset(id, version, preview: false, includeToken: false);
        Assert.IsTrue(denied.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Found);
        await LoginAsAdmin();
        using var missingToken = await Reset(id, version, preview: false, includeToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingToken.StatusCode);
        await AssertUnchanged(id, before);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RegenerationWithoutTemplateIsRejectedWithoutChangingAnyRows(bool preview)
    {
        await LoginAsAdmin();
        var (id, version) = await SeedGroup(withRule: false);
        var before = await ReadState(id);
        using var response = await Reset(id, version, preview, regenerate: true, includeManual: true);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("Please bind a scheduling template to the team first.", result.GetProperty("errors")[0].GetString());
        await AssertUnchanged(id, before);
    }
}

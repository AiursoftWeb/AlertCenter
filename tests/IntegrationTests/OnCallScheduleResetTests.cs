using System.Net;
using System.Text.Json;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class OnCallScheduleResetTests : TestBase
{
    private async Task<(Guid Id, Guid Version)> SeedGroup(bool withRule = true)
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
            Shift(today.AddDays(-1), today.AddDays(1), OnCallShiftSource.Generated),
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
        bool includeManual = false, bool includeToken = true) => PostForm("/OnCall/ResetSchedule", new()
        {
            ["id"] = id.ToString(), ["version"] = version.ToString(),
            ["preview"] = preview.ToString(), ["regenerate"] = regenerate.ToString(),
            ["includeManual"] = includeManual.ToString()
        }, $"/OnCall/GroupDetails/{id}", includeToken);

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
        foreach (var field in new[] { "deletedCount", "generatedCount", "startUtc", "endUtc", "autoExtend", "version" })
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

        var clearedAfter = DateTime.UtcNow;
        using var execute = await Reset(id, version, preview: false, includeManual: includeManual);
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

        using var staleExecute = await Reset(id, version, preview: false, includeManual: includeManual);
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

        using var execute = await Reset(id, version, preview: false, regenerate: true, includeManual: includeManual);
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
        using var response = await Reset(id, version, preview: false, regenerate: true, includeManual: true);
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

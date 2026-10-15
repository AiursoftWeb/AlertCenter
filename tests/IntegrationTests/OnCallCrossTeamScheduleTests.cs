using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class OnCallCrossTeamScheduleTests : TestBase
{
    private readonly List<Guid> groupIds = [];
    private readonly List<Guid> ruleIds = [];
    private string[] userIds = [];
    private static readonly DateTime Anchor = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    [TestCleanup]
    public override async Task CleanServer()
    {
        if (Server != null)
        {
            using var scope = Server.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.OnCallShiftMembers.RemoveRange(await db.OnCallShiftMembers.Where(member => groupIds.Contains(member.Shift.GroupId)).ToListAsync());
            db.OnCallShifts.RemoveRange(await db.OnCallShifts.Where(shift => groupIds.Contains(shift.GroupId)).ToListAsync());
            db.OnCallGroupMembers.RemoveRange(await db.OnCallGroupMembers.Where(member => groupIds.Contains(member.GroupId)).ToListAsync());
            db.OnCallGroups.RemoveRange(await db.OnCallGroups.Where(group => groupIds.Contains(group.Id)).ToListAsync());
            db.OnCallRules.RemoveRange(await db.OnCallRules.Where(rule => ruleIds.Contains(rule.Id)).ToListAsync());
            db.Users.RemoveRange(await db.Users.Where(user => userIds.Contains(user.Id)).ToListAsync());
            await db.SaveChangesAsync();
        }
        await base.CleanServer();
    }

    private async Task<Guid> SeedGroup(bool withRule = true)
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var users = Enumerable.Range(0, 6).Select(index => new User
        {
            UserName = $"cross-team-{Guid.NewGuid():N}", DisplayName = $"Cross-team staff {index}"
        }).ToArray();
        db.Users.AddRange(users);
        userIds = users.Select(user => user.Id).ToArray();
        var rule = withRule ? new OnCallRule { Name = "Cross-team rotation" } : null;
        var group = new OnCallGroup
        {
            Name = "Primary team", Rule = rule, AnchorUtc = Anchor, TimeZoneId = "Asia/Shanghai",
            Members = users.Take(4).Select((user, index) => new OnCallGroupMember { UserId = user.Id, RotationOrder = index }).ToList()
        };
        db.OnCallGroups.Add(group);
        groupIds.Add(group.Id);
        if (rule != null) ruleIds.Add(rule.Id);
        await db.SaveChangesAsync();
        return group.Id;
    }

    private async Task<Guid> AddOtherGroup(bool withRule, params string[] members)
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var rule = withRule ? new OnCallRule { Name = "Other rotation" } : null;
        var group = new OnCallGroup
        {
            Name = "Secondary team", Rule = rule, AnchorUtc = Anchor,
            Members = members.Select((id, index) => new OnCallGroupMember { UserId = id, RotationOrder = index }).ToList()
        };
        db.OnCallGroups.Add(group);
        groupIds.Add(group.Id);
        if (rule != null) ruleIds.Add(rule.Id);
        await db.SaveChangesAsync();
        return group.Id;
    }

    private static string ScheduleUrl(Guid id, DateTime start) => $"/OnCall/GetSchedule?id={id}&startUtc={Uri.EscapeDataString(start.ToString("O"))}";

    private async Task<SaveOnCallScheduleRequest> Draft(Guid id, DateTime start) =>
        (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>(ScheduleUrl(id, start)))!;

    private async Task<HttpResponseMessage> Generate(SaveOnCallScheduleRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/OnCall/GenerateSchedule")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("RequestVerificationToken", await GetAntiCsrfToken($"/OnCall/ScheduleDetails/{request.GroupId}"));
        return await Http.SendAsync(message);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreviewAndGenerationSkipSharedMembersWithoutAnyOtherSavedShifts(bool otherHasRule)
    {
        await LoginAsAdmin();
        var id = await SeedGroup();
        var otherId = await AddOtherGroup(otherHasRule, userIds[0]);
        var metadata = await Http.GetFromJsonAsync<JsonElement>(ScheduleUrl(id, Anchor));
        Assert.IsTrue(metadata.GetProperty("canGenerate").GetBoolean());
        var shared = metadata.GetProperty("members").EnumerateArray().Single(member => member.GetProperty("id").GetString() == userIds[0]);
        Assert.AreEqual(otherId, shared.GetProperty("otherGroups")[0].GetProperty("id").GetGuid());
        Assert.AreEqual("Secondary team", shared.GetProperty("otherGroups")[0].GetProperty("name").GetString());
        Assert.AreEqual(0, metadata.GetProperty("otherGroupShifts").GetArrayLength());
        Assert.IsTrue(metadata.GetProperty("members").EnumerateArray().Where(member => member.GetProperty("id").GetString() != userIds[0])
            .All(member => member.GetProperty("otherGroups").GetArrayLength() == 0));
        var draft = await Draft(id, Anchor);
        Assert.AreEqual(21, draft.Slots.Count);
        Assert.IsTrue(draft.Slots.All(slot => slot.UserIds.Count == 1 && !slot.UserIds.Contains(userIds[0])));

        Assert.AreEqual(HttpStatusCode.OK, (await Generate(draft)).StatusCode);

        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(21, await db.OnCallShifts.CountAsync(shift => shift.GroupId == id));
        Assert.IsFalse(await db.OnCallShiftMembers.AnyAsync(member => member.Shift.GroupId == id && member.UserId == userIds[0]));
        Assert.IsFalse(await db.OnCallShifts.AnyAsync(shift => shift.GroupId == otherId));
    }

    [TestMethod]
    public async Task InsufficientSingleGroupMembersRejectGenerationWithoutWritesAndLeavingRestoresEligibility()
    {
        await LoginAsAdmin();
        var id = await SeedGroup();
        var otherId = await AddOtherGroup(false, userIds[0], userIds[1]);
        var metadata = await Http.GetFromJsonAsync<JsonElement>(ScheduleUrl(id, Anchor));
        Assert.IsFalse(metadata.GetProperty("canGenerate").GetBoolean());
        var error = metadata.GetProperty("generationError").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
        Assert.Contains("2", error);
        Assert.Contains("3", error);
        Assert.Contains("多值班组", error);
        var draft = await Draft(id, Anchor);
        Assert.IsTrue(draft.Slots.All(slot => slot.UserIds.Count == 0));
        var response = await Generate(draft);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(result.GetProperty("errors").EnumerateArray().Any(item => !string.IsNullOrWhiteSpace(item.GetString())));
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual(draft.Version, (await db.OnCallGroups.SingleAsync(group => group.Id == id)).ScheduleVersion);
            Assert.AreEqual(0, await db.OnCallShifts.CountAsync(shift => groupIds.Contains(shift.GroupId)));
            Assert.AreEqual(0, await db.OnCallShiftMembers.CountAsync(member => groupIds.Contains(member.Shift.GroupId)));
            db.OnCallGroupMembers.Remove(await db.OnCallGroupMembers.SingleAsync(member => member.GroupId == otherId && member.UserId == userIds[1]));
            await db.SaveChangesAsync();
        }

        metadata = await Http.GetFromJsonAsync<JsonElement>(ScheduleUrl(id, Anchor));
        Assert.IsTrue(metadata.GetProperty("canGenerate").GetBoolean());
        draft = await Draft(id, Anchor);
        Assert.IsTrue(draft.Slots.Any(slot => slot.UserIds.Contains(userIds[1])));
        Assert.IsFalse(draft.Slots.Any(slot => slot.UserIds.Contains(userIds[0])));
        Assert.AreEqual(HttpStatusCode.OK, (await Generate(draft)).StatusCode);
    }

    [TestMethod]
    public async Task JoiningSecondGroupViaTeamEditorPreservesSavedAutomaticAssignmentsAndOnlyChangesLaterGeneration()
    {
        await LoginAsAdmin();
        var id = await SeedGroup();
        Assert.AreEqual(HttpStatusCode.OK, (await Generate(await Draft(id, Anchor))).StatusCode);
        var saved = await Draft(id, Anchor);
        var snapshot = JsonSerializer.Serialize(saved.Slots);
        Guid[] originalIds;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            originalIds = await db.OnCallShifts.Where(shift => shift.GroupId == id).OrderBy(shift => shift.StartsAtUtc).Select(shift => shift.Id).ToArrayAsync();
        }
        var otherId = await AddOtherGroup(false);
        Guid version;
        using (var scope = Server!.Services.CreateScope())
            version = (await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallGroups.SingleAsync(group => group.Id == otherId)).ScheduleVersion;
        AssertRedirect(await PostForm("/OnCall/UpdateGroup", new()
        {
            ["id"] = otherId.ToString(), ["GroupInput.Name"] = "Secondary team", ["GroupInput.TimeZoneId"] = "UTC",
            ["GroupInput.AnchorUtc"] = Anchor.ToString("O"), ["GroupInput.Version"] = version.ToString(),
            ["GroupInput.UserIds[0]"] = userIds[3]
        }, $"/OnCall/GroupEdit/{otherId}"), $"/OnCall/GroupDetails/{otherId}");

        var persisted = await Draft(id, Anchor);
        Assert.AreEqual(snapshot, JsonSerializer.Serialize(persisted.Slots));
        Assert.IsTrue(persisted.Slots.Any(slot => slot.UserIds.Contains(userIds[3])));
        var next = await Draft(id, Anchor.AddDays(7));
        Assert.IsFalse(next.Slots.Any(slot => slot.UserIds.Contains(userIds[3])));
        Assert.AreEqual(HttpStatusCode.OK, (await Generate(next)).StatusCode);
        Assert.AreEqual(snapshot, JsonSerializer.Serialize((await Draft(id, Anchor)).Slots));
        using var checkScope = Server!.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        CollectionAssert.AreEqual(originalIds, await checkDb.OnCallShifts.Where(shift => shift.GroupId == id && shift.StartsAtUtc < Anchor.AddDays(7))
            .OrderBy(shift => shift.StartsAtUtc).Select(shift => shift.Id).ToArrayAsync());
        Assert.IsFalse(await checkDb.OnCallShiftMembers.AnyAsync(member => member.Shift.GroupId == id &&
            member.Shift.StartsAtUtc >= Anchor.AddDays(7) && member.UserId == userIds[3]));
    }

    [TestMethod]
    public async Task ExternalShiftsAreFilteredReadOnlyAndSplitAcrossDisplayRegionsWithoutEnteringOwnSlotsOrTotals()
    {
        await LoginAsAdmin();
        var id = await SeedGroup(false);
        var otherId = await AddOtherGroup(false, userIds[0], userIds[4], userIds[5]);
        var start = Anchor.AddDays(7);
        var intervals = new[]
        {
            (Start: start.AddDays(-2), End: start.AddHours(2.5)),
            (Start: start.AddHours(5.25), End: start.AddHours(10.75)),
            (Start: start.AddDays(7).AddHours(-1.5), End: start.AddDays(9)),
            (Start: start.AddDays(-7).AddHours(-4), End: start.AddDays(-7).AddHours(1)),
            (Start: start.AddDays(14).AddHours(-1), End: start.AddDays(14).AddHours(4))
        };
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            foreach (var interval in intervals)
                db.OnCallShifts.Add(new OnCallShift
                {
                    GroupId = otherId, StartsAtUtc = interval.Start, EndsAtUtc = interval.End,
                    Members = [new OnCallShiftMember { UserId = userIds[0] }, new OnCallShiftMember { UserId = userIds[5] }]
                });
            foreach (var interval in new[]
                {
                    (Start: start.AddDays(-8), End: start.AddDays(-7)),
                    (Start: start.AddDays(14), End: start.AddDays(14).AddHours(8))
                })
                db.OnCallShifts.Add(new OnCallShift
                {
                    GroupId = otherId, StartsAtUtc = interval.Start, EndsAtUtc = interval.End,
                    Members = [new OnCallShiftMember { UserId = userIds[0] }]
                });
            db.OnCallShifts.AddRange(new OnCallShift
            {
                GroupId = otherId, StartsAtUtc = start.AddHours(12), EndsAtUtc = start.AddHours(20),
                Members = [new OnCallShiftMember { UserId = userIds[5] }]
            }, new OnCallShift
            {
                GroupId = id, StartsAtUtc = start.AddHours(-8), EndsAtUtc = start,
                Members = [new OnCallShiftMember { UserId = userIds[4] }]
            }, new OnCallShift
            {
                GroupId = otherId, StartsAtUtc = start.AddHours(40), EndsAtUtc = start.AddHours(48),
                Members = [new OnCallShiftMember { UserId = userIds[4] }]
            });
            await db.SaveChangesAsync();
        }

        var metadata = await Http.GetFromJsonAsync<JsonElement>(ScheduleUrl(id, start));
        var external = metadata.GetProperty("otherGroupShifts").EnumerateArray().ToArray();
        Assert.AreEqual(6, external.Length);
        Assert.IsTrue(external.All(shift => shift.GetProperty("groupId").GetGuid() == otherId && shift.GetProperty("groupName").GetString() == "Secondary team"));
        Assert.IsTrue(external.All(shift => shift.GetProperty("userIds").EnumerateArray().All(user => user.GetString() != userIds[5])));
        Assert.IsTrue(external.Any(shift => shift.GetProperty("userIds").EnumerateArray().Any(user => user.GetString() == userIds[4])));
        foreach (var interval in intervals)
            Assert.IsTrue(external.Any(shift => shift.GetProperty("startsAtUtc").GetDateTime() == interval.Start && shift.GetProperty("endsAtUtc").GetDateTime() == interval.End));
        var departed = metadata.GetProperty("members").EnumerateArray().Single(member => member.GetProperty("id").GetString() == userIds[4]);
        Assert.IsTrue(departed.GetProperty("departed").GetBoolean());
        Assert.AreEqual(otherId, departed.GetProperty("otherGroups")[0].GetProperty("id").GetGuid());
        Assert.IsTrue((await Draft(id, start)).Slots.All(slot => slot.UserIds.Count == 0));

        var rawHtml = await Http.GetStringAsync($"/OnCall/ScheduleDetails/{id}?startUtc={Uri.EscapeDataString(start.ToString("O"))}");
        var html = WebUtility.HtmlDecode(rawHtml);
        Assert.Contains("id=\"oncall-shared-members\"", html);
        var buttons = Regex.Matches(rawHtml, "<button\\b[^>]*class=\"ac-gantt-shift ac-gantt-external\"[^>]*>")
            .Select(match => WebUtility.HtmlDecode(match.Value)).ToArray();
        Assert.AreEqual(6, buttons.Length);
        Assert.IsTrue(buttons.All(button => button.Contains("data-readonly=\"true\"") && button.Contains($"data-group-id=\"{otherId}\"") && !button.Contains("data-slot=")));
        Assert.AreEqual(1, buttons.Count(button => button.Contains("data-edge=\"previous\"")));
        Assert.AreEqual(1, buttons.Count(button => button.Contains("data-edge=\"next\"")));
        Assert.IsTrue(buttons.Where(button => button.Contains("data-edge=\"previous\"") || button.Contains("data-edge=\"next\""))
            .All(button => button.Contains("hidden=\"hidden\"")));
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        foreach (var interval in intervals.Take(3))
        {
            var localStart = TimeZoneInfo.ConvertTime(new DateTimeOffset(interval.Start), zone).ToString("yyyy-MM-dd HH:mm zzz");
            var localEnd = TimeZoneInfo.ConvertTime(new DateTimeOffset(interval.End), zone).ToString("yyyy-MM-dd HH:mm zzz");
            Assert.IsTrue(buttons.Any(button => button.Contains($"data-start=\"{localStart}\"") && button.Contains($"data-end=\"{localEnd}\"") && button.Contains("Secondary team")));
        }
        var spans = buttons.Select(button => Regex.Match(button, "style=\"left: ([^%;]+)%; width: ([^%;]+)%\""))
            .Select(match => (Left: double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), Width: double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))).ToArray();
        var expected = new[] { (-36.0, 36.0), (0.0, 2.5), (5.25, 5.5), (166.5, 1.5), (168.0, 36.0), (40.0, 8.0) };
        foreach (var (left, width) in expected)
            Assert.IsTrue(spans.Any(span => Math.Abs(span.Left - left / 168 * 100) < 0.000001 && Math.Abs(span.Width - width / 168 * 100) < 0.000001));
        var root = Regex.Match(rawHtml, "<div id=\"oncall-schedule\"[^>]*>").Value;
        var slotsJson = WebUtility.HtmlDecode(Regex.Match(root, "data-slots=\"([^\"]*)\"").Groups[1].Value);
        var editorSlots = JsonDocument.Parse(slotsJson).RootElement.EnumerateArray().ToArray();
        Assert.AreEqual(63, editorSlots.Length);
        Assert.IsTrue(editorSlots.Skip(21).Take(21).All(slot => slot.GetProperty("userIds").GetArrayLength() == 0));
        Assert.AreEqual(1, editorSlots.Sum(slot => slot.GetProperty("userIds").GetArrayLength()));
        Assert.IsTrue(Regex.IsMatch(html, "data-oncall-stat=\"shifts\">0</strong>"));
        Assert.IsTrue(Regex.IsMatch(html, "data-oncall-stat=\"hours\">0 小时</strong>"));
    }
}

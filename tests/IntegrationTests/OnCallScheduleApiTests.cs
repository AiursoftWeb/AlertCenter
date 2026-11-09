using System.Net;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class OnCallScheduleApiTests : TestBase
{
    private async Task<Guid> SeedGroup(bool withTemplate = true)
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var users = Enumerable.Range(0, 4).Select(index => new User { UserName = $"api-staff-{index}", DisplayName = $"Staff {index}" }).ToArray();
        db.Users.AddRange(users);
        var group = new OnCallGroup
        {
            Name = "API team", Rule = withTemplate ? new OnCallRule { Name = "Three shifts" } : null,
            AnchorUtc = DateTime.UtcNow.Date.AddDays(-1),
            Members = users.Select((user, index) => new OnCallGroupMember { UserId = user.Id, RotationOrder = index }).ToList()
        };
        db.OnCallGroups.Add(group);
        await db.SaveChangesAsync();
        return group.Id;
    }

    private async Task<HttpResponseMessage> Save(string action, SaveOnCallScheduleRequest payload, string? tokenPath = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/OnCall/{action}");
        message.Content = JsonContent.Create(payload);
        message.Headers.Add("RequestVerificationToken", await GetAntiCsrfToken(tokenPath ?? $"/OnCall/ScheduleDetails/{payload.GroupId}"));
        return await Http.SendAsync(message);
    }

    [TestMethod]
    public async Task ClearPreviewUsesTeamTimeAndSaveAtomicallyDisablesExtensionWithVersionCheck()
    {
        await LoginAsAdmin();
        var id = await SeedGroup();
        var now = DateTime.UtcNow;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.SingleAsync(item => item.Id == id);
            group.TimeZoneId = "Asia/Shanghai";
            group.AutoExtend = true;
            db.OnCallShifts.Add(new() { GroupId = id, StartsAtUtc = now.AddDays(1), EndsAtUtc = now.AddDays(2), Source = OnCallShiftSource.Generated });
            await db.SaveChangesAsync();
        }
        var path = $"/OnCall/GetSchedule?id={id}&startUtc={Uri.EscapeDataString(now.ToString("O"))}&endUtc={Uri.EscapeDataString(now.AddDays(3).ToString("O"))}";
        var metadata = await Http.GetFromJsonAsync<System.Text.Json.JsonElement>(path);
        Assert.IsTrue(metadata.GetProperty("autoExtend").GetBoolean());
        Assert.AreEqual(now.AddDays(2), metadata.GetProperty("lastShiftEndUtc").GetDateTime());
        Assert.AreEqual(1, metadata.GetProperty("slots")[0].GetProperty("source").GetInt32());
        var request = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>(path))!;
        request.ClearRanges = [new() { StartUtc = now, EndUtc = now.AddDays(3), AutoExtendAction = "disable" }];
        var preview = await Save("PreviewSchedule?clear=true", request);
        Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode);
        var responseJson = await preview.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.IsTrue(responseJson.GetProperty("clearRanges")[0].TryGetProperty("clearedAtUtc", out _));
        Assert.IsTrue(responseJson.GetProperty("clearRanges")[0].TryGetProperty("startUtc", out _));
        var result = System.Text.Json.JsonSerializer.Deserialize<SaveOnCallScheduleRequest>(responseJson,
            System.Text.Json.JsonSerializerOptions.Web)!;
        Assert.AreEqual(0, result.Slots.Count);
        Assert.IsNotNull(result.ClearRanges.Single().ClearedAtUtc);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual(1, await db.OnCallShifts.CountAsync(shift => shift.GroupId == id));
            Assert.IsTrue((await db.OnCallGroups.SingleAsync(group => group.Id == id)).AutoExtend);
        }
        request.Slots = result.Slots;
        request.ClearRanges = result.ClearRanges;
        Assert.AreEqual(HttpStatusCode.OK, (await Save("SaveSchedule", request)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, (await Save("SaveSchedule", request)).StatusCode);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual(0, await db.OnCallShifts.CountAsync(shift => shift.GroupId == id));
            Assert.IsFalse((await db.OnCallGroups.SingleAsync(group => group.Id == id)).AutoExtend);
        }
        await RegisterAndLoginAsync();
        Assert.DoesNotContain("id=\"oncall-clear-schedule\"", await Http.GetStringAsync($"/OnCall/ScheduleDetails/{id}"));
        var forbidden = await Save("PreviewSchedule?clear=true", request, "/Manage/ChangePassword");
        Assert.IsTrue(forbidden.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Found);
    }

    [TestMethod]
    public async Task AtomicMultiweekApiSavesWarningsAndRejectsConflictsMalformedWeeksAndStaleVersions()
    {
        await LoginAsAdmin();
        var id = await SeedGroup();
        DateTime anchor;
        Guid version;
        string[] users;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.Include(item => item.Rule).Include(item => item.Members).SingleAsync(item => item.Id == id);
            group.Rule!.MinimumStaff = 2;
            group.Rule.TargetStaff = 2;
            group.MinimumShiftStaff = 2;
            group.MinimumRestHours = 16;
            await db.SaveChangesAsync();
            anchor = DateTime.SpecifyKind(group.AnchorUtc, DateTimeKind.Utc);
            version = group.ScheduleVersion;
            users = group.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray();
        }
        List<OnCallSlotRequest> Slots(DateTime start) => Enumerable.Range(0, 21).Select(index => new OnCallSlotRequest
        {
            StartsAtUtc = start.AddHours(index * 8), UserIds = [users[index % 3]]
        }).ToList();
        var payload = new SaveOnCallScheduleRequest
        {
            GroupId = id, Version = version, StartUtc = anchor.AddDays(7), Slots = Slots(anchor.AddDays(7)),
            AdjacentWeeks =
            [
                new() { StartUtc = anchor, Slots = Slots(anchor) },
                new() { StartUtc = anchor.AddDays(14), Slots = Slots(anchor.AddDays(14)) }
            ]
        };

        Assert.AreEqual(HttpStatusCode.BadRequest, (await Save("GenerateSchedule", payload)).StatusCode);
        var response = await Save("SaveSchedule", payload);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.IsTrue(result.GetProperty("warnings").GetArrayLength() > 0);
        Assert.AreEqual(HttpStatusCode.Conflict, (await Save("SaveSchedule", payload)).StatusCode);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            payload.Version = (await db.OnCallGroups.SingleAsync(item => item.Id == id)).ScheduleVersion;
            Assert.AreNotEqual(version, payload.Version);
            Assert.AreEqual(63, await db.OnCallShifts.CountAsync(shift => shift.GroupId == id));
            Assert.AreEqual(63, await db.OnCallShiftMembers.CountAsync(member => member.Shift.GroupId == id));
        }
        payload.Slots[5].UserIds = [users[3]];
        payload.AdjacentWeeks[0].Slots[^1].UserIds = [users[0]];
        response = await Save("SaveSchedule", payload);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.IsTrue(result.GetProperty("warnings").EnumerateArray().Any(error => error.GetString()!.Contains("insufficient rest")));
        payload.Version = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}"))!.Version;
        payload.AdjacentWeeks[0].Slots[^1].UserIds = [users[2]];
        payload.AdjacentWeeks[1].StartUtc = payload.AdjacentWeeks[0].StartUtc;
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Save("SaveSchedule", payload)).StatusCode);
        payload.AdjacentWeeks = null!;
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Save("SaveSchedule", payload)).StatusCode);
        payload.AdjacentWeeks = [null!];
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Save("SaveSchedule", payload)).StatusCode);
        using var checkScope = Server!.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(payload.Version, (await checkDb.OnCallGroups.SingleAsync(item => item.Id == id)).ScheduleVersion);
        Assert.AreEqual(63, await checkDb.OnCallShifts.CountAsync(shift => shift.GroupId == id));
        Assert.AreEqual(63, await checkDb.OnCallShiftMembers.CountAsync(member => member.Shift.GroupId == id));
        Assert.IsTrue(await checkDb.OnCallShiftMembers.AnyAsync(member => member.Shift.GroupId == id && member.UserId == users[3]));
    }

    [TestMethod]
    public async Task AdminCanGenerateReadAndEditScheduleWithValidationAndVersionChecks()
    {
        await LoginAsAdmin();
        var id = await SeedGroup();
        var suggestion = await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}");
        Assert.IsNotNull(suggestion);
        Assert.AreEqual(id, suggestion.GroupId);
        using (var metadataScope = Server!.Services.CreateScope())
        {
            var group = await metadataScope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallGroups.SingleAsync(item => item.Id == id);
            Assert.AreEqual(group.ScheduleVersion, suggestion.Version);
            Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync($"/OnCall/GetSchedule?id={group.RuleId}")).StatusCode);
        }
        Assert.AreEqual(21, suggestion.Slots.Count);
        Assert.AreEqual(21, suggestion.Slots.Sum(slot => slot.UserIds.Count));
        var metadata = await Http.GetFromJsonAsync<System.Text.Json.JsonElement>($"/OnCall/GetSchedule?id={id}");
        Assert.AreEqual(16, metadata.GetProperty("restHours").GetInt32());
        Assert.IsFalse(metadata.GetProperty("balanceShiftTimes").GetBoolean());
        Assert.IsTrue(metadata.GetProperty("members")[0].TryGetProperty("id", out _));
        var noToken = await Http.PostAsJsonAsync("/OnCall/GenerateSchedule", suggestion);
        Assert.AreEqual(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await Save("GenerateSchedule", suggestion)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, (await Save("SaveSchedule", suggestion)).StatusCode);

        var saved = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}"))!;
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Save("GenerateSchedule", saved)).StatusCode);
        var removed = saved.Slots[0].UserIds[0];
        saved.Slots[0].UserIds.Remove(removed);
        Assert.AreEqual(HttpStatusCode.OK, (await Save("SaveSchedule", saved)).StatusCode);
        var edited = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}"))!;
        Assert.DoesNotContain(removed, edited.Slots[0].UserIds);
        edited.Slots[0].UserIds.Clear();
        var empty = await Save("SaveSchedule", edited);
        Assert.AreEqual(HttpStatusCode.OK, empty.StatusCode);
        Assert.IsTrue(System.Text.Json.JsonDocument.Parse(await empty.Content.ReadAsStringAsync()).RootElement.GetProperty("warnings").GetArrayLength() > 0);
        using var scope = Server!.Services.CreateScope();
        Assert.AreEqual(20, await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallShiftMembers.CountAsync(member => member.Shift.GroupId == id));
        var nextStart = edited.StartUtc.AddDays(7);
        var next = await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}&startUtc={Uri.EscapeDataString(nextStart.ToString("O"))}");
        Assert.AreEqual(nextStart, next!.StartUtc);
        Assert.AreEqual(21, next.Slots.Count);
    }

    [TestMethod]
    public async Task TeamThresholdWarningsPersistAndEmptySlotsSave()
    {
        await LoginAsAdmin();
        var id = await SeedGroup();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.Include(item => item.Rule).SingleAsync(item => item.Id == id);
            group.Rule!.MinimumStaff = 2;
            group.Rule.TargetStaff = 2;
            group.MinimumShiftStaff = 2;
            await db.SaveChangesAsync();
        }
        Guid ruleId;
        using (var scope = Server!.Services.CreateScope())
            ruleId = (await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallGroups.SingleAsync(group => group.Id == id)).RuleId!.Value;
        var editPage = await Http.GetStringAsync($"/OnCall/Edit/{ruleId}");
        foreach (var field in new[] { "StaffPerShift" })
        {
            var input = System.Text.RegularExpressions.Regex.Match(editPage, $"<input[^>]*name=\"RuleInput.{field}\"[^>]*>").Value;
            Assert.Contains("value=\"1\"", input);
        }
        var metadata = await Http.GetFromJsonAsync<System.Text.Json.JsonElement>($"/OnCall/GetSchedule?id={id}");
        Assert.AreEqual(2, metadata.GetProperty("minimumStaff").GetInt32());
        Assert.AreEqual(2, metadata.GetProperty("targetStaff").GetInt32());
        Assert.IsTrue(metadata.GetProperty("canGenerate").GetBoolean());
        var draft = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}"))!;
        var users = metadata.GetProperty("members").EnumerateArray().Select(user => user.GetProperty("id").GetString()!).Take(3).ToArray();
        for (var index = 0; index < draft.Slots.Count; index++)
            draft.Slots[index].UserIds = [users[index % 3]];
        var response = await Save("SaveSchedule", draft);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.IsTrue(result.GetProperty("warnings").GetArrayLength() > 0);
        Assert.IsFalse(result.TryGetProperty("errors", out _));
        metadata = await Http.GetFromJsonAsync<System.Text.Json.JsonElement>($"/OnCall/GetSchedule?id={id}");
        Assert.IsTrue(metadata.GetProperty("saved").GetBoolean());
        Assert.IsTrue(metadata.GetProperty("warnings").GetArrayLength() > 0);
        CollectionAssert.AreEqual(result.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()).ToArray(),
            metadata.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()).ToArray());
        var details = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/ScheduleDetails/{id}"));
        Assert.Contains("Shift staffing is below the team threshold.", details);
        Assert.Contains("id=\"oncall-warning-lane\"", details);

        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.Include(item => item.Rule).SingleAsync(item => item.Id == id);
            group.Rule!.MinimumStaff = 1;
            group.Rule.TargetStaff = null;
            await db.SaveChangesAsync();
        }
        metadata = await Http.GetFromJsonAsync<System.Text.Json.JsonElement>($"/OnCall/GetSchedule?id={id}");
        Assert.AreEqual(2, metadata.GetProperty("minimumStaff").GetInt32());
        Assert.AreEqual(System.Text.Json.JsonValueKind.Null, metadata.GetProperty("targetStaff").ValueKind);
        Assert.IsTrue(metadata.GetProperty("warnings").GetArrayLength() > 0);
        draft = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}"))!;
        var version = draft.Version;
        draft.Slots[0].UserIds.Clear();
        response = await Save("SaveSchedule", draft);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.IsTrue(result.GetProperty("warnings").EnumerateArray().Any(error => error.GetString()!.Contains("no on-duty")));
        using var checkScope = Server!.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreNotEqual(version, (await checkDb.OnCallGroups.SingleAsync(group => group.Id == id)).ScheduleVersion);
        Assert.AreEqual(20, await checkDb.OnCallShiftMembers.CountAsync(member => member.Shift.GroupId == id));
        Assert.IsTrue(await checkDb.OnCallShifts.Where(shift => shift.GroupId == id).AllAsync(shift => shift.MinimumStaff == 2));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task WarningLaneRendersMergedSegmentsWithUniqueCountsAndCompleteAccessibleDetails(bool administrator)
    {
        if (administrator) await LoginAsAdmin();
        else await RegisterAndLoginAsync();
        var id = await SeedGroup(withTemplate: false);
        var start = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.SingleAsync(item => item.Id == id);
            group.TimeZoneId = "Asia/Shanghai";
            group.MinimumShiftStaff = 2;
            db.OnCallShifts.AddRange(
                new OnCallShift { GroupId = id, StartsAtUtc = start, EndsAtUtc = start.AddHours(6) },
                new OnCallShift { GroupId = id, StartsAtUtc = start.AddHours(2), EndsAtUtc = start.AddHours(8) },
                new OnCallShift { GroupId = id, StartsAtUtc = start.AddHours(8), EndsAtUtc = start.AddHours(10) });
            await db.SaveChangesAsync();
        }

        var rawHtml = await Http.GetStringAsync($"/OnCall/ScheduleDetails/{id}?startUtc={Uri.EscapeDataString(start.ToString("O"))}");
        Assert.Contains("class=\"ac-gantt-row ac-gantt-warning-row\"", rawHtml);
        var buttons = System.Text.RegularExpressions.Regex.Matches(rawHtml,
            "<button[^>]*class=\"ac-gantt-shift ac-gantt-warning bg-warning text-dark\"[^>]*>.*?</button>",
            System.Text.RegularExpressions.RegexOptions.Singleline).Select(match => match.Value).ToArray();
        Assert.AreEqual(3, buttons.Length);
        var expectedMessages = new[]
        {
            new[] { "Schedule contains a coverage gap.", "Schedule contains overlapping shifts.",
                "Shift has no on-duty engineers.", "Shift staffing is below the team threshold." },
            new[] { "Schedule contains a coverage gap.", "Shift has no on-duty engineers.",
                "Shift staffing is below the team threshold." },
            new[] { "Schedule contains a coverage gap." }
        };
        var boundaries = new[] { start, start.AddHours(8), start.AddHours(10), start.AddDays(7) };
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        for (var index = 0; index < buttons.Length; index++)
        {
            var button = buttons[index];
            string Attribute(string name) => WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(
                button, $"{name}=\"([^\"]*)\"").Groups[1].Value);
            var summary = string.Join("\n", expectedMessages[index]);
            var localStart = TimeZoneInfo.ConvertTime(new DateTimeOffset(boundaries[index]), zone).ToString("yyyy-MM-dd HH:mm zzz");
            var localEnd = TimeZoneInfo.ConvertTime(new DateTimeOffset(boundaries[index + 1]), zone).ToString("yyyy-MM-dd HH:mm zzz");
            var details = $"{summary} · {localStart} → {localEnd} · Asia/Shanghai";
            Assert.AreEqual(summary, Attribute("data-summary"));
            Assert.AreEqual(localStart, Attribute("data-start"));
            Assert.AreEqual(localEnd, Attribute("data-end"));
            Assert.AreEqual(details, Attribute("title"));
            Assert.AreEqual(details, Attribute("aria-label"));
            Assert.AreEqual("true", Attribute("data-readonly"));
            Assert.AreEqual("oncall-shift-detail", Attribute("aria-describedby"));
            var left = (boundaries[index] - start).TotalHours / 168 * 100;
            var width = (boundaries[index + 1] - boundaries[index]).TotalHours / 168 * 100;
            Assert.AreEqual($"left: {left.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}%; width: {width.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}%", Attribute("style"));
            Assert.Contains($"<span class=\"badge bg-dark ac-gantt-warning-count\" aria-hidden=\"true\">{expectedMessages[index].Length}</span>", button);
            Assert.Contains($"<span class=\"ac-gantt-warning-text\" aria-hidden=\"true\">{summary}</span>", WebUtility.HtmlDecode(button));
        }
    }

    [TestMethod]
    public async Task GanttRetainsUnassignedEngineersAndTableAllowsFreeShiftEditing()
    {
        await LoginAsAdmin();
        var id = await SeedGroup();
        var draft = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}"))!;
        var redundant = draft.Slots[0].UserIds[0];
        var replacement = draft.Slots[1].UserIds[0];
        foreach (var slot in draft.Slots)
            slot.UserIds = slot.UserIds.Select(userId => userId == redundant ? replacement : userId).ToList();
        Assert.IsTrue(draft.Slots.All(slot => slot.UserIds.Count > 0));
        Assert.AreEqual(HttpStatusCode.OK, (await Save("SaveSchedule", draft)).StatusCode);
        var details = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/ScheduleDetails/{id}"));
        Assert.Contains($"data-user-id=\"{redundant}\"", details);
        Assert.Contains("0 shifts / 0 hours", details);
        Assert.DoesNotContain("class=\"ac-gantt-cell\"", details);
        Assert.Contains("id=\"oncall-shift-rows\"", details);
        Assert.Contains("id=\"oncall-add-shift\"", details);
        Assert.Contains("id=\"oncall-reset-schedule\"", details);
        Assert.Contains("id=\"oncall-undo-schedule\"", details);
        Assert.Contains("id=\"oncall-redo-schedule\"", details);
        Assert.Contains("id=\"oncall-handover-lane\"", details);
        Assert.Contains("id=\"oncall-shift-dialog\"", details);
        Assert.IsTrue(details.IndexOf("id=\"oncall-gantt\"", StringComparison.Ordinal) < details.IndexOf("id=\"oncall-shift-rows\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TeamWithoutTemplateCanReadEmptyWeekAndSaveManualScheduleWithDefaultRest()
    {
        await LoginAsAdmin();
        var id = await SeedGroup(false);
        var draft = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}"))!;
        var metadata = await Http.GetFromJsonAsync<System.Text.Json.JsonElement>($"/OnCall/GetSchedule?id={id}");
        Assert.AreEqual(id, draft.GroupId);
        Assert.AreEqual(0, draft.Slots.Count);
        Assert.IsFalse(metadata.GetProperty("saved").GetBoolean());
        Assert.IsFalse(metadata.GetProperty("canGenerate").GetBoolean());
        Assert.AreEqual(16, metadata.GetProperty("restHours").GetInt32());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Save("GenerateSchedule", draft)).StatusCode);
        var details = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/ScheduleDetails/{id}"));
        Assert.Contains("id=\"oncall-schedule\"", details);
        Assert.Contains("id=\"oncall-editor\"", details);
        Assert.DoesNotContain("id=\"oncall-generate\"", details);
        var users = metadata.GetProperty("members").EnumerateArray().Select(user => user.GetProperty("id").GetString()!).Take(3).ToArray();
        draft.Slots = [new() { StartsAtUtc = draft.StartUtc.AddMinutes(17), EndsAtUtc = draft.StartUtc.AddDays(3), UserIds = [users[0], users[1]] }];
        for (var index = 0; index < draft.Slots.Count; index++)
            draft.Slots[index].UserIds = [users[index % 3]];
        Assert.AreEqual(HttpStatusCode.OK, (await Save("SaveSchedule", draft)).StatusCode);
        metadata = await Http.GetFromJsonAsync<System.Text.Json.JsonElement>($"/OnCall/GetSchedule?id={id}");
        Assert.IsTrue(metadata.GetProperty("saved").GetBoolean());
        Assert.AreEqual(16, metadata.GetProperty("restHours").GetInt32());
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.IsNull((await db.OnCallGroups.SingleAsync(group => group.Id == id)).RuleId);
        Assert.AreEqual(1, await db.OnCallShifts.CountAsync(shift => shift.GroupId == id));
        Assert.IsTrue(await db.OnCallShifts.Where(shift => shift.GroupId == id).AllAsync(shift => shift.RestHours == 16));
    }

    [TestMethod]
    public async Task MembershipRemovalPreservesDepartedAssignmentsAndShowsWarnings()
    {
        await LoginAsAdmin();
        var id = await SeedGroup();
        var future = DateTime.UtcNow.Date.AddDays(7);
        var scheduleUrl = $"/OnCall/GetSchedule?id={id}&startUtc={Uri.EscapeDataString(future.ToString("O"))}";
        var draft = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>(scheduleUrl))!;
        Assert.AreEqual(HttpStatusCode.OK, (await Save("GenerateSchedule", draft)).StatusCode);
        var saved = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>(scheduleUrl))!;
        string departed;
        DateTime editedAt;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.Include(item => item.Members).SingleAsync(item => item.Id == id);
            var ordered = group.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray();
            departed = ordered[1];
            var update = new Dictionary<string, string>
            {
                ["id"] = id.ToString(), ["GroupInput.Name"] = group.Name, ["GroupInput.TimeZoneId"] = group.TimeZoneId,
                ["GroupInput.AnchorUtc"] = group.AnchorUtc.ToString("O"), ["GroupInput.RuleId"] = group.RuleId.ToString()!,
                ["GroupInput.Version"] = group.ScheduleVersion.ToString()
            };
            var retained = ordered.Where(userId => userId != departed).ToArray();
            for (var index = 0; index < retained.Length; index++) update[$"GroupInput.UserIds[{index}]"] = retained[index];
            editedAt = DateTime.UtcNow;
            AssertRedirect(await PostForm("/OnCall/UpdateGroup", update, $"/OnCall/GroupEdit/{id}"), $"/OnCall/GroupDetails/{id}");
        }
        Assert.AreEqual(HttpStatusCode.Conflict, (await Save("SaveSchedule", saved)).StatusCode);
        var metadata = await Http.GetFromJsonAsync<System.Text.Json.JsonElement>(scheduleUrl);
        var current = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>(scheduleUrl))!;
        Assert.AreNotEqual(saved.Version, current.Version);
        Assert.AreEqual(21, current.Slots.Count);
        Assert.IsTrue(current.Slots.Any(slot => slot.UserIds.Contains(departed)));
        Assert.IsTrue(metadata.GetProperty("warnings").EnumerateArray().Any(warning => warning.GetString()!.Contains("departed")));
        Assert.IsTrue(metadata.GetProperty("members").EnumerateArray()
            .Single(member => member.GetProperty("id").GetString() == departed).GetProperty("departed").GetBoolean());
        var details = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/ScheduleDetails/{id}?startUtc={Uri.EscapeDataString(future.ToString("O"))}"));
        Assert.Contains("Departed", details);
        Assert.Contains("id=\"oncall-schedule\"", details);
        Assert.Contains("id=\"oncall-gantt\"", details);
        Assert.DoesNotContain("class=\"ac-gantt-cell\"", details);
        Assert.Contains("id=\"oncall-shift-rows\"", details);
        using var checkScope = Server!.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var shifts = await checkDb.OnCallShifts.Include(shift => shift.Members).Where(shift => shift.GroupId == id).ToListAsync();
        Assert.AreEqual(21, shifts.Count);
        Assert.IsTrue(shifts.Where(shift => shift.StartsAtUtc > editedAt).Any(shift => shift.Members.Any(member => member.UserId == departed)));
        CollectionAssert.AreEqual(saved.Slots.SelectMany(slot => slot.UserIds).ToArray(),
            shifts.OrderBy(shift => shift.StartsAtUtc).SelectMany(shift => shift.Members.Select(member => member.UserId)).ToArray());
        Assert.IsTrue(shifts.All(shift => shift.RestHours == 16));
    }

    [TestMethod]
    public async Task FlexibleApiRoundTripsCrossWeekEmptyAndOverlappingShiftsWithoutLegacyEditor()
    {
        await LoginAsAdmin();
        var id = await SeedGroup();
        var start = DateTime.UtcNow.Date;
        var url = $"/OnCall/GetSchedule?id={id}&startUtc={Uri.EscapeDataString(start.ToString("O"))}";
        var draft = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>(url))!;
        var user = draft.Slots.SelectMany(slot => slot.UserIds).First();
        draft.Slots = [new() { StartsAtUtc = start.AddDays(-2).AddMinutes(17), EndsAtUtc = start.AddDays(2), UserIds = [user] },
            new() { StartsAtUtc = start.AddMinutes(11), EndsAtUtc = start.AddMinutes(48) },
            new() { StartsAtUtc = start.AddMinutes(11), EndsAtUtc = start.AddHours(12), UserIds = [user] }];
        var response = await Save("SaveSchedule", draft);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var saved = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>(url))!;
        Assert.AreEqual(3, saved.Slots.Count);
        Assert.IsTrue(saved.Slots.All(slot => slot.Id.HasValue && slot.EndsAtUtc.HasValue));
        Assert.IsTrue(saved.Slots.Any(slot => slot.UserIds.Count == 0 && slot.EndsAtUtc - slot.StartsAtUtc == TimeSpan.FromMinutes(37)));
        Assert.IsTrue(saved.Slots.Any(slot => slot.StartsAtUtc < start));
        Assert.AreEqual(HttpStatusCode.OK, (await Save("SaveSchedule", saved)).StatusCode);
        var page = await Http.GetStringAsync($"/OnCall/ScheduleDetails/{id}?startUtc={Uri.EscapeDataString(start.ToString("O"))}");
        Assert.DoesNotContain("Flexible shifts are read-only", WebUtility.HtmlDecode(page));
        Assert.Contains("id=\"oncall-schedule\"", page);
        Assert.Contains("id=\"oncall-editor\"", page);
        Assert.Contains("id=\"oncall-gantt\"", page);
        using var scope = Server!.Services.CreateScope();
        Assert.AreEqual(3, await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallShifts.CountAsync(shift => shift.GroupId == id));
    }

    [TestMethod]
    public async Task FormsRoundTripWeeklyTemplateAndOptionalTeamSettingsIncludingFallbackOutsideTeam()
    {
        await LoginAsAdmin();
        var id = await SeedGroup();
        Guid ruleId;
        string fallback;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            ruleId = (await db.OnCallGroups.SingleAsync(group => group.Id == id)).RuleId!.Value;
            fallback = await db.Users.Where(user => !db.OnCallGroupMembers.Any(member => member.GroupId == id && member.UserId == user.Id))
                .Select(user => user.Id).FirstAsync();
        }
        AssertRedirect(await PostForm("/OnCall/SaveRule", new()
        {
            ["RuleInput.Id"] = ruleId.ToString(), ["RuleInput.Name"] = "Weekly template", ["RuleInput.SplitMode"] = "weekly",
            ["RuleInput.StaffPerShift"] = "2", ["RuleInput.SwapAlternateRounds"] = "true",
            ["RuleInput.HandoverPoints[0].DayOfWeek"] = "1", ["RuleInput.HandoverPoints[0].MinuteOfDay"] = "1320",
            ["RuleInput.HandoverPoints[1].DayOfWeek"] = "5", ["RuleInput.HandoverPoints[1].MinuteOfDay"] = "1320"
        }, $"/OnCall/Edit/{ruleId}"), $"/OnCall/Details/{ruleId}");
        var editTemplate = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/Edit/{ruleId}"));
        var existingPoints = editTemplate[..editTemplate.IndexOf("<template", StringComparison.Ordinal)];
        Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(existingPoints, "value=\"22:00\"").Count);
        var weeklyOption = System.Text.RegularExpressions.Regex.Match(editTemplate, "<option[^>]*value=\"weekly\"[^>]*>").Value;
        Assert.Contains("selected=\"selected\"", weeklyOption);
        Dictionary<string, string> update;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.Include(item => item.Members).SingleAsync(item => item.Id == id);
            update = new()
            {
                ["id"] = id.ToString(), ["GroupInput.Name"] = group.Name, ["GroupInput.TimeZoneId"] = "Asia/Shanghai",
                ["GroupInput.AnchorUtc"] = group.AnchorUtc.ToString("O"), ["GroupInput.Version"] = group.ScheduleVersion.ToString(),
                ["GroupInput.RuleId"] = ruleId.ToString(), ["GroupInput.WeekStartsOn"] = "5", ["GroupInput.MinimumRestHours"] = "1.5",
                ["GroupInput.MaximumContinuousHours"] = "96", ["GroupInput.MinimumShiftStaff"] = "2", ["GroupInput.AutoExtend"] = "true",
                ["GroupInput.FallbackSelectionSubmitted"] = "true", ["GroupInput.FallbackUserIds[0]"] = fallback
            };
            foreach (var (member, index) in group.Members.OrderBy(member => member.RotationOrder).Select((member, index) => (member, index)))
                update[$"GroupInput.UserIds[{index}]"] = member.UserId;
        }
        AssertRedirect(await PostForm("/OnCall/UpdateGroup", update, $"/OnCall/GroupEdit/{id}"), $"/OnCall/GroupDetails/{id}");
        var editTeam = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/GroupEdit/{id}"));
        Assert.Contains($"name=\"GroupInput.FallbackUserIds\" value=\"{fallback}\"", editTeam);
        Assert.Contains("value=\"1.5\"", editTeam);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.Include(item => item.FallbackRecipients).SingleAsync(item => item.Id == id);
            Assert.IsTrue(group.AutoExtend);
            Assert.AreEqual(DayOfWeek.Friday, group.WeekStartsOn);
            Assert.AreEqual(fallback, group.FallbackRecipients.Single().UserId);
            var rule = await db.OnCallRules.Include(item => item.HandoverPoints).SingleAsync(item => item.Id == ruleId);
            Assert.AreEqual(2, rule.HandoverPoints.Count);
            Assert.AreEqual(2, rule.StaffPerShift);
            Assert.IsTrue(rule.SwapAlternateRounds);
        }
        using var culture = await Http.GetAsync("/Culture/Set?culture=zh-CN&returnUrl=/OnCall");
        Assert.AreEqual(HttpStatusCode.Found, culture.StatusCode);
        var chinese = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/Edit/{ruleId}"));
        Assert.Contains("切分方式", chinese);
        Assert.Contains("每周交接点", chinese);
    }

    [TestMethod]
    public async Task LocalTimeEditorPreviewsWarningsWithoutWritesAndGenerationRequiresCurrentConfirmation()
    {
        await LoginAsAdmin();
        var id = await SeedGroup();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.SingleAsync(item => item.Id == id);
            group.TimeZoneId = "Asia/Shanghai";
            group.MinimumShiftStaff = 2;
            group.MaximumContinuousHours = 24;
            await db.SaveChangesAsync();
        }
        var draft = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}"))!;
        var user = draft.Slots[0].UserIds[0];
        draft.StartLocal = "2026-11-09T00:00";
        draft.EndLocal = "2026-11-16T00:00";
        draft.Slots = [new() { StartLocal = "2026-11-09T22:00", EndLocal = "2026-11-13T22:00", UserIds = [user] },
            new() { StartLocal = "2026-11-13T21:17", EndLocal = "2026-11-16T22:00" }];
        var preview = await Save("PreviewSchedule", draft);
        Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode);
        var data = await preview.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.IsTrue(data.GetProperty("warnings").GetArrayLength() >= 4);
        Assert.IsTrue(data.GetProperty("timelineWarnings").GetArrayLength() >= 4);
        Assert.AreEqual(new DateTime(2026, 11, 9, 14, 0, 0, DateTimeKind.Utc), data.GetProperty("slots")[0].GetProperty("startsAtUtc").GetDateTime());
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual(0, await db.OnCallShifts.CountAsync(shift => shift.GroupId == id));
            Assert.AreEqual(draft.Version, (await db.OnCallGroups.SingleAsync(group => group.Id == id)).ScheduleVersion);
        }
        Assert.AreEqual(HttpStatusCode.OK, (await Save("SaveSchedule", draft)).StatusCode);
        var saved = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}&startLocal=2026-11-09T00:00&endLocal=2026-11-16T00:00"))!;
        Assert.AreEqual(2, saved.Slots.Count);
        Assert.IsTrue(saved.Slots.Any(slot => slot.EndsAtUtc > saved.EndUtc));
        var before = System.Text.Json.JsonSerializer.Serialize(saved.Slots);
        var generated = await Save("PreviewSchedule?generate=true", saved);
        Assert.AreEqual(HttpStatusCode.OK, generated.StatusCode);
        var generatedData = await generated.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.AreEqual(2, generatedData.GetProperty("replacedSlots").GetArrayLength());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Save("GenerateSchedule", saved)).StatusCode);
        Assert.AreEqual(before, System.Text.Json.JsonSerializer.Serialize((await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}&startLocal=2026-11-09T00:00&endLocal=2026-11-16T00:00"))!.Slots));
        saved.ConfirmReplace = true;
        Assert.AreEqual(HttpStatusCode.OK, (await Save("GenerateSchedule", saved)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, (await Save("GenerateSchedule", saved)).StatusCode);
    }

    [TestMethod]
    public async Task LocalEditorRejectsSkippedDstTimesAndUsesStandardTimeForRepeatedTimes()
    {
        await LoginAsAdmin();
        var id = await SeedGroup(false);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            (await db.OnCallGroups.SingleAsync(group => group.Id == id)).TimeZoneId = "America/New_York";
            await db.SaveChangesAsync();
        }
        var draft = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}"))!;
        draft.StartLocal = "2026-03-08T00:00"; draft.EndLocal = "2026-03-09T00:00";
        draft.Slots = [new() { StartLocal = "2026-03-08T02:30", EndLocal = "2026-03-08T04:00" }];
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Save("SaveSchedule", draft)).StatusCode);
        draft.StartLocal = "2026-11-01T00:00"; draft.EndLocal = "2026-11-02T00:00";
        draft.Slots[0].StartLocal = "2026-11-01T01:30"; draft.Slots[0].EndLocal = "2026-11-01T02:17";
        Assert.AreEqual(HttpStatusCode.OK, (await Save("SaveSchedule", draft)).StatusCode);
        var saved = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}&startLocal=2026-11-01T00:00&endLocal=2026-11-02T00:00"))!;
        Assert.AreEqual(new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc), saved.Slots[0].StartsAtUtc);
        Assert.AreEqual(TimeSpan.FromMinutes(47), saved.Slots[0].EndsAtUtc - saved.Slots[0].StartsAtUtc);
        var page = await Http.GetStringAsync($"/OnCall/ScheduleDetails/{id}?startLocal=2026-11-01T00:00&endLocal=2026-11-02T00:00");
        Assert.Contains("data-duration=\"25\"", page);
    }

    [TestMethod]
    public async Task NonAdministratorCanViewButCannotReadOrModifyAdministratorApi()
    {
        var id = await SeedGroup();
        await RegisterAndLoginAsync();
        var details = await Http.GetStringAsync($"/OnCall/ScheduleDetails/{id}");
        Assert.DoesNotContain("id=\"oncall-schedule\"", details);
        Assert.DoesNotContain("class=\"ac-gantt-cell\"", details);
        Assert.DoesNotContain("ac-gantt-week\"", details);
        Assert.DoesNotContain("data-schedule-mode=", details);
        var endpoints = new[] { "GetSchedule", "GenerateSchedule", "SaveSchedule", "PreviewSchedule" };
        foreach (var endpoint in endpoints)
        {
            var response = endpoint == "GetSchedule" ? await Http.GetAsync($"/OnCall/{endpoint}?id={id}")
                : await Save(endpoint, new SaveOnCallScheduleRequest { GroupId = id }, "/Manage/ChangePassword");
            Assert.IsTrue(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Found);
            if (response.StatusCode == HttpStatusCode.Found)
                Assert.Contains("/Error/Code403", response.Headers.Location!.OriginalString);
        }
        using var scope = Server!.Services.CreateScope();
        Assert.AreEqual(0, await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallShifts.CountAsync(shift => shift.GroupId == id));
    }
}

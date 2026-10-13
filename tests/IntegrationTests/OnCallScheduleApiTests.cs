using System.Net;
using System.Net.Http.Json;
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
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/OnCall/{action}") { Content = JsonContent.Create(payload) };
        message.Headers.Add("RequestVerificationToken", await GetAntiCsrfToken(tokenPath ?? $"/OnCall/ScheduleDetails/{payload.GroupId}"));
        return await Http.SendAsync(message);
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
        Assert.AreEqual(28, suggestion.Slots.Sum(slot => slot.UserIds.Count));
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
        var removed = saved.Slots[0].UserIds[1];
        saved.Slots[0].UserIds.Remove(removed);
        Assert.AreEqual(HttpStatusCode.OK, (await Save("SaveSchedule", saved)).StatusCode);
        var edited = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}"))!;
        Assert.DoesNotContain(removed, edited.Slots[0].UserIds);
        edited.Slots[0].UserIds.Clear();
        var empty = await Save("SaveSchedule", edited);
        Assert.AreEqual(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("空班", System.Text.Json.JsonDocument.Parse(await empty.Content.ReadAsStringAsync()).RootElement.GetProperty("errors")[0].GetString()!);
        using var scope = Server!.Services.CreateScope();
        Assert.AreEqual(27, await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallShiftMembers.CountAsync(member => member.Shift.GroupId == id));
        var nextStart = edited.StartUtc.AddDays(7);
        var next = await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}&startUtc={Uri.EscapeDataString(nextStart.ToString("O"))}");
        Assert.AreEqual(nextStart, next!.StartUtc);
        Assert.AreEqual(21, next.Slots.Count);
    }

    [TestMethod]
    public async Task UnderstaffedManualScheduleReturnsWarningsAndSnapshotMetadataWhileEmptySlotsFail()
    {
        await LoginAsAdmin();
        var id = await SeedGroup();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var group = await db.OnCallGroups.Include(item => item.Rule).SingleAsync(item => item.Id == id);
            group.Rule!.MinimumStaff = 2;
            group.Rule.TargetStaff = 2;
            await db.SaveChangesAsync();
        }
        Guid ruleId;
        using (var scope = Server!.Services.CreateScope())
            ruleId = (await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallGroups.SingleAsync(group => group.Id == id)).RuleId!.Value;
        var editPage = await Http.GetStringAsync($"/OnCall/Edit/{ruleId}");
        foreach (var field in new[] { "MinimumStaff", "TargetStaff" })
        {
            var input = System.Text.RegularExpressions.Regex.Match(editPage, $"<input[^>]*name=\"RuleInput.{field}\"[^>]*>").Value;
            Assert.Contains("value=\"2\"", input);
        }
        var metadata = await Http.GetFromJsonAsync<System.Text.Json.JsonElement>($"/OnCall/GetSchedule?id={id}");
        Assert.AreEqual(2, metadata.GetProperty("minimumStaff").GetInt32());
        Assert.AreEqual(2, metadata.GetProperty("targetStaff").GetInt32());
        Assert.IsFalse(metadata.GetProperty("canGenerate").GetBoolean());
        var draft = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}"))!;
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Save("GenerateSchedule", draft)).StatusCode);
        var users = metadata.GetProperty("members").EnumerateArray().Select(user => user.GetProperty("id").GetString()!).Take(3).ToArray();
        for (var index = 0; index < draft.Slots.Count; index++)
            draft.Slots[index].UserIds = [users[index % 3]];
        var response = await Save("SaveSchedule", draft);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.AreEqual(21, result.GetProperty("warnings").GetArrayLength());
        Assert.IsFalse(result.TryGetProperty("errors", out _));
        metadata = await Http.GetFromJsonAsync<System.Text.Json.JsonElement>($"/OnCall/GetSchedule?id={id}");
        Assert.IsTrue(metadata.GetProperty("saved").GetBoolean());
        Assert.AreEqual(21, metadata.GetProperty("warnings").GetArrayLength());
        CollectionAssert.AreEqual(result.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()).ToArray(),
            metadata.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()).ToArray());
        var details = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/ScheduleDetails/{id}"));
        Assert.Contains("data-minimum-staff=\"2\"", details);

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
        Assert.AreEqual(21, metadata.GetProperty("warnings").GetArrayLength());
        draft = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}"))!;
        var version = draft.Version;
        draft.Slots[0].UserIds.Clear();
        response = await Save("SaveSchedule", draft);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.IsTrue(result.GetProperty("errors").EnumerateArray().Any(error => error.GetString()!.Contains("空班")));
        using var checkScope = Server!.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(version, (await checkDb.OnCallGroups.SingleAsync(group => group.Id == id)).ScheduleVersion);
        Assert.AreEqual(21, await checkDb.OnCallShiftMembers.CountAsync(member => member.Shift.GroupId == id));
        Assert.IsTrue(await checkDb.OnCallShifts.Where(shift => shift.GroupId == id).AllAsync(shift => shift.MinimumStaff == 2));
    }

    [TestMethod]
    public async Task GanttRetainsUnassignedEngineersAsDropTargets()
    {
        await LoginAsAdmin();
        var id = await SeedGroup();
        var draft = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}"))!;
        var redundant = draft.Slots[0].UserIds[1];
        foreach (var slot in draft.Slots) slot.UserIds.Remove(redundant);
        Assert.IsTrue(draft.Slots.All(slot => slot.UserIds.Count > 0));
        Assert.AreEqual(HttpStatusCode.OK, (await Save("SaveSchedule", draft)).StatusCode);
        var details = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/ScheduleDetails/{id}"));
        Assert.Contains($"data-user-id=\"{redundant}\"", details);
        Assert.Contains("0 班 / 0 小时", details);
        Assert.AreEqual(84, System.Text.RegularExpressions.Regex.Matches(details, "class=\"ac-gantt-cell\"").Count);
        Assert.Contains("撤销上一步", details);
        Assert.Contains("data-schedule-mode=\"add\"", details);
        Assert.Contains("data-schedule-mode=\"remove\"", details);
    }

    [TestMethod]
    public async Task TeamWithoutTemplateCanReadEmptyWeekAndSaveManualScheduleWithDefaultRest()
    {
        await LoginAsAdmin();
        var id = await SeedGroup(false);
        var draft = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}"))!;
        var metadata = await Http.GetFromJsonAsync<System.Text.Json.JsonElement>($"/OnCall/GetSchedule?id={id}");
        Assert.AreEqual(id, draft.GroupId);
        Assert.AreEqual(21, draft.Slots.Count);
        Assert.IsTrue(draft.Slots.All(slot => slot.UserIds.Count == 0));
        Assert.IsFalse(metadata.GetProperty("saved").GetBoolean());
        Assert.IsFalse(metadata.GetProperty("canGenerate").GetBoolean());
        Assert.AreEqual(16, metadata.GetProperty("restHours").GetInt32());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Save("GenerateSchedule", draft)).StatusCode);
        var details = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/ScheduleDetails/{id}"));
        Assert.Contains("id=\"oncall-schedule\"", details);
        Assert.Contains("id=\"oncall-editor\"", details);
        Assert.DoesNotContain("id=\"oncall-generate\"", details);
        var users = metadata.GetProperty("members").EnumerateArray().Select(user => user.GetProperty("id").GetString()!).Take(3).ToArray();
        for (var index = 0; index < draft.Slots.Count; index++)
            draft.Slots[index].UserIds = [users[index % 3]];
        Assert.AreEqual(HttpStatusCode.OK, (await Save("SaveSchedule", draft)).StatusCode);
        metadata = await Http.GetFromJsonAsync<System.Text.Json.JsonElement>($"/OnCall/GetSchedule?id={id}");
        Assert.IsTrue(metadata.GetProperty("saved").GetBoolean());
        Assert.AreEqual(16, metadata.GetProperty("restHours").GetInt32());
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.IsNull((await db.OnCallGroups.SingleAsync(group => group.Id == id)).RuleId);
        Assert.AreEqual(21, await db.OnCallShifts.CountAsync(shift => shift.GroupId == id));
        Assert.IsTrue(await db.OnCallShifts.Where(shift => shift.GroupId == id).AllAsync(shift => shift.RestHours == 16));
    }

    [TestMethod]
    public async Task MembershipRemovalShowsDepartedHistoryAndEmptyFutureSlotsWithoutRegenerating()
    {
        await LoginAsAdmin();
        var id = await SeedGroup();
        var draft = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}"))!;
        Assert.AreEqual(HttpStatusCode.OK, (await Save("GenerateSchedule", draft)).StatusCode);
        var saved = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}"))!;
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
        var metadata = await Http.GetFromJsonAsync<System.Text.Json.JsonElement>($"/OnCall/GetSchedule?id={id}");
        var current = (await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}"))!;
        Assert.AreNotEqual(saved.Version, current.Version);
        Assert.AreEqual(21, current.Slots.Count);
        Assert.IsTrue(current.Slots.Any(slot => slot.UserIds.Count == 0));
        Assert.Contains("空班", metadata.GetProperty("generationError").GetString()!);
        Assert.IsTrue(metadata.GetProperty("members").EnumerateArray()
            .Single(member => member.GetProperty("id").GetString() == departed).GetProperty("departed").GetBoolean());
        var details = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/ScheduleDetails/{id}"));
        Assert.Contains("已退出", details);
        Assert.Contains("id=\"oncall-schedule\"", details);
        Assert.Contains("id=\"oncall-gantt\"", details);
        Assert.AreEqual(63, System.Text.RegularExpressions.Regex.Matches(details, "class=\"ac-gantt-cell\"").Count);
        using var checkScope = Server!.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var shifts = await checkDb.OnCallShifts.Include(shift => shift.Members).Where(shift => shift.GroupId == id).ToListAsync();
        Assert.AreEqual(21, shifts.Count);
        Assert.IsFalse(shifts.Where(shift => shift.StartsAtUtc > editedAt).Any(shift => shift.Members.Any(member => member.UserId == departed)));
        Assert.IsTrue(shifts.Where(shift => shift.StartsAtUtc <= editedAt).Any(shift => shift.Members.Any(member => member.UserId == departed)));
        Assert.IsTrue(shifts.All(shift => shift.RestHours == 16));
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
        var endpoints = new[] { "GetSchedule", "GenerateSchedule", "SaveSchedule" };
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

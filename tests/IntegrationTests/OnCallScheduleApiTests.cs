using System.Net;
using System.Net.Http.Json;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class OnCallScheduleApiTests : TestBase
{
    private async Task<Guid> SeedRule()
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var users = Enumerable.Range(0, 4).Select(index => new User { UserName = $"api-staff-{index}", DisplayName = $"Staff {index}" }).ToArray();
        db.Users.AddRange(users);
        var rule = new OnCallRule
        {
            Group = new OnCallGroup { Name = "API team" }, Name = "Three shifts", AnchorUtc = DateTime.UtcNow.Date.AddDays(-1),
            Members = users.Select((user, index) => new OnCallRuleMember { UserId = user.Id, RotationOrder = index }).ToList()
        };
        db.OnCallRules.Add(rule);
        await db.SaveChangesAsync();
        return rule.Id;
    }

    private async Task<HttpResponseMessage> Save(string action, SaveOnCallScheduleRequest payload, string? tokenPath = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/OnCall/{action}") { Content = JsonContent.Create(payload) };
        message.Headers.Add("RequestVerificationToken", await GetAntiCsrfToken(tokenPath ?? $"/OnCall/Details/{payload.RuleId}"));
        return await Http.SendAsync(message);
    }

    [TestMethod]
    public async Task AdminCanGenerateReadAndEditScheduleWithValidationAndVersionChecks()
    {
        await LoginAsAdmin();
        var id = await SeedRule();
        var suggestion = await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}");
        Assert.IsNotNull(suggestion);
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
        Assert.AreEqual(27, await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallShiftMembers.CountAsync(member => member.Shift.RuleId == id));
        var nextStart = edited.StartUtc.AddDays(7);
        var next = await Http.GetFromJsonAsync<SaveOnCallScheduleRequest>($"/OnCall/GetSchedule?id={id}&startUtc={Uri.EscapeDataString(nextStart.ToString("O"))}");
        Assert.AreEqual(nextStart, next!.StartUtc);
        Assert.AreEqual(21, next.Slots.Count);
    }

    [TestMethod]
    public async Task NonAdministratorCanViewButCannotReadOrModifyAdministratorApi()
    {
        var id = await SeedRule();
        await RegisterAndLoginAsync();
        var details = await Http.GetStringAsync($"/OnCall/Details/{id}");
        Assert.DoesNotContain("id=\"oncall-schedule\"", details);
        var endpoints = new[] { "GetSchedule", "GenerateSchedule", "SaveSchedule" };
        foreach (var endpoint in endpoints)
        {
            var response = endpoint == "GetSchedule" ? await Http.GetAsync($"/OnCall/{endpoint}?id={id}")
                : await Save(endpoint, new SaveOnCallScheduleRequest { RuleId = id }, "/Manage/ChangePassword");
            Assert.IsTrue(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Found);
            if (response.StatusCode == HttpStatusCode.Found)
                Assert.Contains("/Error/Code403", response.Headers.Location!.OriginalString);
        }
        using var scope = Server!.Services.CreateScope();
        Assert.AreEqual(0, await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallShifts.CountAsync(shift => shift.RuleId == id));
    }
}

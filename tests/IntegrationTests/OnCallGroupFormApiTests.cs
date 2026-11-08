using System.Net;
using System.Text.Json;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class OnCallGroupFormApiTests : TestBase
{
    private readonly List<Guid> groupIds = [];
    private readonly List<Guid> ruleIds = [];
    private readonly List<string> userIds = [];

    [TestCleanup]
    public override async Task CleanServer()
    {
        if (Server != null)
        {
            using var scope = Server.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.OnCallShifts.RemoveRange(await db.OnCallShifts.Where(shift => groupIds.Contains(shift.GroupId)).ToListAsync());
            db.OnCallGroups.RemoveRange(await db.OnCallGroups.Where(group => groupIds.Contains(group.Id)).ToListAsync());
            db.RemoveRange(await db.Set<OnCallHandoverPoint>().Where(point => ruleIds.Contains(point.RuleId)).ToListAsync());
            db.OnCallRules.RemoveRange(await db.OnCallRules.Where(rule => ruleIds.Contains(rule.Id)).ToListAsync());
            db.Users.RemoveRange(await db.Users.Where(user => userIds.Contains(user.Id)).ToListAsync());
            await db.SaveChangesAsync();
        }
        await base.CleanServer();
    }

    private async Task<HttpResponseMessage> Preview(object? request, string tokenPath = "/OnCall/GroupCreate")
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/OnCall/GroupFormPreview");
        message.Content = JsonContent.Create(request);
        message.Headers.Add("RequestVerificationToken", await GetAntiCsrfToken(tokenPath));
        return await Http.SendAsync(message);
    }

    [TestMethod]
    public async Task PreviewReturnsCamelCaseFourSegmentsAndNeverPersistsDraftData()
    {
        await LoginAsAdmin();
        Guid ruleId;
        string[] ids;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var users = new[] { "Preview First", "Preview Second", "Preview Third" }
                .Select(name => new User { DisplayName = name, UserName = Guid.NewGuid().ToString() }).ToArray();
            db.Users.AddRange(users);
            ids = users.Select(user => user.Id).ToArray();
            userIds.AddRange(ids);
            var rule = new OnCallRule
            {
                Name = "Weekly", HandoverPoints =
                [new() { DayOfWeek = DayOfWeek.Monday, MinuteOfDay = 1320 }, new() { DayOfWeek = DayOfWeek.Friday, MinuteOfDay = 1320 }]
            };
            db.OnCallRules.Add(rule);
            await db.SaveChangesAsync();
            ruleId = rule.Id;
            ruleIds.Add(ruleId);
        }
        var request = new OnCallGroupFormPreviewRequest
        {
            RuleId = ruleId, TimeZoneId = "Asia/Shanghai", AnchorLocal = "2026-11-09T22:00", UserIds = [ids[2], ids[0], ids[1]]
        };
        var response = await Preview(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("2026-11-09T22:00", json.GetProperty("anchorLocal").GetString());
        Assert.IsFalse(json.GetProperty("anchorLocked").GetBoolean());
        Assert.AreEqual(2, json.GetProperty("suggestions").GetArrayLength());
        Assert.AreEqual(json.GetProperty("suggestedAnchorLocal").GetString(), json.GetProperty("suggestions")[0].GetProperty("anchorLocal").GetString());
        Assert.AreEqual(0, json.GetProperty("warnings").GetArrayLength());
        Assert.AreEqual(0, json.GetProperty("errors").GetArrayLength());
        var slots = json.GetProperty("slots");
        Assert.AreEqual(4, slots.GetArrayLength());
        Assert.AreEqual(new DateTime(2026, 11, 9, 14, 0, 0, DateTimeKind.Utc), slots[0].GetProperty("startsAtUtc").GetDateTime());
        Assert.AreEqual(new DateTime(2026, 11, 13, 14, 0, 0, DateTimeKind.Utc), slots[0].GetProperty("endsAtUtc").GetDateTime());
        Assert.AreEqual("2026-11-09T22:00", slots[0].GetProperty("startLocal").GetString());
        Assert.AreEqual("2026-11-13T22:00", slots[0].GetProperty("endLocal").GetString());
        CollectionAssert.AreEqual(new[] { ids[2], ids[0], ids[1], ids[2] },
            slots.EnumerateArray().Select(slot => slot.GetProperty("userIds")[0].GetString()).ToArray());
        request.UserIds = [];
        json = await (await Preview(request)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(0, json.GetProperty("slots").GetArrayLength());
        Assert.Contains("Add rotation queue members", json.GetProperty("errors")[0].GetString()!);
        request.RuleId = null;
        request.AnchorLocal = null;
        json = await (await Preview(request)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(0, json.GetProperty("slots").GetArrayLength());
        Assert.AreEqual(0, json.GetProperty("errors").GetArrayLength());
        Assert.IsTrue(DateTime.ParseExact(json.GetProperty("anchorLocal").GetString()!, "yyyy-MM-ddTHH:mm", null).Minute == 0);
        using var checkScope = Server!.Services.CreateScope();
        var check = checkScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(0, await check.OnCallGroups.CountAsync());
        Assert.AreEqual(0, await check.OnCallShifts.CountAsync());
    }

    [TestMethod]
    public async Task PreviewLocksSavedAnchorInNewTimezoneWithoutChangingPersistence()
    {
        await LoginAsAdmin();
        var anchor = new DateTime(2026, 11, 1, 5, 30, 17, DateTimeKind.Utc);
        Guid groupId;
        Guid version;
        Guid ruleId;
        string userId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            userId = (await db.Users.FirstAsync()).Id;
            var group = new OnCallGroup { Name = "Locked", TimeZoneId = "America/New_York", AnchorUtc = anchor,
                Rule = new OnCallRule { Name = "Fixed" } };
            db.OnCallShifts.Add(new OnCallShift { Group = group, StartsAtUtc = anchor, EndsAtUtc = anchor.AddHours(8) });
            await db.SaveChangesAsync();
            groupId = group.Id;
            version = group.ScheduleVersion;
            ruleId = group.RuleId!.Value;
            groupIds.Add(groupId);
            ruleIds.Add(ruleId);
        }
        var request = new OnCallGroupFormPreviewRequest
        {
            GroupId = groupId, RuleId = ruleId, TimeZoneId = "Asia/Shanghai", AnchorLocal = "not-a-date", UserIds = [userId]
        };
        var response = await Preview(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(json.GetProperty("anchorLocked").GetBoolean());
        Assert.AreEqual("2026-11-01T13:30", json.GetProperty("anchorLocal").GetString());
        Assert.AreEqual(anchor.AddHours(8), json.GetProperty("slots")[0].GetProperty("startsAtUtc").GetDateTime());
        Assert.AreEqual(4, json.GetProperty("slots").GetArrayLength());
        request.TimeZoneId = "America/New_York";
        request.AnchorLocal = null;
        json = await (await Preview(request)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("2026-11-01T01:30", json.GetProperty("anchorLocal").GetString());
        Assert.AreEqual(anchor.AddHours(8), json.GetProperty("slots")[0].GetProperty("startsAtUtc").GetDateTime());
        using var checkScope = Server!.Services.CreateScope();
        var check = checkScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var saved = await check.OnCallGroups.SingleAsync();
        Assert.AreEqual(anchor, saved.AnchorUtc);
        Assert.AreEqual("America/New_York", saved.TimeZoneId);
        Assert.AreEqual(version, saved.ScheduleVersion);
        Assert.AreEqual(1, await check.OnCallShifts.CountAsync());
        Assert.AreEqual(0, await check.OnCallGroupMembers.CountAsync());
    }

    [TestMethod]
    public async Task PreviewRejectsMalformedIdsOversizedQueuesAndInvalidLocalTimes()
    {
        await LoginAsAdmin();
        string userId;
        using (var scope = Server!.Services.CreateScope())
            userId = (await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().Users.FirstAsync()).Id;
        object?[] invalid =
        [
            null,
            new { timeZoneId = "UTC", groupId = "not-guid", userIds = new[] { userId } },
            new OnCallGroupFormPreviewRequest { TimeZoneId = "Invalid/Zone" },
            new OnCallGroupFormPreviewRequest { TimeZoneId = "UTC", GroupId = Guid.NewGuid() },
            new OnCallGroupFormPreviewRequest { TimeZoneId = "UTC", RuleId = Guid.NewGuid() },
            new OnCallGroupFormPreviewRequest { TimeZoneId = "UTC", UserIds = null },
            new OnCallGroupFormPreviewRequest { TimeZoneId = "UTC", UserIds = ["missing"] },
            new OnCallGroupFormPreviewRequest { TimeZoneId = "UTC", UserIds = [userId, userId] },
            new OnCallGroupFormPreviewRequest { TimeZoneId = "UTC", UserIds = [" "] },
            new OnCallGroupFormPreviewRequest { TimeZoneId = "UTC", UserIds = Enumerable.Range(0, 201).Select(index => $"staff-{index}").ToList() },
            new OnCallGroupFormPreviewRequest { TimeZoneId = "America/New_York", AnchorLocal = "2026-03-08T02:30" },
            new OnCallGroupFormPreviewRequest { TimeZoneId = "UTC", AnchorLocal = "0001-01-01T00:00" },
            new OnCallGroupFormPreviewRequest { TimeZoneId = "UTC", AnchorLocal = "9999-12-31T23:59" }
        ];
        foreach (var request in invalid)
        {
            var response = await Preview(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.IsTrue(json.GetProperty("errors").GetArrayLength() > 0);
            Assert.AreEqual(0, json.GetProperty("slots").GetArrayLength());
        }
        var overlap = await Preview(new OnCallGroupFormPreviewRequest
        {
            TimeZoneId = "America/New_York", AnchorLocal = "2026-11-01T01:30"
        });
        Assert.AreEqual(HttpStatusCode.OK, overlap.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Http.PostAsJsonAsync("/OnCall/GroupFormPreview", new { timeZoneId = "UTC" })).StatusCode);
    }

    [TestMethod]
    public async Task PreviewIsRestrictedToOnCallManagers()
    {
        var anonymous = await Http.PostAsJsonAsync("/OnCall/GroupFormPreview", new { timeZoneId = "UTC" });
        Assert.IsTrue(anonymous.StatusCode is HttpStatusCode.Found or HttpStatusCode.Unauthorized);
        await RegisterAndLoginAsync();
        var forbidden = await Preview(new OnCallGroupFormPreviewRequest { TimeZoneId = "UTC" }, "/Manage/ChangePassword");
        Assert.IsTrue(forbidden.StatusCode is HttpStatusCode.Found or HttpStatusCode.Forbidden);
    }
}

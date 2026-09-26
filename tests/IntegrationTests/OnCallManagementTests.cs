using System.Net;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class OnCallManagementTests : TestBase
{
    [TestMethod]
    public async Task GroupFormsOfferSearchableTimeZonesAndKeepValidationOnPage()
    {
        await LoginAsAdmin();
        var page = await Http.GetStringAsync("/OnCall/Manage");
        Assert.Contains("name=\"GroupForm.TimeZoneId\"", page);
        Assert.Contains("js-time-zone", page);
        Assert.Contains("select2({", page);
        Assert.Contains("value=\"UTC\"", page);

        using var invalidZone = await PostForm("/OnCall/SaveGroup", new()
        {
            ["GroupForm.Name"] = "Operations",
            ["GroupForm.TimeZoneId"] = "Not/AZone"
        }, "/OnCall/Manage");
        Assert.AreEqual(HttpStatusCode.OK, invalidZone.StatusCode);
        var html = WebUtility.HtmlDecode(await invalidZone.Content.ReadAsStringAsync());
        Assert.Contains("请选择列表中的有效时区", html);
        Assert.Contains("Operations", html);
        Assert.Contains("管理值班", html);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual(0, await db.OnCallGroups.CountAsync());
        }

        using var created = await PostForm("/OnCall/SaveGroup", new()
        {
            ["GroupForm.Name"] = "Operations",
            ["GroupForm.TimeZoneId"] = "UTC"
        }, "/OnCall/Manage");
        Assert.AreEqual(HttpStatusCode.Found, created.StatusCode);
        Guid groupId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            groupId = (await db.OnCallGroups.SingleAsync()).Id;
        }
        var manageUrl = $"/OnCall/Manage/{groupId}";
        Assert.Contains("selected=\"selected\"", await Http.GetStringAsync(manageUrl));

        using var duplicate = await PostForm("/OnCall/SaveGroup", new()
        {
            ["GroupForm.Name"] = "Operations",
            ["GroupForm.TimeZoneId"] = "UTC"
        }, manageUrl);
        Assert.AreEqual(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Contains("值班组名称已存在", WebUtility.HtmlDecode(await duplicate.Content.ReadAsStringAsync()));
    }

    [TestMethod]
    public async Task OnCallCenterShowsIndependentAdminPages()
    {
        await LoginAsAdmin();
        var group = new OnCallGroup { Name = "Operations", TimeZoneId = "UTC" };
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.OnCallGroups.Add(group);
            await db.SaveChangesAsync();
        }
        var overview = await Http.GetStringAsync("/OnCall/Index");
        Assert.Contains("值班中心", overview);
        foreach (var (action, label) in new[]
        {
            ("Manage", "值班组"), ("Schedule", "发布日程"), ("Templates", "轮转模板"),
            ("Members", "成员"), ("Requests", "请假"), ("Debts", "补班债务")
        })
        {
            var page = await Http.GetStringAsync($"/OnCall/{action}/{group.Id}");
            Assert.Contains(label, page);
            Assert.DoesNotContain("id=\"onCallTabs\"", page);
        }
    }

    [TestMethod]
    public async Task TemplateCreationReturnsToItsPageAndMembersShowAssignedTemplate()
    {
        await LoginAsAdmin();
        Guid groupId;
        string userId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            userId = await db.Users.Where(user => user.Email == "admin@default.com").Select(user => user.Id).SingleAsync();
            var group = new OnCallGroup { Name = "Operations", TimeZoneId = "UTC" };
            db.OnCallGroupMembers.Add(new OnCallGroupMember { Group = group, UserId = userId });
            await db.SaveChangesAsync();
            groupId = group.Id;
        }
        var templatesUrl = $"/OnCall/Templates/{groupId}";
        using var saved = await PostForm("/OnCall/SaveTemplate", new()
        {
            ["GroupId"] = groupId.ToString(), ["Name"] = "早班", ["StartMinuteOfDay"] = "0",
            ["DurationMinutes"] = "480", ["UserIds"] = userId
        }, templatesUrl);
        AssertRedirect(saved, templatesUrl);
        var membersPage = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/Members/{groupId}"));
        Assert.Contains("早班", membersPage);
        Assert.DoesNotContain("未命名模板", membersPage);
    }

    [TestMethod]
    public async Task OnCallAdminLinksAreNotShownToRegularUsers()
    {
        await RegisterAndLoginAsync();
        var page = await Http.GetStringAsync("/OnCall/Index");
        Assert.Contains("值班中心", page);
        Assert.DoesNotContain("值班组配置", page);
        using var denied = await Http.GetAsync("/OnCall/Members");
        Assert.AreEqual(HttpStatusCode.Found, denied.StatusCode);
        Assert.Contains("/Error/Code403", denied.Headers.Location!.ToString());
    }

    [TestMethod]
    public async Task IncomingHandoverOutsideSelectedDayIsVisibleOnOverview()
    {
        await LoginAsAdmin();
        var group = new OnCallGroup { Name = "Operations", TimeZoneId = "UTC" };
        var userId = string.Empty;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            userId = await db.Users.Where(user => user.Email == "admin@default.com").Select(user => user.Id).SingleAsync();
            db.OnCallGroups.Add(group);
            db.OnCallHandoverRequests.Add(new OnCallHandoverRequest
            {
                Shift = new OnCallShift
                {
                    Group = group, UserId = userId,
                    StartsAtUtc = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc),
                    EndsAtUtc = new DateTime(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc)
                },
                RequestedByUserId = userId, ReceivingUserId = userId,
                StartsAtUtc = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc),
                EndsAtUtc = new DateTime(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();
        }
        var overview = await Http.GetStringAsync($"/OnCall/Index?groupId={group.Id}&date=2026-09-26");
        Assert.Contains("待我确认的交接", overview);
        Assert.Contains("确认接班", overview);
        Assert.Contains("2026-09-22", overview);
    }

    [TestMethod]
    public async Task ScheduleDefaultsToFirstUnpublishedDay()
    {
        await LoginAsAdmin();
        Guid groupId;
        var end = DateTime.UtcNow.Date.AddDays(6);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var userId = await db.Users.Where(user => user.Email == "admin@default.com").Select(user => user.Id).SingleAsync();
            var group = new OnCallGroup { Name = "Operations", TimeZoneId = "UTC" };
            db.OnCallShifts.Add(new OnCallShift { Group = group, UserId = userId,
                StartsAtUtc = end.AddHours(-8), EndsAtUtc = end });
            await db.SaveChangesAsync();
            groupId = group.Id;
        }
        var page = await Http.GetStringAsync($"/OnCall/Schedule/{groupId}");
        Assert.Contains($"name=\"startDate\" type=\"date\" value=\"{end:yyyy-MM-dd}\"", page);
    }

    [TestMethod]
    public async Task ScheduleSuggestsFollowingDayWhenLastShiftEndsMidday()
    {
        await LoginAsAdmin();
        Guid groupId;
        var end = DateTime.UtcNow.Date.AddDays(6).AddHours(8);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var userId = await db.Users.Where(user => user.Email == "admin@default.com").Select(user => user.Id).SingleAsync();
            var group = new OnCallGroup { Name = "Operations", TimeZoneId = "UTC" };
            db.OnCallShifts.Add(new OnCallShift { Group = group, UserId = userId,
                StartsAtUtc = end.AddHours(-8), EndsAtUtc = end });
            await db.SaveChangesAsync();
            groupId = group.Id;
        }
        var page = await Http.GetStringAsync($"/OnCall/Schedule/{groupId}");
        Assert.Contains($"name=\"startDate\" type=\"date\" value=\"{end.AddDays(1):yyyy-MM-dd}\"", page);
    }

    [TestMethod]
    public async Task ScheduleCanShowPublishedShiftsOutsideDefaultWindow()
    {
        await LoginAsAdmin();
        Guid groupId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var userId = await db.Users.Where(user => user.Email == "admin@default.com").Select(user => user.Id).SingleAsync();
            var group = new OnCallGroup { Name = "Operations", TimeZoneId = "UTC" };
            db.OnCallShifts.Add(new OnCallShift { Group = group, UserId = userId,
                StartsAtUtc = new DateTime(2027, 1, 5, 0, 0, 0, DateTimeKind.Utc),
                EndsAtUtc = new DateTime(2027, 1, 5, 8, 0, 0, DateTimeKind.Utc) });
            await db.SaveChangesAsync();
            groupId = group.Id;
        }
        var page = await Http.GetStringAsync($"/OnCall/Schedule/{groupId}?date=2027-01-05");
        Assert.Contains("01-05 00:00", page);
        Assert.Contains("安排代班", page);
        Assert.Contains("value=\"2027-01-05\"", page);
    }

    [TestMethod]
    public async Task PendingRequestsRemainActionableBeyondRecentScheduleAndTwentyRecords()
    {
        await LoginAsAdmin();
        Guid groupId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var userId = await db.Users.Where(user => user.Email == "admin@default.com").Select(user => user.Id).SingleAsync();
            var group = new OnCallGroup { Name = "Operations", TimeZoneId = "UTC" };
            db.OnCallGroups.Add(group);
            for (var i = 0; i < 21; i++)
                db.OnCallLeaveRequests.Add(new OnCallLeaveRequest
                {
                    Group = group, UserId = userId,
                    StartsAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
                    EndsAtUtc = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc).AddDays(i), Reason = "leave"
                });
            db.OnCallHandoverRequests.Add(new OnCallHandoverRequest
            {
                Shift = new OnCallShift
                {
                    Group = group, UserId = userId,
                    StartsAtUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
                    EndsAtUtc = new DateTime(2026, 8, 1, 8, 0, 0, DateTimeKind.Utc)
                },
                RequestedByUserId = userId, ReceivingUserId = userId,
                StartsAtUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
                EndsAtUtc = new DateTime(2026, 8, 1, 8, 0, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();
            groupId = group.Id;
        }
        var page = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/Requests/{groupId}"));
        Assert.Contains("21", page);
        Assert.Contains("确认接班", page);
        Assert.Contains("08-01", page);
    }

    [TestMethod]
    public async Task PublishingFailureReturnsToManagementWithVisibleReason()
    {
        await LoginAsAdmin();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.OnCallGroups.Add(new OnCallGroup { Name = $"Operations {Guid.NewGuid():N}", TimeZoneId = "UTC" });
            await db.SaveChangesAsync();
        }
        Guid groupId;
        using (var scope = Server!.Services.CreateScope())
            groupId = await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().OnCallGroups
                .Where(group => group.Name.StartsWith("Operations ")).OrderByDescending(group => group.CreatedAtUtc)
                .Select(group => group.Id).FirstAsync();
        var manageUrl = $"/OnCall/Manage/{groupId}";
        using var response = await PostForm("/OnCall/Publish", new()
        {
            ["GroupId"] = groupId.ToString(),
            ["StartDate"] = "2026-09-23",
            ["EndDate"] = "2026-09-23"
        }, manageUrl);
        var scheduleUrl = $"/OnCall/Schedule/{groupId}";
        AssertRedirect(response, scheduleUrl);
        var html = WebUtility.HtmlDecode(await Http.GetStringAsync(scheduleUrl));
        Assert.Contains("尚未配置班次模板", html);
        Assert.DoesNotContain("尚未配置班次模板", WebUtility.HtmlDecode(await Http.GetStringAsync(scheduleUrl)));
    }
}

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class MyOnCallPagesTests : TestBase
{
    private static readonly HttpClient Anonymous = new();
    private readonly List<Guid> groupIds = [];
    private readonly List<string> userIds = [];

    [TestCleanup]
    public override async Task CleanServer()
    {
        if (Server != null)
        {
            using var scope = Server.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.OnCallShiftMembers.RemoveRange(await db.OnCallShiftMembers.Where(member => groupIds.Contains(member.Shift.GroupId)).ToListAsync());
            db.OnCallShifts.RemoveRange(await db.OnCallShifts.Where(shift => groupIds.Contains(shift.GroupId)).ToListAsync());
            db.Set<OnCallFallbackRecipient>().RemoveRange(await db.Set<OnCallFallbackRecipient>().Where(person => groupIds.Contains(person.GroupId)).ToListAsync());
            db.OnCallGroupMembers.RemoveRange(await db.OnCallGroupMembers.Where(member => groupIds.Contains(member.GroupId)).ToListAsync());
            db.OnCallGroups.RemoveRange(await db.OnCallGroups.Where(group => groupIds.Contains(group.Id)).ToListAsync());
            db.Users.RemoveRange(await db.Users.Where(user => userIds.Contains(user.Id)).ToListAsync());
            await db.SaveChangesAsync();
        }
        await base.CleanServer();
    }

    [TestMethod]
    public async Task PersonalCalendarContainsOnlyMyUpcomingShiftsAndPreservesCrossTeamDetails()
    {
        await LoginAsAdmin();
        var now = DateTime.UtcNow;
        Guid currentId;
        Guid futureId;
        Guid unrelatedId;
        Guid historyId;
        Guid outsideWindowId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var admin = await db.Users.SingleAsync(person => person.Email == "admin@default.com");
            var coworker = new User { UserName = "calendar-coworker-" + Guid.NewGuid(), DisplayName = "Calendar coworker <Chen>", Email = "calendar-coworker@example.com" };
            userIds.Add(coworker.Id);
            var east = new OnCallGroup { Name = "Calendar East <team>", TimeZoneId = "Asia/Shanghai" };
            var west = new OnCallGroup { Name = "Calendar West", TimeZoneId = "America/New_York" };
            groupIds.AddRange([east.Id, west.Id]);
            var current = new OnCallShift { Group = east, StartsAtUtc = now.AddDays(-1), EndsAtUtc = now.AddDays(1), Members = [new() { UserId = admin.Id }, new() { User = coworker }] };
            var future = new OnCallShift { Group = west, StartsAtUtc = now.AddDays(10), EndsAtUtc = now.AddDays(12), Members = [new() { UserId = admin.Id }, new() { User = coworker }] };
            var unrelated = new OnCallShift { Group = east, StartsAtUtc = now.AddDays(3), EndsAtUtc = now.AddDays(4), Members = [new() { User = coworker }] };
            var history = new OnCallShift { Group = east, StartsAtUtc = now.AddDays(-3), EndsAtUtc = now.AddDays(-2), Members = [new() { UserId = admin.Id }] };
            var outsideWindow = new OnCallShift { Group = west, StartsAtUtc = now.AddDays(57), EndsAtUtc = now.AddDays(58), Members = [new() { UserId = admin.Id }] };
            currentId = current.Id;
            futureId = future.Id;
            unrelatedId = unrelated.Id;
            historyId = history.Id;
            outsideWindowId = outsideWindow.Id;
            db.OnCallShifts.AddRange(current, future, unrelated, history, outsideWindow);
            await db.SaveChangesAsync();
        }
        var rawHtml = await Http.GetStringAsync("/OnCall/Mine");
        var container = Regex.Match(rawHtml, "<div id=\"oncall-personal-calendar\"[^>]*>").Value;
        Assert.Contains("hidden", container);
        using var data = JsonDocument.Parse(WebUtility.HtmlDecode(Regex.Match(container, "data-shifts=\"([^\"]*)\"").Groups[1].Value));
        var shifts = data.RootElement.EnumerateArray().ToList();
        CollectionAssert.AreEquivalent(new[] { currentId, futureId }, shifts.Select(shift => shift.GetProperty("id").GetGuid()).ToArray());
        Assert.AreEqual(2, shifts.Select(shift => shift.GetProperty("groupId").GetGuid()).Distinct().Count());
        var currentData = shifts.Single(shift => shift.GetProperty("id").GetGuid() == currentId);
        Assert.AreEqual("Calendar East <team>", currentData.GetProperty("groupName").GetString());
        Assert.AreEqual(now.AddDays(-1), currentData.GetProperty("startsAtUtc").GetDateTime());
        Assert.AreEqual(now.AddDays(1), currentData.GetProperty("endsAtUtc").GetDateTime());
        Assert.AreEqual(DateTimeKind.Utc, currentData.GetProperty("startsAtUtc").GetDateTime().Kind);
        foreach (var id in new[] { currentId, futureId })
        {
            var article = Regex.Match(rawHtml, $"<article[^>]*data-personal-shift=\"{id}\".*?</article>", RegexOptions.Singleline).Value;
            Assert.Contains("Calendar coworker &lt;Chen&gt;", article);
            Assert.Contains("href=\"/OnCall/GroupDetails/", article);
            Assert.AreEqual(2, Regex.Matches(article, "data-oncall-time").Count);
            Assert.Contains("48 hours", article);
        }
        Assert.Contains("id=\"personal-calendar-fallback\"", rawHtml);
        Assert.Contains("data-zone=\"Asia/Shanghai\"", rawHtml);
        Assert.Contains("data-zone=\"America/New_York\"", rawHtml);
        Assert.Contains($"data-personal-shift=\"{historyId}\"", rawHtml);
        Assert.DoesNotContain(unrelatedId.ToString(), rawHtml);
        Assert.DoesNotContain(outsideWindowId.ToString(), rawHtml);
        Assert.Contains("oncall-personal-calendar.js", rawHtml);
    }

    [TestMethod]
    public async Task EmptyPersonalCalendarKeepsTheNoShiftsFallback()
    {
        await RegisterAndLoginAsync();
        var html = await Http.GetStringAsync("/OnCall/Mine");
        Assert.Contains("id=\"oncall-personal-calendar\"", html);
        Assert.Contains("data-shifts=\"[]\"", html);
        Assert.Contains("No shifts in this period.", html);
        Assert.Contains("id=\"personal-calendar-fallback\"", html);
    }

    [TestMethod]
    public async Task CalendarIsAnonymousPrivateWindowedAndResetRevokesOldToken()
    {
        Assert.AreEqual(HttpStatusCode.Found, (await Http.GetAsync("/OnCall/Mine")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync("/OnCall/Calendar.ics")).StatusCode);
        await LoginAsAdmin();
        var now = DateTime.UtcNow;
        Guid included;
        string userId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var user = await db.Users.SingleAsync(person => person.Email == "admin@default.com");
            userId = user.Id;
            var team = new OnCallGroup { Name = "Calendar team" };
            groupIds.Add(team.Id);
            var shift = new OnCallShift { Group = team, StartsAtUtc = now.AddDays(70), EndsAtUtc = now.AddDays(71),
                Members = [new OnCallShiftMember { UserId = user.Id }] };
            included = shift.Id;
            db.OnCallShifts.Add(shift);
            foreach (var offset in new[] { -32, -29, 85 })
                db.OnCallShifts.Add(new OnCallShift { Group = team, StartsAtUtc = now.AddDays(offset), EndsAtUtc = now.AddDays(offset).AddHours(1),
                    Members = [new OnCallShiftMember { UserId = user.Id }] });
            db.OnCallShifts.Add(new OnCallShift { Group = team, StartsAtUtc = now, EndsAtUtc = now.AddDays(1) });
            await db.SaveChangesAsync();
        }
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Http.PostAsync("/OnCall/ResetSubscription", new FormUrlEncodedContent([]))).StatusCode);
        var generated = await PostForm("/OnCall/ResetSubscription", [], "/OnCall/Mine");
        generated.EnsureSuccessStatusCode();
        Assert.IsTrue(generated.Headers.CacheControl!.NoStore);
        var html = WebUtility.HtmlDecode(await generated.Content.ReadAsStringAsync());
        var url = Regex.Match(html, "id=\"oncall-subscription\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.IsFalse(string.IsNullOrEmpty(url));
        var token = new Uri(url).Query.Split("token=")[1];
        using (var scope = Server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var hash = (await db.Users.FindAsync(userId))!.OnCallSubscriptionTokenHash;
            Assert.AreEqual(OnCallCalendar.Hash(token), hash);
            Assert.AreNotEqual(token, hash);
        }
        var result = await Anonymous.GetAsync(new Uri(Http.BaseAddress!, "/OnCall/Calendar.ics?token=" + token));
        result.EnsureSuccessStatusCode();
        Assert.AreEqual("text/calendar", result.Content.Headers.ContentType!.MediaType);
        var calendar = await result.Content.ReadAsStringAsync();
        Assert.Contains($"UID:{included:D}@alertcenter.aiursoft.com", calendar);
        Assert.AreEqual(2, Regex.Matches(calendar, "BEGIN:VEVENT").Count);
        Assert.Contains("Calendar team", calendar);
        Assert.Contains("https://alertcenter.aiursoft.com/OnCall/ScheduleDetails/", calendar.Replace("\r\n ", ""));
        Assert.DoesNotContain("id=\"oncall-subscription\"", await Http.GetStringAsync("/OnCall/Mine"));
        (await PostForm("/OnCall/ResetSubscription", [], "/OnCall/Mine")).EnsureSuccessStatusCode();
        Assert.AreEqual(HttpStatusCode.NotFound, (await Anonymous.GetAsync(new Uri(Http.BaseAddress!, "/OnCall/Calendar.ics?token=" + token))).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Anonymous.GetAsync(new Uri(Http.BaseAddress!, "/OnCall/Calendar.ics?token=" + new string('0', 64)))).StatusCode);
    }

    [TestMethod]
    public async Task CurrentBannerMergesOverlapsShowsAllTeamsAndFallbacksAndBadgeOnlyForDuty()
    {
        await LoginAsAdmin();
        var now = DateTime.UtcNow;
        Guid activeId;
        Guid gapId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var admin = await db.Users.SingleAsync(person => person.Email == "admin@default.com");
            var coworker = new User { UserName = "banner-coworker-" + Guid.NewGuid(), DisplayName = "Banner coworker", Email = "banner@example.com" };
            db.Users.Add(coworker);
            userIds.Add(coworker.Id);
            var active = new OnCallGroup { Name = "Active banner team", TimeZoneId = "Asia/Shanghai" };
            var gap = new OnCallGroup { Name = "Gap fallback team", FallbackRecipients = [new OnCallFallbackRecipient { UserId = admin.Id }] };
            var vacant = new OnCallGroup { Name = "Vacant banner team" };
            groupIds.AddRange([active.Id, gap.Id, vacant.Id]);
            activeId = active.Id;
            gapId = gap.Id;
            db.OnCallGroups.AddRange(active, gap, vacant);
            db.OnCallShifts.AddRange(
                new OnCallShift { Group = active, StartsAtUtc = now.AddHours(-1), EndsAtUtc = now.AddHours(2), Members = [new() { UserId = admin.Id }] },
                new OnCallShift { Group = active, StartsAtUtc = now.AddMinutes(-30), EndsAtUtc = now.AddHours(1), Members = [new() { UserId = admin.Id }, new() { UserId = coworker.Id }] },
                new OnCallShift { Group = active, StartsAtUtc = now.AddHours(2), EndsAtUtc = now.AddHours(3), Members = [new() { UserId = admin.Id }] },
                new OnCallShift { Group = active, StartsAtUtc = now.AddHours(2), EndsAtUtc = now.AddHours(4), Members = [new() { UserId = coworker.Id }] },
                new OnCallShift { Group = gap, StartsAtUtc = now.AddHours(-1), EndsAtUtc = now.AddHours(1) });
            await db.SaveChangesAsync();
        }
        var home = WebUtility.HtmlDecode(await Http.GetStringAsync("/Dashboard/Index"));
        Assert.Contains("id=\"current-oncall\"", home);
        Assert.Contains("id=\"my-oncall-card\"", home);
        Assert.Contains("id=\"on-duty-badge\"", home);
        Assert.Contains("href=\"/OnCall/Mine\"", home);
        Assert.Contains("Active banner team", home);
        Assert.Contains("Gap fallback team", home);
        Assert.Contains("Vacant banner team", home);
        Assert.Contains("Alerts will go to fallback recipients", home);
        Assert.Contains("No fallback configured: alerts cannot be delivered", home);
        var activeRow = Regex.Match(home, $"data-team-id=\"{activeId}\"(.*?)</div>\\s*</div>", RegexOptions.Singleline).Value;
        Assert.Contains("data-on-duty=\"true\"", activeRow);
        Assert.AreEqual(1, Regex.Matches(activeRow, "mailto:admin@default.com").Count);
        Assert.AreEqual(1, Regex.Matches(activeRow, "mailto:banner@example.com").Count);
        Assert.AreEqual(2, Regex.Matches(activeRow, "Banner coworker").Count);
        Assert.Contains("Next shift", activeRow);
        var detail = WebUtility.HtmlDecode(await Http.GetStringAsync($"/OnCall/GroupDetails/{gapId}"));
        Assert.Contains("Alerts will go to fallback recipients", detail);
        Assert.DoesNotContain($"data-team-id=\"{activeId}\"", detail);
        var mine = WebUtility.HtmlDecode(await Http.GetStringAsync("/OnCall/Mine"));
        Assert.Contains("On duty", mine);
        Assert.Contains("data-zone=\"Asia/Shanghai\"", mine);
        Assert.Contains("data-countdown=", mine);
        using (var scope = Server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            foreach (var shift in await db.OnCallShifts.Where(shift => shift.GroupId == activeId && shift.StartsAtUtc < now).ToListAsync())
                shift.EndsAtUtc = now.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        home = await Http.GetStringAsync("/Dashboard/Index");
        Assert.DoesNotContain("id=\"on-duty-badge\"", home);
        mine = await Http.GetStringAsync("/OnCall/Mine");
        Assert.Contains("Starts in", mine);
        Assert.AreEqual(HttpStatusCode.Found, (await Http.GetAsync("/Culture/Set?culture=zh-CN&returnUrl=/OnCall/Mine")).StatusCode);
        var chinese = WebUtility.HtmlDecode(await Http.GetStringAsync("/OnCall/Mine"));
        Assert.Contains("我的值班", chinese);
        Assert.Contains("私密日历订阅", chinese);
        Assert.Contains("未来 8 周班段", chinese);
        chinese = WebUtility.HtmlDecode(await Http.GetStringAsync("/Dashboard/Index"));
        Assert.Contains("当前值班", chinese);
        Assert.Contains("告警将发给兜底接收人", chinese);
        Assert.Contains("查看班段、值班时长和私密日历订阅。", chinese);
    }
}

using System.Net;
using System.Text.RegularExpressions;
using Aiursoft.AlertCenter.Entities;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class AlertDemoTests
{
    private async Task<InAppNotification[]> SeedInboxFilters()
    {
        await LoginAsAdmin();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var userId = await db.Users.Where(user => user.Email == "admin@default.com").Select(user => user.Id).SingleAsync();
        var otherUser = new User { UserName = "other-reader", DisplayName = "Other reader" };
        db.Users.Add(otherUser);
        var application = new SourceApplication { Name = "Orders application" };
        var otherApplication = new SourceApplication { Name = "Translation application" };
        var privateApplication = new SourceApplication { Name = "Other reader private application" };
        var channel = new PushChannel { Name = "Orders inbox", Type = "inapp" };
        var otherChannel = new PushChannel { Name = "Translation inbox", Type = "inapp" };
        var privateChannel = new PushChannel { Name = "Other reader private channel", Type = "inapp" };
        var start = new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);
        InAppNotification Notification(string title, string? content, string level, DateTime time,
            bool read = false, SourceApplication? source = null, PushChannel? via = null, string? recipient = null) => new()
        {
            UserId = recipient ?? userId, CreatedAtUtc = time, ReadAtUtc = read ? start : null,
            Delivery = new PushDelivery
            {
                Channel = via ?? channel, Status = PushDelivery.Succeeded,
                Message = new ReportedMessage
                {
                    Application = source ?? application, ReportId = Guid.NewGuid(), Title = title, Content = content,
                    Level = level, OccurredAtUtc = start.AddYears(-1)
                }
            }
        };
        var items = new[]
        {
            Notification("Needle at start", null, "sev1", start),
            Notification("Body match", new string('x', 300) + " NEEDLE 50%_ <script>", "sev1", start.AddDays(1).AddTicks(-1)),
            Notification("Already read", "needle", "sev2", start.AddHours(2), read: true),
            Notification("Different application", "needle", "sev1", start.AddHours(3), source: otherApplication),
            Notification("Different channel", "needle", "sev1", start.AddHours(4), via: otherChannel),
            Notification("Next day", "needle", "sev1", start.AddDays(1)),
            Notification("Previous day", "needle", "sev1", start.AddTicks(-1)),
            Notification("Different severity", "needle", "sev4", start.AddHours(5)),
            Notification("Private unread", "needle", "sev1", start, source: privateApplication, via: privateChannel, recipient: otherUser.Id),
            new InAppNotification
            {
                UserId = userId, CreatedAtUtc = start.AddHours(6),
                Delivery = new PushDelivery { Channel = channel, Status = PushDelivery.Succeeded }
            }
        };
        db.InAppNotifications.AddRange(items);
        await db.SaveChangesAsync();
        return items;
    }

    private static Guid[] InboxNotificationIds(string html) => Regex.Matches(html, "href=\"/Push/Notification/([a-fA-F0-9-]+)\"")
        .Select(match => Guid.Parse(match.Groups[1].Value)).ToArray();

    private static Guid[] InboxMessageIds(string html) => InboxNotificationIds(html);

    [TestMethod]
    public async Task InboxDefaultsToUnreadAndSupportsReadStatusAndLegacyLinks()
    {
        var items = await SeedInboxFilters();
        var html = await Http.GetStringAsync("/Push/Inbox");
        CollectionAssert.AreEquivalent(items.Where((_, index) => index != 2 && index != 8)
            .Select(item => item.Id).ToArray(), InboxNotificationIds(html));
        Assert.Contains($"/Push/Notification/{items[9].Id}", html);
        Assert.DoesNotContain("Other reader private", html);
        Assert.Contains("8 Unread", WebUtility.HtmlDecode(html));
        Assert.AreEqual(0, InboxMessageIds(await Http.GetStringAsync(
            $"/Push/Inbox?applicationId={items[8].Delivery.Message!.ApplicationId}&channelId={items[8].Delivery.ChannelId}")).Length);
        CollectionAssert.AreEqual(new[] { items[2].Id },
            InboxNotificationIds(await Http.GetStringAsync("/Push/Inbox?readStatus=read")));
        Assert.AreEqual(9, InboxMessageIds(await Http.GetStringAsync("/Push/Inbox?readStatus=all")).Length);
        Assert.AreEqual(9, InboxMessageIds(await Http.GetStringAsync("/Push/Inbox?unreadOnly=false")).Length);
        Assert.AreEqual(8, InboxMessageIds(await Http.GetStringAsync("/Push/Inbox?unreadOnly=true")).Length);
        Assert.AreEqual(9, InboxMessageIds(await Http.GetStringAsync("/Push/Inbox?unreadOnly=true&readStatus=all")).Length);
    }

    [TestMethod]
    public async Task InboxStatusLinksExposeAllNotificationsAndPreserveSearchFilters()
    {
        var items = await SeedInboxFilters();
        static string StatusLink(string html, string status) => Regex.Match(html,
            $"<a[^>]*id=\"inbox-status-{status}\"[^>]*>[^<]*</a>").Value;
        static string Href(string link) => WebUtility.HtmlDecode(Regex.Match(link, "href=\"([^\"]+)\"").Groups[1].Value);

        var html = await Http.GetStringAsync("/Push/Inbox");
        Assert.Contains("aria-current=\"page\"", StatusLink(html, "unread"));
        var allLink = StatusLink(html, "all");
        Assert.Contains("All notifications", WebUtility.HtmlDecode(allLink));
        html = await Http.GetStringAsync(Href(allLink));
        Assert.AreEqual(9, InboxMessageIds(html).Length);
        Assert.Contains(items[2].Id, InboxMessageIds(html));
        Assert.Contains("aria-current=\"page\"", StatusLink(html, "all"));

        var query = $"keyword=needle&applicationId={items[0].Delivery.Message!.ApplicationId}" +
            $"&channelId={items[0].Delivery.ChannelId}&level=sev2&fromDate=2026-09-08&toDate=2026-09-08";
        html = await Http.GetStringAsync("/Push/Inbox?" + query + "&page=2");
        Assert.AreEqual(0, InboxMessageIds(html).Length);
        foreach (var status in new[] { "all", "read", "unread" })
        {
            var link = Href(StatusLink(html, status));
            var parameters = QueryHelpers.ParseQuery(new Uri(Http.BaseAddress!, link).Query);
            Assert.AreEqual(status, parameters["readStatus"].ToString());
            Assert.IsFalse(parameters.ContainsKey("page"));
            foreach (var parameter in QueryHelpers.ParseQuery("?" + query))
                Assert.AreEqual(parameter.Value.ToString(), parameters[parameter.Key].ToString());
            var result = await Http.GetStringAsync(link);
            CollectionAssert.AreEqual(status == "unread" ? Array.Empty<Guid>() : new[] { items[2].Id }, InboxMessageIds(result));
            Assert.Contains("aria-current=\"page\"", StatusLink(result, status));
        }
    }

    [TestMethod]
    public async Task InboxCombinesFiltersWithInclusiveUtcDatesAndLiteralKeywords()
    {
        var items = await SeedInboxFilters();
        var url = $"/Push/Inbox?keyword=NeEdLe&applicationId={items[0].Delivery.Message!.ApplicationId}" +
            $"&channelId={items[0].Delivery.ChannelId}&level=sev1&fromDate=2026-09-08&toDate=2026-09-08";
        CollectionAssert.AreEquivalent(new[] { items[0].Id, items[1].Id },
            InboxMessageIds(await Http.GetStringAsync(url)));
        CollectionAssert.AreEqual(new[] { items[1].Id },
            InboxMessageIds(await Http.GetStringAsync("/Push/Inbox?keyword=" + Uri.EscapeDataString("50%_"))));
        var escaped = await Http.GetStringAsync("/Push/Inbox?keyword=" + Uri.EscapeDataString("<script>"));
        CollectionAssert.AreEqual(new[] { items[1].Id }, InboxMessageIds(escaped));
        Assert.Contains("&lt;script&gt;", escaped);
        Assert.DoesNotContain("value=\"<script>\"", escaped);
        var test = await Http.GetStringAsync("/Push/Inbox?keyword=" + Uri.EscapeDataString("Channel test notification"));
        Assert.AreEqual(1, InboxMessageIds(test).Length);
        Assert.Contains($"/Push/Notification/{items[9].Id}", test);
        Assert.DoesNotContain($"/Push/Notification/{items[9].Id}", await Http.GetStringAsync("/Push/Inbox?level=sev3"));
        Assert.AreEqual(0, InboxMessageIds(await Http.GetStringAsync($"/Push/Inbox?applicationId={Guid.NewGuid()}")).Length);
        Assert.AreEqual(8, InboxMessageIds(await Http.GetStringAsync("/Push/Inbox?toDate=9999-12-31")).Length);
        Assert.AreEqual(0, InboxMessageIds(await Http.GetStringAsync($"/Push/Inbox?channelId={Guid.NewGuid()}")).Length);
        var tooLong = await Http.GetStringAsync("/Push/Inbox?keyword=" + new string('a', 201));
        Assert.Contains("validation-summary-errors", tooLong);
        Assert.AreEqual(0, InboxMessageIds(tooLong).Length);
    }

    [TestMethod]
    [DataRow("fromDate=invalid")]
    [DataRow("fromDate=2026-09-09&toDate=2026-09-08")]
    [DataRow("applicationId=invalid")]
    [DataRow("channelId=invalid")]
    [DataRow("readStatus=invalid")]
    [DataRow("level=sev5")]
    public async Task InboxInvalidFiltersShowValidationInsteadOfUnfilteredResults(string query)
    {
        await SeedInboxFilters();
        var html = await Http.GetStringAsync("/Push/Inbox?" + query);
        Assert.Contains("validation-summary-errors", html);
        Assert.AreEqual(0, InboxMessageIds(html).Length);
    }

    [TestMethod]
    public async Task InboxReadActionsRejectInvalidFiltersAndPreserveUserIsolation()
    {
        var items = await SeedInboxFilters();
        using var invalidRead = await PostForm("/Push/MarkRead?level=sev5", new() { ["id"] = items[0].Id.ToString() }, "/Push/Inbox");
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidRead.StatusCode);
        using var invalidAll = await PostForm("/Push/MarkAllRead?fromDate=invalid", new(), "/Push/Inbox");
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidAll.StatusCode);
        using var missingToken = await PostForm("/Push/MarkAllRead", new(), includeToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingToken.StatusCode);
        using var otherUser = await PostForm("/Push/MarkRead", new() { ["id"] = items[8].Id.ToString() }, "/Push/Inbox");
        Assert.AreEqual(HttpStatusCode.NotFound, otherUser.StatusCode);
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(9, await db.InAppNotifications.CountAsync(item => item.ReadAtUtc == null));
    }

    [TestMethod]
    public async Task InboxFiltersPersistThroughPaginationAndReadActions()
    {
        var items = await SeedInboxFilters();
        var first = items[0];
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            for (var index = 0; index < 22; index++)
            {
                db.InAppNotifications.Add(new InAppNotification
                {
                    UserId = first.UserId, CreatedAtUtc = first.CreatedAtUtc.AddMinutes(index + 1),
                    Delivery = new PushDelivery
                    {
                        ChannelId = first.Delivery.ChannelId, Status = PushDelivery.Succeeded,
                        Message = new ReportedMessage
                        {
                            ApplicationId = first.Delivery.Message!.ApplicationId, ReportId = Guid.NewGuid(),
                            Title = "Needle page " + index, Level = "sev1", OccurredAtUtc = first.CreatedAtUtc
                        }
                    }
                });
            }
            await db.SaveChangesAsync();
        }
        var query = $"keyword=needle&readStatus=all&applicationId={first.Delivery.Message!.ApplicationId}" +
            $"&channelId={first.Delivery.ChannelId}&level=sev1&fromDate=2026-09-08&toDate=2026-09-08";
        var html = await Http.GetStringAsync("/Push/Inbox?" + query);
        Assert.Contains("id=\"inbox-page-jump\"", html);
        Assert.AreEqual(20, InboxMessageIds(html).Length);
        var pageJump = WebUtility.HtmlDecode(Regex.Match(html, "<form id=\"inbox-page-jump-form\"[^>]*action=\"([^\"]+)\"").Groups[1].Value);
        Assert.IsFalse(string.IsNullOrEmpty(pageJump));
        var pageJumpParameters = QueryHelpers.ParseQuery(new Uri(Http.BaseAddress!, pageJump).Query);
        foreach (var parameter in QueryHelpers.ParseQuery("?" + query)) Assert.AreEqual(parameter.Value.ToString(), pageJumpParameters[parameter.Key].ToString());
        Assert.IsFalse(pageJumpParameters.ContainsKey("page"));
        var next = WebUtility.HtmlDecode(Regex.Match(html, "href=\"([^\"]+)\"[^>]*>Next Page</a>").Groups[1].Value);
        Assert.IsFalse(string.IsNullOrEmpty(next));
        var nextQuery = QueryHelpers.ParseQuery(new Uri(Http.BaseAddress!, next).Query);
        foreach (var parameter in QueryHelpers.ParseQuery("?" + query)) Assert.AreEqual(parameter.Value.ToString(), nextQuery[parameter.Key].ToString());
        Assert.AreEqual("2", nextQuery["page"].ToString());
        html = await Http.GetStringAsync(next);
        Assert.AreEqual(4, InboxMessageIds(html).Length);
        var action = WebUtility.HtmlDecode(Regex.Match(html, "<form[^>]*action=\"([^\"]*MarkRead[^\"]*)\"").Groups[1].Value);
        Assert.IsFalse(string.IsNullOrEmpty(action));
        using var read = await PostForm(action, new() { ["id"] = first.Id.ToString() }, next);
        Assert.AreEqual(HttpStatusCode.Found, read.StatusCode);
        var returned = QueryHelpers.ParseQuery(new Uri(Http.BaseAddress!, read.Headers.Location!).Query);
        foreach (var parameter in nextQuery) Assert.AreEqual(parameter.Value.ToString(), returned[parameter.Key].ToString());
        var globalAction = WebUtility.HtmlDecode(Regex.Match(html, "<form[^>]*action=\"([^\"]*MarkAllRead[^\"]*)\"").Groups[1].Value);
        using var allRead = await PostForm(globalAction, new(), next);
        Assert.AreEqual(HttpStatusCode.Found, allRead.StatusCode);
        using var finalScope = Server!.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(0, await finalDb.InAppNotifications.CountAsync(item => item.UserId == first.UserId && item.ReadAtUtc == null));
        Assert.IsNull((await finalDb.InAppNotifications.SingleAsync(item => item.Id == items[8].Id)).ReadAtUtc);
        Assert.AreEqual(items[2].ReadAtUtc, (await finalDb.InAppNotifications.SingleAsync(item => item.Id == items[2].Id)).ReadAtUtc);
        var globalReturn = QueryHelpers.ParseQuery(new Uri(Http.BaseAddress!, allRead.Headers.Location!).Query);
        foreach (var parameter in nextQuery) Assert.AreEqual(parameter.Value.ToString(), globalReturn[parameter.Key].ToString());
        html = await Http.GetStringAsync("/Push/Inbox?keyword=needle&page=2");
        Assert.AreEqual(0, InboxMessageIds(html).Length);
        Assert.Contains("Previous Page", WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("Next Page</a>", html);
    }
}

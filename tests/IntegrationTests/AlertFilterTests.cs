using System.Net;
using System.Text.RegularExpressions;
using Aiursoft.AlertCenter.Entities;
using Microsoft.AspNetCore.WebUtilities;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class AlertDemoTests
{
    private async Task<ReportedMessage[]> SeedAlertFilters()
    {
        await LoginAsAdmin();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var orders = new SourceApplication { Name = "Orders application" };
        var inventory = new SourceApplication { Name = "Inventory application" };
        var start = new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);
        var messages = new[]
        {
            new ReportedMessage { Application = orders, ReportId = Guid.NewGuid(), Title = "Needle at start", Level = "sev1", ReceivedAtUtc = start },
            new ReportedMessage { Application = orders, ReportId = Guid.NewGuid(), Title = "Body match", Content = new string('x', 300) + " NEEDLE 50%_ <script>", Level = "sev1", ReceivedAtUtc = start.AddDays(1).AddTicks(-1) },
            new ReportedMessage { Application = orders, ReportId = Guid.NewGuid(), Title = "Different severity", Content = "needle", Level = "sev4", ReceivedAtUtc = start.AddHours(1) },
            new ReportedMessage { Application = inventory, ReportId = Guid.NewGuid(), Title = "Different application", Content = "needle", Level = "sev1", ReceivedAtUtc = start.AddHours(2) },
            new ReportedMessage { Application = orders, ReportId = Guid.NewGuid(), Title = "Next day", Content = "needle", Level = "sev1", ReceivedAtUtc = start.AddDays(1) },
            new ReportedMessage { Application = orders, ReportId = Guid.NewGuid(), Title = "Previous day", Content = "needle", Level = "sev1", ReceivedAtUtc = start.AddTicks(-1) }
        };
        db.ReportedMessages.AddRange(messages);
        await db.SaveChangesAsync();
        return messages;
    }

    private static Guid[] AlertMessageIds(string html) => Regex.Matches(html, "href=\"/Alerts/Details/([a-fA-F0-9-]+)\"")
        .Select(match => Guid.Parse(match.Groups[1].Value)).ToArray();

    [TestMethod]
    public async Task AlertMessagesSupportCombinedFiltersWithInclusiveDatesAndLiteralKeywords()
    {
        var messages = await SeedAlertFilters();
        var url = $"/Alerts?keyword=NeEdLe&applicationId={messages[0].ApplicationId}&level=sev1" +
            "&fromDate=2026-09-08&toDate=2026-09-08";
        CollectionAssert.AreEquivalent(new[] { messages[0].Id, messages[1].Id }, AlertMessageIds(await Http.GetStringAsync(url)));
        CollectionAssert.AreEqual(new[] { messages[1].Id }, AlertMessageIds(await Http.GetStringAsync(
            "/Alerts?keyword=" + Uri.EscapeDataString("50%_"))));
        var escaped = await Http.GetStringAsync("/Alerts?keyword=" + Uri.EscapeDataString("<script>"));
        CollectionAssert.AreEqual(new[] { messages[1].Id }, AlertMessageIds(escaped));
        Assert.Contains("&lt;script&gt;", escaped);
        Assert.DoesNotContain("value=\"<script>\"", escaped);
        Assert.AreEqual(6, AlertMessageIds(await Http.GetStringAsync("/Alerts?toDate=9999-12-31")).Length);
    }

    [TestMethod]
    public async Task AlertMessageFiltersRenderControlsAndPersistThroughPagination()
    {
        var messages = await SeedAlertFilters();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            for (var index = 0; index < 20; index++)
            {
                db.ReportedMessages.Add(new ReportedMessage
                {
                    ApplicationId = messages[0].ApplicationId, ReportId = Guid.NewGuid(), Title = "Needle page " + index,
                    Level = "sev1", ReceivedAtUtc = messages[0].ReceivedAtUtc.AddMinutes(index + 1)
                });
            }
            await db.SaveChangesAsync();
        }

        var query = $"keyword=needle&applicationId={messages[0].ApplicationId}&level=sev1&fromDate=2026-09-08&toDate=2026-09-08";
        var html = await Http.GetStringAsync("/Alerts?" + query);
        Assert.Contains("id=\"alert-filters\"", html);
        Assert.Contains("id=\"filter-keyword\"", html);
        Assert.Contains("id=\"filter-application\"", html);
        Assert.Contains("id=\"filter-level\"", html);
        Assert.Contains("id=\"filter-from-date\"", html);
        Assert.Contains("id=\"filter-to-date\"", html);
        Assert.Contains("id=\"alert-page-jump\"", html);
        Assert.Contains("<th>关联事故</th>", html);
        Assert.Contains("<th class=\"text-nowrap pe-4\">接收时间 (UTC)</th>", html);
        Assert.AreEqual(20, AlertMessageIds(html).Length);
        var pageJump = WebUtility.HtmlDecode(Regex.Match(html, "<form id=\"alert-page-jump-form\"[^>]*action=\"([^\"]+)\"").Groups[1].Value);
        Assert.IsFalse(string.IsNullOrEmpty(pageJump));
        var pageJumpParameters = QueryHelpers.ParseQuery(new Uri(Http.BaseAddress!, pageJump).Query);
        foreach (var parameter in QueryHelpers.ParseQuery("?" + query))
            Assert.AreEqual(parameter.Value.ToString(), pageJumpParameters[parameter.Key].ToString());
        Assert.IsFalse(pageJumpParameters.ContainsKey("page"));
        var next = WebUtility.HtmlDecode(Regex.Match(html, "href=\"([^\"]+)\"[^>]*>下一页</a>").Groups[1].Value);
        Assert.IsFalse(string.IsNullOrEmpty(next));
        var parameters = QueryHelpers.ParseQuery(new Uri(Http.BaseAddress!, next).Query);
        foreach (var parameter in QueryHelpers.ParseQuery("?" + query))
            Assert.AreEqual(parameter.Value.ToString(), parameters[parameter.Key].ToString());
        Assert.AreEqual("2", parameters["page"].ToString());
        Assert.AreEqual(2, AlertMessageIds(await Http.GetStringAsync(next)).Length);
    }

    [TestMethod]
    [DataRow("keyword=" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [DataRow("fromDate=invalid")]
    [DataRow("fromDate=2026-09-09&toDate=2026-09-08")]
    [DataRow("applicationId=invalid")]
    [DataRow("level=sev5")]
    public async Task AlertMessageInvalidFiltersShowValidationInsteadOfUnfilteredResults(string query)
    {
        await SeedAlertFilters();
        var response = await Http.GetAsync("/Alerts?" + query);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("validation-summary-errors", html);
        Assert.AreEqual(0, AlertMessageIds(html).Length);
    }
}

using System.Net;
using System.Text.RegularExpressions;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.DbTools;
using Microsoft.EntityFrameworkCore;
using static Aiursoft.WebTools.Extends;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class IncidentSearchPagesTests : TestBase
{
    [TestInitialize]
    public override async Task CreateServer()
    {
        using var reservation = new TestPortReservation();
        Http.BaseAddress = new Uri($"http://localhost:{reservation.Port}");
        // 视图本地化按宿主程序集查找资源，测试入口 testhost 不包含应用的翻译。
        Server = await AppAsync<Startup>(["--applicationName", typeof(Startup).Assembly.GetName().Name!], port: reservation.Port);
        try
        {
            reservation.ConfigureServer(Server);
            await Server.UpdateDbAsync<TemplateDbContext>();
            await Server.SeedAsync();
            await Server.StartAsync();
        }
        catch
        {
            Server.Dispose();
            Server = null;
            throw;
        }
    }

    [TestMethod]
    [DataRow("/Search/Index")]
    [DataRow("/Search/Index?q=private-incident")]
    public async Task AnonymousSearchRedirectsToLogin(string url)
    {
        using var response = await Http.GetAsync(url);
        AssertRedirect(response, $"/Account/Login?ReturnUrl={Uri.EscapeDataString(url)}");
        Assert.DoesNotContain("id=\"incident-search-query\"", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [DataRow("/Search/Index")]
    [DataRow("/Search/Index?q=")]
    [DataRow("/Search/Index?q=%20%20%20")]
    public async Task EmptySearchShowsFormWithoutResults(string url)
    {
        await LoginAsAdmin();
        using var response = await Http.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore == true);
        Assert.IsTrue(response.Headers.CacheControl?.NoCache == true);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"incident-search-query\"", html);
        Assert.Contains("name=\"q\" type=\"search\" value=\"\"", html);
        Assert.Contains("maxlength=\"200\"", html);
        Assert.DoesNotContain("id=\"incident-search-results\"", html);
        Assert.DoesNotContain("No matching incidents", html);
    }

    [TestMethod]
    public async Task LongSearchShowsValidationAndEscapesRefilledQuery()
    {
        await LoginAsAdmin();
        var query = "<script>alert(1)</script>" + new string('x', 201);
        using var response = await Http.GetAsync($"/Search/Index?q={Uri.EscapeDataString("  " + query + "  ")}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore == true);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Search queries must not exceed 200 characters.", html);
        Assert.Contains("aria-invalid=\"true\"", html);
        Assert.Contains($"value=\"{WebUtility.HtmlEncode(query)}\"", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.DoesNotContain("id=\"incident-search-results\"", html);
    }

    [TestMethod]
    public async Task SearchPageAndValidationAreLocalizedInChinese()
    {
        await LoginAsAdmin();
        using var culture = await Http.GetAsync("/Culture/Set?culture=zh-CN&returnUrl=/Search/Index");
        Assert.AreEqual(HttpStatusCode.Found, culture.StatusCode);
        using var empty = await Http.GetAsync("/Search/Index");
        Assert.AreEqual(HttpStatusCode.OK, empty.StatusCode);
        var emptyHtml = WebUtility.HtmlDecode(await empty.Content.ReadAsStringAsync());
        Assert.Contains("事件搜索", emptyHtml);
        Assert.Contains("搜索事件…", emptyHtml);
        Assert.Contains("输入不超过 200 个字符以搜索事件。", emptyHtml);

        using var invalid = await Http.GetAsync($"/Search/Index?q={new string('x', 201)}");
        Assert.AreEqual(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains("搜索词不能超过 200 个字符。", WebUtility.HtmlDecode(await invalid.Content.ReadAsStringAsync()));
    }

    [TestMethod]
    public async Task NavbarSearchUsesGetQueryAndSubmitButtonOnlyAfterLogin()
    {
        using var anonymous = await Http.GetAsync("/Error/Code403");
        Assert.AreEqual(HttpStatusCode.OK, anonymous.StatusCode);
        Assert.IsFalse(NavbarSearch(await anonymous.Content.ReadAsStringAsync()).Success);

        await LoginAsAdmin();
        using var dashboard = await Http.GetAsync("/Dashboard/Index");
        Assert.AreEqual(HttpStatusCode.OK, dashboard.StatusCode);
        var navbarSearch = NavbarSearch(await dashboard.Content.ReadAsStringAsync());
        Assert.IsTrue(navbarSearch.Success);
        var form = navbarSearch.Value;
        Assert.Contains("action=\"/Search/Index\"", form);
        Assert.Contains("method=\"get\"", form);
        Assert.Contains("role=\"search\"", form);
        Assert.Contains("name=\"q\"", form);
        Assert.Contains("type=\"submit\"", form);
        Assert.DoesNotContain("type=\"button\"", form);
        Assert.Contains("aria-label=", form);
    }

    [TestMethod]
    public async Task KeywordResultsArePermissionScopedEscapedAndPaginated()
    {
        var (email, _) = await RegisterAndLoginAsync();
        const string query = "outage & recovery";
        var incidents = await SeedKeywordSearchIncidents(email, query, 12);
        using var first = await Http.GetAsync($"/Search/Index?q={Uri.EscapeDataString("  " + query + "  ")}");
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        Assert.IsTrue(first.Headers.CacheControl?.NoStore == true);
        var firstHtml = await first.Content.ReadAsStringAsync();
        Assert.Contains("Keyword search", firstHtml);
        Assert.Contains("12 matching incidents you can access", firstHtml);
        Assert.Contains($"value=\"{WebUtility.HtmlEncode(query)}\"", firstHtml);
        Assert.Contains(">sev1</span>", firstHtml);
        Assert.Contains(">Mitigated</dd>", firstHtml);
        Assert.Contains("data-utc-time=", firstHtml);
        Assert.AreEqual(10, SearchResultLinks(firstHtml).Length);

        var next = Regex.Match(firstHtml, "<a\\b[^>]*href=\"([^\"]+)\"[^>]*>Next page</a>");
        Assert.IsTrue(next.Success);
        var nextUrl = WebUtility.HtmlDecode(next.Groups[1].Value);
        var nextQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(Http.BaseAddress!, nextUrl).Query);
        Assert.AreEqual(query, nextQuery["q"].ToString());
        Assert.AreEqual("2", nextQuery["page"].ToString());
        using var second = await Http.GetAsync(nextUrl);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        var secondHtml = await second.Content.ReadAsStringAsync();
        Assert.AreEqual(2, SearchResultLinks(secondHtml).Length);
        Assert.Contains("12 matching incidents you can access", secondHtml);
        Assert.Contains("Page 2", secondHtml);
        Assert.IsFalse(Regex.IsMatch(secondHtml, "<a\\b[^>]*>Next page</a>"));
        CollectionAssert.AreEquivalent(incidents.Select(incident => $"/Incidents/Details/{incident.Id}").ToArray(),
            SearchResultLinks(firstHtml).Concat(SearchResultLinks(secondHtml)).ToArray());

        foreach (var html in new[] { firstHtml, secondHtml })
        {
            Assert.Contains(WebUtility.HtmlEncode(query + " <img src=x onerror=alert(1)>"), html);
            Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; visible evidence", html);
            Assert.Contains("Allowed &lt;payments&gt; &amp; app", html);
            Assert.DoesNotContain("<img src=x onerror=alert(1)>", html);
            Assert.DoesNotContain("<script>alert(1)</script>", html);
            Assert.DoesNotContain("Private search application", html);
            Assert.DoesNotContain("private-hidden-incident", html);
            Assert.DoesNotContain("Unrelated item", html);
            Assert.DoesNotContain("context-only-secret", html);
            Assert.DoesNotContain("metadata-only-secret", html);
            Assert.DoesNotContain("summary-tail-hidden", html);
        }
    }

    [TestMethod]
    public async Task ChineseKeywordResultsAndEmptyStateAreLocalized()
    {
        var (email, _) = await RegisterAndLoginAsync();
        const string query = "outage & recovery";
        await SeedKeywordSearchIncidents(email, query, 1);
        using var culture = await Http.GetAsync("/Culture/Set?culture=zh-CN&returnUrl=/Search/Index");
        Assert.AreEqual(HttpStatusCode.Found, culture.StatusCode);
        using var results = await Http.GetAsync($"/Search/Index?q={Uri.EscapeDataString(query)}");
        Assert.AreEqual(HttpStatusCode.OK, results.StatusCode);
        var resultsHtml = WebUtility.HtmlDecode(await results.Content.ReadAsStringAsync());
        Assert.Contains("关键词搜索", resultsHtml);
        Assert.Contains("您可访问的事件中有 1 个匹配结果", resultsHtml);
        Assert.Contains("来源应用", resultsHtml);
        Assert.Contains("已缓解", resultsHtml);
        Assert.AreEqual(1, SearchResultLinks(resultsHtml).Length);
        Assert.DoesNotContain("Private search application", resultsHtml);
        Assert.DoesNotContain("private-hidden-incident", resultsHtml);

        using var empty = await Http.GetAsync("/Search/Index?q=no-such-incident");
        Assert.AreEqual(HttpStatusCode.OK, empty.StatusCode);
        var emptyHtml = WebUtility.HtmlDecode(await empty.Content.ReadAsStringAsync());
        Assert.Contains("关键词搜索", emptyHtml);
        Assert.Contains("您可访问的事件中有 0 个匹配结果", emptyHtml);
        Assert.Contains("没有匹配的事件", emptyHtml);
        Assert.Contains("请尝试其他搜索词。", emptyHtml);
        Assert.AreEqual(0, SearchResultLinks(emptyHtml).Length);
    }

    private async Task<List<Incident>> SeedKeywordSearchIncidents(string email, string query, int count)
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var userId = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
        var adminId = await db.Users.Where(user => user.Email == "admin@default.com").Select(user => user.Id).SingleAsync();
        var allowed = new SourceApplication { Name = "Allowed <payments> & app", OwnerId = adminId };
        db.ApplicationUserAccesses.Add(new ApplicationUserAccess
        {
            Application = allowed,
            UserId = userId,
            CanViewIncidents = true
        });
        var incidents = Enumerable.Range(0, count).Select(index => new Incident
        {
            Application = allowed,
            Title = $"{query} <img src=x onerror=alert(1)> {index:D2}",
            Content = "<script>alert(1)</script> & visible evidence " + new string('x', 250) + "summary-tail-hidden",
            ContextJson = "{\"secret\":\"context-only-secret\"}",
            MetadataJson = "{\"secret\":\"metadata-only-secret\"}",
            Level = "sev1",
            Status = Incident.Mitigated,
            ReceivedAtUtc = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(index)
        }).ToList();
        db.Incidents.AddRange(incidents);
        db.Incidents.AddRange(
            new Incident { Application = allowed, Title = "Unrelated item", Content = "Routine maintenance" },
            new Incident
            {
                Application = new SourceApplication { Name = "Private search application", OwnerId = adminId },
                Title = query + " private-hidden-incident",
                Level = "sev1"
            });
        await db.SaveChangesAsync();
        return incidents;
    }

    private static string[] SearchResultLinks(string html) => Regex.Matches(html,
        "<a\\b[^>]*href=\"(/Incidents/Details/[^\"]+)\"[^>]*>").Select(match => match.Groups[1].Value).ToArray();

    private static Match NavbarSearch(string html) => Regex.Match(html,
        "<form\\b[^>]*class=\"d-none d-sm-inline-block\"[^>]*>.*?</form>", RegexOptions.Singleline);
}

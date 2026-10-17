using System.Net;
using System.Text.RegularExpressions;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class ErrorControllerTests : TestBase
{
    [TestMethod]
    [DataRow("/Error/Code500")]
    [DataRow("/Error/Code403?returnUrl=/dashboard")]
    [DataRow("/Error/Code400")]
    [DataRow("/Error/Code401")]
    [DataRow("/Error/Code404")]
    [DataRow("/Error/Code999")]
    public async Task GetError(string url)
    {
        var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("javascript:void(document.body.dataset.returnUrlMarker=1)")]
    [DataRow("JaVaScRiPt:void(0)")]
    [DataRow("\tjavascript:void(0)")]
    [DataRow("java\nscript:void(0)")]
    [DataRow("data:text/html,<script>void(0)</script>")]
    [DataRow("https://untrusted.example/path")]
    [DataRow("http://untrusted.example/path")]
    [DataRow("//untrusted.example/path")]
    [DataRow("/\\untrusted.example/path")]
    [DataRow("~/\\untrusted.example/path")]
    [DataRow("~//untrusted.example/path")]
    [DataRow("/dashboard\n")]
    [DataRow("dashboard")]
    public async Task ErrorPagesOmitRetryLinksForUnsafeOrMissingReturnUrls(string? returnUrl)
    {
        foreach (var authenticated in new[] { false, true })
        {
            if (authenticated) await LoginAsAdmin();
            var query = returnUrl is null ? string.Empty : "?returnUrl=" + Uri.EscapeDataString(returnUrl);
            using var response = await Http.GetAsync("/Error/Code403" + query);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync();

            Assert.IsFalse(Regex.IsMatch(html, "<a\\b[^>]*class=\"btn btn-secondary btn-lg\""),
                $"An unsafe retry link was rendered (authenticated: {authenticated}).");
            Assert.IsTrue(Regex.IsMatch(html, "<a\\b[^>]*class=\"btn btn-primary btn-lg\""));
        }
    }

    [TestMethod]
    [DataRow("/", "/")]
    [DataRow("/dashboard", "/dashboard")]
    [DataRow("/Incidents?level=sev1&page=2#results", "/Incidents?level=sev1&page=2#results")]
    [DataRow("~/dashboard", "/dashboard")]
    [DataRow("/Incidents?keyword=\"<test>\"&level=sev1", "/Incidents?keyword=\"<test>\"&level=sev1")]
    public async Task ErrorPagesPreserveAndEncodeLocalRetryLinks(string returnUrl, string expectedHref)
    {
        foreach (var authenticated in new[] { false, true })
        {
            if (authenticated) await LoginAsAdmin();
            using var response = await Http.GetAsync("/Error/Code403?returnUrl=" + Uri.EscapeDataString(returnUrl));
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync();
            var retry = Regex.Match(html, "<a\\s+href=\"([^\"]*)\"\\s+class=\"btn btn-secondary btn-lg\">");

            Assert.IsTrue(retry.Success, $"A valid local retry link was omitted (authenticated: {authenticated}).");
            Assert.AreEqual(expectedHref, WebUtility.HtmlDecode(retry.Groups[1].Value));
            Assert.DoesNotContain("<test>", retry.Value);
        }
    }
}

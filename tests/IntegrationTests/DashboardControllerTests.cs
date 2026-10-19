using System.Net;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class DashboardControllerTests : TestBase
{
    [TestMethod]
    [DataRow("/Dashboard")]
    [DataRow("/Dashboard/Index")]
    public async Task AnonymousDashboardRedirectsToLogin(string url)
    {
        using var response = await Http.GetAsync(url);
        AssertRedirect(response, $"/Account/Login?ReturnUrl={Uri.EscapeDataString(url)}");
        Assert.DoesNotContain("Monitoring Dashboard", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task AuthenticatedDashboardIsNotCached()
    {
        await LoginAsAdmin();
        using var response = await Http.GetAsync("/Dashboard/Index");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Monitoring Dashboard", await response.Content.ReadAsStringAsync());
        Assert.IsTrue(response.Headers.CacheControl?.NoStore == true);
        Assert.IsTrue(response.Headers.CacheControl?.NoCache == true);
    }
}

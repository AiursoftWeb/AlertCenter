using Aiursoft.AlertCenter.Configuration;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class GlobalSettingsControllerTests : TestBase
{
    [TestMethod]
    public async Task TestGlobalSettingsWorkflow()
    {
        await LoginAsAdmin();

        // 1. Index
        var indexResponse = await Http.GetAsync("/GlobalSettings/Index");
        indexResponse.EnsureSuccessStatusCode();
        var indexHtml = await indexResponse.Content.ReadAsStringAsync();
        Assert.Contains("Global Settings", indexHtml);
        Assert.Contains(SettingsMap.ProjectName, indexHtml);

        // 2. Edit (POST)
        var newBrandName = "My New Brand " + Guid.NewGuid();
        var editResponse = await PostForm("/GlobalSettings/Edit", new Dictionary<string, string>
        {
            { "Key", SettingsMap.BrandName },
            { "Value", newBrandName }
        });
        AssertRedirect(editResponse, "/GlobalSettings");

        // 3. Verify Edit
        var indexResponse2 = await Http.GetAsync("/GlobalSettings/Index");
        var indexHtml2 = await indexResponse2.Content.ReadAsStringAsync();
        Assert.Contains(newBrandName, indexHtml2);

        // 4. Edit (invalid key)
        var invalidEditResponse = await PostForm("/GlobalSettings/Edit", new Dictionary<string, string>
        {
            { "Key", "InvalidKey" },
            { "Value", "SomeValue" }
        });
        AssertRedirect(invalidEditResponse, "/GlobalSettings");
    }

    [TestMethod]
    public async Task TestEditConfiguredProjectNameKeepsConfigurationAndDatabaseUnchanged()
    {
        await LoginAsAdmin();
        var configuredName = GetService<IConfiguration>()[$"GlobalSettings:{SettingsMap.ProjectName}"];
        Assert.IsFalse(string.IsNullOrWhiteSpace(configuredName));
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var originalValue = await db.GlobalSettings.AsNoTracking()
            .Where(setting => setting.Key == SettingsMap.ProjectName).Select(setting => setting.Value).SingleAsync();
        var attemptedName = "Must not replace configuration " + Guid.NewGuid();

        var response = await PostForm("/GlobalSettings/Edit", new Dictionary<string, string>
        {
            { "Key", SettingsMap.ProjectName },
            { "Value", attemptedName }
        });
        AssertRedirect(response, "/GlobalSettings");

        var html = await Http.GetStringAsync("/GlobalSettings/Index");
        Assert.Contains(configuredName!, html);
        Assert.DoesNotContain(attemptedName, html);
        var savedValue = await db.GlobalSettings.AsNoTracking()
            .Where(setting => setting.Key == SettingsMap.ProjectName).Select(setting => setting.Value).SingleAsync();
        Assert.AreEqual(originalValue, savedValue);
    }

    [TestMethod]
    public async Task TestEditInvalidModel()
    {
        await LoginAsAdmin();
        // Missing Key
        var response = await PostForm("/GlobalSettings/Edit", new Dictionary<string, string>
        {
            { "Value", "SomeValue" }
        });
        AssertRedirect(response, "/GlobalSettings");
    }
}

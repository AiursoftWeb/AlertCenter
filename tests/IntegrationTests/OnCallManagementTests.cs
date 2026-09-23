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
        AssertRedirect(response, manageUrl);
        var html = WebUtility.HtmlDecode(await Http.GetStringAsync(manageUrl));
        Assert.Contains("操作未完成", html);
        Assert.Contains("尚未配置班次模板", html);
        Assert.DoesNotContain("尚未配置班次模板", WebUtility.HtmlDecode(await Http.GetStringAsync(manageUrl)));
    }
}

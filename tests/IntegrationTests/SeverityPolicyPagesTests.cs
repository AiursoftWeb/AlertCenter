using System.Net;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class IncidentPagesTests
{
    [TestMethod]
    public async Task SeverityMatrixSavesAllChannelsAndRejectsIncompleteOrDuplicateRows()
    {
        await LoginAsAdmin();
        var html = await Http.GetStringAsync("/SeverityPolicy");
        Assert.Contains("SMS policy can be saved", html);
        Assert.Contains("Rules[4].ShowInLists", html);
        var values = new Dictionary<string, string>();
        for (var i = 0; i < 5; i++)
        {
            values[$"Rules[{i}].Level"] = $"sev{i}";
            values[$"Rules[{i}].Email"] = (i == 4).ToString();
            values[$"Rules[{i}].Sms"] = (i == 3).ToString();
            values[$"Rules[{i}].Webhook"] = (i == 2).ToString();
            values[$"Rules[{i}].InApp"] = (i == 1).ToString();
            values[$"Rules[{i}].ShowInLists"] = (i == 0).ToString();
        }
        using var noToken = await PostForm("/SeverityPolicy/Save", values, "/SeverityPolicy", includeToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, noToken.StatusCode);
        using var saved = await PostForm("/SeverityPolicy/Save", values, "/SeverityPolicy");
        Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var rows = await db.SeverityRules.OrderBy(rule => rule.Level).ToListAsync();
            Assert.AreEqual(5, rows.Count);
            for (var i = 0; i < 5; i++)
            {
                Assert.AreEqual(i == 4, rows[i].Email);
                Assert.AreEqual(i == 3, rows[i].Sms);
                Assert.AreEqual(i == 2, rows[i].Webhook);
                Assert.AreEqual(i == 1, rows[i].InApp);
                Assert.AreEqual(i == 0, rows[i].ShowInLists);
            }
        }
        values["Rules[4].Level"] = "sev3";
        using var duplicate = await PostForm("/SeverityPolicy/Save", values, "/SeverityPolicy");
        Assert.AreEqual(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Contains("Submit exactly one rule", await duplicate.Content.ReadAsStringAsync());
        using var incomplete = await PostForm("/SeverityPolicy/Save", new(), "/SeverityPolicy");
        Assert.Contains("Submit exactly one rule", await incomplete.Content.ReadAsStringAsync());
        using var finalScope = Server!.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.IsTrue((await finalDb.SeverityRules.SingleAsync(rule => rule.Level == "sev4")).Email);
    }

    [TestMethod]
    public async Task SeverityPolicyRequiresManagePushPermission()
    {
        await RegisterAndLoginAsync();
        using var get = await Http.GetAsync("/SeverityPolicy");
        Assert.AreEqual(HttpStatusCode.Redirect, get.StatusCode);
        Assert.Contains("/Error/Code403", get.Headers.Location!.ToString());
        using var post = await PostForm("/SeverityPolicy/Save", new(), "/");
        Assert.AreEqual(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Contains("/Error/Code403", post.Headers.Location!.ToString());
    }

    [TestMethod]
    public async Task HiddenSeverityCanBeExplicitlyBrowsedWithoutChangingResponseCounts()
    {
        var key = await CreateApplication();
        using var report = await Report(key, Guid.NewGuid(), level: "sev4");
        Assert.AreEqual(HttpStatusCode.Created, report.StatusCode);
        string title;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var incident = await db.Incidents.SingleAsync();
            incident.Title = "Quiet incident deliberately hidden";
            title = incident.Title;
            (await db.SeverityRules.SingleAsync(rule => rule.Level == "sev4")).ShowInLists = false;
            await db.SaveChangesAsync();
        }
        Assert.DoesNotContain(title, await Http.GetStringAsync("/Incidents"));
        Assert.DoesNotContain(title, await Http.GetStringAsync("/Dashboard"));
        Assert.Contains(title, await Http.GetStringAsync("/Incidents?level=sev4"));
        Assert.Contains(title, await Http.GetStringAsync("/Incidents?level=lte-sev4"));
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            (await db.SeverityRules.SingleAsync(rule => rule.Level == "sev4")).ShowInLists = true;
            await db.SaveChangesAsync();
        }
        Assert.Contains(title, await Http.GetStringAsync("/Incidents"));
        Assert.Contains(title, await Http.GetStringAsync("/Dashboard"));
    }
}

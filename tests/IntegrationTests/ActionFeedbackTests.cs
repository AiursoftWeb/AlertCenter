using System.Net;
using System.Text.RegularExpressions;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class AlertDemoTests
{
    private async Task<HttpResponseMessage> DialogPost(string action, Dictionary<string, string> fields, string tokenUrl)
    {
        fields["__RequestVerificationToken"] = await GetAntiCsrfToken(tokenUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, action) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Add("X-AlertCenter-Dialog", "true");
        request.Headers.Accept.ParseAdd("application/json");
        return await Http.SendAsync(request);
    }

    [TestMethod]
    public async Task IncidentActionErrorsStayStructuredWhileNormalPostsKeepOriginalStatusAndRedirects()
    {
        var key = await CreateApplication();
        using var reported = await Report(key, Guid.NewGuid());
        Guid id;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            id = (await db.Incidents.SingleAsync()).Id;
        }
        var url = $"/Incidents/Details/{id}";
        var html = await Http.GetStringAsync(url);
        Assert.AreEqual(1, Regex.Matches(html, "id=\"action-feedback\"").Count);
        Assert.Contains("aria-describedby=\"action-feedback-message\"", html);
        Assert.Contains("/scripts/action-feedback.js", html);
        Assert.Contains("data-action-feedback", html);
        var stale = await IncidentForm(id);
        using var ack = await DialogPost("/Incidents/Acknowledge", await IncidentForm(id), url);
        Assert.AreEqual(HttpStatusCode.OK, ack.StatusCode);
        Assert.AreEqual(url, JObject.Parse(await ack.Content.ReadAsStringAsync())["redirectUrl"]!.Value<string>());
        using var alreadyOwner = await DialogPost("/Incidents/TakeOwnership", await IncidentForm(id), url);
        Assert.AreEqual(HttpStatusCode.BadRequest, alreadyOwner.StatusCode);
        Assert.AreEqual("application/json", alreadyOwner.Content.Headers.ContentType!.MediaType);
        Assert.AreEqual("你已经是当前负责人。", JObject.Parse(await alreadyOwner.Content.ReadAsStringAsync())["message"]!.Value<string>());
        using var staleResponse = await DialogPost("/Incidents/Mitigate", stale, url);
        Assert.AreEqual(HttpStatusCode.Conflict, staleResponse.StatusCode);
        Assert.IsTrue(JObject.Parse(await staleResponse.Content.ReadAsStringAsync())["refresh"]!.Value<bool>());
        using var normal = await PostForm("/Incidents/TakeOwnership", await IncidentForm(id), url);
        Assert.AreEqual(HttpStatusCode.BadRequest, normal.StatusCode);
        Assert.AreNotEqual("application/json", normal.Content.Headers.ContentType!.MediaType);
    }

    [TestMethod]
    public async Task DialogValidationPreservesCreationFormAndOneTimeKeyIsNotConsumedByFetch()
    {
        await LoginAsAdmin();
        using var invalid = await DialogPost("/Alerts/CreateApplication", new() { ["Name"] = "   " }, "/Alerts/Applications");
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.IsNotNull(JObject.Parse(await invalid.Content.ReadAsStringAsync())["message"]);
        using var created = await DialogPost("/Alerts/CreateApplication", new() { ["Name"] = "Dialog application" }, "/Alerts/Applications");
        Assert.AreEqual(HttpStatusCode.OK, created.StatusCode);
        var redirect = JObject.Parse(await created.Content.ReadAsStringAsync())["redirectUrl"]!.Value<string>()!;
        var page = await Http.GetStringAsync(redirect);
        Assert.Contains("id=\"api-key\"", page);
        Assert.Contains("Dialog application", page);
        Assert.DoesNotContain("id=\"api-key\"", await Http.GetStringAsync(redirect));
    }

    [TestMethod]
    public async Task RuleDeletionConflictUsesDialogWithoutChangingJsonScheduleEndpoints()
    {
        await LoginAsAdmin();
        Guid ruleId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var rule = new OnCallRule { Name = "Bound" };
            db.OnCallGroups.Add(new OnCallGroup { Name = "Team", Rule = rule });
            await db.SaveChangesAsync();
            ruleId = rule.Id;
        }
        using var response = await DialogPost("/OnCall/DeleteRule", new() { ["id"] = ruleId.ToString() }, "/OnCall/Index");
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("仍被团队绑定", JObject.Parse(await response.Content.ReadAsStringAsync())["message"]!.Value<string>()!);
        using var page = await Http.GetAsync("/OnCall/Index");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("data-action-feedback", html);
        Assert.Contains("/scripts/action-feedback.js", html);
    }

    [TestMethod]
    public async Task PlainTextReasonsRenderWithoutTemplateIndentationOrHtmlExecution()
    {
        var key = await CreateApplication();
        using var reported = await Report(key, Guid.NewGuid());
        Guid id;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            id = (await db.Incidents.SingleAsync()).Id;
        }
        var url = $"/Incidents/Details/{id}";
        using var note = await PostForm("/Incidents/AddNote", await IncidentForm(id, "造成了数据库错误\n<script>alert('unsafe')</script>"), url);
        AssertRedirect(note, url);
        var html = await Http.GetStringAsync(url);
        Assert.Contains("class=\"ac-prose ac-prose-note", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>alert('unsafe')</script>", html);
        Assert.IsTrue(Regex.IsMatch(html, "class=\"ac-prose ac-prose-note[^\"]*\">&#x", RegexOptions.CultureInvariant));
        Assert.DoesNotContain("white-space: pre-wrap; line-height: 1.6;", html);
    }
}

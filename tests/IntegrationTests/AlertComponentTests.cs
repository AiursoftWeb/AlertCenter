using System.Net;
using System.Text.RegularExpressions;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class IncidentPagesTests
{
    [TestMethod]
    public async Task UiStackAlertComponentsRenderGuideAccessAndApplicationDialogs()
    {
        var key = await CreateApplication();
        var applicationId = Guid.Parse(key.Split('.')[0]);
        var guide = AssertUiStackAlerts(await Http.GetStringAsync("/Applications/GettingStarted"));
        Assert.IsTrue(guide.Any(alert => alert.Classes.Contains("alert-warning")));
        Assert.IsTrue(guide.Any(alert => alert.Classes.Contains("alert-info")));
        Assert.IsTrue(guide.All(alert => alert.Root.Contains("class=\"alert-icon") && alert.Root.Contains("role=\"alert\"")));

        var access = AssertUiStackAlerts(await Http.GetStringAsync($"/Applications/Access/{applicationId}"));
        Assert.IsTrue(access.Any(alert => alert.Classes.Contains("alert-info") && alert.Root.Contains("class=\"alert-icon")));
        var applications = AssertUiStackAlerts(await Http.GetStringAsync("/Applications/Applications"));
        foreach (var id in new[] { "delete-application-description" })
        {
            var alert = applications.Single(item => item.Root.Contains($"id=\"{id}\""));
            Assert.Contains("class=\"alert-icon", alert.Root);
            Assert.Contains("role=\"alert\"", alert.Root);
            Assert.DoesNotContain("data-bs-dismiss=\"alert\"", alert.Root);
        }

        var onCall = AssertUiStackAlerts(await Http.GetStringAsync($"/Applications/OnCall/{applicationId}"));
        Assert.IsTrue(onCall.Any(alert => alert.Classes.Contains("alert-info")));
        using var saved = await PostForm("/Applications/SaveOnCall", new()
        {
            ["ApplicationId"] = applicationId.ToString()
        }, $"/Applications/OnCall/{applicationId}");
        AssertRedirect(saved, $"/Applications/OnCall/{applicationId}");
        AssertDismissibleSuccess(await Http.GetStringAsync(saved.Headers.Location));
    }

    [TestMethod]
    [DataRow("webhook", false)]
    [DataRow("email", false)]
    [DataRow("inapp", false)]
    [DataRow("webhook", true)]
    [DataRow("email", true)]
    [DataRow("inapp", true)]
    public async Task UiStackAlertComponentsKeepChannelValidationVisibleInsideMessage(string type, bool editing)
    {
        await LoginAsAdmin();
        Guid? channelId = null;
        if (editing)
        {
            using var scope = Server!.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var channel = new PushChannel
            {
                Name = "Alert component channel", Type = type,
                Endpoint = type == "webhook" ? "https://example.com/alerts" : null,
                EmailRecipient = type == "email" ? "oncall@example.com" : string.Empty
            };
            db.PushChannels.Add(channel);
            await db.SaveChangesAsync();
            channelId = channel.Id;
        }
        var url = editing ? $"/Push/Edit/{channelId}" : $"/Push/Create?type={type}";
        var initial = AssertUiStackAlerts(await Http.GetStringAsync(url));
        var initialValidation = initial.Single(alert => alert.Classes.Contains("alert-danger"));
        Assert.Contains("d-none", initialValidation.Classes);
        Assert.Contains("data-valmsg-summary=\"true\"", initialValidation.Message);
        if (type != "webhook")
        {
            var information = initial.Single(alert => alert.Classes.Contains("alert-info"));
            Assert.Contains("class=\"alert-icon", information.Root);
            Assert.Contains(type == "email" ? "SMTP" : "In-app notification scope", WebUtility.HtmlDecode(information.Message));
        }

        var fields = new Dictionary<string, string>
        {
            ["Type"] = type, ["Name"] = "", ["Endpoint"] = "not-a-url", ["EmailRecipient"] = "invalid"
        };
        if (channelId.HasValue) fields["Id"] = channelId.Value.ToString();
        using var invalid = await PostForm("/Push/Save", fields, url);
        Assert.AreEqual(HttpStatusCode.OK, invalid.StatusCode);
        var validation = AssertUiStackAlerts(await invalid.Content.ReadAsStringAsync())
            .Single(alert => alert.Classes.Contains("alert-danger"));
        Assert.DoesNotContain("d-none", validation.Classes);
        Assert.Contains("role=\"alert\"", validation.Root);
        Assert.Contains("validation-summary-errors", validation.Message);
        Assert.Contains("data-valmsg-summary=\"true\"", validation.Message);
        Assert.IsTrue(Regex.IsMatch(validation.Message, "<ul[^>]*>\\s*<li>.+?</li>", RegexOptions.Singleline));
        using var checkScope = Server!.Services.CreateScope();
        Assert.AreEqual(editing ? 1 : 0, await checkScope.ServiceProvider.GetRequiredService<TemplateDbContext>().PushChannels.CountAsync());
    }

    [TestMethod]
    [DataRow(PushDelivery.Failed, "alert-danger", "alert")]
    [DataRow(PushDelivery.Succeeded, "alert-success", "status")]
    [DataRow(PushDelivery.Pending, "alert-primary", "status")]
    public async Task UiStackAlertComponentsRenderDeliveryStatesWithSiblingIcon(string status, string alertClass, string role)
    {
        var key = await CreateApplication();
        using var reported = await Report(key, Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Created, reported.StatusCode);
        // Keep the test delivery pending until the page has been read.
        await Server!.Services.GetServices<IHostedService>().OfType<PushDeliveryWorker>().Single().StopAsync(CancellationToken.None);
        Guid deliveryId;
        using (var scope = Server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var delivery = new PushDelivery
            {
                IncidentId = (await db.Incidents.SingleAsync()).Id,
                Channel = new PushChannel { Name = "Delivery alert", Type = "inapp" },
                Status = status, Attempts = status == PushDelivery.Pending ? 0 : 1,
                Error = status == PushDelivery.Failed ? "Remote endpoint rejected <unsafe> payload" : null,
                AttemptedAtUtc = status == PushDelivery.Pending ? null : DateTime.UtcNow.AddSeconds(-1),
                CompletedAtUtc = status == PushDelivery.Pending ? null : DateTime.UtcNow
            };
            db.PushDeliveries.Add(delivery);
            await db.SaveChangesAsync();
            deliveryId = delivery.Id;
        }
        var alert = AssertUiStackAlerts(await Http.GetStringAsync($"/Push/Delivery/{deliveryId}"))
            .Single(item => item.Classes.Contains(alertClass));
        Assert.Contains("class=\"alert-icon", alert.Root);
        Assert.Contains($"role=\"{role}\"", alert.Root);
        if (status == PushDelivery.Failed)
        {
            Assert.Contains("Remote endpoint rejected &lt;unsafe&gt; payload", alert.Message);
            Assert.Contains("action=\"/Push/Retry\"", alert.Message);
            Assert.DoesNotContain("<unsafe>", alert.Root);
        }
        else if (status == PushDelivery.Succeeded)
        {
            Assert.Contains("Delivery completed successfully", WebUtility.HtmlDecode(alert.Message));
            Assert.Contains("flex-md-row", alert.Message);
        }
        else
        {
            Assert.Contains("spinner-border", alert.Root);
            Assert.Contains($"href=\"/Push/Delivery/{deliveryId}\"", alert.Message);
        }
    }

    [TestMethod]
    public async Task UiStackAlertComponentsKeepScheduleWarningsCollapsibleInsideMessage()
    {
        await LoginAsAdmin();
        Guid groupId;
        var start = DateTime.UtcNow.Date.AddDays(7);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var user = await db.Users.SingleAsync(item => item.Email == "admin@default.com");
            var group = new OnCallGroup
            {
                Name = "Understaffed alert team", AnchorUtc = start,
                Rule = new OnCallRule { Name = "Alert rule", MinimumStaff = 2 },
                Members = [new OnCallGroupMember { UserId = user.Id }]
            };
            db.OnCallGroups.AddRange(group, new OnCallGroup
            {
                Name = "Shared alert team", Members = [new OnCallGroupMember { UserId = user.Id }]
            });
            db.OnCallShifts.Add(new OnCallShift
            {
                Group = group, StartsAtUtc = start, EndsAtUtc = start.AddHours(8), MinimumStaff = 2,
                Members = [new OnCallShiftMember { UserId = user.Id }]
            });
            await db.SaveChangesAsync();
            groupId = group.Id;
        }
        var alerts = AssertUiStackAlerts(await Http.GetStringAsync($"/OnCall/ScheduleDetails/{groupId}?startUtc={Uri.EscapeDataString(start.ToString("O"))}"));
        var staffing = alerts.Single(alert => WebUtility.HtmlDecode(alert.Message).Contains("Insufficient staff warning"));
        Assert.Contains("alert-warning", staffing.Classes);
        Assert.IsTrue(Regex.IsMatch(staffing.Message, "<div[^>]*class=\"alert-message[^\"]*\"[^>]*>\\s*<details\\b[^>]*open[^>]*>\\s*<summary\\b", RegexOptions.Singleline));
        Assert.Contains("</summary>", staffing.Message);
        Assert.Contains("</details>", staffing.Message);
        var shared = alerts.Single(alert => alert.Root.Contains("id=\"oncall-shared-members\""));
        Assert.Contains("class=\"alert-icon", shared.Root);
        Assert.IsTrue(alerts.Any(alert => WebUtility.HtmlDecode(alert.Message).Contains("unassigned shifts")));
        var summary = alerts.Single(alert => alert.Classes.Contains("alert-primary"));
        Assert.Contains("role=\"status\"", summary.Root);
        Assert.Contains("Starting soon", WebUtility.HtmlDecode(summary.Message));
        Assert.Contains("d-flex", summary.Message);
        Assert.IsTrue(alerts.Where(alert => alert.Classes.Contains("alert-warning"))
            .All(alert => alert.Root.Contains("role=\"alert\"") && !alert.Root.Contains("data-bs-dismiss=\"alert\"")));
        var groupDetails = AssertUiStackAlerts(await Http.GetStringAsync($"/OnCall/GroupDetails/{groupId}"));
        Assert.IsTrue(groupDetails.Any(alert => alert.Classes.Contains("alert-info")));
    }

    [TestMethod]
    public async Task UiStackAlertComponentsKeepGroupAndRuleSuccessDismissible()
    {
        await LoginAsAdmin();
        using var groupCreated = await PostForm("/OnCall/CreateGroup", new()
        {
            ["GroupInput.Name"] = "Alert success team", ["GroupInput.TimeZoneId"] = "UTC"
        }, "/OnCall/GroupCreate");
        Assert.AreEqual(HttpStatusCode.Found, groupCreated.StatusCode);
        var groupLocation = groupCreated.Headers.Location!.OriginalString;
        var groupId = Guid.Parse(groupLocation.Split('/').Last());
        var groupAlerts = AssertDismissibleSuccess(await Http.GetStringAsync(groupLocation));
        Assert.IsTrue(groupAlerts.Any(alert => alert.Root.Contains("id=\"delete-oncall-group-description\"")));
        using var groupDeleted = await PostForm("/OnCall/DeleteGroup", new() { ["id"] = groupId.ToString() }, groupLocation);
        AssertRedirect(groupDeleted, "/OnCall/Groups");
        AssertDismissibleSuccess(await Http.GetStringAsync(groupDeleted.Headers.Location));

        using var ruleCreated = await PostForm("/OnCall/SaveRule", new() { ["RuleInput.Name"] = "Alert success rule" }, "/OnCall/Create");
        Assert.AreEqual(HttpStatusCode.Found, ruleCreated.StatusCode);
        var ruleLocation = ruleCreated.Headers.Location!.OriginalString;
        var ruleId = Guid.Parse(ruleLocation.Split('/').Last());
        AssertDismissibleSuccess(await Http.GetStringAsync(ruleLocation));
        using var ruleDeleted = await PostForm("/OnCall/DeleteRule", new() { ["id"] = ruleId.ToString() }, $"/OnCall/Edit/{ruleId}");
        AssertRedirect(ruleDeleted, "/OnCall");
        AssertDismissibleSuccess(await Http.GetStringAsync(ruleDeleted.Headers.Location));
    }

    private static List<(string Root, string Classes, string Message)> AssertDismissibleSuccess(string html)
    {
        var alerts = AssertUiStackAlerts(html);
        var success = alerts.Single(alert => alert.Classes.Contains("alert-success"));
        Assert.Contains("role=\"status\"", success.Root);
        Assert.Contains("alert-dismissible", success.Classes);
        Assert.IsTrue(Regex.IsMatch(success.Root, "<button[^>]*class=\"btn-close\"[^>]*data-bs-dismiss=\"alert\"[^>]*aria-label=\"[^\"]+\""));
        return alerts;
    }

    private static List<(string Root, string Classes, string Message)> AssertUiStackAlerts(string html)
    {
        const string balancedBody = @"(?:(?<depth><div\b[^>]*>)|(?<-depth></div>)|(?!</?div\b)[\s\S])*(?(depth)(?!))";
        const string dismissButton = @"(?:<button\b[^>]*data-bs-dismiss=""alert""[^>]*>[\s\S]*?</button>\s*)?";
        var alerts = new List<(string Root, string Classes, string Message)>();
        foreach (Match opening in Regex.Matches(html, @"<(?<tag>\w+)\b[^>]*\bclass=""(?<classes>[^""]*)""[^>]*>"))
        {
            var classes = opening.Groups["classes"].Value;
            if (!classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("alert")) continue;
            Assert.AreEqual("div", opening.Groups["tag"].Value, "Native details must be inside alert-message, not the flex alert root.");
            Assert.IsFalse(Regex.IsMatch(classes, @"(?:^|\s)(?:p[trblxyse]?-(?:\w+-)?\d+|d-flex|flex-\S+|gap-\S+|align-items-\S+|justify-content-\S+)(?:\s|$)"), opening.Value);
            Assert.IsFalse(Regex.IsMatch(opening.Value, "style=\"[^\"]*(?:padding|display\\s*:\\s*flex)", RegexOptions.IgnoreCase), opening.Value);
            var root = Regex.Match(html[opening.Index..], @"\A<div\b[^>]*>(?<body>" + balancedBody + @")</div>");
            Assert.IsTrue(root.Success, opening.Value);
            var children = Regex.Match(root.Groups["body"].Value,
                @"\A\s*" + dismissButton +
                @"(?:<div\b[^>]*class=""[^""]*\balert-icon\b[^""]*""[^>]*>" + balancedBody + @"</div>\s*)?" +
                @"(?<message><div\b[^>]*class=""[^""]*\balert-message\b[^""]*""[^>]*>" + balancedBody + @"</div>)\s*" + dismissButton + @"\z");
            Assert.IsTrue(children.Success, $"Alert must contain a direct alert-message and optional sibling icon/dismiss button: {root.Value}");
            alerts.Add((root.Value, classes, children.Groups["message"].Value));
        }
        Assert.IsNotEmpty(alerts, "Expected the rendered page to contain an alert component.");
        return alerts;
    }
}

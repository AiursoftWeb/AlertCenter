using System.Net;
using System.Text.RegularExpressions;
using Aiursoft.AlertCenter.Entities;
using Microsoft.AspNetCore.WebUtilities;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class IncidentPagesTests
{
    private async Task<Incident[]> SeedIncidentFilters()
    {
        await LoginAsAdmin();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var orders = new SourceApplication { OwnerId = db.Users.Single(user => user.Email == "admin@default.com").Id, Name = "Orders application" };
        var inventory = new SourceApplication { OwnerId = db.Users.Single(user => user.Email == "admin@default.com").Id, Name = "Inventory application" };
        var start = new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);
        var messages = new[]
        {
            new Incident { Application = orders, ReportId = Guid.NewGuid(), Title = "Needle at start", Level = "sev1", ReceivedAtUtc = start },
            new Incident { Application = orders, ReportId = Guid.NewGuid(), Title = "Body match", Content = new string('x', 300) + " NEEDLE 50%_ <script>", Level = "sev1", ReceivedAtUtc = start.AddDays(1).AddTicks(-1) },
            new Incident { Application = orders, ReportId = Guid.NewGuid(), Title = "Different severity", Content = "needle", Level = "sev4", ReceivedAtUtc = start.AddHours(1) },
            new Incident { Application = inventory, ReportId = Guid.NewGuid(), Title = "Different application", Content = "needle", Level = "sev1", ReceivedAtUtc = start.AddHours(2) },
            new Incident { Application = orders, ReportId = Guid.NewGuid(), Title = "Next day", Content = "needle", Level = "sev1", ReceivedAtUtc = start.AddDays(1) },
            new Incident { Application = orders, ReportId = Guid.NewGuid(), Title = "Previous day", Content = "needle", Level = "sev1", ReceivedAtUtc = start.AddTicks(-1) }
        };
        db.Incidents.AddRange(messages);
        await db.SaveChangesAsync();
        return messages;
    }

    private static Guid[] AlertIncidentIds(string html) => Regex.Matches(html, "href=\"/Incidents/Details/([a-fA-F0-9-]+)\"")
        .Select(match => Guid.Parse(match.Groups[1].Value)).Distinct().ToArray();

    [TestMethod]
    public async Task AlertMessagesSupportCombinedFiltersWithInclusiveDatesAndLiteralKeywords()
    {
        var messages = await SeedIncidentFilters();
        var url = $"/Incidents?keyword=NeEdLe&applicationId={messages[0].ApplicationId}&level=sev1" +
            "&fromDate=2026-09-08&toDate=2026-09-08";
        CollectionAssert.AreEquivalent(new[] { messages[0].Id, messages[1].Id }, AlertIncidentIds(await Http.GetStringAsync(url)));
        CollectionAssert.AreEqual(new[] { messages[1].Id }, AlertIncidentIds(await Http.GetStringAsync(
            "/Incidents?keyword=" + Uri.EscapeDataString("50%_"))));
        var escaped = await Http.GetStringAsync("/Incidents?keyword=" + Uri.EscapeDataString("<script>"));
        CollectionAssert.AreEqual(new[] { messages[1].Id }, AlertIncidentIds(escaped));
        Assert.Contains("&lt;script&gt;", escaped);
        Assert.DoesNotContain("value=\"<script>\"", escaped);
        Assert.AreEqual(6, AlertIncidentIds(await Http.GetStringAsync("/Incidents?toDate=9999-12-31")).Length);
    }

    [TestMethod]
    [DataRow("sev1")]
    [DataRow("lte-sev3")]
    public async Task AlertMessageFiltersRenderControlsAndPersistThroughPagination(string level)
    {
        var messages = await SeedIncidentFilters();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            for (var index = 0; index < 20; index++)
            {
                db.Incidents.Add(new Incident
                {
                    ApplicationId = messages[0].ApplicationId, ReportId = Guid.NewGuid(), Title = "Needle page " + index,
                    Level = "sev1", ReceivedAtUtc = messages[0].ReceivedAtUtc.AddMinutes(index + 1)
                });
            }
            await db.SaveChangesAsync();
        }

        var query = $"keyword=needle&applicationId={messages[0].ApplicationId}&level={level}&fromDate=2026-09-08&toDate=2026-09-08";
        var html = await Http.GetStringAsync("/Incidents?" + query);
        Assert.Contains("id=\"incident-keyword\"", html);
        Assert.Contains("id=\"incident-from-date\"", html);
        Assert.Contains("id=\"incident-to-date\"", html);
        Assert.AreEqual(20, AlertIncidentIds(html).Length);
        var next = WebUtility.HtmlDecode(Regex.Match(html, "href=\"([^\"]+)\"[^>]*>Next page</a>").Groups[1].Value);
        Assert.IsFalse(string.IsNullOrEmpty(next));
        var parameters = QueryHelpers.ParseQuery(new Uri(Http.BaseAddress!, next).Query);
        foreach (var parameter in QueryHelpers.ParseQuery("?" + query))
            Assert.AreEqual(parameter.Value.ToString(), parameters[parameter.Key].ToString());
        Assert.AreEqual("2", parameters["page"].ToString());
        Assert.AreEqual(2, AlertIncidentIds(await Http.GetStringAsync(next)).Length);
    }

    [TestMethod]
    [DataRow("sev0", 0, 0)]
    [DataRow("sev1", 1, 1)]
    [DataRow("sev2", 2, 2)]
    [DataRow("sev3", 3, 3)]
    [DataRow("sev4", 4, 4)]
    [DataRow("lte-sev1", 0, 1)]
    [DataRow("lte-sev2", 0, 2)]
    [DataRow("lte-sev3", 0, 3)]
    [DataRow("lte-sev4", 0, 4)]
    public async Task SeverityFiltersIncludeExactlyTheRequestedLevels(string level, int first, int last)
    {
        await LoginAsAdmin();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var application = new SourceApplication
        {
            OwnerId = db.Users.Single(user => user.Email == "admin@default.com").Id,
            Name = "Severity filter application"
        };
        var incidents = Enumerable.Range(0, 5).Select(index => new Incident
        {
            Application = application, ReportId = Guid.NewGuid(), Title = "Severity " + index,
            Level = "sev" + index
        }).ToArray();
        db.Incidents.AddRange(incidents);
        db.Incidents.Add(new Incident
        {
            Application = new SourceApplication { Name = "Unshared application" },
            ReportId = Guid.NewGuid(), Title = "Private critical incident", Level = "sev0"
        });
        await db.SaveChangesAsync();

        var html = await Http.GetStringAsync("/Incidents?level=" + level);
        CollectionAssert.AreEquivalent(incidents.Skip(first).Take(last - first + 1).Select(incident => incident.Id).ToArray(),
            AlertIncidentIds(html));
        Assert.IsTrue(Regex.IsMatch(html, "<option value=\"" + level + "\" selected(?:=\"selected\")?>"));
    }

    [TestMethod]
    [DataRow("keyword=" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [DataRow("fromDate=invalid")]
    [DataRow("fromDate=2026-09-09&toDate=2026-09-08")]
    [DataRow("applicationId=invalid")]
    [DataRow("level=sev5")]
    [DataRow("level=lte-sev5")]
    [DataRow("level=gt-sev3")]
    public async Task AlertMessageInvalidFiltersShowValidationInsteadOfUnfilteredResults(string query)
    {
        await SeedIncidentFilters();
        var response = await Http.GetAsync("/Incidents?" + query);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("validation-summary-errors", html);
        Assert.AreEqual(0, AlertIncidentIds(html).Length);
    }
}

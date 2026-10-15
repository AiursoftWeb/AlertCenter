using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

public partial class IncidentWorkflowTests
{
    [TestMethod]
    [DataRow("cache:A", "cache:a")]
    [DataRow("cafe", "café")]
    public async Task DistinctIncidentKeysDoNotShareAnActiveIncident(string firstKey, string secondKey)
    {
        var application = await CreateApplication();
        var first = await Report(application, "Outage", "sev1", firstKey);
        var second = await Report(application, "Outage", "sev1", secondKey);
        var repeated = await Report(application, "Different title", "sev2", firstKey);

        Assert.AreNotEqual(first.IncidentId, second.IncidentId);
        Assert.AreEqual(first.IncidentId, repeated.IncidentId);
        Assert.AreEqual(2, await _db.Incidents.CountAsync());
    }

    [TestMethod]
    [DataRow("Cache failure", "cache failure")]
    [DataRow("Cafe failure", "Café failure")]
    [DataRow("Cache failure", "Cache failure ")]
    public async Task DistinctFallbackTitlesDoNotShareAnActiveIncident(string firstTitle, string secondTitle)
    {
        var application = await CreateApplication();
        var first = await Report(application, firstTitle, "sev1", null);
        var second = await Report(application, secondTitle, "sev1", null);
        var repeated = await Report(application, firstTitle, "sev1", null);

        Assert.AreNotEqual(first.IncidentId, second.IncidentId);
        Assert.AreEqual(first.IncidentId, repeated.IncidentId);
        Assert.AreEqual(2, await _db.Incidents.CountAsync());
    }

    [TestMethod]
    [DataRow("cache:A", "cache:a", true)]
    [DataRow("cafe", "café", true)]
    [DataRow("Cache failure", "cache failure", false)]
    [DataRow("Cafe failure", "Café failure", false)]
    [DataRow("Cache failure", "Cache failure ", false)]
    public async Task MatchingWithoutActiveKeyStillUsesExactKeysAndTitles(string first, string second, bool useKey)
    {
        var application = await CreateApplication();
        var original = new Incident
        {
            ApplicationId = application.Id, Title = first, AlertTitle = useKey ? "Outage" : first,
            IncidentKey = useKey ? first : null, MatchLevel = "sev1", AlertLevel = "sev1"
        };
        _db.Incidents.Add(original);
        await _db.SaveChangesAsync();

        var different = await Report(application, useKey ? "Outage" : second, "sev1", useKey ? second : null);
        var matching = await Report(application, useKey ? "Outage" : first, "sev1", useKey ? first : null);

        Assert.AreNotEqual(original.Id, different.IncidentId);
        Assert.AreEqual(original.Id, matching.IncidentId);
        Assert.AreEqual(2, await _db.Incidents.CountAsync());
    }
}

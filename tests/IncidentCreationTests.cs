using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

public partial class IncidentWorkflowTests
{
    [TestMethod]
    [DataRow("00000000-0000-0000-0000-000000000000", "402239")]
    [DataRow("00112233-4455-6677-8899-aabbccddeeff", "143980")]
    [DataRow("00000000-0000-0000-0000-000000000036", "000512")]
    public void ConversationReferenceIsStableAcrossIncidentInstances(string id, string expected)
    {
        var first = new Incident { Id = Guid.Parse(id) };
        var reloaded = new Incident { Id = Guid.Parse(id), Title = "Changed title", Status = Incident.Resolved };
        Assert.AreEqual(expected, first.ShortId);
        Assert.AreEqual(expected, reloaded.ShortId);
    }

    [TestMethod]
    public async Task IdenticalReportsCreateIndependentIncidentsAndDeliveries()
    {
        var application = await CreateApplication();
        var first = await Report(application, "Outage", "sev1");
        var second = await Report(application, "Outage", "sev1");
        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreEqual(2, await _db.Incidents.CountAsync());
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync());
        Assert.AreEqual(2, await _db.IncidentTimelineEntries.CountAsync());
    }
}

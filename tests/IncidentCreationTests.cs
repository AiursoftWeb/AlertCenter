using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

public partial class IncidentWorkflowTests
{
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

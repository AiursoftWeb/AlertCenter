using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class IncidentSearchTests : TestBase
{
    [TestMethod]
    public async Task SearchFiltersByIncidentAndApplicationFields()
    {
        await LoginAsAdmin();
        Guid ordersId;
        Guid billingId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var orders = new SourceApplication { Name = "Orders" };
            var billing = new SourceApplication { Name = "Billing" };
            db.Incidents.AddRange(
                new Incident
                {
                    Application = orders, Title = "Payment outage", IncidentKey = "payments-2026",
                    AlertTitle = "Provider unavailable", AlertLevel = "sev1", Status = Incident.New,
                    TriggeredAtUtc = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc)
                },
                new Incident
                {
                    Application = billing, Title = "Invoice delay", AlertTitle = "Queue backlog", AlertLevel = "sev3",
                    Status = Incident.Resolved, TriggeredAtUtc = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc)
                });
            await db.SaveChangesAsync();
            ordersId = orders.Id;
            billingId = billing.Id;
        }

        foreach (var query in new[]
                 {
                     "keyword=payment%20outage", "keyword=provider%20unavailable", "keyword=orders", "keyword=payments-2026",
                     "applicationId=" + ordersId, "status=new", "level=sev1", "fromDate=2026-09-10&toDate=2026-09-10"
                 })
        {
            var html = await Http.GetStringAsync("/Incidents/Index?" + query);
            Assert.Contains("Payment outage", html);
            Assert.DoesNotContain("Invoice delay", html);
        }

        var resolved = await Http.GetStringAsync("/Incidents/Index?applicationId=" + billingId + "&status=resolved&level=sev3");
        Assert.Contains("Invoice delay", resolved);
        Assert.DoesNotContain("Payment outage", resolved);
        var noMatch = await Http.GetStringAsync("/Incidents/Index?keyword=missing");
        Assert.Contains("name=\"keyword\" value=\"missing\"", noMatch);
        Assert.DoesNotContain("Payment outage", noMatch);
    }
}

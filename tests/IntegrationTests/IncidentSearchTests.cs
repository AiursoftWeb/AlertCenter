using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class IncidentSearchTests : TestBase
{
    [TestMethod]
    public async Task SearchMatchesIncidentAndApplicationFields()
    {
        await LoginAsAdmin();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var orders = new SourceApplication { Name = "Orders" };
            var billing = new SourceApplication { Name = "Billing" };
            db.Incidents.AddRange(
                new Incident
                {
                    Application = orders, Title = "Payment outage", IncidentKey = "payments-2026",
                    AlertTitle = "Provider unavailable", AlertLevel = "sev1"
                },
                new Incident
                {
                    Application = billing, Title = "Invoice delay", AlertTitle = "Queue backlog", AlertLevel = "sev3"
                });
            await db.SaveChangesAsync();
        }

        foreach (var keyword in new[] { "payment outage", "provider unavailable", "orders", "payments-2026" })
        {
            var html = await Http.GetStringAsync("/Incidents/Index?keyword=" + Uri.EscapeDataString(keyword));
            Assert.Contains("Payment outage", html);
            Assert.DoesNotContain("Invoice delay", html);
            Assert.Contains("value=\"" + keyword + "\"", html);
        }

        var noMatch = await Http.GetStringAsync("/Incidents/Index?keyword=missing");
        Assert.Contains("name=\"keyword\" value=\"missing\"", noMatch);
        Assert.DoesNotContain("Payment outage", noMatch);
    }
}

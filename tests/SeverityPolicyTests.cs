using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

public partial class PushCenterTests
{
    [TestMethod]
    [DataRow("sev0", true, true)]
    [DataRow("sev1", true, true)]
    [DataRow("sev2", true, true)]
    [DataRow("sev3", true, false)]
    [DataRow("sev4", false, false)]
    public async Task DefaultPolicySeparatesNotificationChannelsFromVisibility(string level, bool email, bool sms)
    {
        var policy = await new SeverityPolicyStore(_db).LoadAsync();
        Assert.AreEqual(email, policy.Allows(level, "email"));
        Assert.AreEqual(sms, policy.Allows(level, "sms"));
        Assert.IsTrue(policy.VisibleIncidents.Compile()(new Incident { Level = level }));
        Assert.IsFalse(policy.Allows(level, "unknown"));
        Assert.IsFalse(policy.Allows("invalid", "email"));
    }

    [TestMethod]
    [DataRow("email")]
    [DataRow("webhook")]
    [DataRow("inapp")]
    public async Task PolicyChangesSuppressPendingDeliveriesAndRetriesForAnySeverity(string channelType)
    {
        var app = await Application();
        var channel = await Channel(channelType);
        await BindAllLevels(app.Id, channel.Id);
        await Report(app, Guid.NewGuid(), "sev1");
        var delivery = await _db.PushDeliveries.SingleAsync();
        var rule = await _db.SeverityRules.SingleAsync(item => item.Level == "sev1");
        rule.Email = false;
        rule.Webhook = false;
        rule.InApp = false;
        await _db.SaveChangesAsync();
        await _processor.ProcessAsync(delivery.Id, default);
        Assert.AreEqual(PushDelivery.Suppressed, delivery.Status);
        Assert.AreEqual(0, _email.Messages.Count);
        Assert.AreEqual(0, _http.Requests.Count);
        Assert.AreEqual(0, await _db.InAppNotifications.CountAsync());
        delivery.Status = PushDelivery.Pending;
        await _db.SaveChangesAsync();
        await _processor.ProcessAsync(delivery.Id, default);
        Assert.AreEqual(PushDelivery.Suppressed, delivery.Status);
        await Report(app, Guid.NewGuid(), "sev1");
        Assert.AreEqual(1, await _db.PushDeliveries.CountAsync());
    }

    [TestMethod]
    public async Task PolicyCanEnableSev4EmailWithoutEnablingOtherChannelsOrChangingBindings()
    {
        var app = await Application();
        var email = await Channel("email");
        var webhook = await Channel();
        await BindAllLevels(app.Id, email.Id, webhook.Id);
        var rule = await _db.SeverityRules.SingleAsync(item => item.Level == "sev4");
        rule.Email = true;
        await _db.SaveChangesAsync();
        await Report(app, Guid.NewGuid(), "sev4");
        var delivery = await _db.PushDeliveries.SingleAsync();
        Assert.AreEqual(email.Id, delivery.ChannelId);
        await _processor.ProcessAsync(delivery.Id, default);
        Assert.AreEqual(PushDelivery.Succeeded, delivery.Status);
        Assert.AreEqual(1, _email.Messages.Count);
        Assert.AreEqual(0, _http.Requests.Count);
    }

    [TestMethod]
    public async Task VisibilityPolicySupportsNoncontiguousAndEmptySelectionsInSql()
    {
        var app = await Application();
        foreach (var rule in await _db.SeverityRules.ToListAsync())
        {
            _db.Incidents.Add(new Incident { Application = app, Level = rule.Level, Title = rule.Level });
            rule.ShowInLists = rule.Level is "sev1" or "sev4";
        }
        await _db.SaveChangesAsync();
        var store = new SeverityPolicyStore(_db);
        var policy = await store.LoadAsync();
        CollectionAssert.AreEqual(new[] { "sev1", "sev4" }, await _db.Incidents.Where(policy.VisibleIncidents)
            .OrderBy(incident => incident.Level).Select(incident => incident.Level).ToArrayAsync());
        foreach (var rule in await _db.SeverityRules.ToListAsync()) rule.ShowInLists = false;
        await _db.SaveChangesAsync();
        policy = await store.LoadAsync();
        Assert.AreEqual(0, await _db.Incidents.Where(policy.VisibleIncidents).CountAsync());
        Assert.AreEqual(5, await _db.Incidents.CountAsync());
    }
}

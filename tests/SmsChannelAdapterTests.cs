using Aiursoft.AlertCenter.Configuration;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TencentCloud.Common;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class SmsChannelAdapterTests
{
    private SqliteContext _db = null!;
    private FakeSmsSender _sender = null!;
    private SmsSettings _settings = null!;
    private SmsChannelAdapter _adapter = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.MigrateAsync();
        _sender = new FakeSmsSender();
        _settings = new SmsSettings
        {
            SecretId = "AKID-test",
            SecretKey = "secret-test",
            SmsSdkAppId = "1401224081",
            SignName = "TestSign",
            Templates = new SmsTemplateSettings
            {
                NewIncident = "tpl-new",
                Escalated = "tpl-escalated",
                Mitigated = "tpl-mitigated",
                Resolved = "tpl-resolved",
                Reopened = "tpl-reopened"
            },
            Region = "ap-guangzhou"
        };
        _adapter = new SmsChannelAdapter(_db, _sender, Options.Create(_settings), TestLocalization.For<SmsChannelAdapter>());
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _db.DisposeAsync();
    }

    [TestMethod]
    public void SmsSettingsValidationAndTemplateResolutionWorkForAllFiveScenarios()
    {
        var invalid = new SmsSettings();
        Assert.IsFalse(invalid.TryValidate(out var error));
        Assert.IsNotNull(error);

        Assert.IsTrue(_settings.TryValidate(out var okError));
        Assert.IsNull(okError);
        Assert.AreEqual("tpl-new", _settings.ResolveTemplateId(null));
        Assert.AreEqual("tpl-escalated", _settings.ResolveTemplateId(IncidentTimelineEntry.LevelChanged));
        Assert.AreEqual("tpl-mitigated", _settings.ResolveTemplateId(IncidentTimelineEntry.Mitigated));
        Assert.AreEqual("tpl-resolved", _settings.ResolveTemplateId(IncidentTimelineEntry.Resolved));
        Assert.AreEqual("tpl-reopened", _settings.ResolveTemplateId(IncidentTimelineEntry.Reopened));

        // Missing any single one of the 5 templates must fail validation.
        var missingOne = new SmsSettings
        {
            SecretId = "AKID-test",
            SecretKey = "secret-test",
            SmsSdkAppId = "1401224081",
            SignName = "TestSign",
            Templates = new SmsTemplateSettings
            {
                NewIncident = "tpl-new",
                Escalated = "tpl-escalated",
                Mitigated = "tpl-mitigated",
                Resolved = "tpl-resolved",
                Reopened = ""
            },
            Region = "ap-guangzhou"
        };
        Assert.IsFalse(missingOne.TryValidate(out var missingError));
        Assert.IsNotNull(missingError);
    }

    [TestMethod]
    [DataRow("+8613800138000", true, "+8613800138000")]
    [DataRow("+86 138-0013-8000", true, "+8613800138000")]
    [DataRow("+1 (415) 555-2671", true, "+14155552671")]
    [DataRow("13800138000", false, "")]
    [DataRow("+0123456789", false, "")]
    [DataRow("+86123", false, "")]
    [DataRow("", false, "")]
    public void PhoneNumberNormalizationEnforcesE164(string input, bool expectedValid, string expectedNormalized)
    {
        var ok = SmsChannelAdapter.TryNormalizePhoneNumber(input, out var normalized, out var error);
        Assert.AreEqual(expectedValid, ok);
        Assert.AreEqual(expectedNormalized, normalized);
        Assert.AreEqual(!expectedValid, error != null);
    }

    [TestMethod]
    public void MaskPhoneNumberHidesMiddleDigits()
    {
        Assert.AreEqual("+861****8000", SmsChannelAdapter.MaskPhoneNumber("+8613800138000"));
        Assert.AreEqual("", SmsChannelAdapter.MaskPhoneNumber(null));
    }

    [TestMethod]
    public async Task DeliversDeduplicatedSmsWithAppNameAndLevelParams()
    {
        var role = new IdentityRole("Ops") { NormalizedName = "OPS" };
        _db.Roles.Add(role);
        var alice = new User { Id = "u-alice", UserName = "alice", DisplayName = "Alice", PhoneNumber = "+8613800138001" };
        var bob = new User { Id = "u-bob", UserName = "bob", DisplayName = "Bob", PhoneNumber = "+8613800138002" };
        var charlie = new User { Id = "u-charlie", UserName = "charlie", DisplayName = "Charlie", PhoneNumber = null };
        var dupPhone = new User { Id = "u-dup", UserName = "dup", DisplayName = "Dup", PhoneNumber = "+86 138-0013-8001" };
        _db.Users.AddRange(alice, bob, charlie, dupPhone);
        _db.UserRoles.Add(new IdentityUserRole<string> { UserId = bob.Id, RoleId = role.Id });
        _db.UserRoles.Add(new IdentityUserRole<string> { UserId = charlie.Id, RoleId = role.Id });

        var app = new SourceApplication { Name = "Payments", Owner = alice };
        var channel = new PushChannel { Name = "SMS Ops", Type = "sms", IsEnabled = true };
        _db.SourceApplications.Add(app);
        _db.PushChannels.Add(channel);
        _db.PushChannelUsers.AddRange(
            new PushChannelUser { Channel = channel, UserId = alice.Id },
            new PushChannelUser { Channel = channel, UserId = dupPhone.Id });
        _db.PushChannelRoles.Add(new PushChannelRole { Channel = channel, RoleId = role.Id });
        _db.ApplicationPushChannels.Add(new ApplicationPushChannel { Application = app, Channel = channel });
        await _db.SaveChangesAsync();

        var incident = new Incident { Application = app, Title = "Database latency spike", Level = "sev1", ReportId = Guid.NewGuid() };
        _db.Incidents.Add(incident);
        var dispatcher = new QueuedIncidentDispatcher(_db, TestLocalization.Factory);
        await dispatcher.DispatchAsync(incident);
        await _db.SaveChangesAsync();

        var delivery = await _db.PushDeliveries.SingleAsync();
        var processor = new PushDeliveryProcessor(_db, [_adapter], NullLogger<PushDeliveryProcessor>.Instance, TestLocalization.For<PushDeliveryProcessor>());
        await processor.ProcessAsync(delivery.Id, CancellationToken.None);

        Assert.AreEqual(PushDelivery.Succeeded, delivery.Status);
        Assert.AreEqual(1, _sender.Messages.Count);
        CollectionAssert.AreEquivalent(new[] { "+8613800138001", "+8613800138002" }, _sender.Messages[0].PhoneNumbers.ToArray());
        Assert.AreEqual("tpl-new", _sender.Messages[0].TemplateId);
        CollectionAssert.AreEqual(new[] { "Payments", "SEV1" }, _sender.Messages[0].TemplateParams.ToArray());
    }

    [TestMethod]
    public async Task OnCallSmsRespectsGroupToggleAndDeliversAllFiveLifecycleTemplates()
    {
        var now = DateTime.UtcNow;
        var oce = new User { Id = "u-oce", UserName = "oce", DisplayName = "OnCall Alice", Email = "oce@example.com", PhoneNumber = "+8613900139000" };
        _db.Users.Add(oce);
        var groupOff = new OnCallGroup { Name = "Team Off", SmsNotificationsEnabled = false };
        var groupOn = new OnCallGroup { Name = "Team On", SmsNotificationsEnabled = true };
        _db.OnCallGroups.AddRange(groupOff, groupOn);
        _db.OnCallShifts.AddRange(
            new OnCallShift
            {
                Group = groupOff, StartsAtUtc = now.AddHours(-1), EndsAtUtc = now.AddHours(2),
                Members = [new OnCallShiftMember { User = oce }]
            },
            new OnCallShift
            {
                Group = groupOn, StartsAtUtc = now.AddHours(-1), EndsAtUtc = now.AddHours(2),
                Members = [new OnCallShiftMember { User = oce }]
            });
        var appOff = new SourceApplication { Name = "AppOff", Owner = oce, OnCallGroup = groupOff };
        var appOn = new SourceApplication { Name = "Orders", Owner = oce, OnCallGroup = groupOn };
        _db.SourceApplications.AddRange(appOff, appOn);
        await _db.SaveChangesAsync();

        var dispatcher = new QueuedIncidentDispatcher(_db, TestLocalization.Factory);

        // Toggle OFF -> only email queued, no SMS.
        var incOff = new Incident { Application = appOff, Title = "Off test", Level = "sev1", OccurredAtUtc = now, ReportId = Guid.NewGuid() };
        _db.Incidents.Add(incOff);
        await dispatcher.DispatchAsync(incOff);
        await _db.SaveChangesAsync();
        Assert.AreEqual(0, await _db.OnCallEmailSnapshots.CountAsync(s => s.ChannelType == "sms"));
        Assert.AreEqual(1, await _db.OnCallEmailSnapshots.CountAsync(s => s.ChannelType == "email"));

        // Toggle ON -> new incident queues On-Call SMS with tpl-new.
        var incOn = new Incident { Application = appOn, Title = "Orders down", Level = "sev2", OccurredAtUtc = now, ReportId = Guid.NewGuid() };
        _db.Incidents.Add(incOn);
        await dispatcher.DispatchAsync(incOn);
        await _db.SaveChangesAsync();

        var processor = new PushDeliveryProcessor(_db, [_adapter], NullLogger<PushDeliveryProcessor>.Instance, TestLocalization.For<PushDeliveryProcessor>());
        var newSmsDelivery = await _db.PushDeliveries.Include(d => d.OnCallEmail)
            .SingleAsync(d => d.IncidentId == incOn.Id && d.OnCallEmail != null && d.OnCallEmail.ChannelType == "sms");
        await processor.ProcessAsync(newSmsDelivery.Id, CancellationToken.None);
        Assert.AreEqual(PushDelivery.Succeeded, newSmsDelivery.Status);
        Assert.AreEqual("tpl-new", _sender.Messages[^1].TemplateId);
        CollectionAssert.AreEqual(new[] { "Orders", "SEV2" }, _sender.Messages[^1].TemplateParams.ToArray());

        // Lifecycle transitions: Escalated, Mitigated, Resolved, Reopened.
        var transitions = new (string Type, string PrevLevel, string NewLevel, string ExpectedTemplate, string ExpectedLevelParam)[]
        {
            (IncidentTimelineEntry.LevelChanged, "sev2", "sev0", "tpl-escalated", "SEV0"),
            (IncidentTimelineEntry.Mitigated, "sev0", "sev0", "tpl-mitigated", "SEV0"),
            (IncidentTimelineEntry.Resolved, "sev0", "sev0", "tpl-resolved", "SEV0"),
            (IncidentTimelineEntry.Reopened, "sev0", "sev0", "tpl-reopened", "SEV0")
        };

        foreach (var (type, prevLevel, newLevel, expectedTemplate, expectedLevelParam) in transitions)
        {
            incOn.Level = newLevel;
            var entry = new IncidentTimelineEntry
            {
                Incident = incOn,
                Type = type,
                ActorName = "Tester",
                ActorIdSnapshot = oce.Id,
                PreviousLevel = prevLevel,
                NewLevel = newLevel,
                CreatedAtUtc = DateTime.UtcNow
            };
            _db.IncidentTimelineEntries.Add(entry);
            await QueuedIncidentDispatcher.DispatchEmailEventAsync(_db, entry, CancellationToken.None, TestLocalization.Factory);
            await _db.SaveChangesAsync();

            var eventSmsDelivery = await _db.PushDeliveries.Include(d => d.OnCallEmail)
                .SingleAsync(d => d.IncidentEventId == entry.Id && d.OnCallEmail != null && d.OnCallEmail.ChannelType == "sms");
            await processor.ProcessAsync(eventSmsDelivery.Id, CancellationToken.None);
            Assert.AreEqual(PushDelivery.Succeeded, eventSmsDelivery.Status, $"Failed on {type}: {eventSmsDelivery.Error}");
            Assert.AreEqual(expectedTemplate, _sender.Messages[^1].TemplateId);
            CollectionAssert.AreEqual(new[] { "Orders", expectedLevelParam }, _sender.Messages[^1].TemplateParams.ToArray());
            CollectionAssert.AreEqual(new[] { "+8613900139000" }, _sender.Messages[^1].PhoneNumbers.ToArray());
        }
    }

    [TestMethod]
    public async Task FailsWhenNoRecipientsHaveBoundPhoneNumber()
    {
        var user = new User { Id = "u-nophone", UserName = "nophone", DisplayName = "NoPhone", PhoneNumber = null };
        _db.Users.Add(user);
        var channel = new PushChannel { Name = "SMS Empty", Type = "sms", IsEnabled = true };
        _db.PushChannels.Add(channel);
        _db.PushChannelUsers.Add(new PushChannelUser { Channel = channel, UserId = user.Id });
        var delivery = new PushDelivery { Channel = channel, TestContent = "Ping" };
        _db.PushDeliveries.Add(delivery);
        await _db.SaveChangesAsync();
        await InAppRecipientSnapshot.CreateAsync(_db, delivery, CancellationToken.None);
        await _db.SaveChangesAsync();

        var result = await _adapter.DeliverAsync(delivery);
        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.ErrorMessage, "phone number");
        Assert.AreEqual(0, _sender.Messages.Count);
    }

    [TestMethod]
    public async Task HandlesProviderErrorsCleanly()
    {
        var user = new User { Id = "u-err", UserName = "err", DisplayName = "Err", PhoneNumber = "+8613800138009" };
        _db.Users.Add(user);
        var channel = new PushChannel { Name = "SMS Err", Type = "sms", IsEnabled = true };
        _db.PushChannels.Add(channel);
        var delivery = new PushDelivery { Channel = channel, TestContent = "Test SMS" };
        _db.PushDeliveries.Add(delivery);
        _db.PushDeliveryRecipients.Add(new PushDeliveryRecipient { Delivery = delivery, UserId = user.Id });
        await _db.SaveChangesAsync();

        _sender.ExceptionToThrow = new InvalidOperationException("Missing Sms settings");
        var configFailure = await _adapter.DeliverAsync(delivery);
        Assert.IsFalse(configFailure.Succeeded);
        StringAssert.Contains(configFailure.ErrorMessage, "Tencent Cloud SMS configuration");

        _sender.ExceptionToThrow = new TencentCloudSDKException("AuthFailure.SecretIdNotFound");
        var sdkFailure = await _adapter.DeliverAsync(delivery);
        Assert.IsFalse(sdkFailure.Succeeded);
        StringAssert.Contains(sdkFailure.ErrorMessage, "AuthFailure.SecretIdNotFound");

        _sender.ExceptionToThrow = new SmsDeliveryException("Rejected");
        var deliveryFailure = await _adapter.DeliverAsync(delivery);
        Assert.IsFalse(deliveryFailure.Succeeded);
        Assert.AreEqual("Rejected", deliveryFailure.ErrorMessage);
    }

    [TestMethod]
    public async Task SeverityPolicyControlsSmsDispatch()
    {
        var user = new User { Id = "u-sev", UserName = "sev", DisplayName = "Sev", PhoneNumber = "+8613800138003" };
        _db.Users.Add(user);
        var app = new SourceApplication { Name = "Billing", Owner = user };
        var channel = new PushChannel { Name = "SMS Sev", Type = "sms", IsEnabled = true };
        _db.SourceApplications.Add(app);
        _db.PushChannels.Add(channel);
        _db.PushChannelUsers.Add(new PushChannelUser { Channel = channel, UserId = user.Id });
        _db.ApplicationPushChannels.Add(new ApplicationPushChannel { Application = app, Channel = channel });
        await _db.SaveChangesAsync();

        var dispatcher = new QueuedIncidentDispatcher(_db, TestLocalization.Factory);
        // Default severity policy disables SMS for sev3.
        var sev3 = new Incident { Application = app, Title = "Minor warning", Level = "sev3", ReportId = Guid.NewGuid() };
        _db.Incidents.Add(sev3);
        await dispatcher.DispatchAsync(sev3);
        await _db.SaveChangesAsync();
        Assert.AreEqual(0, await _db.PushDeliveries.CountAsync());

        // Enabling sev3 SMS in SeverityRules allows dispatching.
        var rule = await _db.SeverityRules.SingleAsync(r => r.Level == "sev3");
        rule.Sms = true;
        await _db.SaveChangesAsync();

        var sev3Allowed = new Incident { Application = app, Title = "Allowed warning", Level = "sev3", ReportId = Guid.NewGuid() };
        _db.Incidents.Add(sev3Allowed);
        await dispatcher.DispatchAsync(sev3Allowed);
        await _db.SaveChangesAsync();
        Assert.AreEqual(1, await _db.PushDeliveries.CountAsync());
    }

    [TestMethod]
    public async Task SmsEventPolicyAndSeverityLevelCombineAsTwoDimensions()
    {
        var user = new User { Id = "u-2d", UserName = "user2d", DisplayName = "User 2D", Email = "u2d@example.com", PhoneNumber = "+8613800138088" };
        _db.Users.Add(user);
        var app = new SourceApplication { Name = "Checkout", Owner = user };
        var channel = new PushChannel { Name = "SMS 2D", Type = "sms", IsEnabled = true };
        _db.SourceApplications.Add(app);
        _db.PushChannels.Add(channel);
        _db.PushChannelUsers.Add(new PushChannelUser { Channel = channel, UserId = user.Id });
        _db.ApplicationPushChannels.Add(new ApplicationPushChannel { Application = app, Channel = channel });

        // Enable only NewIncident, Escalated, Reopened; disable Mitigated and Resolved.
        var store = new SeverityPolicyStore(_db);
        await store.StageSmsEventsAsync(new SmsEventPolicy
        {
            NewIncident = true,
            Escalated = true,
            Reopened = true,
            Mitigated = false,
            Resolved = false
        });
        await _db.SaveChangesAsync();

        var dispatcher = new QueuedIncidentDispatcher(_db, TestLocalization.Factory);
        var sev1 = new Incident { Application = app, Title = "Checkout timeout", Level = "sev1", ReportId = Guid.NewGuid() };
        _db.Incidents.Add(sev1);
        await dispatcher.DispatchAsync(sev1);
        await _db.SaveChangesAsync();
        Assert.AreEqual(1, await _db.PushDeliveries.CountAsync());

        // Mitigated & Resolved must NOT queue SMS even though sev1 allows SMS.
        foreach (var blockedType in new[] { IncidentTimelineEntry.Mitigated, IncidentTimelineEntry.Resolved })
        {
            var blockedEntry = new IncidentTimelineEntry
            {
                Incident = sev1, Type = blockedType, ActorName = "Ops", ActorIdSnapshot = user.Id,
                PreviousLevel = "sev1", NewLevel = "sev1", CreatedAtUtc = DateTime.UtcNow
            };
            _db.IncidentTimelineEntries.Add(blockedEntry);
            await QueuedIncidentDispatcher.DispatchEmailEventAsync(_db, blockedEntry, CancellationToken.None, TestLocalization.Factory);
            await _db.SaveChangesAsync();
            Assert.IsFalse(await _db.PushDeliveries.AnyAsync(d => d.IncidentEventId == blockedEntry.Id));
        }

        // Reopened & Escalated MUST queue SMS.
        var reopened = new IncidentTimelineEntry
        {
            Incident = sev1, Type = IncidentTimelineEntry.Reopened, ActorName = "Ops", ActorIdSnapshot = user.Id,
            PreviousLevel = "sev1", NewLevel = "sev1", CreatedAtUtc = DateTime.UtcNow
        };
        _db.IncidentTimelineEntries.Add(reopened);
        await QueuedIncidentDispatcher.DispatchEmailEventAsync(_db, reopened, CancellationToken.None, TestLocalization.Factory);
        await _db.SaveChangesAsync();
        var reopenedDelivery = await _db.PushDeliveries.SingleAsync(d => d.IncidentEventId == reopened.Id);

        // Turning off Reopened before processing suppresses the queued delivery.
        await store.StageSmsEventsAsync(new SmsEventPolicy
        {
            NewIncident = true, Escalated = true, Reopened = false, Mitigated = false, Resolved = false
        });
        await _db.SaveChangesAsync();
        var processor = new PushDeliveryProcessor(_db, [_adapter], NullLogger<PushDeliveryProcessor>.Instance, TestLocalization.For<PushDeliveryProcessor>());
        await processor.ProcessAsync(reopenedDelivery.Id, CancellationToken.None);
        Assert.AreEqual(PushDelivery.Suppressed, reopenedDelivery.Status);
        Assert.AreEqual(0, _sender.Messages.Count);
    }

    private sealed class FakeSmsSender : ISmsSender
    {
        public List<SmsMessage> Messages { get; } = [];
        public Exception? ExceptionToThrow { get; set; }

        public Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
        {
            if (ExceptionToThrow != null) throw ExceptionToThrow;
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}

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

        // Null Templates or null template values must resolve safely to empty string without throwing.
        var nullTemplates = new SmsSettings { Templates = null! };
        Assert.AreEqual(string.Empty, nullTemplates.ResolveTemplateId(null));
        Assert.AreEqual(string.Empty, nullTemplates.ResolveTemplateId(IncidentTimelineEntry.Resolved));
        var nullValue = new SmsSettings { Templates = new SmsTemplateSettings { NewIncident = null! } };
        Assert.AreEqual(string.Empty, nullValue.ResolveTemplateId(null));
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
        Assert.AreEqual("+12****67", SmsChannelAdapter.MaskPhoneNumber("+1234567"));
        Assert.AreEqual("+12****89", SmsChannelAdapter.MaskPhoneNumber("+123456789"));
        Assert.AreEqual("+1****7", SmsChannelAdapter.MaskPhoneNumber("+123457"));
        Assert.AreEqual("****", SmsChannelAdapter.MaskPhoneNumber("1234"));
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

        _sender.ExceptionToThrow = new TencentCloudSDKException("HTTP timeout", new TaskCanceledException("Timed out"));
        var wrappedTimeoutFailure = await _adapter.DeliverAsync(delivery);
        Assert.IsFalse(wrappedTimeoutFailure.Succeeded);
        StringAssert.Contains(wrappedTimeoutFailure.ErrorMessage, "timed out");
    }

    [TestMethod]
    public void CreateMessageTruncatesApplicationNameToTwelveCharacters()
    {
        var delivery = new PushDelivery
        {
            Incident = new Incident { Title = "Outage", Level = "sev0" }
        };

        var exact = _adapter.CreateMessage(delivery, ["+8613800138000"], "tpl-new", "AlertCenter!");
        Assert.AreEqual("AlertCenter!", exact.TemplateParams[0]);
        Assert.AreEqual("SEV0", exact.TemplateParams[1]);

        var longEnglish = _adapter.CreateMessage(delivery, ["+8613800138000"], "tpl-new", "Payment Gateway Production");
        Assert.AreEqual("Payment Gate", longEnglish.TemplateParams[0]);
        Assert.AreEqual(12, longEnglish.TemplateParams[0].Length);

        var longChinese = _adapter.CreateMessage(delivery, ["+8613800138000"], "tpl-new", "核心订单支付网关告警系统生产环境");
        Assert.AreEqual("核心订单支付网关告警系统", longChinese.TemplateParams[0]);
        Assert.AreEqual(12, longChinese.TemplateParams[0].Length);
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

    [TestMethod]
    public async Task TencentCloudSendSmsRequestSerializesPhoneNumberSet()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var capturedTask = CaptureSingleHttpRequestAsync(
            listener,
            "{\"Response\":{\"SendStatusSet\":[{\"Code\":\"Ok\",\"Message\":\"send success\",\"PhoneNumber\":\"+8618535059765\"}],\"RequestId\":\"req-1\"}}");

        var previousDefaultSettings = Newtonsoft.Json.JsonConvert.DefaultSettings;
        try
        {
            // Simulate AiurProtocol setting a global camelCase contract resolver.
            Newtonsoft.Json.JsonConvert.DefaultSettings = () => new Newtonsoft.Json.JsonSerializerSettings
            {
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver
                {
                    NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy
                    {
                        OverrideSpecifiedNames = true
                    }
                }
            };

            var sender = new TencentCloudSmsSender(Options.Create(new SmsSettings
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
                Region = "ap-guangzhou",
                Endpoint = $"http://127.0.0.1:{port}"
            }));

            await sender.SendAsync(new SmsMessage(["+8618535059765"], "tpl-new", ["sample", "SEV0"]));
        }
        finally
        {
            Newtonsoft.Json.JsonConvert.DefaultSettings = previousDefaultSettings;
        }

        var rawHttp = await capturedTask;
        StringAssert.Contains(rawHttp, "\"PhoneNumberSet\":[\"+8618535059765\"]", $"Actual HTTP:\n{rawHttp}");
        StringAssert.Contains(rawHttp, "\"SmsSdkAppId\":\"1401224081\"", $"Actual HTTP:\n{rawHttp}");
        StringAssert.Contains(rawHttp, "\"TemplateId\":\"tpl-new\"", $"Actual HTTP:\n{rawHttp}");
        StringAssert.Contains(rawHttp, "\"TemplateParamSet\":[\"sample\",\"SEV0\"]", $"Actual HTTP:\n{rawHttp}");
    }

    [TestMethod]
    public async Task TencentCloudSendSmsThrowsWhenSendStatusSetIsEmpty()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = CaptureSingleHttpRequestAsync(
            listener,
            "{\"Response\":{\"SendStatusSet\":[],\"RequestId\":\"req-empty\"}}");

        var sender = new TencentCloudSmsSender(Options.Create(new SmsSettings
        {
            SecretId = "AKID-test",
            SecretKey = "secret-test",
            SmsSdkAppId = "1401224081",
            SignName = "TestSign",
            Templates = _settings.Templates,
            Region = "ap-guangzhou",
            Endpoint = $"http://127.0.0.1:{port}/"
        }));

        await Assert.ThrowsAsync<SmsDeliveryException>(async () =>
            await sender.SendAsync(new SmsMessage(["+8613800138000"], "tpl-new", ["sample", "SEV0"])));
        await serverTask;
    }

    [TestMethod]
    public async Task OnCallSmsLifecycleEventsUseLatestOcePhoneNumber()
    {
        var now = DateTime.UtcNow;
        var oce = new User { Id = "u-oce-phone", UserName = "ocephone", DisplayName = "OCE Phone", Email = "oce@example.com", PhoneNumber = null };
        _db.Users.Add(oce);
        var group = new OnCallGroup { Name = "Team Phone", SmsNotificationsEnabled = true };
        _db.OnCallGroups.Add(group);
        _db.OnCallShifts.Add(new OnCallShift
        {
            Group = group, StartsAtUtc = now.AddHours(-1), EndsAtUtc = now.AddHours(2),
            Members = [new OnCallShiftMember { User = oce }]
        });
        var app = new SourceApplication { Name = "Inventory", Owner = oce, OnCallGroup = group };
        _db.SourceApplications.Add(app);
        await _db.SaveChangesAsync();

        var dispatcher = new QueuedIncidentDispatcher(_db, TestLocalization.Factory);
        var incident = new Incident { Application = app, Title = "Stock sync stalled", Level = "sev1", OccurredAtUtc = now, ReportId = Guid.NewGuid() };
        _db.Incidents.Add(incident);
        await dispatcher.DispatchAsync(incident);
        await _db.SaveChangesAsync();

        // Initial SMS delivery fails because OCE had no bound phone number at creation time.
        var initialSms = await _db.PushDeliveries.Include(d => d.OnCallEmail)
            .SingleAsync(d => d.IncidentId == incident.Id && d.OnCallEmail != null && d.OnCallEmail.ChannelType == "sms");
        Assert.AreEqual(PushDelivery.Failed, initialSms.Status);

        // OCE binds a phone number; subsequent escalation should succeed and use the newly bound number.
        oce.PhoneNumber = "+8613800001111";
        await _db.SaveChangesAsync();

        var processor = new PushDeliveryProcessor(_db, [_adapter], NullLogger<PushDeliveryProcessor>.Instance, TestLocalization.For<PushDeliveryProcessor>());
        var escalated = new IncidentTimelineEntry
        {
            Incident = incident, Type = IncidentTimelineEntry.LevelChanged, ActorName = "Ops", ActorIdSnapshot = oce.Id,
            PreviousLevel = "sev1", NewLevel = "sev0", CreatedAtUtc = now.AddMinutes(5)
        };
        incident.Level = "sev0";
        _db.IncidentTimelineEntries.Add(escalated);
        await QueuedIncidentDispatcher.DispatchEmailEventAsync(_db, escalated, CancellationToken.None, TestLocalization.Factory);
        await _db.SaveChangesAsync();

        var escalatedSms = await _db.PushDeliveries.Include(d => d.OnCallEmail)
            .SingleAsync(d => d.IncidentEventId == escalated.Id && d.OnCallEmail != null && d.OnCallEmail.ChannelType == "sms");
        await processor.ProcessAsync(escalatedSms.Id, CancellationToken.None);
        Assert.AreEqual(PushDelivery.Succeeded, escalatedSms.Status);
        CollectionAssert.AreEqual(new[] { "+8613800001111" }, _sender.Messages[^1].PhoneNumbers.ToArray());

        // OCE updates their phone number; subsequent resolution should send to the new phone number.
        oce.PhoneNumber = "+8613800002222";
        await _db.SaveChangesAsync();

        var resolved = new IncidentTimelineEntry
        {
            Incident = incident, Type = IncidentTimelineEntry.Resolved, ActorName = "Ops", ActorIdSnapshot = oce.Id,
            PreviousLevel = "sev0", NewLevel = "sev0", CreatedAtUtc = now.AddMinutes(10)
        };
        _db.IncidentTimelineEntries.Add(resolved);
        await QueuedIncidentDispatcher.DispatchEmailEventAsync(_db, resolved, CancellationToken.None, TestLocalization.Factory);
        await _db.SaveChangesAsync();

        var resolvedSms = await _db.PushDeliveries.Include(d => d.OnCallEmail)
            .SingleAsync(d => d.IncidentEventId == resolved.Id && d.OnCallEmail != null && d.OnCallEmail.ChannelType == "sms");
        await processor.ProcessAsync(resolvedSms.Id, CancellationToken.None);
        Assert.AreEqual(PushDelivery.Succeeded, resolvedSms.Status);
        CollectionAssert.AreEqual(new[] { "+8613800002222" }, _sender.Messages[^1].PhoneNumbers.ToArray());

        // OCE unbinds their phone number; subsequent reopen should fail instead of texting the old number.
        oce.PhoneNumber = null;
        await _db.SaveChangesAsync();

        var reopened = new IncidentTimelineEntry
        {
            Incident = incident, Type = IncidentTimelineEntry.Reopened, ActorName = "Ops", ActorIdSnapshot = oce.Id,
            PreviousLevel = "sev0", NewLevel = "sev0", CreatedAtUtc = now.AddMinutes(15)
        };
        _db.IncidentTimelineEntries.Add(reopened);
        await QueuedIncidentDispatcher.DispatchEmailEventAsync(_db, reopened, CancellationToken.None, TestLocalization.Factory);
        await _db.SaveChangesAsync();

        var reopenedSms = await _db.PushDeliveries.Include(d => d.OnCallEmail)
            .SingleAsync(d => d.IncidentEventId == reopened.Id && d.OnCallEmail != null && d.OnCallEmail.ChannelType == "sms");
        Assert.AreEqual(PushDelivery.Failed, reopenedSms.Status);
        Assert.AreEqual(2, _sender.Messages.Count);
    }

    private static async Task<string> CaptureSingleHttpRequestAsync(System.Net.Sockets.TcpListener listener, string body)
    {
        using var tcp = await listener.AcceptTcpClientAsync();
        await using var stream = tcp.GetStream();
        var buffer = new byte[8192];
        var read = await stream.ReadAsync(buffer);
        var raw = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {System.Text.Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(response));
        return raw;
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

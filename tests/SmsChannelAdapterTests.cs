using Aiursoft.AlertCenter.Configuration;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TencentCloud.Common;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class SmsChannelAdapterTests
{
    private SqliteContext _db = null!;
    private FakeSmsSender _sender = null!;
    private SmsChannelAdapter _adapter = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.MigrateAsync();
        _sender = new FakeSmsSender();
        _adapter = new SmsChannelAdapter(_db, _sender, TestLocalization.For<SmsChannelAdapter>());
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _db.DisposeAsync();
    }

    [TestMethod]
    public void SmsSettingsValidationChecksAllRequiredFields()
    {
        var invalid = new SmsSettings();
        Assert.IsFalse(invalid.TryValidate(out var error));
        Assert.IsNotNull(error);

        var valid = new SmsSettings
        {
            SecretId = "AKID-test",
            SecretKey = "secret-test",
            SmsSdkAppId = "1400000000",
            SignName = "AlertCenter",
            TemplateId = "123456",
            Region = "ap-guangzhou"
        };
        Assert.IsTrue(valid.TryValidate(out var okError));
        Assert.IsNull(okError);
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
    public async Task DeliversDeduplicatedSmsToBoundUsersAndRoleMembers()
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
        Assert.AreEqual("SEV1", _sender.Messages[0].TemplateParams[0]);
        StringAssert.Contains(_sender.Messages[0].TemplateParams[1], "Database latency spike");
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

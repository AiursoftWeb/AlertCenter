using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.SDK.Models;
using System.Net.Mail;
using System.Security.Claims;
using Aiursoft.AlertCenter.Authorization;
using Aiursoft.AlertCenter.Controllers;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiursoft.AlertCenter.Tests;

public partial class PushCenterTests
{
    private async Task<OnCallGroup> OnCallTeam(SourceApplication application, string? secondEmail = "bob@example.com")
    {
        _db.Users.AddRange(
            new User { Id = "oce-alice", UserName = "alice", DisplayName = "Alice", Email = "alice@example.com" },
            new User { Id = "oce-bob", UserName = "bob", DisplayName = "Bob", Email = secondEmail },
            new User { Id = "off-duty", UserName = "off-duty", DisplayName = "Off duty", Email = "off@example.com" });
        var group = new OnCallGroup
        {
            Name = "Production OCE", TimeZoneId = "Asia/Shanghai",
            EmailCcRecipients = "team@example.com; ALICE@example.com\nTEAM@example.com",
            Members = [new OnCallGroupMember { UserId = "oce-alice", RotationOrder = 0 },
                new OnCallGroupMember { UserId = "oce-bob", RotationOrder = 1 },
                new OnCallGroupMember { UserId = "off-duty", RotationOrder = 2 }]
        };
        _db.OnCallShifts.Add(new OnCallShift
        {
            Group = group, StartsAtUtc = DateTime.UtcNow.AddHours(-1), EndsAtUtc = DateTime.UtcNow.AddHours(7),
            Members = [new OnCallShiftMember { UserId = "oce-alice" }, new OnCallShiftMember { UserId = "oce-bob" }]
        });
        application.OnCallGroup = group;
        await _db.SaveChangesAsync();
        return group;
    }

    private Task<PushDelivery> BuiltInDelivery() => _db.PushDeliveries.Include(delivery => delivery.OnCallEmail)
        .ThenInclude(snapshot => snapshot!.Recipients).SingleAsync(delivery => delivery.OnCallEmail != null);

    [TestMethod]
    [DataRow("sev0")]
    [DataRow("sev1")]
    [DataRow("sev2")]
    [DataRow("sev3")]
    [DataRow("sev4")]
    public async Task EverySeverityQueuesMandatoryOceEmailWithoutCreatingCustomChannels(string level)
    {
        var application = await Application();
        var group = await OnCallTeam(application);
        var reportId = Guid.NewGuid();
        Assert.AreEqual(Code.JobDone, ((ReportMessageResponse)((JsonResult)await Report(application, reportId, level)).Value!).Code);
        var delivery = await BuiltInDelivery();
        var snapshot = delivery.OnCallEmail!;
        Assert.IsNull(delivery.ChannelId);
        Assert.AreEqual(group.Id, snapshot.GroupId);
        Assert.AreEqual("Production OCE", snapshot.GroupName);
        Assert.AreEqual("team@example.com", snapshot.CcRecipients);
        Assert.AreEqual((await _db.ReportedMessages.SingleAsync()).ReceivedAtUtc, snapshot.NotificationAtUtc);
        Assert.AreEqual((await _db.OnCallShifts.SingleAsync()).Id, snapshot.ShiftId);
        CollectionAssert.AreEquivalent(new[] { "oce-alice", "oce-bob" }, snapshot.Recipients.Select(recipient => recipient.UserId).ToArray());
        Assert.AreEqual(0, await _db.PushChannels.CountAsync());
        Assert.AreEqual(0, await _db.ApplicationPushChannels.CountAsync());
        Assert.AreEqual(0, await _db.InAppNotifications.CountAsync());
        Assert.AreEqual(Code.NoActionTaken, ((ReportMessageResponse)((JsonResult)await Report(application, reportId, level)).Value!).Code);
        Assert.AreEqual(1, await _db.PushDeliveries.CountAsync());
        await _processor.ProcessAsync(delivery.Id, default);
        await _processor.ProcessAsync(delivery.Id, default);
        var sent = _email.Messages.Single();
        CollectionAssert.AreEquivalent(new[] { "alice@example.com", "bob@example.com" }, sent.Recipients.ToArray());
        CollectionAssert.AreEqual(new[] { "team@example.com" }, sent.CcRecipients.ToArray());
        Assert.AreEqual(PushDelivery.Succeeded, (await BuiltInDelivery()).Status);
        Assert.AreEqual(1, (await BuiltInDelivery()).Attempts);
    }

    [TestMethod]
    public async Task CustomWebhookInAppAndEmailFanOutRemainsAdditiveAndIndependent()
    {
        await AddAdministrators();
        var application = await Application();
        await OnCallTeam(application);
        var webhook = await Channel();
        var inbox = await Channel("inapp");
        var extraEmail = await Channel("email");
        var disabled = await Channel(enabled: false);
        await BindAllLevels(application.Id, webhook.Id, inbox.Id, extraEmail.Id, disabled.Id);
        await Report(application, Guid.NewGuid());
        Assert.AreEqual(4, await _db.PushDeliveries.CountAsync());
        _http.ThrowNetworkError = true;
        foreach (var id in await _db.PushDeliveries.Select(delivery => delivery.Id).ToListAsync())
            await _processor.ProcessAsync(id, default);
        Assert.AreEqual(PushDelivery.Succeeded, (await BuiltInDelivery()).Status);
        Assert.AreEqual(PushDelivery.Failed, (await _db.PushDeliveries.SingleAsync(delivery => delivery.ChannelId == webhook.Id)).Status);
        Assert.AreEqual(2, _email.Messages.Count);
        Assert.AreEqual(2, await _db.InAppNotifications.CountAsync());
        await BindAllLevels(application.Id);
        await Report(application, Guid.NewGuid());
        Assert.AreEqual(2, await _db.OnCallEmailSnapshots.CountAsync());
        Assert.AreEqual(5, await _db.PushDeliveries.CountAsync());
    }

    [TestMethod]
    public async Task OceEmailRetryUsesOriginalRecipientsEvenAfterTeamScheduleAndProfileChanges()
    {
        var application = await Application();
        var group = await OnCallTeam(application);
        await Report(application, Guid.NewGuid());
        var id = (await BuiltInDelivery()).Id;
        var failureSender = new FailingEmailSender();
        var failing = new PushDeliveryProcessor(_db, [new EmailChannelAdapter(failureSender, localizer: TestLocalization.For<EmailChannelAdapter>())], NullLogger<PushDeliveryProcessor>.Instance, localizer: TestLocalization.For<PushDeliveryProcessor>());
        await failing.ProcessAsync(id, default);
        Assert.AreEqual(PushDelivery.Failed, (await BuiltInDelivery()).Status);
        Assert.AreEqual(1, failureSender.Attempts);
        _db.ChangeTracker.Clear();
        var shift = await _db.OnCallShifts.Include(item => item.Members).SingleAsync();
        _db.OnCallShiftMembers.RemoveRange(shift.Members);
        shift.Members = [new OnCallShiftMember { UserId = "off-duty" }];
        var storedGroup = await _db.OnCallGroups.SingleAsync();
        storedGroup.Name = "Renamed";
        storedGroup.EmailCcRecipients = "new-team@example.com";
        (await _db.Users.SingleAsync(user => user.Id == "oce-alice")).Email = "new-alice@example.com";
        (await _db.SourceApplications.SingleAsync()).OnCallGroupId = null;
        await _db.SaveChangesAsync();
        Assert.IsInstanceOfType<RedirectToActionResult>(await Controller().Retry(id));
        await _processor.ProcessAsync(id, default);
        _db.ChangeTracker.Clear();
        var delivery = await BuiltInDelivery();
        Assert.AreEqual(PushDelivery.Succeeded, delivery.Status, delivery.Error);
        Assert.AreEqual(2, delivery.Attempts);
        Assert.AreEqual(group.Id, delivery.OnCallEmail!.GroupId);
        Assert.AreEqual("Production OCE", delivery.OnCallEmail.GroupName);
        CollectionAssert.AreEquivalent(new[] { "alice@example.com", "bob@example.com" }, _email.Messages.Single().Recipients.ToArray());
        CollectionAssert.AreEqual(new[] { "team@example.com" }, _email.Messages.Single().CcRecipients.ToArray());
        _db.OnCallGroupMembers.RemoveRange(await _db.OnCallGroupMembers.Where(member => member.UserId.StartsWith("oce-")).ToListAsync());
        await _db.SaveChangesAsync();
        _db.Users.RemoveRange(await _db.Users.Where(user => user.Id.StartsWith("oce-")).ToListAsync());
        await _db.SaveChangesAsync();
        Assert.AreEqual(2, (await BuiltInDelivery()).OnCallEmail!.Recipients.Count);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("not-an-email")]
    public async Task MissingOceEmailRecordsFailureWithoutBlockingConfiguredCustomChannel(string? email)
    {
        var application = await Application();
        await OnCallTeam(application, email);
        var webhook = await Channel();
        await BindAllLevels(application.Id, webhook.Id);
        Assert.AreEqual(Code.JobDone, ((ReportMessageResponse)((JsonResult)await Report(application, Guid.NewGuid())).Value!).Code);
        var builtIn = await BuiltInDelivery();
        Assert.AreEqual(PushDelivery.Failed, builtIn.Status);
        Assert.Contains("registered email", builtIn.Error!);
        Assert.AreEqual(0, builtIn.Attempts);
        await _processor.ProcessAsync(builtIn.Id, default);
        Assert.AreEqual(0, _email.Messages.Count);
        await _processor.ProcessAsync((await _db.PushDeliveries.SingleAsync(delivery => delivery.ChannelId == webhook.Id)).Id, default);
        Assert.AreEqual(1, _http.Requests.Count);
        Assert.IsInstanceOfType<ConflictObjectResult>(await Controller().Retry(builtIn.Id));
        Assert.IsNull(builtIn.OnCallEmail!.Recipients.Single(recipient => recipient.UserId == "oce-bob").Email);
    }

    [TestMethod]
    [DataRow("gap")]
    [DataRow("empty")]
    [DataRow("overlap")]
    [DataRow("invalid-cc")]
    public async Task InvalidScheduleOrCcCreatesExplicitNonRetryableDeliveryFailure(string scenario)
    {
        var application = await Application();
        await OnCallTeam(application);
        var shift = await _db.OnCallShifts.Include(item => item.Members).SingleAsync();
        if (scenario == "gap") shift.EndsAtUtc = DateTime.UtcNow.AddMinutes(-1);
        if (scenario == "empty") _db.OnCallShiftMembers.RemoveRange(shift.Members);
        if (scenario == "overlap") _db.OnCallShifts.Add(new OnCallShift
        {
            GroupId = shift.GroupId, StartsAtUtc = shift.StartsAtUtc.AddMinutes(1), EndsAtUtc = shift.EndsAtUtc,
            Members = [new OnCallShiftMember { UserId = "off-duty" }]
        });
        if (scenario == "invalid-cc") (await _db.OnCallGroups.SingleAsync()).EmailCcRecipients = "not-an-email";
        await _db.SaveChangesAsync();
        await Report(application, Guid.NewGuid());
        var failed = await BuiltInDelivery();
        Assert.AreEqual(PushDelivery.Failed, failed.Status);
        Assert.IsNotNull(failed.OnCallEmail!.ValidationError);
        Assert.IsNotNull(failed.CompletedAtUtc);
        Assert.IsInstanceOfType<ConflictObjectResult>(await Controller().Retry(failed.Id));
    }

    [TestMethod]
    public async Task SharedMailboxesAreDeduplicatedAndCcNeverRepeatsTo()
    {
        var application = await Application();
        await OnCallTeam(application, "ALICE@example.com");
        (await _db.OnCallGroups.SingleAsync()).EmailCcRecipients = "alice@example.com; ALICE@example.com";
        await _db.SaveChangesAsync();
        await Report(application, Guid.NewGuid());
        var delivery = await BuiltInDelivery();
        Assert.AreEqual(2, delivery.OnCallEmail!.Recipients.Count);
        await _processor.ProcessAsync(delivery.Id, default);
        Assert.AreEqual(1, _email.Messages.Single().Recipients.Count);
        Assert.AreEqual(0, _email.Messages.Single().CcRecipients.Count);
    }

    [TestMethod]
    public async Task AcknowledgementSuppressesBothRoutesWithoutChangingQueuedRecipients()
    {
        var application = await Application();
        await OnCallTeam(application);
        await Report(application, Guid.NewGuid());
        var incident = await _db.Incidents.SingleAsync();
        incident.Status = Incident.Acked;
        await _db.SaveChangesAsync();
        await Report(application, Guid.NewGuid());
        Assert.AreEqual(1, await _db.PushDeliveries.CountAsync());
        Assert.AreEqual(1, await _db.ReportedMessages.CountAsync(message => message.IsArchived));
        await _processor.ProcessAsync((await BuiltInDelivery()).Id, default);
        Assert.AreEqual(1, _email.Messages.Count);
    }

    [TestMethod]
    public async Task BuiltInDeliveryAccessUsesApplicationGrantsWithoutCustomChannelGrant()
    {
        await AddAdministrators();
        var application = await Application();
        await OnCallTeam(application);
        await Report(application, Guid.NewGuid());
        var id = (await BuiltInDelivery()).Id;
        Assert.IsInstanceOfType<ForbidResult>(await Controller("ordinary-user", false).Delivery(id, default));
        _db.ApplicationUserAccesses.Add(new ApplicationUserAccess { ApplicationId = application.Id, UserId = "ordinary-user" });
        await _db.SaveChangesAsync();
        Assert.IsInstanceOfType<ForbidResult>(await Controller("ordinary-user", false).Retry(id));
        (await _db.ApplicationUserAccesses.SingleAsync()).CanManage = true;
        var failed = await BuiltInDelivery();
        failed.Status = PushDelivery.Failed;
        await _db.SaveChangesAsync();
        Assert.IsInstanceOfType<RedirectToActionResult>(await Controller("ordinary-user", false,
            new Claim(AppPermissions.Type, AppPermissionNames.CanManagePush)).Retry(id));
    }

    [TestMethod]
    public async Task DeletingCustomChannelDoesNotDeleteBuiltInMailHistory()
    {
        var application = await Application();
        await OnCallTeam(application);
        var custom = await Channel();
        await BindAllLevels(application.Id, custom.Id);
        await Report(application, Guid.NewGuid());
        Assert.IsInstanceOfType<RedirectToActionResult>(await Controller().Delete(custom.Id));
        Assert.AreEqual(1, await _db.PushDeliveries.CountAsync());
        Assert.AreEqual(1, await _db.OnCallEmailSnapshots.CountAsync());
        Assert.AreEqual(2, (await BuiltInDelivery()).OnCallEmail!.Recipients.Count);
        Assert.IsInstanceOfType<RedirectToActionResult>(await new AlertsController(_db, new ApplicationAccessService(_db), localizer: TestLocalization.For<AlertsController>(), localizerFactory: TestLocalization.Factory)
        {
            ControllerContext = Controller().ControllerContext
        }.DeleteApplication(application.Id, application.Name, default));
        Assert.AreEqual(0, await _db.PushDeliveries.CountAsync());
        Assert.AreEqual(0, await _db.OnCallEmailSnapshots.CountAsync());
    }

    [TestMethod]
    public async Task SmtpFailureDoesNotPreventCustomWebhookDelivery()
    {
        var application = await Application();
        await OnCallTeam(application);
        var webhook = await Channel();
        await BindAllLevels(application.Id, webhook.Id);
        await Report(application, Guid.NewGuid());
        var processor = new PushDeliveryProcessor(_db,
            [new EmailChannelAdapter(new FailingEmailSender(), localizer: TestLocalization.For<EmailChannelAdapter>()), _adapters.Single(adapter => adapter.Definition.Key == "webhook")],
            NullLogger<PushDeliveryProcessor>.Instance, localizer: TestLocalization.For<PushDeliveryProcessor>());
        await processor.ProcessAsync((await BuiltInDelivery()).Id, default);
        await processor.ProcessAsync((await _db.PushDeliveries.SingleAsync(delivery => delivery.ChannelId == webhook.Id)).Id, default);
        Assert.AreEqual(PushDelivery.Failed, (await BuiltInDelivery()).Status);
        Assert.AreEqual(PushDelivery.Succeeded, (await _db.PushDeliveries.SingleAsync(delivery => delivery.ChannelId == webhook.Id)).Status);
        Assert.AreEqual(1, _http.Requests.Count);
    }

    [TestMethod]
    public async Task SnapshotPersistenceFailureRollsBackAlertIncidentAndAllRoutes()
    {
        var application = await Application();
        await OnCallTeam(application);
        var channel = await Channel();
        await BindAllLevels(application.Id, channel.Id);
        await _db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectOceSnapshot BEFORE INSERT ON OnCallEmailSnapshots BEGIN SELECT RAISE(ABORT, 'Snapshot persistence failure'); END");
        var reportId = Guid.NewGuid();
        await Assert.ThrowsAsync<DbUpdateException>(() => Report(application, reportId));
        _db.ChangeTracker.Clear();
        Assert.AreEqual(0, await _db.ReportedMessages.CountAsync());
        Assert.AreEqual(0, await _db.Incidents.CountAsync());
        Assert.AreEqual(0, await _db.PushDeliveries.CountAsync());
        Assert.AreEqual(0, await _db.OnCallEmailSnapshots.CountAsync());
        await _db.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectOceSnapshot");
        Assert.AreEqual(Code.JobDone, ((ReportMessageResponse)((JsonResult)await Report(application, reportId)).Value!).Code);
        Assert.AreEqual(1, await _db.OnCallEmailSnapshots.CountAsync());
        Assert.AreEqual(2, await _db.PushDeliveries.CountAsync());
    }

    private sealed class FailingEmailSender : IEmailSender
    {
        public int Attempts { get; private set; }
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new SmtpException("Private SMTP failure");
        }
    }
}

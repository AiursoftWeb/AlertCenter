using System.Diagnostics.CodeAnalysis;
using Aiursoft.DbTools;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Entities;

[ExcludeFromCodeCoverage]

public abstract class TemplateDbContext(DbContextOptions options) : IdentityDbContext<User>(options), ICanMigrate
{
    public DbSet<GlobalSetting> GlobalSettings => Set<GlobalSetting>();
    public DbSet<SourceApplication> SourceApplications => Set<SourceApplication>();
    public DbSet<ReportedMessage> ReportedMessages => Set<ReportedMessage>();
    public DbSet<AlertRoutingFailure> AlertRoutingFailures => Set<AlertRoutingFailure>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<IncidentTimelineEntry> IncidentTimelineEntries => Set<IncidentTimelineEntry>();
    public DbSet<ApplicationUserAccess> ApplicationUserAccesses => Set<ApplicationUserAccess>();
    public DbSet<ApplicationRoleAccess> ApplicationRoleAccesses => Set<ApplicationRoleAccess>();
    public DbSet<PushChannel> PushChannels => Set<PushChannel>();
    public DbSet<ApplicationPushChannel> ApplicationPushChannels => Set<ApplicationPushChannel>();
    public DbSet<PushDelivery> PushDeliveries => Set<PushDelivery>();
    public DbSet<PushDeliveryRecipient> PushDeliveryRecipients => Set<PushDeliveryRecipient>();
    public DbSet<InAppNotification> InAppNotifications => Set<InAppNotification>();
    public DbSet<OnCallGroup> OnCallGroups => Set<OnCallGroup>();
    public DbSet<OnCallGroupMember> OnCallGroupMembers => Set<OnCallGroupMember>();
    public DbSet<OnCallShiftTemplate> OnCallShiftTemplates => Set<OnCallShiftTemplate>();
    public DbSet<OnCallShiftTemplateMember> OnCallShiftTemplateMembers => Set<OnCallShiftTemplateMember>();
    public DbSet<OnCallShift> OnCallShifts => Set<OnCallShift>();
    public DbSet<OnCallShiftOverride> OnCallShiftOverrides => Set<OnCallShiftOverride>();
    public DbSet<OnCallLeaveRequest> OnCallLeaveRequests => Set<OnCallLeaveRequest>();
    public DbSet<OnCallHandoverRequest> OnCallHandoverRequests => Set<OnCallHandoverRequest>();
    public DbSet<OnCallCoverageDebt> OnCallCoverageDebts => Set<OnCallCoverageDebt>();

    public DbSet<PushChannelUser> PushChannelUsers => Set<PushChannelUser>();
    public DbSet<PushChannelRole> PushChannelRoles => Set<PushChannelRole>();
    public DbSet<PushChannelBindingUser> PushChannelBindingUsers => Set<PushChannelBindingUser>();
    public DbSet<PushChannelBindingRole> PushChannelBindingRoles => Set<PushChannelBindingRole>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<PushChannel>().Property(channel => channel.WebhookHeadersProtected).HasColumnType("text");
        builder.Entity<PushChannel>().Property(channel => channel.WebhookPayloadTemplate).HasColumnType("text");
        builder.Entity<PushChannel>().Property(channel => channel.EmailCcRecipients).HasColumnType("text");
        builder.Entity<PushChannel>().HasIndex(channel => channel.SystemKey).IsUnique();
        builder.Entity<PushChannelRole>().HasKey(link => new { link.ChannelId, link.RoleId });
        builder.Entity<PushChannelRole>().HasOne(link => link.Channel).WithMany()
            .HasForeignKey(link => link.ChannelId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<PushChannelRole>().HasOne(link => link.Role).WithMany()
            .HasForeignKey(link => link.RoleId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<PushChannelUser>().HasKey(link => new { link.ChannelId, link.UserId });
        builder.Entity<PushChannelUser>().HasOne(link => link.Channel).WithMany()
            .HasForeignKey(link => link.ChannelId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<PushChannelUser>().HasOne(link => link.User).WithMany()
            .HasForeignKey(link => link.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<PushChannelBindingRole>().HasKey(link => new { link.ChannelId, link.RoleId });
        builder.Entity<PushChannelBindingRole>().HasOne(link => link.Channel).WithMany()
            .HasForeignKey(link => link.ChannelId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<PushChannelBindingRole>().HasOne(link => link.Role).WithMany()
            .HasForeignKey(link => link.RoleId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<PushChannelBindingUser>().HasKey(link => new { link.ChannelId, link.UserId });
        builder.Entity<PushChannelBindingUser>().HasOne(link => link.Channel).WithMany()
            .HasForeignKey(link => link.ChannelId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<PushChannelBindingUser>().HasOne(link => link.User).WithMany()
            .HasForeignKey(link => link.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ReportedMessage>().HasIndex(m => new { m.ApplicationId, m.ReportId }).IsUnique();
        builder.Entity<ReportedMessage>().HasIndex(m => new { m.ApplicationId, m.ReceivedAtUtc });
        builder.Entity<ReportedMessage>().HasIndex(m => new { m.IncidentId, m.ReceivedAtUtc });
        builder.Entity<ReportedMessage>().HasOne(m => m.Application).WithMany()
            .HasForeignKey(m => m.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<ReportedMessage>().HasOne(m => m.Incident).WithMany(incident => incident.Messages)
            .HasForeignKey(m => m.IncidentId).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<AlertRoutingFailure>().HasIndex(failure => failure.MessageId).IsUnique();
        builder.Entity<AlertRoutingFailure>().HasIndex(failure => new { failure.ApplicationId, failure.CreatedAtUtc });
        builder.Entity<AlertRoutingFailure>().HasOne(failure => failure.Application).WithMany()
            .HasForeignKey(failure => failure.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<AlertRoutingFailure>().HasOne(failure => failure.Message).WithMany(message => message.RoutingFailures)
            .HasForeignKey(failure => failure.MessageId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<SourceApplication>().HasOne(application => application.OnCallGroup).WithMany()
            .HasForeignKey(application => application.OnCallGroupId).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<SourceApplication>().HasIndex(application => application.OnCallGroupId);
        builder.Entity<Incident>().HasIndex(incident => new { incident.ApplicationId, incident.IncidentKey });
        builder.Entity<Incident>().HasIndex(incident => new { incident.ApplicationId, incident.ActiveMatchKey }).IsUnique();
        builder.Entity<Incident>().HasIndex(incident => new { incident.ApplicationId, incident.AlertTitle, incident.AlertLevel, incident.Status });
        builder.Entity<Incident>().HasIndex(incident => new { incident.OnCallGroupId, incident.OceUserId });
        builder.Entity<Incident>().HasOne(incident => incident.Application).WithMany()
            .HasForeignKey(incident => incident.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<IncidentTimelineEntry>().HasOne(entry => entry.Incident).WithMany(incident => incident.TimelineEntries)
            .HasForeignKey(entry => entry.IncidentId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<IncidentTimelineEntry>().HasOne(entry => entry.Actor).WithMany()
            .HasForeignKey(entry => entry.ActorId).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<IncidentTimelineEntry>().HasIndex(entry => new { entry.IncidentId, entry.CreatedAtUtc });
        builder.Entity<ApplicationUserAccess>().HasKey(access => new { access.ApplicationId, access.UserId });
        builder.Entity<ApplicationUserAccess>().HasOne(access => access.Application).WithMany()
            .HasForeignKey(access => access.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ApplicationUserAccess>().HasOne(access => access.User).WithMany()
            .HasForeignKey(access => access.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ApplicationRoleAccess>().HasKey(access => new { access.ApplicationId, access.RoleId });
        builder.Entity<ApplicationRoleAccess>().HasOne(access => access.Application).WithMany()
            .HasForeignKey(access => access.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ApplicationRoleAccess>().HasOne(access => access.Role).WithMany()
            .HasForeignKey(access => access.RoleId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ApplicationPushChannel>().HasKey(link => new { link.ApplicationId, link.ChannelId });
        builder.Entity<ApplicationPushChannel>().Property(link => link.ReceiveSev0).HasDefaultValue(true);
        builder.Entity<ApplicationPushChannel>().Property(link => link.ReceiveSev1).HasDefaultValue(true);
        builder.Entity<ApplicationPushChannel>().Property(link => link.ReceiveSev2).HasDefaultValue(true);
        builder.Entity<ApplicationPushChannel>().Property(link => link.ReceiveSev3).HasDefaultValue(true);
        builder.Entity<ApplicationPushChannel>().Property(link => link.ReceiveSev4).HasDefaultValue(true);
        builder.Entity<ApplicationPushChannel>().HasOne(link => link.Application).WithMany()
            .HasForeignKey(link => link.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ApplicationPushChannel>().HasOne(link => link.Channel).WithMany()
            .HasForeignKey(link => link.ChannelId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<PushDelivery>().HasOne(delivery => delivery.Channel).WithMany()
            .HasForeignKey(delivery => delivery.ChannelId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<PushDelivery>().HasOne(delivery => delivery.Message).WithMany()
            .HasForeignKey(delivery => delivery.MessageId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<PushDelivery>().HasIndex(delivery => new { delivery.MessageId, delivery.ChannelId }).IsUnique();
        builder.Entity<PushDelivery>().HasIndex(delivery => new { delivery.Status, delivery.CreatedAtUtc });
        builder.Entity<PushDeliveryRecipient>().HasKey(recipient => new { recipient.DeliveryId, recipient.UserId });
        builder.Entity<PushDeliveryRecipient>().HasOne(recipient => recipient.Delivery).WithMany()
            .HasForeignKey(recipient => recipient.DeliveryId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<PushDeliveryRecipient>().HasOne(recipient => recipient.User).WithMany()
            .HasForeignKey(recipient => recipient.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<InAppNotification>().HasOne(notification => notification.Delivery).WithMany()
            .HasForeignKey(notification => notification.DeliveryId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<InAppNotification>().HasOne(notification => notification.User).WithMany()
            .HasForeignKey(notification => notification.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<InAppNotification>().HasIndex(notification => new { notification.DeliveryId, notification.UserId }).IsUnique();
        builder.Entity<InAppNotification>().HasIndex(notification => new { notification.UserId, notification.CreatedAtUtc });
        builder.Entity<OnCallGroup>().HasIndex(group => group.Name).IsUnique();
        builder.Entity<OnCallGroupMember>().HasIndex(member => new { member.GroupId, member.UserId }).IsUnique();
        builder.Entity<OnCallGroupMember>().HasOne(member => member.Group).WithMany()
            .HasForeignKey(member => member.GroupId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<OnCallGroupMember>().HasOne(member => member.User).WithMany()
            .HasForeignKey(member => member.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<OnCallShiftTemplate>().HasIndex(template => new { template.GroupId, template.Name }).IsUnique();
        builder.Entity<OnCallShiftTemplate>().HasOne(template => template.Group).WithMany()
            .HasForeignKey(template => template.GroupId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<OnCallShiftTemplateMember>().HasIndex(member => new { member.TemplateId, member.UserId }).IsUnique();
        builder.Entity<OnCallShiftTemplateMember>().HasIndex(member => new { member.TemplateId, member.RotationOrder }).IsUnique();
        builder.Entity<OnCallShiftTemplateMember>().HasOne(member => member.Template).WithMany()
            .HasForeignKey(member => member.TemplateId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<OnCallShiftTemplateMember>().HasOne(member => member.User).WithMany()
            .HasForeignKey(member => member.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<OnCallShift>().HasIndex(shift => new { shift.GroupId, shift.StartsAtUtc, shift.EndsAtUtc });
        builder.Entity<OnCallShift>().HasOne(shift => shift.Group).WithMany()
            .HasForeignKey(shift => shift.GroupId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<OnCallShift>().HasOne(shift => shift.Template).WithMany()
            .HasForeignKey(shift => shift.TemplateId).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<OnCallShift>().HasOne(shift => shift.User).WithMany()
            .HasForeignKey(shift => shift.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<OnCallShiftOverride>().HasIndex(overrideItem => new { overrideItem.GroupId, overrideItem.StartsAtUtc, overrideItem.EndsAtUtc });
        builder.Entity<OnCallShiftOverride>().HasIndex(overrideItem => overrideItem.OriginalShiftId);
        builder.Entity<OnCallShiftOverride>().HasOne(overrideItem => overrideItem.Group).WithMany()
            .HasForeignKey(overrideItem => overrideItem.GroupId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<OnCallShiftOverride>().HasOne(overrideItem => overrideItem.OriginalShift).WithMany()
            .HasForeignKey(overrideItem => overrideItem.OriginalShiftId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<OnCallShiftOverride>().HasOne(overrideItem => overrideItem.CoveringUser).WithMany()
            .HasForeignKey(overrideItem => overrideItem.CoveringUserId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<OnCallShiftOverride>().HasOne(overrideItem => overrideItem.CreatedByUser).WithMany()
            .HasForeignKey(overrideItem => overrideItem.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<OnCallLeaveRequest>().HasIndex(request => new { request.GroupId, request.UserId, request.StartsAtUtc, request.EndsAtUtc });
        builder.Entity<OnCallLeaveRequest>().HasOne(request => request.Group).WithMany()
            .HasForeignKey(request => request.GroupId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<OnCallLeaveRequest>().HasOne(request => request.User).WithMany()
            .HasForeignKey(request => request.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<OnCallLeaveRequest>().HasOne(request => request.ReviewedByUser).WithMany()
            .HasForeignKey(request => request.ReviewedByUserId).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<OnCallHandoverRequest>().HasIndex(request => new { request.ShiftId, request.Status });
        builder.Entity<OnCallHandoverRequest>().HasOne(request => request.Shift).WithMany()
            .HasForeignKey(request => request.ShiftId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<OnCallHandoverRequest>().HasOne(request => request.RequestedByUser).WithMany()
            .HasForeignKey(request => request.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<OnCallHandoverRequest>().HasOne(request => request.ReceivingUser).WithMany()
            .HasForeignKey(request => request.ReceivingUserId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<OnCallHandoverRequest>().HasOne(request => request.Override).WithMany()
            .HasForeignKey(request => request.OverrideId).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<OnCallCoverageDebt>().HasIndex(debt => debt.OriginOverrideId).IsUnique();
        builder.Entity<OnCallCoverageDebt>().HasIndex(debt => debt.SettlementOverrideId).IsUnique();
        builder.Entity<OnCallCoverageDebt>().HasOne(debt => debt.Group).WithMany()
            .HasForeignKey(debt => debt.GroupId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<OnCallCoverageDebt>().HasOne(debt => debt.OriginOverride).WithMany()
            .HasForeignKey(debt => debt.OriginOverrideId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<OnCallCoverageDebt>().HasOne(debt => debt.OwingUser).WithMany()
            .HasForeignKey(debt => debt.OwingUserId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<OnCallCoverageDebt>().HasOne(debt => debt.CoveringUser).WithMany()
            .HasForeignKey(debt => debt.CoveringUserId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<OnCallCoverageDebt>().HasOne(debt => debt.SettlementOverride).WithMany()
            .HasForeignKey(debt => debt.SettlementOverrideId).OnDelete(DeleteBehavior.SetNull);
    }

    public virtual  Task MigrateAsync(CancellationToken cancellationToken) =>
        Database.MigrateAsync(cancellationToken);

    public virtual  Task<bool> CanConnectAsync() =>
        Database.CanConnectAsync();
}

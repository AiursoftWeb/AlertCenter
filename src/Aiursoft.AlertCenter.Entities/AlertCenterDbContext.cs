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
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<IncidentTimelineEntry> IncidentTimelineEntries => Set<IncidentTimelineEntry>();
    public DbSet<IncidentComment> IncidentComments => Set<IncidentComment>();
    public DbSet<OnCallGroup> OnCallGroups => Set<OnCallGroup>();
    public DbSet<OnCallRule> OnCallRules => Set<OnCallRule>();
    public DbSet<OnCallGroupMember> OnCallGroupMembers => Set<OnCallGroupMember>();
    public DbSet<OnCallShift> OnCallShifts => Set<OnCallShift>();
    public DbSet<OnCallShiftMember> OnCallShiftMembers => Set<OnCallShiftMember>();
    public DbSet<ApplicationUserAccess> ApplicationUserAccesses => Set<ApplicationUserAccess>();
    public DbSet<ApplicationRoleAccess> ApplicationRoleAccesses => Set<ApplicationRoleAccess>();
    public DbSet<PushChannel> PushChannels => Set<PushChannel>();
    public DbSet<ApplicationPushChannel> ApplicationPushChannels => Set<ApplicationPushChannel>();
    public DbSet<PushDelivery> PushDeliveries => Set<PushDelivery>();
    public DbSet<PushDeliveryRecipient> PushDeliveryRecipients => Set<PushDeliveryRecipient>();
    public DbSet<OnCallEmailSnapshot> OnCallEmailSnapshots => Set<OnCallEmailSnapshot>();
    public DbSet<InAppNotification> InAppNotifications => Set<InAppNotification>();

    public DbSet<PushChannelUser> PushChannelUsers => Set<PushChannelUser>();
    public DbSet<PushChannelRole> PushChannelRoles => Set<PushChannelRole>();
    public DbSet<PushChannelBindingUser> PushChannelBindingUsers => Set<PushChannelBindingUser>();
    public DbSet<PushChannelBindingRole> PushChannelBindingRoles => Set<PushChannelBindingRole>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<SourceApplication>().HasOne(application => application.Owner).WithMany()
            .HasForeignKey(application => application.OwnerId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<PushChannel>().Property(channel => channel.WebhookHeadersProtected).HasColumnType("text");
        builder.Entity<PushChannel>().Property(channel => channel.WebhookPayloadTemplate).HasColumnType("text");
        builder.Entity<PushChannel>().Property(channel => channel.EmailCcRecipients).HasColumnType("text");
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
        builder.Entity<SourceApplication>().HasOne(application => application.OnCallGroup).WithMany()
            .HasForeignKey(application => application.OnCallGroupId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Incident>().HasIndex(m => new { m.ApplicationId, m.ReportId }).IsUnique();
        builder.Entity<Incident>().HasIndex(m => new { m.ApplicationId, m.ReceivedAtUtc });
        builder.Entity<Incident>().Property(incident => incident.Version).IsConcurrencyToken();
        builder.Entity<Incident>().HasOne(incident => incident.Owner).WithMany()
            .HasForeignKey(incident => incident.OwnerId).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<Incident>().HasIndex(incident => new { incident.ApplicationId, incident.Level, incident.Status });
        builder.Entity<Incident>().HasOne(incident => incident.Application).WithMany()
            .HasForeignKey(incident => incident.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<IncidentTimelineEntry>().HasOne(entry => entry.Incident).WithMany(incident => incident.TimelineEntries)
            .HasForeignKey(entry => entry.IncidentId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<IncidentTimelineEntry>().HasOne(entry => entry.Actor).WithMany()
            .HasForeignKey(entry => entry.ActorId).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<IncidentTimelineEntry>().HasIndex(entry => new { entry.IncidentId, entry.CreatedAtUtc });
        builder.Entity<IncidentComment>().Property(comment => comment.DeletedAtUtc).IsConcurrencyToken();
        builder.Entity<IncidentComment>().HasOne(comment => comment.Incident).WithMany()
            .HasForeignKey(comment => comment.IncidentId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<IncidentComment>().HasOne(comment => comment.Author).WithMany()
            .HasForeignKey(comment => comment.AuthorId).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<IncidentComment>().HasOne(comment => comment.DeletedBy).WithMany()
            .HasForeignKey(comment => comment.DeletedById).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<IncidentComment>().HasIndex(comment => new { comment.IncidentId, comment.CreatedAtUtc, comment.Id });
        builder.Entity<OnCallGroup>().HasOne(group => group.Rule).WithMany()
            .HasForeignKey(group => group.RuleId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<OnCallGroup>().Property(group => group.ScheduleVersion).IsConcurrencyToken();
        builder.Entity<OnCallShift>().HasOne(shift => shift.Group).WithMany()
            .HasForeignKey(shift => shift.GroupId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<OnCallShift>().HasIndex(shift => new { shift.GroupId, shift.StartsAtUtc }).IsUnique();
        builder.Entity<OnCallShiftMember>().HasKey(member => new { member.ShiftId, member.UserId });
        builder.Entity<OnCallShiftMember>().HasOne(member => member.Shift).WithMany(shift => shift.Members)
            .HasForeignKey(member => member.ShiftId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<OnCallShiftMember>().HasOne(member => member.User).WithMany()
            .HasForeignKey(member => member.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<OnCallGroupMember>().HasKey(member => new { member.GroupId, member.UserId });
        builder.Entity<OnCallGroupMember>().HasOne(member => member.Group).WithMany(group => group.Members)
            .HasForeignKey(member => member.GroupId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<OnCallGroupMember>().HasOne(member => member.User).WithMany()
            .HasForeignKey(member => member.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<OnCallGroupMember>().HasIndex(member => new { member.GroupId, member.RotationOrder }).IsUnique();
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
        builder.Entity<PushDelivery>().HasOne(delivery => delivery.Incident).WithMany()
            .HasForeignKey(delivery => delivery.IncidentId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<PushDelivery>().HasOne(delivery => delivery.IncidentEvent).WithMany()
            .HasForeignKey(delivery => delivery.IncidentEventId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<PushDelivery>().HasIndex(delivery => new { delivery.IncidentId, delivery.ChannelId }).IsUnique();
        builder.Entity<PushDelivery>().HasIndex(delivery => new { delivery.Status, delivery.CreatedAtUtc });
        builder.Entity<OnCallEmailSnapshot>().HasKey(snapshot => snapshot.DeliveryId);
        builder.Entity<OnCallEmailSnapshot>().HasOne(snapshot => snapshot.Delivery).WithOne(delivery => delivery.OnCallEmail)
            .HasForeignKey<OnCallEmailSnapshot>(snapshot => snapshot.DeliveryId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<OnCallEmailSnapshot>().Property(snapshot => snapshot.CcRecipients).HasColumnType("text");
        builder.Entity<OnCallEmailRecipient>().HasKey(recipient => new { recipient.DeliveryId, recipient.UserId });
        builder.Entity<OnCallEmailRecipient>().HasOne(recipient => recipient.Snapshot).WithMany(snapshot => snapshot.Recipients)
            .HasForeignKey(recipient => recipient.DeliveryId).OnDelete(DeleteBehavior.Cascade);
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
    }

    public virtual  Task MigrateAsync(CancellationToken cancellationToken) =>
        Database.MigrateAsync(cancellationToken);

    public virtual  Task<bool> CanConnectAsync() =>
        Database.CanConnectAsync();
}

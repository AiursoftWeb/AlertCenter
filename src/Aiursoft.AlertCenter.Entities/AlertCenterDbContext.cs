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
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<IncidentTimelineEntry> IncidentTimelineEntries => Set<IncidentTimelineEntry>();
    public DbSet<ApplicationUserAccess> ApplicationUserAccesses => Set<ApplicationUserAccess>();
    public DbSet<ApplicationRoleAccess> ApplicationRoleAccesses => Set<ApplicationRoleAccess>();
    public DbSet<PushChannel> PushChannels => Set<PushChannel>();
    public DbSet<ApplicationPushChannel> ApplicationPushChannels => Set<ApplicationPushChannel>();
    public DbSet<PushDelivery> PushDeliveries => Set<PushDelivery>();
    public DbSet<PushDeliveryRecipient> PushDeliveryRecipients => Set<PushDeliveryRecipient>();
    public DbSet<InAppNotification> InAppNotifications => Set<InAppNotification>();

    public DbSet<PushChannelUser> PushChannelUsers => Set<PushChannelUser>();
    public DbSet<PushChannelRole> PushChannelRoles => Set<PushChannelRole>();
    public DbSet<PushChannelBindingUser> PushChannelBindingUsers => Set<PushChannelBindingUser>();
    public DbSet<PushChannelBindingRole> PushChannelBindingRoles => Set<PushChannelBindingRole>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<PushChannel>().Property(channel => channel.WebhookHeadersProtected).HasColumnType("text");
        builder.Entity<PushChannel>().Property(channel => channel.WebhookPayloadTemplate).HasColumnType("text");
        builder.Entity<PushChannel>().Property(channel => channel.EmailRecipients).HasColumnType("text");
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
        builder.Entity<Incident>().HasIndex(incident => new { incident.ApplicationId, incident.IncidentKey });
        builder.Entity<Incident>().HasIndex(incident => new { incident.ApplicationId, incident.ActiveMatchKey }).IsUnique();
        builder.Entity<Incident>().HasIndex(incident => new { incident.ApplicationId, incident.AlertTitle, incident.AlertLevel, incident.Status });
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
    }

    public virtual  Task MigrateAsync(CancellationToken cancellationToken) =>
        Database.MigrateAsync(cancellationToken);

    public virtual  Task<bool> CanConnectAsync() =>
        Database.CanConnectAsync();
}

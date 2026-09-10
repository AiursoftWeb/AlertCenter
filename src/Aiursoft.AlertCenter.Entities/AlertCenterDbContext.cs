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
    public DbSet<PushChannel> PushChannels => Set<PushChannel>();
    public DbSet<ApplicationPushChannel> ApplicationPushChannels => Set<ApplicationPushChannel>();
    public DbSet<PushDelivery> PushDeliveries => Set<PushDelivery>();
    public DbSet<InAppNotification> InAppNotifications => Set<InAppNotification>();

    public DbSet<PushChannelUser> PushChannelUsers => Set<PushChannelUser>();
    public DbSet<PushChannelRole> PushChannelRoles => Set<PushChannelRole>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<PushChannel>().Property(channel => channel.WebhookHeadersProtected).HasColumnType("text");
        builder.Entity<PushChannel>().Property(channel => channel.WebhookPayloadTemplate).HasColumnType("text");
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
        builder.Entity<ReportedMessage>().HasIndex(m => new { m.ApplicationId, m.ReportId }).IsUnique();
        builder.Entity<ReportedMessage>().HasIndex(m => m.ReceivedAtUtc);
        builder.Entity<ReportedMessage>().HasOne(m => m.Application).WithMany()
            .HasForeignKey(m => m.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<ApplicationPushChannel>().HasKey(link => new { link.ApplicationId, link.ChannelId });
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

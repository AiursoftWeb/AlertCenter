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

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<ReportedMessage>().HasIndex(m => new { m.ApplicationId, m.ReportId }).IsUnique();
        builder.Entity<ReportedMessage>().HasIndex(m => m.ReceivedAtUtc);
        builder.Entity<ReportedMessage>().HasOne(m => m.Application).WithMany()
            .HasForeignKey(m => m.ApplicationId).OnDelete(DeleteBehavior.Restrict);
    }

    public virtual  Task MigrateAsync(CancellationToken cancellationToken) =>
        Database.MigrateAsync(cancellationToken);

    public virtual  Task<bool> CanConnectAsync() =>
        Database.CanConnectAsync();
}

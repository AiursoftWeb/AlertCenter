using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.MySql;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

/// <summary>
/// This test class ensures that the Entity Framework migrations are up-to-date for all supported database providers.
/// If you change the database model (entities), you must create a new migration for both SQLite and MySQL.
/// </summary>
[TestClass]
public class MigrationTests
{
    [TestMethod]
    public void TestSqliteMigrations()
    {
        var options = new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        using var context = new SqliteContext(options);
        var hasPendingChanges = context.Database.HasPendingModelChanges();
        Assert.IsFalse(hasPendingChanges, "There are pending model changes for Sqlite. Please run 'dotnet ef migrations add' for Sqlite.");
    }

    [TestMethod]
    public async Task SeverityMigrationPreservesExistingBindingsAndDeliveries()
    {
        await using var context = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        await context.GetService<IMigrator>().MigrateAsync("20260906192508_AddPushCenter");
        var applicationId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var disabledChannelId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO SourceApplications (Id, Name, SecretHash, IsEnabled, CreatedAtUtc)
            VALUES ({applicationId}, {"Existing application"}, {"hash"}, {true}, {now});
            INSERT INTO PushChannels (Id, Name, Type, IsEnabled, CreatedAtUtc)
            VALUES ({channelId}, {"Existing channel"}, {"inapp"}, {true}, {now}),
                   ({disabledChannelId}, {"Disabled channel"}, {"inapp"}, {false}, {now});
            INSERT INTO ReportedMessages (Id, ApplicationId, ReportId, Title, Content, ContextJson, Level, OccurredAtUtc, ReceivedAtUtc)
            VALUES ({messageId}, {applicationId}, {Guid.NewGuid()}, {"Existing alert"}, {null}, {null}, {"sev3"}, {now}, {now});
            INSERT INTO PushDeliveries (Id, ChannelId, MessageId, Status, Attempts, CreatedAtUtc)
            VALUES ({deliveryId}, {channelId}, {messageId}, {PushDelivery.Pending}, {0}, {now});
            INSERT INTO ApplicationPushChannels (ApplicationId, ChannelId)
            VALUES ({applicationId}, {channelId}), ({applicationId}, {disabledChannelId});
            """);

        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();
        var bindings = await context.ApplicationPushChannels.ToListAsync();
        Assert.AreEqual(2, bindings.Count);
        Assert.IsTrue(bindings.All(link => link.ApplicationId == applicationId &&
            link.ReceiveSev1 && link.ReceiveSev2 && link.ReceiveSev3 && link.ReceiveSev4));
        CollectionAssert.AreEquivalent(new[] { channelId, disabledChannelId }, bindings.Select(link => link.ChannelId).ToArray());
        Assert.AreEqual(messageId, (await context.ReportedMessages.SingleAsync()).Id);
        Assert.AreEqual(deliveryId, (await context.PushDeliveries.SingleAsync()).Id);
        Assert.AreEqual(PushDelivery.Pending, (await context.PushDeliveries.SingleAsync()).Status);
        Assert.IsFalse((await context.PushChannels.SingleAsync(item => item.Id == disabledChannelId)).IsEnabled);
    }

    [TestMethod]
    public async Task ActiveIncidentMigrationKeepsOnlyTheNewestDuplicateMatchActive()
    {
        await using var context = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        await context.GetService<IMigrator>().MigrateAsync("20260915171818_AddApplicationAccess");
        var applicationId = Guid.NewGuid();
        var olderIncidentId = Guid.NewGuid();
        var newerIncidentId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO SourceApplications (Id, Name, SecretHash, IsEnabled, CreatedAtUtc)
            VALUES ({applicationId}, {"Existing application"}, {"hash"}, {true}, {now});
            INSERT INTO Incidents (Id, ApplicationId, Title, IncidentKey, AlertTitle, AlertLevel, Status, TriggeredAtUtc)
            VALUES ({olderIncidentId}, {applicationId}, {"Older outage"}, {"payment-provider"}, {"Payment failed"}, {"sev1"}, {Incident.Triggered}, {now.AddMinutes(-1)}),
                   ({newerIncidentId}, {applicationId}, {"Newer outage"}, {"payment-provider"}, {"Payment failed"}, {"sev1"}, {Incident.Acknowledged}, {now});
            """);

        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();
        Assert.IsNull((await context.Incidents.SingleAsync(incident => incident.Id == olderIncidentId)).ActiveMatchKey);
        Assert.AreEqual("key:payment-provider", (await context.Incidents.SingleAsync(incident => incident.Id == newerIncidentId)).ActiveMatchKey);
    }

    [TestMethod]
    public async Task EmailRecipientsMigrationPreservesExistingChannels()
    {
        await using var context = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        await context.GetService<IMigrator>().MigrateAsync("20260910144921_AddWebhookCustomization");
        var channelId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO PushChannels (Id, Name, Type, IsEnabled, CreatedAtUtc)
            VALUES ({channelId}, {"Existing channel"}, {"inapp"}, {true}, {DateTime.UtcNow});
            """);

        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();
        var channel = await context.PushChannels.SingleAsync();
        Assert.AreEqual(channelId, channel.Id);
        Assert.IsNull(channel.EmailRecipients);
    }

    [TestMethod]
    public void TestMySqlMigrations()
    {
        var options = new DbContextOptionsBuilder<MySqlContext>()
            .UseMySql("Server=localhost;Database=test;Uid=root;Pwd=password;", new MySqlServerVersion(new Version(8, 0, 31)))
            .Options;
        using var context = new MySqlContext(options);
        var hasPendingChanges = context.Database.HasPendingModelChanges();
        Assert.IsFalse(hasPendingChanges, "There are pending model changes for MySql. Please run 'dotnet ef migrations add' for MySql.");
    }
}

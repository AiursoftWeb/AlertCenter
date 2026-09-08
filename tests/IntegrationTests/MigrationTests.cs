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
        var application = new SourceApplication { Name = "Existing application" };
        var channel = new PushChannel { Name = "Existing channel", Type = "inapp" };
        var disabled = new PushChannel { Name = "Disabled channel", Type = "inapp", IsEnabled = false };
        var message = new ReportedMessage
        {
            Application = application, ReportId = Guid.NewGuid(), Title = "Existing alert", OccurredAtUtc = DateTime.UtcNow
        };
        var delivery = new PushDelivery { Channel = channel, Message = message };
        context.PushChannels.Add(disabled);
        context.PushDeliveries.Add(delivery);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ApplicationPushChannels (ApplicationId, ChannelId)
            VALUES ({application.Id}, {channel.Id}), ({application.Id}, {disabled.Id});
            """);

        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();
        var bindings = await context.ApplicationPushChannels.ToListAsync();
        Assert.AreEqual(2, bindings.Count);
        Assert.IsTrue(bindings.All(link => link.ApplicationId == application.Id &&
            link.ReceiveSev1 && link.ReceiveSev2 && link.ReceiveSev3 && link.ReceiveSev4));
        CollectionAssert.AreEquivalent(new[] { channel.Id, disabled.Id }, bindings.Select(link => link.ChannelId).ToArray());
        Assert.AreEqual(message.Id, (await context.ReportedMessages.SingleAsync()).Id);
        Assert.AreEqual(delivery.Id, (await context.PushDeliveries.SingleAsync()).Id);
        Assert.AreEqual(PushDelivery.Pending, (await context.PushDeliveries.SingleAsync()).Status);
        Assert.IsFalse((await context.PushChannels.SingleAsync(item => item.Id == disabled.Id)).IsEnabled);
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

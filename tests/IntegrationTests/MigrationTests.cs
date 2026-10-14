using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.MySql;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

/// <summary>
/// This test class ensures that the Entity Framework migrations are up-to-date for all supported database providers.
/// If you change the database model (entities), create a corresponding migration for both SQLite and MySQL.
/// </summary>
[TestClass]
public class MigrationTests
{
    [TestMethod]
    public async Task SqliteMigrationsCreateCurrentSchema()
    {
        await using var context = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await context.Database.OpenConnectionAsync();

        Assert.AreEqual(3, context.Database.GetMigrations().Count());
        Assert.EndsWith("_InitialCreate", context.Database.GetMigrations().First());
        Assert.EndsWith("_OnCallEmailNotifications", context.Database.GetMigrations().Last());
        Assert.IsFalse(context.Database.HasPendingModelChanges(),
            "There are pending model changes for Sqlite. Please run 'dotnet ef migrations add' for Sqlite.");

        await context.Database.MigrateAsync();

        CollectionAssert.AreEqual(context.Database.GetMigrations().ToArray(),
            (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.IsTrue(await context.Database.CanConnectAsync());
    }

    [TestMethod]
    public async Task ApplicationTeamMigrationKeepsExistingApplicationsUnboundAndEnforcesForeignKey()
    {
        await using var context = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(context.Database.GetMigrations().First());
        var applicationId = Guid.NewGuid();
        var createdAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO SourceApplications (Id, Name, SecretHash, IsEnabled, CreatedAtUtc) VALUES ({applicationId}, {"Existing application"}, {"existing-hash"}, {false}, {createdAt})");
        await context.Database.MigrateAsync();
        var application = await context.SourceApplications.SingleAsync();
        Assert.AreEqual(applicationId, application.Id);
        Assert.AreEqual("Existing application", application.Name);
        Assert.AreEqual("existing-hash", application.SecretHash);
        Assert.IsFalse(application.IsEnabled);
        Assert.AreEqual(createdAt, application.CreatedAtUtc);
        Assert.IsNull(application.OnCallGroupId);
        var group = new OnCallGroup { Name = "Operations" };
        context.OnCallGroups.Add(group);
        await context.SaveChangesAsync();
        application.OnCallGroupId = group.Id;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var saved = await context.SourceApplications.Include(item => item.OnCallGroup).SingleAsync();
        Assert.AreEqual(group.Id, saved.OnCallGroupId);
        Assert.AreEqual("Operations", saved.OnCallGroup!.Name);
        saved.OnCallGroupId = Guid.NewGuid();
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();
        Assert.AreEqual(group.Id, (await context.SourceApplications.SingleAsync()).OnCallGroupId);
    }

    [TestMethod]
    public async Task OnCallEmailMigrationPreservesCustomDeliveriesAndCanRevertBuiltInHistory()
    {
        await using var context = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        var migrator = context.GetService<IMigrator>();
        var previous = context.Database.GetMigrations().Single(id => id.EndsWith("_AddApplicationOnCallGroup"));
        await migrator.MigrateAsync(previous);
        var channelId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow;
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO PushChannels (Id, Name, Type, EmailRecipient, IsEnabled, CreatedAtUtc) VALUES ({channelId}, {"Custom"}, {"email"}, {"custom@example.com"}, {true}, {createdAt})");
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO PushDeliveries (Id, ChannelId, Status, Attempts, CreatedAtUtc) VALUES ({deliveryId}, {channelId}, {PushDelivery.Failed}, {1}, {createdAt})");
        await context.Database.MigrateAsync();
        Assert.AreEqual(channelId, (await context.PushDeliveries.SingleAsync()).ChannelId);
        var builtIn = new PushDelivery
        {
            OnCallEmail = new OnCallEmailSnapshot
            {
                GroupId = Guid.NewGuid(), GroupName = "Snapshot", NotificationAtUtc = createdAt,
                CcRecipients = "cc@example.com",
                Recipients = [new OnCallEmailRecipient { UserId = "deleted-user", DisplayName = "Historical", Email = "oce@example.com" }]
            }
        };
        context.PushDeliveries.Add(builtIn);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var snapshot = await context.OnCallEmailSnapshots.Include(item => item.Recipients).SingleAsync();
        Assert.AreEqual("oce@example.com", snapshot.Recipients.Single().Email);
        Assert.AreEqual("cc@example.com", snapshot.CcRecipients);
        await migrator.MigrateAsync(previous);
        Assert.AreEqual(1, await context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM PushDeliveries").SingleAsync());
        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();
        Assert.AreEqual(deliveryId, (await context.PushDeliveries.SingleAsync()).Id);
        Assert.AreEqual(0, await context.OnCallEmailSnapshots.CountAsync());
    }

    [TestMethod]
    public async Task InitialMigrationPersistsStaffingDefaultsAndExplicitRequirements()
    {
        await using var context = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        await context.Database.MigrateAsync();
        var start = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        var rule = new OnCallRule { Name = "Default staffing", RestHours = 24, BalanceShiftTimes = true };
        var group = new OnCallGroup { Name = "Team", Rule = rule, AnchorUtc = start };
        var shift = new OnCallShift
        {
            Group = group, StartsAtUtc = start, EndsAtUtc = start.AddHours(8), RestHours = 24
        };
        var explicitRule = new OnCallRule { Name = "Redundant staffing", MinimumStaff = 2, TargetStaff = 3 };
        context.OnCallShifts.Add(shift);
        context.OnCallRules.Add(explicitRule);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var savedRule = await context.OnCallRules.SingleAsync(item => item.Id == rule.Id);
        Assert.AreEqual("Default staffing", savedRule.Name);
        Assert.AreEqual(24, savedRule.RestHours);
        Assert.IsTrue(savedRule.BalanceShiftTimes);
        Assert.AreEqual(1, savedRule.MinimumStaff);
        Assert.IsNull(savedRule.TargetStaff);
        var savedGroup = await context.OnCallGroups.SingleAsync();
        Assert.AreEqual(group.Id, savedGroup.Id);
        Assert.AreEqual(rule.Id, savedGroup.RuleId);
        Assert.AreEqual(group.ScheduleVersion, savedGroup.ScheduleVersion);
        var savedShift = await context.OnCallShifts.SingleAsync();
        Assert.AreEqual(shift.Id, savedShift.Id);
        Assert.AreEqual(group.Id, savedShift.GroupId);
        Assert.AreEqual(start, savedShift.StartsAtUtc);
        Assert.AreEqual(start.AddHours(8), savedShift.EndsAtUtc);
        Assert.AreEqual(24, savedShift.RestHours);
        Assert.AreEqual(1, savedShift.MinimumStaff);
        var savedExplicitRule = await context.OnCallRules.SingleAsync(item => item.Id == explicitRule.Id);
        Assert.AreEqual(2, savedExplicitRule.MinimumStaff);
        Assert.AreEqual(3, savedExplicitRule.TargetStaff);
    }

    [TestMethod]
    public void MySqlMigrationsMatchCurrentModel()
    {
        var options = new DbContextOptionsBuilder<MySqlContext>()
            .UseMySql("Server=localhost;Database=test;Uid=root;Pwd=password;", new MySqlServerVersion(new Version(8, 0, 31)))
            .Options;
        using var context = new MySqlContext(options);

        Assert.AreEqual(3, context.Database.GetMigrations().Count());
        Assert.EndsWith("_InitialCreate", context.Database.GetMigrations().First());
        Assert.EndsWith("_OnCallEmailNotifications", context.Database.GetMigrations().Last());
        Assert.IsFalse(context.Database.HasPendingModelChanges(),
            "There are pending model changes for MySql. Please run 'dotnet ef migrations add' for MySql.");
        var script = context.GetService<IMigrator>().GenerateScript();
        StringAssert.Contains(script, "CREATE TABLE `OnCallGroups`");
        StringAssert.Contains(script, "CREATE TABLE `OnCallGroupMembers`");
        StringAssert.Contains(script, "CREATE TABLE `OnCallRules`");
        StringAssert.Contains(script, "CREATE TABLE `OnCallShifts`");
        StringAssert.Contains(script, "`MinimumStaff`");
        StringAssert.Contains(script, "`TargetStaff`");
        StringAssert.Contains(script, "`OnCallGroupId`");
        StringAssert.Contains(script, "FK_SourceApplications_OnCallGroups_OnCallGroupId");
        StringAssert.Contains(script, "ON DELETE RESTRICT");
        StringAssert.Contains(script, "CREATE TABLE `OnCallEmailSnapshots`");
        StringAssert.Contains(script, "CREATE TABLE `OnCallEmailRecipient`");
        StringAssert.Contains(script, "`EmailCcRecipients`");
        Assert.DoesNotContain("OnCallRuleMembers", script);
    }
}

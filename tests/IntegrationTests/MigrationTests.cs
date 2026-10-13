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
    public async Task SqliteInitialMigrationCreatesCurrentSchema()
    {
        await using var context = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await context.Database.OpenConnectionAsync();

        Assert.AreEqual(7, context.Database.GetMigrations().Count());
        Assert.EndsWith("_OnCallStaffingRequirements", context.Database.GetMigrations().Last());
        Assert.IsFalse(context.Database.HasPendingModelChanges(),
            "There are pending model changes for Sqlite. Please run 'dotnet ef migrations add' for Sqlite.");

        await context.Database.MigrateAsync();

        Assert.AreEqual(7, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.IsTrue(await context.Database.CanConnectAsync());
    }

    [TestMethod]
    public async Task StaffingMigrationPreservesExistingRulesAndShiftsWithLegacyDefaults()
    {
        await using var context = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261013150715_TeamOwnedOnCallSchedules");
        var ruleId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var version = Guid.NewGuid();
        var start = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO OnCallRules (Id, Name, ShiftHours, RestHours, BalanceShiftTimes) VALUES ({ruleId}, {"Legacy rule"}, {8}, {24}, {true})");
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO OnCallGroups (Id, Name, TimeZoneId, RuleId, AnchorUtc, ScheduleVersion) VALUES ({groupId}, {"Legacy team"}, {"UTC"}, {ruleId}, {start}, {version})");
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO OnCallShifts (Id, GroupId, StartsAtUtc, EndsAtUtc, RestHours) VALUES ({shiftId}, {groupId}, {start}, {start.AddHours(8)}, {24})");

        await migrator.MigrateAsync();
        var rule = await context.OnCallRules.SingleAsync();
        Assert.AreEqual(ruleId, rule.Id);
        Assert.AreEqual("Legacy rule", rule.Name);
        Assert.AreEqual(24, rule.RestHours);
        Assert.IsTrue(rule.BalanceShiftTimes);
        Assert.AreEqual(1, rule.MinimumStaff);
        Assert.IsNull(rule.TargetStaff);
        var group = await context.OnCallGroups.SingleAsync();
        Assert.AreEqual(groupId, group.Id);
        Assert.AreEqual(ruleId, group.RuleId);
        Assert.AreEqual(version, group.ScheduleVersion);
        var shift = await context.OnCallShifts.SingleAsync();
        Assert.AreEqual(shiftId, shift.Id);
        Assert.AreEqual(groupId, shift.GroupId);
        Assert.AreEqual(start, shift.StartsAtUtc);
        Assert.AreEqual(start.AddHours(8), shift.EndsAtUtc);
        Assert.AreEqual(24, shift.RestHours);
        Assert.AreEqual(1, shift.MinimumStaff);
    }

    [TestMethod]
    public void MySqlInitialMigrationMatchesCurrentModel()
    {
        var options = new DbContextOptionsBuilder<MySqlContext>()
            .UseMySql("Server=localhost;Database=test;Uid=root;Pwd=password;", new MySqlServerVersion(new Version(8, 0, 31)))
            .Options;
        using var context = new MySqlContext(options);

        Assert.AreEqual(7, context.Database.GetMigrations().Count());
        Assert.EndsWith("_OnCallStaffingRequirements", context.Database.GetMigrations().Last());
        Assert.IsFalse(context.Database.HasPendingModelChanges(),
            "There are pending model changes for MySql. Please run 'dotnet ef migrations add' for MySql.");
    }
}

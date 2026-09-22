using Aiursoft.AlertCenter.MySql;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.EntityFrameworkCore;

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

        var expectedMigrationCount = context.Database.GetMigrations().Count();
        Assert.IsTrue(expectedMigrationCount > 0);
        Assert.IsFalse(context.Database.HasPendingModelChanges(),
            "There are pending model changes for Sqlite. Please run 'dotnet ef migrations add' for Sqlite.");

        await context.Database.MigrateAsync();

        Assert.AreEqual(expectedMigrationCount, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.IsTrue(await context.Database.CanConnectAsync());
    }

    [TestMethod]
    public void MySqlInitialMigrationMatchesCurrentModel()
    {
        var options = new DbContextOptionsBuilder<MySqlContext>()
            .UseMySql("Server=localhost;Database=test;Uid=root;Pwd=password;", new MySqlServerVersion(new Version(8, 0, 31)))
            .Options;
        using var context = new MySqlContext(options);

        Assert.IsTrue(context.Database.GetMigrations().Any());
        Assert.IsFalse(context.Database.HasPendingModelChanges(),
            "There are pending model changes for MySql. Please run 'dotnet ef migrations add' for MySql.");
    }
}

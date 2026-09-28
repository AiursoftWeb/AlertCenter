using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallRulePersistenceTests
{
    [TestMethod]
    public async Task OneGroupCanSaveMultipleIndependentRulesAndOrderedMembers()
    {
        await using var db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        db.Users.AddRange(
            new User { Id = "a", UserName = "a", NormalizedUserName = "A", DisplayName = "Alice" },
            new User { Id = "b", UserName = "b", NormalizedUserName = "B", DisplayName = "Bob" },
            new User { Id = "c", UserName = "c", NormalizedUserName = "C", DisplayName = "Carol" });
        var group = new OnCallGroup { Name = "Platform", TimeZoneId = "Asia/Shanghai" };
        db.OnCallGroups.Add(group);
        var anchor = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        db.OnCallRules.AddRange(
            new OnCallRule
            {
                Group = group, Name = "Normal", AnchorUtc = anchor,
                Members = [new OnCallRuleMember { UserId = "b", RotationOrder = 0 },
                    new OnCallRuleMember { UserId = "a", RotationOrder = 1 },
                    new OnCallRuleMember { UserId = "c", RotationOrder = 2 }]
            },
            new OnCallRule
            {
                Group = group, Name = "Short", AnchorUtc = anchor, ShiftHours = 4, RestHours = 8,
                Members = [new OnCallRuleMember { UserId = "c", RotationOrder = 0 },
                    new OnCallRuleMember { UserId = "b", RotationOrder = 1 },
                    new OnCallRuleMember { UserId = "a", RotationOrder = 2 }]
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var rules = await db.OnCallRules.Include(rule => rule.Members).OrderBy(rule => rule.Name).ToListAsync();
        Assert.AreEqual(2, rules.Count);
        CollectionAssert.AreEqual(new[] { "b", "a", "c" },
            rules.Single(rule => rule.Name == "Normal").Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray());
        Assert.AreEqual(4, rules.Single(rule => rule.Name == "Short").ShiftHours);
    }
}

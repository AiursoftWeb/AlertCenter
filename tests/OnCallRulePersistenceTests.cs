using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallRulePersistenceTests
{
    [TestMethod]
    public async Task MigrationsCreateTeamOwnedSchedules()
    {
        await using var db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync();
        var user = new User { Id = "staff", UserName = "staff", DisplayName = "Staff" };
        db.Users.Add(user);
        var anchor = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        var group = new OnCallGroup
        {
            Name = "Team", Rule = new OnCallRule { Name = "Reusable" }, AnchorUtc = anchor,
            Members = [new OnCallGroupMember { UserId = user.Id, RotationOrder = 0 }]
        };
        db.OnCallShifts.Add(new OnCallShift
        {
            Group = group, StartsAtUtc = anchor, EndsAtUtc = anchor.AddHours(8), RestHours = 24,
            Members = [new OnCallShiftMember { UserId = user.Id }]
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.AreEqual("Staff", (await db.Users.SingleAsync()).DisplayName);
        Assert.AreEqual(24, (await db.OnCallShifts.SingleAsync()).RestHours);
        Assert.AreEqual(1, await db.OnCallGroupMembers.CountAsync());
        Assert.AreEqual(1, await db.OnCallShiftMembers.CountAsync());
        Assert.AreEqual(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'OnCallRuleMembers'").SingleAsync());

        await migrator.MigrateAsync();
        Assert.AreEqual(1, await db.OnCallGroups.CountAsync());
        Assert.AreEqual(1, await db.Users.CountAsync());
    }

    [TestMethod]
    public async Task TeamsShareTemplateButPersistIndependentOrderedMembersAnchorsAndVersions()
    {
        await using var db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        db.Users.AddRange(new[] { "a", "b", "c" }.Select(id => new User
        {
            Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant(), DisplayName = id
        }));
        var rule = new OnCallRule { Name = "Shared", BalanceShiftTimes = true };
        var anchor = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        var first = new OnCallGroup
        {
            Name = "Platform", TimeZoneId = "Asia/Shanghai", Rule = rule, AnchorUtc = anchor,
            Members = [new OnCallGroupMember { UserId = "b", RotationOrder = 0 },
                new OnCallGroupMember { UserId = "a", RotationOrder = 1 },
                new OnCallGroupMember { UserId = "c", RotationOrder = 2 }]
        };
        var second = new OnCallGroup
        {
            Name = "Operations", Rule = rule, AnchorUtc = anchor.AddHours(4),
            Members = [new OnCallGroupMember { UserId = "c", RotationOrder = 0 },
                new OnCallGroupMember { UserId = "a", RotationOrder = 1 }]
        };
        var manual = new OnCallGroup { Name = "Manual", AnchorUtc = anchor };
        db.OnCallGroups.AddRange(first, second, manual);
        db.OnCallRules.Add(new OnCallRule { Name = "Unused" });
        var shift = new OnCallShift
        {
            Group = first, StartsAtUtc = anchor, EndsAtUtc = anchor.AddHours(8), RestHours = 24,
            Members = [new OnCallShiftMember { UserId = "b" }]
        };
        db.OnCallShifts.Add(shift);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var groups = await db.OnCallGroups.Include(group => group.Rule)
            .Include(group => group.Members).ThenInclude(member => member.User).ToDictionaryAsync(group => group.Id);
        Assert.AreEqual(2, await db.OnCallRules.CountAsync());
        Assert.AreEqual(rule.Id, groups[first.Id].RuleId);
        Assert.AreEqual(rule.Id, groups[second.Id].RuleId);
        Assert.IsTrue(groups[first.Id].Rule!.BalanceShiftTimes);
        Assert.IsNull(groups[manual.Id].RuleId);
        CollectionAssert.AreEqual(new[] { "b", "a", "c" },
            groups[first.Id].Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray());
        CollectionAssert.AreEqual(new[] { "c", "a" },
            groups[second.Id].Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray());
        Assert.IsTrue(groups[first.Id].Members.All(member => member.GroupId == first.Id && member.User.Id == member.UserId));
        Assert.AreEqual(anchor, groups[first.Id].AnchorUtc);
        Assert.AreEqual(anchor.AddHours(4), groups[second.Id].AnchorUtc);
        Assert.AreEqual(first.ScheduleVersion, groups[first.Id].ScheduleVersion);
        Assert.AreNotEqual(groups[first.Id].ScheduleVersion, groups[second.Id].ScheduleVersion);
        var saved = await db.OnCallShifts.Include(item => item.Group).Include(item => item.Members).SingleAsync();
        Assert.AreEqual(first.Id, saved.GroupId);
        Assert.AreEqual(24, saved.RestHours);
        Assert.AreEqual("b", saved.Members.Single().UserId);
    }
}

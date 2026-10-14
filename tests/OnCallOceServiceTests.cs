using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallOceServiceTests
{
    [TestMethod]
    [DataRow("UTC")]
    [DataRow("Asia/Shanghai")]
    [DataRow("America/New_York")]
    public async Task QueriesHalfOpenUtcBoundariesAndAllSavedAssigneesRegardlessOfTeamMembership(string zone)
    {
        await using var db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        db.Users.AddRange(new[] { "a", "b", "c" }.Select(id => new User
        {
            Id = id, UserName = id, DisplayName = id, Email = id + "@example.com"
        }));
        var start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var group = new OnCallGroup { Name = "Team", TimeZoneId = zone };
        var first = new OnCallShift
        {
            Group = group, StartsAtUtc = start, EndsAtUtc = start.AddHours(8),
            Members = [new OnCallShiftMember { UserId = "a" }, new OnCallShiftMember { UserId = "b" }]
        };
        var next = new OnCallShift
        {
            Group = group, StartsAtUtc = start.AddHours(8), EndsAtUtc = start.AddHours(16),
            Members = [new OnCallShiftMember { UserId = "c" }]
        };
        db.OnCallShifts.AddRange(first, next);
        await db.SaveChangesAsync();
        var service = new OnCallOceService(db);
        Assert.IsNotNull((await service.QueryAsync(group.Id, start.AddTicks(-1))).Error);
        foreach (var at in new[] { start, start.AddHours(8).AddTicks(-1) })
        {
            var result = await service.QueryAsync(group.Id, at);
            Assert.IsNull(result.Error);
            Assert.AreEqual(first.Id, result.ShiftId);
            CollectionAssert.AreEqual(new[] { "a", "b" }, result.Engineers.Select(engineer => engineer.UserId).ToArray());
            Assert.AreEqual(DateTimeKind.Utc, result.StartsAtUtc!.Value.Kind);
            Assert.AreEqual(DateTimeKind.Utc, result.EndsAtUtc!.Value.Kind);
        }
        var boundary = await service.QueryAsync(group.Id, start.AddHours(8));
        Assert.AreEqual(next.Id, boundary.ShiftId);
        Assert.AreEqual("c", boundary.Engineers.Single().UserId);
        Assert.IsNotNull((await service.QueryAsync(group.Id, start.AddHours(16))).Error);
        await Assert.ThrowsAsync<ArgumentException>(() => service.QueryAsync(group.Id, DateTime.SpecifyKind(start, DateTimeKind.Unspecified)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.QueryAsync(group.Id, DateTime.SpecifyKind(start, DateTimeKind.Local)));
    }
}

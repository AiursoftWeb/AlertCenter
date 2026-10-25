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
    public async Task SeverityPolicyMigrationSeedsDefaultsWithoutResettingAdministratorChoices()
    {
        await using var context = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        await context.Database.MigrateAsync();
        var rules = await context.SeverityRules.OrderBy(rule => rule.Level).ToListAsync();
        Assert.AreEqual(5, rules.Count);
        Assert.AreEqual(4, rules.Count(rule => rule.Email));
        Assert.AreEqual(3, rules.Count(rule => rule.Sms));
        Assert.IsTrue(rules.All(rule => rule.ShowInLists));
        rules[0].Email = false;
        rules[4].Email = true;
        await context.SaveChangesAsync();
        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();
        Assert.IsFalse((await context.SeverityRules.SingleAsync(rule => rule.Level == "sev0")).Email);
        Assert.IsTrue((await context.SeverityRules.SingleAsync(rule => rule.Level == "sev4")).Email);
    }

    [TestMethod]
    public async Task MitigationScopeMigrationPreservesExistingKeysWithoutGrantingNewPermission()
    {
        await using var context = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        await context.GetService<IMigrator>().MigrateAsync("20261018173719_IncidentSeverityScope");
        var applicationId = Guid.NewGuid();
        var keyId = Guid.NewGuid();
        var createdAt = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO SourceApplications (Id, Name, IsEnabled, CreatedAtUtc) VALUES ({applicationId}, {"Existing app"}, {true}, {createdAt})");
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ApplicationApiKeys (Id, ApplicationId, Name, SecretHash, IsEnabled, CanQueryIncidents, CanCreateIncidents, CanResolveIncidents, CanChangeIncidentSeverity, CreatedAtUtc, TotalCalls) VALUES ({keyId}, {applicationId}, {"Existing key"}, {"existing-hash"}, {true}, {true}, {false}, {true}, {true}, {createdAt}, {5L})");
        await context.Database.MigrateAsync();
        var key = await context.ApplicationApiKeys.SingleAsync();
        Assert.AreEqual(keyId, key.Id);
        Assert.AreEqual(applicationId, key.ApplicationId);
        Assert.AreEqual("existing-hash", key.SecretHash);
        Assert.AreEqual(5L, key.TotalCalls);
        Assert.IsTrue(key.IsEnabled);
        Assert.IsTrue(key.CanQueryIncidents);
        Assert.IsFalse(key.CanCreateIncidents);
        Assert.IsTrue(key.CanResolveIncidents);
        Assert.IsTrue(key.CanChangeIncidentSeverity);
        Assert.IsFalse(key.CanReopenIncidents);
        Assert.IsFalse(key.CanMitigateIncidents);
        Assert.IsFalse(key.CanPatchIncidentMetadata);
        Assert.IsFalse(key.CanCommentIncidents);
    }

    [TestMethod]
    public async Task MetadataMigrationPreservesExistingIncidentAndKeyScopes()
    {
        await using var context = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        await context.GetService<IMigrator>().MigrateAsync("IndependentIncidentAcknowledgement");
        var app = Guid.NewGuid();
        var id = Guid.NewGuid();
        var version = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO SourceApplications (Id, Name, IsEnabled, CreatedAtUtc) VALUES ({app}, {"Old app"}, {true}, {now})");
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Incidents (Id, ApplicationId, ReportId, Title, Level, Status, Version, OccurredAtUtc, ReceivedAtUtc, ContextJson) VALUES ({id}, {app}, {Guid.NewGuid()}, {"Old incident"}, {"sev2"}, {"new"}, {version}, {now}, {now}, {"{\"keep\":true}"})");
        await context.Database.MigrateAsync();
        var incident = await context.Incidents.SingleAsync();
        Assert.AreEqual(id, incident.Id);
        Assert.AreEqual(version, incident.Version);
        Assert.AreEqual("sev2", incident.Level);
        Assert.AreEqual("new", incident.Status);
        Assert.AreEqual("{\"keep\":true}", incident.ContextJson);
        Assert.IsNull(incident.MetadataJson);
    }

    [TestMethod]
    public async Task SqliteMigrationsCreateCurrentSchema()
    {
        await using var context = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await context.Database.OpenConnectionAsync();

        Assert.AreEqual(11, context.Database.GetMigrations().Count());
        Assert.EndsWith("_InitialCreate", context.Database.GetMigrations().First());
        Assert.IsFalse(context.Database.HasPendingModelChanges(),
            "There are pending model changes for Sqlite. Please run 'dotnet ef migrations add' for Sqlite.");

        await context.Database.MigrateAsync();

        CollectionAssert.AreEqual(context.Database.GetMigrations().ToArray(),
            (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.IsTrue(await context.Database.CanConnectAsync());
    }

    [TestMethod]
    public async Task ApplicationTeamBindingIsOptionalAndEnforcesForeignKey()
    {
        await using var context = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        await context.Database.MigrateAsync();
        var applicationId = Guid.NewGuid();
        var createdAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO SourceApplications (Id, Name, IsEnabled, CreatedAtUtc) VALUES ({applicationId}, {"Existing application"}, {false}, {createdAt})");
        await context.Database.MigrateAsync();
        var application = await context.SourceApplications.SingleAsync();
        Assert.AreEqual(applicationId, application.Id);
        Assert.AreEqual("Existing application", application.Name);
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
    public async Task InitialMigrationPersistsCustomDeliveriesAndIndependentOnCallSnapshots()
    {
        await using var context = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        await context.Database.MigrateAsync();
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
        Assert.AreEqual("deleted-user", snapshot.Recipients.Single().UserId);
        Assert.AreEqual("Historical", snapshot.Recipients.Single().DisplayName);
        Assert.AreEqual(2, await context.PushDeliveries.CountAsync());
        Assert.AreEqual(channelId, (await context.PushDeliveries.SingleAsync(delivery => delivery.Id == deliveryId)).ChannelId);
    }

    [TestMethod]
    public async Task SingleIncidentUpgradeDiscardsOldHistoryAndPreservesConfiguration()
    {
        await using var context = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(context.Database.GetMigrations().First());
        var application = new SourceApplication { Name = "Existing app" };
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO SourceApplications (Id, Name, SecretHash, IsEnabled, CreatedAtUtc) VALUES ({application.Id}, {application.Name}, {"discarded-old-hash"}, {true}, {application.CreatedAtUtc})");
        context.Users.Add(new User { Id = "existing-user", UserName = "existing", DisplayName = "Existing user" });
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Incidents (Id, ApplicationId, Title, AlertTitle, AlertLevel, MatchLevel, Status, Version, TriggeredAtUtc) VALUES ({Guid.NewGuid()}, {application.Id}, {"Old incident"}, {"Old report"}, {"sev1"}, {"sev1"}, {"new"}, {Guid.NewGuid()}, {DateTime.UtcNow})");
        await migrator.MigrateAsync();
        context.ChangeTracker.Clear();
        Assert.AreEqual("Existing app", (await context.SourceApplications.SingleAsync()).Name);
        Assert.AreEqual(0, await context.ApplicationApiKeys.CountAsync());
        Assert.AreEqual("Existing user", (await context.Users.SingleAsync()).DisplayName);
        Assert.AreEqual(0, await context.Incidents.CountAsync());
        Assert.AreEqual(0, await context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'ReportedMessages'").SingleAsync());
        context.Incidents.Add(new Incident { ApplicationId = application.Id, Title = "New incident" });
        await context.SaveChangesAsync();
        Assert.AreEqual(1, await context.Incidents.CountAsync());
        CollectionAssert.AreEqual(context.Database.GetMigrations().ToArray(), (await context.Database.GetAppliedMigrationsAsync()).ToArray());
    }

    [TestMethod]
    public async Task InitialMigrationPersistsIndependentCommentHistoryWithoutChangingIncident()
    {
        await using var context = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        await context.Database.MigrateAsync();
        var incident = new Incident { Application = new SourceApplication { Name = "Existing app" }, Title = "Existing incident", Status = Incident.Resolved };
        context.Incidents.Add(incident);
        await context.SaveChangesAsync();
        var version = incident.Version;
        await context.Database.MigrateAsync();
        context.IncidentComments.Add(new IncidentComment
        {
            IncidentId = incident.Id, AuthorName = "Historical engineer", Markdown = "**Incident evidence**",
            DeletedAtUtc = DateTime.UtcNow, DeletedByName = "Application manager"
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var comment = await context.IncidentComments.SingleAsync();
        Assert.AreEqual("**Incident evidence**", comment.Markdown);
        Assert.AreEqual("Historical engineer", comment.AuthorName);
        Assert.AreEqual("Application manager", comment.DeletedByName);
        Assert.IsNotNull(comment.DeletedAtUtc);
        Assert.AreEqual(version, (await context.Incidents.SingleAsync()).Version);
        Assert.AreEqual(1, await context.IncidentComments.CountAsync());
        Assert.AreEqual(Incident.Resolved, (await context.Incidents.SingleAsync()).Status);
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

        Assert.AreEqual(11, context.Database.GetMigrations().Count());
        Assert.EndsWith("_InitialCreate", context.Database.GetMigrations().First());
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
        StringAssert.Contains(script, "FK_Incidents_AspNetUsers_OwnerId");
        StringAssert.Contains(script, "FK_PushDeliveries_IncidentTimelineEntries_IncidentEventId");
        StringAssert.Contains(script, "`ActorIdSnapshot`");
        StringAssert.Contains(script, "`ActorName`");
        Assert.DoesNotContain("UPDATE Incidents", script);
        StringAssert.Contains(script, "CREATE TABLE `IncidentComments`");
        StringAssert.Contains(script, "FK_IncidentComments_AspNetUsers_AuthorId");
        StringAssert.Contains(script, "FK_IncidentComments_AspNetUsers_DeletedById");
        Assert.DoesNotContain("OnCallRuleMembers", script);
    }
    [TestMethod]
    public async Task IndependentAcknowledgementMigrationPreservesProgressOwnershipAndAuditHistory()
    {
        await using var context = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        await context.GetService<IMigrator>().MigrateAsync("20261019033852_IncidentMitigationScope");
        var owner = new User { UserName = "historical-owner", DisplayName = "Historical owner" };
        var application = new SourceApplication { Name = "Existing application" };
        var oldTime = new DateTime(2026, 10, 18, 1, 0, 0, DateTimeKind.Utc);
        var latestTime = oldTime.AddHours(1);
        var legacy = new Incident { Application = application, Title = "Legacy acknowledged", Status = Incident.Acked,
            Owner = owner, OwnerDisplayName = owner.DisplayName, AcknowledgedAtUtc = oldTime };
        var reopened = new Incident { Application = application, Title = "Previously reopened", Status = Incident.New };
        var resolved = new Incident { Application = application, Title = "Already resolved", Status = Incident.Resolved,
            AcknowledgedAtUtc = latestTime.AddHours(1), MitigatedAtUtc = oldTime, ResolvedAtUtc = latestTime };
        context.Users.Add(owner);
        context.SourceApplications.Add(application);
        await context.SaveChangesAsync();
        // Seed the historical schema explicitly; the current entity also contains newer columns.
        foreach (var item in new[] { legacy, reopened, resolved })
        {
            var ownerId = item.Owner?.Id;
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Incidents (Id, ApplicationId, ReportId, Title, Level, Status, Version, OccurredAtUtc, ReceivedAtUtc, OwnerId, OwnerDisplayName, AcknowledgedAtUtc, MitigatedAtUtc, ResolvedAtUtc) VALUES ({item.Id}, {application.Id}, {item.ReportId}, {item.Title}, {item.Level}, {item.Status}, {item.Version}, {item.OccurredAtUtc}, {item.ReceivedAtUtc}, {ownerId}, {item.OwnerDisplayName}, {item.AcknowledgedAtUtc}, {item.MitigatedAtUtc}, {item.ResolvedAtUtc})");
            context.IncidentTimelineEntries.Add(new IncidentTimelineEntry { IncidentId = item.Id, Type = IncidentTimelineEntry.Acked,
                ActorId = owner.Id, ActorIdSnapshot = owner.Id, ActorName = owner.DisplayName, CreatedAtUtc = latestTime });
        }
        await context.SaveChangesAsync();
        var version = legacy.Version;
        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();
        var saved = await context.Incidents.SingleAsync(item => item.Id == legacy.Id);
        Assert.AreEqual(Incident.New, saved.Status);
        Assert.IsTrue(saved.IsAcknowledged);
        Assert.AreEqual(latestTime, saved.AcknowledgedAtUtc);
        Assert.AreEqual(owner.Id, saved.OwnerId);
        Assert.AreEqual(owner.DisplayName, saved.OwnerDisplayName);
        Assert.AreEqual(version, saved.Version);
        saved = await context.Incidents.SingleAsync(item => item.Id == reopened.Id);
        Assert.AreEqual(Incident.New, saved.Status);
        Assert.AreEqual(latestTime, saved.AcknowledgedAtUtc);
        Assert.IsNull(saved.OwnerId);
        saved = await context.Incidents.SingleAsync(item => item.Id == resolved.Id);
        Assert.AreEqual(Incident.Resolved, saved.Status);
        Assert.AreEqual(latestTime.AddHours(1), saved.AcknowledgedAtUtc);
        Assert.AreEqual(oldTime, saved.MitigatedAtUtc);
        Assert.AreEqual(latestTime, saved.ResolvedAtUtc);
        Assert.AreEqual(3, await context.IncidentTimelineEntries.CountAsync());
        Assert.IsTrue(await context.IncidentTimelineEntries.AllAsync(entry => entry.Type == IncidentTimelineEntry.Acked && entry.ActorIdSnapshot == owner.Id));
        await context.GetService<IMigrator>().MigrateAsync("20261019033852_IncidentMitigationScope");
        context.ChangeTracker.Clear();
        Assert.AreEqual(Incident.Acked, await context.Database.SqlQuery<string>($"SELECT Status AS Value FROM Incidents WHERE Id = {legacy.Id}").SingleAsync());
        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();
        Assert.IsTrue((await context.Incidents.SingleAsync(item => item.Id == legacy.Id)).IsAcknowledged);
    }

}

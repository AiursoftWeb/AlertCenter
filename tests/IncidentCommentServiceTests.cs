using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class IncidentCommentServiceTests
{
    private SqliteContext db = null!;
    private Incident incident = null!;
    private IncidentCommentService service = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        db.Users.AddRange(new[] { "author", "viewer", "manager", "owner", "oce", "outsider" }.Select(id => new User
        {
            Id = id, UserName = id, DisplayName = id + " name", Email = id + "@example.com"
        }));
        var application = new SourceApplication { Name = "App" };
        incident = new Incident { Application = application, ApplicationId = application.Id, Title = "Outage", OwnerId = "owner" };
        db.Incidents.Add(incident);
        foreach (var id in new[] { "author", "viewer", "manager" })
            db.ApplicationUserAccesses.Add(new ApplicationUserAccess { ApplicationId = incident.ApplicationId, UserId = id, CanViewIncidents = true, CanUpdateIncidents = id != "viewer", CanManageApiKeys = id == "manager", CanManageApplication = id == "manager" });
        await db.SaveChangesAsync();
        service = new IncidentCommentService(db, new ApplicationAccessService(db), localizer: TestLocalization.For<IncidentCommentService>());
    }

    [TestCleanup]
    public async Task Cleanup() => await db.DisposeAsync();

    private static ClaimsPrincipal Actor(string id) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));
    private AddIncidentCommentRequest Request(string markdown = "Comment") => new() { IncidentId = incident.Id, Markdown = markdown };

    [TestMethod]
    [DataRow(Incident.New)]
    [DataRow(Incident.Acked)]
    [DataRow(Incident.Mitigated)]
    [DataRow(Incident.Resolved)]
    public async Task EditorsCanCommentInEveryStateWithoutChangingWorkflowOrSendingNotifications(string state)
    {
        incident.Status = state == Incident.Acked ? Incident.New : state;
        incident.AcknowledgedAtUtc = state == Incident.Acked ? DateTime.UtcNow : null;
        await db.SaveChangesAsync();
        var version = incident.Version;
        const string markdown = "  # 排查\n\n**影响**：数据库错误\n```sql\nSELECT 1;\n```\n ";
        Assert.AreEqual(200, (await service.AddAsync(Actor("author"), Request(markdown))).StatusCode);
        db.ChangeTracker.Clear();
        var saved = await db.IncidentComments.SingleAsync();
        Assert.AreEqual(markdown, saved.Markdown);
        Assert.AreEqual("author", saved.AuthorId);
        Assert.AreEqual("author name", saved.AuthorName);
        Assert.IsTrue(saved.CreatedAtUtc > DateTime.UtcNow.AddMinutes(-1));
        Assert.AreEqual(version, (await db.Incidents.SingleAsync()).Version);
        Assert.AreEqual(state == Incident.Acked ? Incident.New : state, (await db.Incidents.SingleAsync()).Status);
        Assert.AreEqual(0, await db.IncidentTimelineEntries.CountAsync());
        Assert.AreEqual(0, await db.PushDeliveries.CountAsync());
    }

    [TestMethod]
    public async Task IncidentAssignmentAndCurrentDutyDoNotGrantCommentAccess()
    {
        var group = new OnCallGroup { Name = "OCE" };
        db.OnCallShifts.Add(new OnCallShift { Group = group, StartsAtUtc = DateTime.UtcNow.AddHours(-1), EndsAtUtc = DateTime.UtcNow.AddHours(1), Members = [new OnCallShiftMember { UserId = "oce" }] });
        incident.Application.OnCallGroup = group;
        await db.SaveChangesAsync();
        foreach (var id in new[] { "viewer", "owner", "oce", "outsider" })
            Assert.AreEqual(403, (await service.AddAsync(Actor(id), Request())).StatusCode);
        Assert.AreEqual(0, await db.IncidentComments.CountAsync());
    }

    [TestMethod]
    public async Task RoleEditAccessAllowsCommentsAndRevocationBlocksPostingAndDeleting()
    {
        var role = new IdentityRole("Readers");
        db.Roles.Add(role);
        db.UserRoles.Add(new IdentityUserRole<string> { UserId = "outsider", RoleId = role.Id });
        db.ApplicationRoleAccesses.Add(new ApplicationRoleAccess { ApplicationId = incident.ApplicationId, RoleId = role.Id, CanViewIncidents = true, CanUpdateIncidents = true });
        await db.SaveChangesAsync();
        Assert.AreEqual(200, (await service.AddAsync(Actor("outsider"), Request())).StatusCode);
        var comment = await db.IncidentComments.SingleAsync();
        db.ApplicationRoleAccesses.Remove(await db.ApplicationRoleAccesses.SingleAsync());
        await db.SaveChangesAsync();
        Assert.AreEqual(403, (await service.AddAsync(Actor("outsider"), Request())).StatusCode);
        Assert.AreEqual(403, (await service.DeleteAsync(Actor("outsider"), new DeleteIncidentCommentRequest
        {
            IncidentId = incident.Id, CommentId = comment.Id
        })).StatusCode);
        Assert.IsNull(comment.DeletedAtUtc);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \r\n\t")]
    public async Task RejectsBlankComments(string? markdown)
    {
        Assert.AreEqual(400, (await service.AddAsync(Actor("author"), Request(markdown!))).StatusCode);
        Assert.AreEqual(0, await db.IncidentComments.CountAsync());
    }

    [TestMethod]
    public async Task EnforcesLengthAndRequiresAnExistingSignedInAuthor()
    {
        Assert.AreEqual(400, (await service.AddAsync(Actor("author"), Request(new string('x', 4001)))).StatusCode);
        Assert.AreEqual(200, (await service.AddAsync(Actor("author"), Request(new string('x', 4000)))).StatusCode);
        var missing = Request();
        missing.IncidentId = Guid.NewGuid();
        Assert.AreEqual(404, (await service.AddAsync(Actor("author"), missing)).StatusCode);
        Assert.AreEqual(403, (await service.AddAsync(new ClaimsPrincipal(new ClaimsIdentity()), Request())).StatusCode);
        Assert.AreEqual(1, await db.IncidentComments.CountAsync());
    }

    [TestMethod]
    [DataRow("author")]
    [DataRow("manager")]
    public async Task AuthorAndApplicationManagerCanSoftDeleteWithImmutableMarkdownAudit(string actor)
    {
        Assert.AreEqual(200, (await service.AddAsync(Actor("author"), Request("**Original evidence**"))).StatusCode);
        var comment = await db.IncidentComments.SingleAsync();
        var version = incident.Version;
        var request = new DeleteIncidentCommentRequest { IncidentId = incident.Id, CommentId = comment.Id };
        Assert.AreEqual(200, (await service.DeleteAsync(Actor(actor), request)).StatusCode);
        db.ChangeTracker.Clear();
        comment = await db.IncidentComments.SingleAsync();
        Assert.AreEqual("**Original evidence**", comment.Markdown);
        Assert.AreEqual(actor, comment.DeletedById);
        Assert.AreEqual(actor + " name", comment.DeletedByName);
        Assert.IsNotNull(comment.DeletedAtUtc);
        Assert.AreEqual(version, (await db.Incidents.SingleAsync()).Version);
        var originalDeleteTime = comment.DeletedAtUtc;
        Assert.AreEqual(409, (await service.DeleteAsync(Actor(actor), request)).StatusCode);
        Assert.AreEqual(originalDeleteTime, comment.DeletedAtUtc);
    }

    [TestMethod]
    public async Task ViewerAndOwnerCannotDeleteOthersAndCrossIncidentIdsAreRejected()
    {
        await service.AddAsync(Actor("author"), Request());
        var comment = await db.IncidentComments.SingleAsync();
        var request = new DeleteIncidentCommentRequest { IncidentId = incident.Id, CommentId = comment.Id };
        foreach (var id in new[] { "viewer", "owner", "outsider" })
            Assert.AreEqual(403, (await service.DeleteAsync(Actor(id), request)).StatusCode);
        request.IncidentId = Guid.NewGuid();
        Assert.AreEqual(404, (await service.DeleteAsync(Actor("manager"), request)).StatusCode);
        Assert.IsNull(comment.DeletedAtUtc);
    }

    [TestMethod]
    public async Task ConcurrentDeletionCannotOverwriteFirstDeletionAuditAndDeletedUsersKeepNames()
    {
        await service.AddAsync(Actor("author"), Request());
        var comment = await db.IncidentComments.SingleAsync();
        await using var second = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite(db.Database.GetDbConnection()).Options);
        await second.IncidentComments.SingleAsync();
        var request = new DeleteIncidentCommentRequest { IncidentId = incident.Id, CommentId = comment.Id };
        Assert.AreEqual(200, (await service.DeleteAsync(Actor("author"), request)).StatusCode);
        Assert.AreEqual(409, (await new IncidentCommentService(second, new ApplicationAccessService(second), localizer: TestLocalization.For<IncidentCommentService>()).DeleteAsync(Actor("manager"), request)).StatusCode);
        db.ChangeTracker.Clear();
        db.Users.Remove(await db.Users.SingleAsync(user => user.Id == "author"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var audit = await db.IncidentComments.SingleAsync();
        Assert.IsNull(audit.AuthorId);
        Assert.IsNull(audit.DeletedById);
        Assert.AreEqual("author name", audit.AuthorName);
        Assert.AreEqual("author name", audit.DeletedByName);
        Assert.AreEqual("Comment", audit.Markdown);
    }
}

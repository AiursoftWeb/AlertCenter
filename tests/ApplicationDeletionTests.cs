using Aiursoft.AlertCenter.Controllers;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class ApplicationDeletionTests
{
    private SqliteContext _db = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.MigrateAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _db.DisposeAsync();

    [TestMethod]
    [DataRow(0, "Delete this application")]
    [DataRow(2, "Delete this application")]
    [DataRow(1, "订单服务 <Orders> & \"通知\"")]
    public async Task DeleteRemovesOnlySelectedApplicationAndItsMessages(int messageCount, string name)
    {
        var application = new SourceApplication { Name = name };
        var other = new SourceApplication { Name = "Keep this application" };
        _db.SourceApplications.AddRange(application, other);
        for (var index = 0; index < messageCount; index++)
        {
            _db.ReportedMessages.Add(Message(application.Id));
        }
        _db.ReportedMessages.Add(Message(other.Id));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var controller = new AlertsController(_db);
        var result = await controller.DeleteApplication(application.Id, application.Name, CancellationToken.None);

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        Assert.AreEqual(nameof(AlertsController.Applications), ((RedirectToActionResult)result).ActionName);
        Assert.AreEqual(other.Id, (await _db.SourceApplications.SingleAsync()).Id);
        Assert.AreEqual(other.Id, (await _db.ReportedMessages.SingleAsync()).ApplicationId);
        Assert.IsInstanceOfType<NotFoundResult>(await controller.DeleteApplication(application.Id, application.Name, CancellationToken.None));

        var reports = new MessageReportsController(_db, new NullAlertDispatcher(NullLogger<NullAlertDispatcher>.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        reports.Request.Headers.Authorization = $"Bearer {application.Id}.old-secret";
        Assert.IsInstanceOfType<UnauthorizedResult>(await reports.Report(new ReportMessageRequest
        {
            ReportId = Guid.NewGuid(), Title = "Rejected report", OccurredAtUtc = DateTimeOffset.UtcNow
        }, CancellationToken.None));
    }

    [TestMethod]
    public async Task UnknownApplicationReturnsNotFoundAndPreservesMessages()
    {
        var application = new SourceApplication { Name = "Keep this application" };
        _db.SourceApplications.Add(application);
        _db.ReportedMessages.Add(Message(application.Id));
        await _db.SaveChangesAsync();

        var result = await new AlertsController(_db).DeleteApplication(Guid.NewGuid(), application.Name, CancellationToken.None);

        Assert.IsInstanceOfType<NotFoundResult>(result);
        Assert.AreEqual(1, await _db.SourceApplications.CountAsync());
        Assert.AreEqual(1, await _db.ReportedMessages.CountAsync());
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("delete this application")]
    [DataRow("Delete this application ")]
    [DataRow(" Delete this application")]
    [DataRow("Delete this")]
    [DataRow("Another application")]
    public async Task IncorrectConfirmationPreservesApplicationAndMessages(string? confirmationName)
    {
        var application = new SourceApplication { Name = "Delete this application" };
        _db.SourceApplications.Add(application);
        _db.ReportedMessages.Add(Message(application.Id));
        await _db.SaveChangesAsync();

        var result = await new AlertsController(_db)
            .DeleteApplication(application.Id, confirmationName, CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        Assert.AreEqual(application.Id, (await _db.SourceApplications.SingleAsync()).Id);
        Assert.AreEqual(application.Id, (await _db.ReportedMessages.SingleAsync()).ApplicationId);
    }

    [TestMethod]
    public async Task FailedApplicationDeletionRollsBackMessageDeletion()
    {
        var application = new SourceApplication { Name = "Preserve on failure" };
        _db.SourceApplications.Add(application);
        _db.ReportedMessages.Add(Message(application.Id));
        await _db.SaveChangesAsync();
        // 在消息删除之后制造应用删除失败，验证事务确实回滚而非留下半删除状态。
        await _db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER RejectApplicationDeletion BEFORE DELETE ON SourceApplications
            BEGIN SELECT RAISE(ABORT, 'Deletion rejected for rollback test'); END;
            """);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            new AlertsController(_db).DeleteApplication(application.Id, application.Name, CancellationToken.None));

        Assert.AreEqual(1, await _db.SourceApplications.CountAsync());
        Assert.AreEqual(1, await _db.ReportedMessages.CountAsync());
    }

    private static ReportedMessage Message(Guid applicationId) => new()
    {
        ApplicationId = applicationId, ReportId = Guid.NewGuid(), Title = "Test message",
        OccurredAtUtc = DateTime.UtcNow
    };
}

using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.SDK.Models;
using Aiursoft.AlertCenter.Authorization;
using System.Security.Claims;
using Aiursoft.AlertCenter.Controllers;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class ApplicationDeletionTests
{
    private SqliteContext _db = null!;
    private ServiceProvider _apiServices = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _apiServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        _db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.MigrateAsync();
        _db.Users.Add(new User { Id = "owner", UserName = "owner", DisplayName = "Owner" });
        await _db.SaveChangesAsync();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _db.DisposeAsync();
        await _apiServices.DisposeAsync();
    }

    [TestMethod]
    [DataRow(0, "Delete this application")]
    [DataRow(2, "Delete this application")]
    [DataRow(1, "订单服务 <Orders> & \"通知\"")]
    public async Task DeleteRemovesOnlySelectedApplicationAndItsMessages(int messageCount, string name)
    {
        var application = new SourceApplication { OwnerId = "owner", Name = name };
        var other = new SourceApplication { OwnerId = "owner", Name = "Keep this application" };
        _db.SourceApplications.AddRange(application, other);
        for (var index = 0; index < messageCount; index++)
        {
            _db.Incidents.Add(Message(application.Id));
        }
        _db.Incidents.Add(Message(other.Id));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var controller = AdminController();
        var result = await controller.DeleteApplication(application.Id, application.Name, CancellationToken.None);

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        Assert.AreEqual(nameof(ApplicationsController.Applications), ((RedirectToActionResult)result).ActionName);
        Assert.AreEqual(other.Id, (await _db.SourceApplications.SingleAsync()).Id);
        Assert.AreEqual(other.Id, (await _db.Incidents.SingleAsync()).ApplicationId);
        Assert.IsInstanceOfType<NotFoundResult>(await controller.DeleteApplication(application.Id, application.Name, CancellationToken.None));

        var reports = new IncidentReportsController(_db, new QueuedIncidentDispatcher(_db, TestLocalization.Factory))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = _apiServices } }
        };
        reports.Request.Headers.Authorization = $"Bearer {application.Id}.old-secret";
        var rejected = (JsonResult)await reports.Create(new CreateIncidentRequest
        {
            ReportId = Guid.NewGuid(), Title = "Rejected report", OccurredAtUtc = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        Assert.AreEqual(Code.WrongKey, ((AiurResponse)rejected.Value!).Code);
    }

    [TestMethod]
    public async Task UnknownApplicationReturnsNotFoundAndPreservesMessages()
    {
        var application = new SourceApplication { OwnerId = "owner", Name = "Keep this application" };
        _db.SourceApplications.Add(application);
        _db.Incidents.Add(Message(application.Id));
        await _db.SaveChangesAsync();

        var result = await AdminController().DeleteApplication(Guid.NewGuid(), application.Name, CancellationToken.None);

        Assert.IsInstanceOfType<NotFoundResult>(result);
        Assert.AreEqual(1, await _db.SourceApplications.CountAsync());
        Assert.AreEqual(1, await _db.Incidents.CountAsync());
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
        var application = new SourceApplication { OwnerId = "owner", Name = "Delete this application" };
        _db.SourceApplications.Add(application);
        _db.Incidents.Add(Message(application.Id));
        await _db.SaveChangesAsync();

        var result = await AdminController()
            .DeleteApplication(application.Id, confirmationName, CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        Assert.AreEqual(application.Id, (await _db.SourceApplications.SingleAsync()).Id);
        Assert.AreEqual(application.Id, (await _db.Incidents.SingleAsync()).ApplicationId);
    }

    [TestMethod]
    public async Task FailedApplicationDeletionRollsBackMessageDeletion()
    {
        var application = new SourceApplication { OwnerId = "owner", Name = "Preserve on failure" };
        _db.SourceApplications.Add(application);
        _db.Incidents.Add(Message(application.Id));
        await _db.SaveChangesAsync();
        // Fail application deletion after incident deletion to verify atomic rollback.
        await _db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER RejectApplicationDeletion BEFORE DELETE ON SourceApplications
            BEGIN SELECT RAISE(ABORT, 'Deletion rejected for rollback test'); END;
            """);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            AdminController().DeleteApplication(application.Id, application.Name, CancellationToken.None));

        Assert.AreEqual(1, await _db.SourceApplications.CountAsync());
        Assert.AreEqual(1, await _db.Incidents.CountAsync());
    }

    private ApplicationsController AdminController() => new(_db, new ApplicationAccessService(_db), localizer: TestLocalization.For<ApplicationsController>())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Role, "Administrators"), .. AppPermissions.GetAllPermissions().Select(permission => new Claim(AppPermissions.Type, permission.Key))], "test"))
            }
        }
    };

    private static Incident Message(Guid applicationId) => new()
    {
        ApplicationId = applicationId, ReportId = Guid.NewGuid(), Title = "Test message",
        OccurredAtUtc = DateTime.UtcNow
    };
}

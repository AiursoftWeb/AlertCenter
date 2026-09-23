using System.Security.Claims;
using Aiursoft.AlertCenter.Controllers;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallControllerTests
{
    private SqliteContext _db = null!;
    private OnCallController _controller = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.MigrateAsync();
        await AddUser("admin", "Administrator");
        _controller = Controller("admin", "Administrators");
    }

    [TestCleanup]
    public async Task Cleanup() => await _db.DisposeAsync();

    [TestMethod]
    public async Task AdministratorCanCreateGroupMembersTemplateAndPublishSchedule()
    {
        var created = await _controller.SaveGroup(new OnCallGroupRequest { Name = "Operations", TimeZoneId = "UTC" }, default);
        Assert.IsInstanceOfType<RedirectToActionResult>(created);
        var group = await _db.OnCallGroups.SingleAsync();
        await AddUser("one", "One");
        await AddUser("two", "Two");
        await AddUser("three", "Three");
        foreach (var userId in new[] { "one", "two", "three" })
            Assert.IsInstanceOfType<RedirectToActionResult>(await _controller.AddMember(new OnCallMemberRequest
                { GroupId = group.Id, UserId = userId }, default));

        foreach (var (name, start) in new[] { ("Night", 0), ("Morning", 480), ("Evening", 960) })
            Assert.IsInstanceOfType<RedirectToActionResult>(await _controller.SaveTemplate(new OnCallTemplateRequest
            {
                GroupId = group.Id, Name = name, StartMinuteOfDay = start, DurationMinutes = 480,
                UserIds = ["one", "two", "three"]
            }, default));

        var date = new DateOnly(2026, 9, 23);
        var published = await _controller.Publish(new OnCallPublishRequest { GroupId = group.Id, StartDate = date, EndDate = date }, default);
        Assert.IsInstanceOfType<RedirectToActionResult>(published);
        Assert.AreEqual(3, await _db.OnCallShifts.CountAsync());

        Assert.AreEqual(3, await _db.OnCallShifts.CountAsync());
    }

    [TestMethod]
    public async Task LeaveApprovalRequiresCompleteCoverageAndConfirmedHandoverCreatesDebt()
    {
        var group = await CreateGroupWithShiftAsync();
        var leave = await _controller.RequestLeave(new OnCallLeaveRequestModel
        {
            GroupId = group.Id, UserId = "one", StartsAtLocal = new DateTime(2026, 9, 22, 0, 0, 0),
            EndsAtLocal = new DateTime(2026, 9, 22, 4, 0, 0), Reason = "Appointment"
        }, default);
        Assert.IsInstanceOfType<RedirectToActionResult>(leave);
        var leaveRequest = await _db.OnCallLeaveRequests.SingleAsync();
        var rejectedApproval = await _controller.ReviewLeave(new OnCallLeaveReviewRequest { Id = leaveRequest.Id, Approve = true }, default);
        Assert.IsInstanceOfType<RedirectToActionResult>(rejectedApproval);

        var shift = await _db.OnCallShifts.SingleAsync();
        var handover = await _controller.RequestHandover(new OnCallHandoverRequestModel
        {
            ShiftId = shift.Id, ReceivingUserId = "two", StartsAtLocal = shift.StartsAtUtc,
            EndsAtLocal = shift.EndsAtUtc, Note = "Cover the shift"
        }, default);
        Assert.IsInstanceOfType<RedirectToActionResult>(handover);
        var request = await _db.OnCallHandoverRequests.SingleAsync();

        _controller = Controller("two");
        var confirmed = await _controller.ReviewHandover(new OnCallHandoverReviewRequest { Id = request.Id, Confirm = true }, default);
        Assert.IsInstanceOfType<RedirectToActionResult>(confirmed);
        Assert.AreEqual(OnCallHandoverRequest.Confirmed, (await _db.OnCallHandoverRequests.SingleAsync()).Status);
        Assert.AreEqual(1, await _db.OnCallShiftOverrides.CountAsync());
        var debt = await _db.OnCallCoverageDebts.SingleAsync();
        Assert.AreEqual("one", debt.OwingUserId);
        Assert.AreEqual("two", debt.CoveringUserId);
        Assert.AreEqual(480, debt.DurationMinutes);

        _controller = Controller("admin", "Administrators");
        var approved = await _controller.ReviewLeave(new OnCallLeaveReviewRequest { Id = leaveRequest.Id, Approve = true }, default);
        Assert.IsInstanceOfType<RedirectToActionResult>(approved);
        Assert.AreEqual(OnCallLeaveRequest.Approved, (await _db.OnCallLeaveRequests.SingleAsync()).Status);
    }

    private async Task<OnCallGroup> CreateGroupWithShiftAsync()
    {
        await AddUser("one", "One");
        await AddUser("two", "Two");
        var group = new OnCallGroup { Name = "Operations", TimeZoneId = "UTC" };
        _db.OnCallGroups.Add(group);
        _db.OnCallGroupMembers.AddRange(
            new OnCallGroupMember { Group = group, UserId = "one" },
            new OnCallGroupMember { Group = group, UserId = "two" });
        _db.OnCallShifts.Add(new OnCallShift
        {
            Group = group, UserId = "one", StartsAtUtc = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc),
            EndsAtUtc = new DateTime(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc)
        });
        await _db.SaveChangesAsync();
        return group;
    }

    private async Task AddUser(string id, string name)
    {
        if (await _db.Users.AnyAsync(item => item.Id == id)) return;
        _db.Users.Add(new User { Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant(), DisplayName = name });
        await _db.SaveChangesAsync();
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private OnCallController Controller(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new OnCallController(_db, new OnCallScheduleService(_db), new ApplicationAccessService(_db))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new TestTempDataProvider()),
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            }
        };
    }
}

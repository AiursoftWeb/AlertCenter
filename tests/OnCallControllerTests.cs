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
    public async Task DefaultTemplatesRequireThreeMembersAndPublishWithoutOverwritingCustomTemplates()
    {
        var group = new OnCallGroup { Name = "Operations", TimeZoneId = "UTC" };
        _db.OnCallGroups.Add(group);
        await _db.SaveChangesAsync();

        var insufficient = await _controller.ApplyDefaultTemplates(group.Id, default);
        Assert.IsInstanceOfType<RedirectToActionResult>(insufficient);
        Assert.Contains("至少需要 3 名", _controller.TempData["OnCallError"] as string ?? string.Empty);
        Assert.AreEqual(0, await _db.OnCallShiftTemplates.CountAsync());

        foreach (var userId in new[] { "one", "two", "three" })
        {
            await AddUser(userId, userId);
            _db.OnCallGroupMembers.Add(new OnCallGroupMember { GroupId = group.Id, UserId = userId });
        }
        await _db.SaveChangesAsync();
        var applied = await _controller.ApplyDefaultTemplates(group.Id, default);
        Assert.IsInstanceOfType<RedirectToActionResult>(applied);
        var templates = await _db.OnCallShiftTemplates.OrderBy(item => item.StartMinuteOfDay).ToListAsync();
        CollectionAssert.AreEqual(new[] { "早班", "中班", "晚班" }, templates.Select(item => item.Name).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 480, 960 }, templates.Select(item => item.StartMinuteOfDay).ToArray());
        foreach (var template in templates)
        {
            Assert.AreEqual(480, template.DurationMinutes);
            var users = await _db.OnCallShiftTemplateMembers.Where(item => item.TemplateId == template.Id)
                .OrderBy(item => item.RotationOrder).Select(item => item.UserId).ToListAsync();
            CollectionAssert.AreEqual(new[] { new[] { "one" }, new[] { "three" }, new[] { "two" } }[templates.IndexOf(template)], users);
        }
        Assert.AreEqual(0, await _db.OnCallShifts.CountAsync());

        var date = new DateOnly(2026, 9, 23);
        await _controller.Publish(new OnCallPublishRequest { GroupId = group.Id, StartDate = date, EndDate = date.AddDays(1) }, default);
        Assert.AreEqual(6, await _db.OnCallShifts.CountAsync());

        await _controller.ApplyDefaultTemplates(group.Id, default);
        Assert.Contains("已有轮转模板", _controller.TempData["OnCallError"] as string ?? string.Empty);
        Assert.AreEqual(3, await _db.OnCallShiftTemplates.CountAsync());
    }

    [TestMethod]
    public async Task DefaultTemplatesRotateExtraMembersWithinEachShift()
    {
        var group = new OnCallGroup { Name = "Operations", TimeZoneId = "UTC" };
        _db.OnCallGroups.Add(group);
        for (var index = 0; index < 6; index++)
        {
            var userId = $"user{index}";
            await AddUser(userId, userId);
            _db.OnCallGroupMembers.Add(new OnCallGroupMember { Group = group, UserId = userId });
        }
        await _db.SaveChangesAsync();
        await _controller.ApplyDefaultTemplates(group.Id, default);

        var date = new DateOnly(2026, 9, 23);
        var shifts = await new OnCallScheduleService(_db).GenerateDailyRotationAsync(group.Id, date, date.AddDays(1));
        Assert.AreEqual(6, shifts.Count);
        foreach (var startMinute in new[] { 0, 480, 960 })
        {
            var templateId = await _db.OnCallShiftTemplates.Where(item => item.GroupId == group.Id && item.StartMinuteOfDay == startMinute)
                .Select(item => item.Id).SingleAsync();
            Assert.AreEqual(2, shifts.Where(item => item.TemplateId == templateId).Select(item => item.UserId).Distinct().Count());
        }
        var result = await new OnCallScheduleService(_db).PublishAsync(group.Id, shifts,
            new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc));
        Assert.IsTrue(result.IsValid, string.Join(" ", result.Errors));
    }

    [TestMethod]
    public async Task DefaultTemplatesCannotBeAppliedByRegularUsers()
    {
        var group = new OnCallGroup { Name = "Operations", TimeZoneId = "UTC" };
        _db.OnCallGroups.Add(group);
        await _db.SaveChangesAsync();
        _controller = Controller("other");
        Assert.IsInstanceOfType<ForbidResult>(await _controller.ApplyDefaultTemplates(group.Id, default));
        Assert.AreEqual(0, await _db.OnCallShiftTemplates.CountAsync());
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

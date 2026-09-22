using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class OnCallController(TemplateDbContext db, IOnCallScheduleService scheduleService,
    IApplicationAccessService applicationAccess) : Controller
{
    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "告警中心", CascadedLinksIcon = "bell", CascadedLinksOrder = 1,
        LinkText = "值班", LinkOrder = 2)]
    public async Task<IActionResult> Index(Guid? groupId, DateOnly? date, CancellationToken cancellationToken)
    {
        var groups = await db.OnCallGroups.AsNoTracking().OrderBy(item => item.Name).ToListAsync(cancellationToken);
        var selectedGroup = groupId.HasValue
            ? groups.SingleOrDefault(item => item.Id == groupId.Value)
            : groups.FirstOrDefault();
        if (groupId.HasValue && selectedGroup == null) return NotFound();
        if (selectedGroup == null) return this.StackView(new OnCallViewModel { Groups = groups });
        var timeZone = GetTimeZone(selectedGroup.TimeZoneId);
        var selectedDate = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone));
        var (startUtc, endUtc) = GetUtcDateRange(selectedDate, selectedDate.AddDays(1), timeZone);
        return this.StackView(await BuildModel(selectedGroup, groups, startUtc, endUtc, cancellationToken));
    }

    public async Task<IActionResult> Manage(Guid? id, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        var groups = await db.OnCallGroups.AsNoTracking().OrderBy(item => item.Name).ToListAsync(cancellationToken);
        if (!id.HasValue || id == Guid.Empty)
            return this.StackView(new OnCallViewModel { Groups = groups });
        var group = groups.SingleOrDefault(item => item.Id == id.Value);
        if (group == null) return NotFound();
        var timeZone = GetTimeZone(group.TimeZoneId);
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone));
        var (startUtc, endUtc) = GetUtcDateRange(localToday, localToday.AddDays(14), timeZone);
        return this.StackView(await BuildModel(group, groups, startUtc, endUtc, cancellationToken));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveGroup(OnCallGroupRequest request, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        if (!TryGetTimeZone(request.TimeZoneId.Trim(), out _))
            ModelState.AddModelError(nameof(request.TimeZoneId), "请输入系统可识别的 IANA 或 Windows 时区 ID。");
        var duplicate = await db.OnCallGroups.AnyAsync(item => item.Name == request.Name.Trim() && item.Id != request.Id, cancellationToken);
        if (duplicate) ModelState.AddModelError(nameof(request.Name), "值班组名称已存在。");
        if (!ModelState.IsValid) return BadRequest("值班组配置无效。");
        var group = request.Id.HasValue
            ? await db.OnCallGroups.FindAsync([request.Id.Value], cancellationToken)
            : new OnCallGroup();
        if (group == null) return NotFound();
        group.Name = request.Name.Trim();
        group.TimeZoneId = request.TimeZoneId.Trim();
        if (!request.Id.HasValue) db.OnCallGroups.Add(group);
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Manage), new { id = group.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMember(OnCallMemberRequest request, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        if (!await db.OnCallGroups.AnyAsync(item => item.Id == request.GroupId, cancellationToken)) return NotFound();
        if (!await db.Users.AnyAsync(item => item.Id == request.UserId, cancellationToken)) return BadRequest("所选用户不存在。");
        if (!await db.OnCallGroupMembers.AnyAsync(item => item.GroupId == request.GroupId && item.UserId == request.UserId, cancellationToken))
        {
            db.OnCallGroupMembers.Add(new OnCallGroupMember { GroupId = request.GroupId, UserId = request.UserId });
            await db.SaveChangesAsync(cancellationToken);
        }
        return RedirectToAction(nameof(Manage), new { id = request.GroupId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMember(Guid groupId, string userId, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        var member = await db.OnCallGroupMembers.SingleOrDefaultAsync(item => item.GroupId == groupId && item.UserId == userId, cancellationToken);
        if (member == null) return NotFound();
        var usedByTemplate = await db.OnCallShiftTemplateMembers.AnyAsync(item => item.UserId == userId &&
            db.OnCallShiftTemplates.Any(template => template.Id == item.TemplateId && template.GroupId == groupId), cancellationToken);
        if (usedByTemplate) return BadRequest("该成员仍在轮转模板中，请先移除模板成员。");
        db.OnCallGroupMembers.Remove(member);
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Manage), new { id = groupId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTemplate(OnCallTemplateRequest request, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        if (!await db.OnCallGroups.AnyAsync(item => item.Id == request.GroupId, cancellationToken)) return NotFound();
        request.UserIds = request.UserIds.Distinct().ToArray();
        if (request.UserIds.Length == 0) ModelState.AddModelError(nameof(request.UserIds), "请至少选择一个轮转成员。");
        var members = await db.OnCallGroupMembers.Where(item => item.GroupId == request.GroupId)
            .Select(item => item.UserId).ToListAsync(cancellationToken);
        if (request.UserIds.Any(item => !members.Contains(item)))
            ModelState.AddModelError(nameof(request.UserIds), "轮转成员必须属于该值班组。");
        var duplicateName = await db.OnCallShiftTemplates.AnyAsync(item => item.GroupId == request.GroupId &&
            item.Name == request.Name.Trim() && item.Id != request.Id, cancellationToken);
        if (duplicateName) ModelState.AddModelError(nameof(request.Name), "班次模板名称已存在。");
        if (!ModelState.IsValid) return BadRequest("班次模板配置无效。");
        var template = request.Id.HasValue
            ? await db.OnCallShiftTemplates.FindAsync([request.Id.Value], cancellationToken)
            : new OnCallShiftTemplate { GroupId = request.GroupId };
        if (template == null || template.GroupId != request.GroupId) return NotFound();
        template.Name = request.Name.Trim();
        template.StartMinuteOfDay = request.StartMinuteOfDay;
        template.DurationMinutes = request.DurationMinutes;
        if (!request.Id.HasValue) db.OnCallShiftTemplates.Add(template);
        var existing = await db.OnCallShiftTemplateMembers.Where(item => item.TemplateId == template.Id).ToListAsync(cancellationToken);
        db.OnCallShiftTemplateMembers.RemoveRange(existing);
        db.OnCallShiftTemplateMembers.AddRange(request.UserIds.Select((userId, index) => new OnCallShiftTemplateMember
        {
            Template = template, UserId = userId, RotationOrder = index
        }));
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Manage), new { id = request.GroupId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTemplate(Guid id, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        var template = await db.OnCallShiftTemplates.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (template == null) return NotFound();
        var hasPublishedShifts = await db.OnCallShifts.AnyAsync(item => item.TemplateId == id, cancellationToken);
        if (hasPublishedShifts) return BadRequest("已有已发布班次引用该模板，不能删除。");
        db.OnCallShiftTemplates.Remove(template);
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Manage), new { id = template.GroupId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(OnCallPublishRequest request, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        if (!ModelState.IsValid) return BadRequest("发布范围无效。");
        var group = await db.OnCallGroups.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.GroupId, cancellationToken);
        if (group == null) return NotFound();
        var timeZone = GetTimeZone(group.TimeZoneId);
        IReadOnlyList<OnCallShift> shifts;
        try
        {
            shifts = await scheduleService.GenerateDailyRotationAsync(group.Id, request.StartDate, request.EndDate, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(exception.Message);
        }
        var (coverageStartUtc, coverageEndUtc) = GetUtcDateRange(request.StartDate, request.EndDate.AddDays(1), timeZone);
        var result = await scheduleService.PublishAsync(group.Id, shifts, coverageStartUtc, coverageEndUtc, cancellationToken);
        if (!result.IsValid) return BadRequest(string.Join(" ", result.Errors));
        return RedirectToAction(nameof(Manage), new { id = group.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestLeave(OnCallLeaveRequestModel request, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Forbid();
        if (!applicationAccess.IsAdministrator(User) && userId != request.UserId) return Forbid();
        var group = await db.OnCallGroups.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.GroupId, cancellationToken);
        if (group == null) return NotFound();
        if (!await db.OnCallGroupMembers.AnyAsync(item => item.GroupId == group.Id && item.UserId == request.UserId, cancellationToken))
            return BadRequest("请假用户不是该值班组成员。");
        if (!ModelState.IsValid) return BadRequest("请假申请无效。");
        if (!TryConvertToUtc(request.StartsAtLocal, group.TimeZoneId, out var startsAtUtc) ||
            !TryConvertToUtc(request.EndsAtLocal, group.TimeZoneId, out var endsAtUtc))
            return BadRequest("本地时间在该时区中无效或存在歧义，请选择其他时间。");
        db.OnCallLeaveRequests.Add(new OnCallLeaveRequest
        {
            GroupId = group.Id, UserId = request.UserId, StartsAtUtc = startsAtUtc, EndsAtUtc = endsAtUtc,
            Reason = request.Reason.Trim()
        });
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Manage), new { id = group.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewLeave(OnCallLeaveReviewRequest request, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        var leave = await db.OnCallLeaveRequests.SingleOrDefaultAsync(item => item.Id == request.Id, cancellationToken);
        if (leave == null) return NotFound();
        if (leave.Status != OnCallLeaveRequest.Pending) return BadRequest("该请假申请已处理。");
        if (request.Approve && !await IsLeaveFullyCoveredAsync(leave, cancellationToken))
            return BadRequest("请假时段仍包含未覆盖的本人班次。请先安排完整代班后再批准请假。");
        leave.Status = request.Approve ? OnCallLeaveRequest.Approved : OnCallLeaveRequest.Rejected;
        leave.ReviewedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        leave.ReviewedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Manage), new { id = leave.GroupId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestHandover(OnCallHandoverRequestModel request, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Forbid();
        var shift = await db.OnCallShifts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.ShiftId, cancellationToken);
        if (shift == null) return NotFound();
        if (!applicationAccess.IsAdministrator(User) && shift.UserId != userId) return Forbid();
        if (!await db.OnCallGroupMembers.AnyAsync(item => item.GroupId == shift.GroupId && item.UserId == request.ReceivingUserId, cancellationToken))
            return BadRequest("接班工程师必须是值班组成员。");
        if (!ModelState.IsValid) return BadRequest("交接申请无效。");
        var group = await db.OnCallGroups.AsNoTracking().SingleAsync(item => item.Id == shift.GroupId, cancellationToken);
        if (!TryConvertToUtc(request.StartsAtLocal, group.TimeZoneId, out var startsAtUtc) ||
            !TryConvertToUtc(request.EndsAtLocal, group.TimeZoneId, out var endsAtUtc))
            return BadRequest("本地时间在该时区中无效或存在歧义，请选择其他时间。");
        if (startsAtUtc < shift.StartsAtUtc || endsAtUtc > shift.EndsAtUtc) return BadRequest("交接时间必须位于原班次内。");
        db.OnCallHandoverRequests.Add(new OnCallHandoverRequest
        {
            ShiftId = shift.Id, RequestedByUserId = shift.UserId, ReceivingUserId = request.ReceivingUserId,
            StartsAtUtc = startsAtUtc, EndsAtUtc = endsAtUtc, Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()
        });
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Manage), new { id = shift.GroupId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewHandover(OnCallHandoverReviewRequest request, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Forbid();
        var handover = await db.OnCallHandoverRequests.Include(item => item.Shift).SingleOrDefaultAsync(item => item.Id == request.Id, cancellationToken);
        if (handover == null) return NotFound();
        if (!applicationAccess.IsAdministrator(User) && handover.ReceivingUserId != userId) return Forbid();
        if (handover.Status != OnCallHandoverRequest.Pending) return BadRequest("该交接申请已处理。");
        if (!request.Confirm)
        {
            handover.Status = OnCallHandoverRequest.Declined;
            handover.RespondedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return RedirectToAction(nameof(Manage), new { id = handover.Shift.GroupId });
        }
        var overrideItem = new OnCallShiftOverride
        {
            GroupId = handover.Shift.GroupId, OriginalShiftId = handover.ShiftId, CoveringUserId = handover.ReceivingUserId,
            StartsAtUtc = handover.StartsAtUtc, EndsAtUtc = handover.EndsAtUtc,
            Reason = $"交接确认：{handover.Note ?? "未填写说明"}", CreatedByUserId = userId
        };
        var result = await scheduleService.AddOverrideAsync(overrideItem, cancellationToken);
        if (!result.IsValid) return BadRequest(string.Join(" ", result.Errors));
        handover.Status = OnCallHandoverRequest.Confirmed;
        handover.RespondedAtUtc = DateTime.UtcNow;
        handover.OverrideId = overrideItem.Id;
        db.OnCallCoverageDebts.Add(new OnCallCoverageDebt
        {
            GroupId = handover.Shift.GroupId, OriginOverrideId = overrideItem.Id, OwingUserId = handover.Shift.UserId,
            CoveringUserId = handover.ReceivingUserId,
            DurationMinutes = (int)(overrideItem.EndsAtUtc - overrideItem.StartsAtUtc).TotalMinutes
        });
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Manage), new { id = handover.Shift.GroupId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateOverride(OnCallOverrideRequest request, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        var shift = await db.OnCallShifts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.OriginalShiftId, cancellationToken);
        if (shift == null) return NotFound();
        if (!ModelState.IsValid) return BadRequest("临时覆盖无效。");
        var group = await db.OnCallGroups.AsNoTracking().SingleAsync(item => item.Id == shift.GroupId, cancellationToken);
        if (!TryConvertToUtc(request.StartsAtLocal, group.TimeZoneId, out var startsAtUtc) ||
            !TryConvertToUtc(request.EndsAtLocal, group.TimeZoneId, out var endsAtUtc))
            return BadRequest("本地时间在该时区中无效或存在歧义，请选择其他时间。");
        var overrideItem = new OnCallShiftOverride
        {
            GroupId = group.Id, OriginalShiftId = shift.Id, CoveringUserId = request.CoveringUserId,
            StartsAtUtc = startsAtUtc, EndsAtUtc = endsAtUtc, Reason = request.Reason.Trim(),
            CreatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)
        };
        var result = await scheduleService.AddOverrideAsync(overrideItem, cancellationToken);
        if (!result.IsValid) return BadRequest(string.Join(" ", result.Errors));
        db.OnCallCoverageDebts.Add(new OnCallCoverageDebt
        {
            GroupId = group.Id, OriginOverrideId = overrideItem.Id, OwingUserId = shift.UserId,
            CoveringUserId = overrideItem.CoveringUserId,
            DurationMinutes = (int)(overrideItem.EndsAtUtc - overrideItem.StartsAtUtc).TotalMinutes
        });
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Manage), new { id = group.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SettleDebt(OnCallDebtSettlementRequest request, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        var debt = await db.OnCallCoverageDebts.SingleOrDefaultAsync(item => item.Id == request.DebtId, cancellationToken);
        if (debt == null) return NotFound();
        if (debt.SettledAtUtc.HasValue) return BadRequest("该补班债务已偿还。");
        var settlement = await db.OnCallShiftOverrides.SingleOrDefaultAsync(item => item.Id == request.SettlementOverrideId, cancellationToken);
        if (settlement == null || settlement.GroupId != debt.GroupId || settlement.CoveringUserId != debt.OwingUserId)
            return BadRequest("补班覆盖必须由欠班工程师在同一值班组承担。");
        var duration = (int)(settlement.EndsAtUtc - settlement.StartsAtUtc).TotalMinutes;
        if (duration < debt.DurationMinutes) return BadRequest("补班时长不足以偿还该债务。");
        debt.SettlementOverrideId = settlement.Id;
        debt.SettledAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Manage), new { id = debt.GroupId });
    }

    private async Task<bool> IsLeaveFullyCoveredAsync(OnCallLeaveRequest leave, CancellationToken cancellationToken)
    {
        var shifts = await db.OnCallShifts.AsNoTracking().Where(item => item.GroupId == leave.GroupId && item.UserId == leave.UserId &&
            item.StartsAtUtc < leave.EndsAtUtc && item.EndsAtUtc > leave.StartsAtUtc).ToListAsync(cancellationToken);
        foreach (var shift in shifts)
        {
            var coverageStart = shift.StartsAtUtc > leave.StartsAtUtc ? shift.StartsAtUtc : leave.StartsAtUtc;
            var coverageEnd = shift.EndsAtUtc < leave.EndsAtUtc ? shift.EndsAtUtc : leave.EndsAtUtc;
            var overrides = await db.OnCallShiftOverrides.AsNoTracking().Where(item => item.OriginalShiftId == shift.Id &&
                item.StartsAtUtc < coverageEnd && item.EndsAtUtc > coverageStart).OrderBy(item => item.StartsAtUtc).ToListAsync(cancellationToken);
            var cursor = coverageStart;
            foreach (var item in overrides)
            {
                if (item.StartsAtUtc > cursor) return false;
                if (item.EndsAtUtc > cursor) cursor = item.EndsAtUtc;
                if (cursor >= coverageEnd) break;
            }
            if (cursor < coverageEnd) return false;
        }
        return true;
    }

    private async Task<OnCallViewModel> BuildModel(OnCallGroup group, List<OnCallGroup> groups, DateTime startUtc,
        DateTime endUtc, CancellationToken cancellationToken)
    {
        var templateIds = db.OnCallShiftTemplates.Where(item => item.GroupId == group.Id).Select(item => item.Id);
        var shiftQuery = db.OnCallShifts.AsNoTracking().Where(item => item.GroupId == group.Id &&
            item.StartsAtUtc < endUtc && item.EndsAtUtc > startUtc);
        var shifts = await shiftQuery.Include(item => item.User).OrderBy(item => item.StartsAtUtc).ToListAsync(cancellationToken);
        var shiftIds = shifts.Select(item => item.Id).ToArray();
        return new OnCallViewModel
        {
            Groups = groups,
            Group = group,
            Members = await db.OnCallGroupMembers.AsNoTracking().Include(item => item.User).Where(item => item.GroupId == group.Id)
                .OrderBy(item => item.User.DisplayName).ToListAsync(cancellationToken),
            Templates = await db.OnCallShiftTemplates.AsNoTracking().Where(item => item.GroupId == group.Id)
                .OrderBy(item => item.StartMinuteOfDay).ToListAsync(cancellationToken),
            TemplateMembers = await db.OnCallShiftTemplateMembers.AsNoTracking().Include(item => item.User)
                .Where(item => templateIds.Contains(item.TemplateId)).OrderBy(item => item.RotationOrder).ToListAsync(cancellationToken),
            Shifts = shifts,
            Overrides = await db.OnCallShiftOverrides.AsNoTracking().Include(item => item.CoveringUser)
                .Where(item => item.GroupId == group.Id && item.StartsAtUtc < endUtc && item.EndsAtUtc > startUtc)
                .OrderBy(item => item.StartsAtUtc).ToListAsync(cancellationToken),
            LeaveRequests = await db.OnCallLeaveRequests.AsNoTracking().Include(item => item.User).Include(item => item.ReviewedByUser)
                .Where(item => item.GroupId == group.Id).OrderByDescending(item => item.SubmittedAtUtc).Take(20).ToListAsync(cancellationToken),
            HandoverRequests = await db.OnCallHandoverRequests.AsNoTracking().Include(item => item.Shift)
                .Include(item => item.RequestedByUser).Include(item => item.ReceivingUser).Where(item => shiftIds.Contains(item.ShiftId))
                .OrderByDescending(item => item.RequestedAtUtc).Take(20).ToListAsync(cancellationToken),
            CoverageDebts = await db.OnCallCoverageDebts.AsNoTracking().Include(item => item.OwingUser).Include(item => item.CoveringUser)
                .Include(item => item.SettlementOverride).Where(item => item.GroupId == group.Id)
                .OrderBy(item => item.SettledAtUtc.HasValue).ThenByDescending(item => item.CreatedAtUtc).Take(20).ToListAsync(cancellationToken),
            Users = await db.Users.AsNoTracking().OrderBy(item => item.DisplayName)
                .Select(item => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem($"{item.DisplayName} ({item.Email ?? item.UserName})", item.Id))
                .ToListAsync(cancellationToken),
            CurrentEngineer = await scheduleService.GetOnCallEngineerAsync(group.Id, DateTime.UtcNow, cancellationToken),
            DisplayStartUtc = startUtc,
            DisplayEndUtc = endUtc
        };
    }

    private static (DateTime StartUtc, DateTime EndUtc) GetUtcDateRange(DateOnly start, DateOnly endExclusive, TimeZoneInfo timeZone)
    {
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(start.ToDateTime(TimeOnly.MinValue), timeZone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(endExclusive.ToDateTime(TimeOnly.MinValue), timeZone);
        return (startUtc, endUtc);
    }

    private static TimeZoneInfo GetTimeZone(string timeZoneId) => TryGetTimeZone(timeZoneId, out var timeZone)
        ? timeZone : throw new InvalidOperationException($"值班组配置的时区“{timeZoneId}”无效。");

    private static bool TryGetTimeZone(string timeZoneId, out TimeZoneInfo timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            timeZone = null!;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            timeZone = null!;
            return false;
        }
    }

    private static bool TryConvertToUtc(DateTime local, string timeZoneId, out DateTime utc)
    {
        var timeZone = GetTimeZone(timeZoneId);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local) || timeZone.IsAmbiguousTime(local))
        {
            utc = default;
            return false;
        }
        utc = TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
        return true;
    }
}

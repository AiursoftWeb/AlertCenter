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
        CascadedLinksGroupName = "值班中心", CascadedLinksIcon = "calendar-clock", CascadedLinksOrder = 3,
        LinkText = "值班概览", LinkOrder = 0)]
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
        var model = await BuildModel(selectedGroup, groups, startUtc, endUtc, cancellationToken);
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId != null)
            model.IncomingHandovers = await db.OnCallHandoverRequests.AsNoTracking().Include(item => item.Shift)
                .Include(item => item.RequestedByUser).Where(item => item.Shift.GroupId == selectedGroup.Id &&
                    item.ReceivingUserId == userId && item.Status == OnCallHandoverRequest.Pending)
                .OrderBy(item => item.StartsAtUtc).ToListAsync(cancellationToken);
        model.OperationError = TempData["OnCallError"] as string;
        return this.StackView(model);
    }

    [Authorize(Policy = "CanManageOnCall")]
    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "值班中心", CascadedLinksIcon = "calendar-clock", CascadedLinksOrder = 3,
        LinkText = "值班组配置", LinkOrder = 6)]
    public async Task<IActionResult> Manage(Guid? id, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        var groups = await db.OnCallGroups.AsNoTracking().OrderBy(item => item.Name).ToListAsync(cancellationToken);
        if (id == Guid.Empty || groups.Count == 0)
            return this.StackView(new OnCallViewModel { Groups = groups, OperationError = TempData["OnCallError"] as string });
        var group = id.HasValue ? groups.SingleOrDefault(item => item.Id == id.Value) : groups[0];
        if (group == null) return NotFound();
        var model = await BuildManageModel(group, groups, cancellationToken);
        model.OperationError = TempData["OnCallError"] as string;
        return this.StackView(model);
    }

    [Authorize(Policy = "CanManageOnCall")]
    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "值班中心", CascadedLinksIcon = "calendar-clock", CascadedLinksOrder = 3,
        LinkText = "排班与代班", LinkOrder = 1)]
    public Task<IActionResult> Schedule(Guid? id, DateOnly? date, CancellationToken cancellationToken) =>
        ManagePage(id, nameof(Schedule), cancellationToken, date);

    [Authorize(Policy = "CanManageOnCall")]
    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "值班中心", CascadedLinksIcon = "calendar-clock", CascadedLinksOrder = 3,
        LinkText = "轮转模板", LinkOrder = 2)]
    public Task<IActionResult> Templates(Guid? id, CancellationToken cancellationToken) => ManagePage(id, nameof(Templates), cancellationToken);

    [Authorize(Policy = "CanManageOnCall")]
    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "值班中心", CascadedLinksIcon = "calendar-clock", CascadedLinksOrder = 3,
        LinkText = "成员管理", LinkOrder = 3)]
    public Task<IActionResult> Members(Guid? id, CancellationToken cancellationToken) => ManagePage(id, nameof(Members), cancellationToken);

    [Authorize(Policy = "CanManageOnCall")]
    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "值班中心", CascadedLinksIcon = "calendar-clock", CascadedLinksOrder = 3,
        LinkText = "请假与交接", LinkOrder = 4)]
    public Task<IActionResult> Requests(Guid? id, CancellationToken cancellationToken) => ManagePage(id, nameof(Requests), cancellationToken);

    [Authorize(Policy = "CanManageOnCall")]
    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "值班中心", CascadedLinksIcon = "calendar-clock", CascadedLinksOrder = 3,
        LinkText = "补班债务", LinkOrder = 5)]
    public Task<IActionResult> Debts(Guid? id, CancellationToken cancellationToken) => ManagePage(id, nameof(Debts), cancellationToken);

    private async Task<IActionResult> ManagePage(Guid? id, string view, CancellationToken cancellationToken, DateOnly? date = null)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        var groups = await db.OnCallGroups.AsNoTracking().OrderBy(item => item.Name).ToListAsync(cancellationToken);
        var group = id.HasValue && id != Guid.Empty
            ? groups.SingleOrDefault(item => item.Id == id.Value)
            : groups.FirstOrDefault();
        if (id.HasValue && id != Guid.Empty && group == null) return NotFound();
        if (group == null) return RedirectToAction(nameof(Manage));
        var model = await BuildManageModel(group, groups, cancellationToken);
        if (view == nameof(Schedule))
        {
            if (date.HasValue)
            {
                if (date.Value > DateOnly.MaxValue.AddDays(-14)) return BadRequest("请选择更早的日期。");
                var (startUtc, endUtc) = GetUtcDateRange(date.Value, date.Value.AddDays(14), GetTimeZone(group.TimeZoneId));
                model.Shifts = await db.OnCallShifts.AsNoTracking().Include(item => item.User)
                    .Where(item => item.GroupId == group.Id && item.StartsAtUtc < endUtc && item.EndsAtUtc > startUtc)
                    .OrderBy(item => item.StartsAtUtc).ToListAsync(cancellationToken);
                model.Overrides = await db.OnCallShiftOverrides.AsNoTracking().Include(item => item.CoveringUser)
                    .Where(item => item.GroupId == group.Id && item.StartsAtUtc < endUtc && item.EndsAtUtc > startUtc)
                    .OrderBy(item => item.StartsAtUtc).ToListAsync(cancellationToken);
                model.DisplayStartUtc = startUtc;
                model.DisplayEndUtc = endUtc;
            }
            var lastShiftEnd = await db.OnCallShifts.Where(item => item.GroupId == group.Id)
                .MaxAsync(item => (DateTime?)item.EndsAtUtc, cancellationToken);
            var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, GetTimeZone(group.TimeZoneId)));
            var lastLocalEnd = lastShiftEnd.HasValue
                ? TimeZoneInfo.ConvertTimeFromUtc(lastShiftEnd.Value, GetTimeZone(group.TimeZoneId))
                : DateTime.MinValue;
            var nextDate = lastShiftEnd.HasValue
                ? DateOnly.FromDateTime(lastLocalEnd).AddDays(lastLocalEnd.TimeOfDay == TimeSpan.Zero ? 0 : 1)
                : localToday;
            model.SuggestedPublishDate = nextDate > localToday ? nextDate : localToday;
        }
        if (view == nameof(Debts))
        {
            model.CoverageDebts = await db.OnCallCoverageDebts.AsNoTracking().Include(item => item.OwingUser)
                .Include(item => item.CoveringUser).Where(item => item.GroupId == group.Id && item.SettledAtUtc == null)
                .OrderBy(item => item.CreatedAtUtc).ToListAsync(cancellationToken);
            model.SettlementOverrides = await db.OnCallShiftOverrides.AsNoTracking()
                .Where(item => item.GroupId == group.Id).OrderByDescending(item => item.StartsAtUtc).ToListAsync(cancellationToken);
        }
        if (view == nameof(Requests))
        {
            model.LeaveRequests = await db.OnCallLeaveRequests.AsNoTracking().Include(item => item.User)
                .Where(item => item.GroupId == group.Id && item.Status == OnCallLeaveRequest.Pending)
                .OrderBy(item => item.StartsAtUtc).ToListAsync(cancellationToken);
            model.HandoverRequests = await db.OnCallHandoverRequests.AsNoTracking().Include(item => item.Shift)
                .Include(item => item.RequestedByUser).Include(item => item.ReceivingUser)
                .Where(item => item.Shift.GroupId == group.Id && item.Status == OnCallHandoverRequest.Pending)
                .OrderBy(item => item.StartsAtUtc).ToListAsync(cancellationToken);
        }
        model.OperationError = TempData["OnCallError"] as string;
        return this.StackView(model, view);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveGroup([Bind(Prefix = "GroupForm")] OnCallGroupRequest request, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        request.Name = request.Name?.Trim() ?? string.Empty;
        request.TimeZoneId = request.TimeZoneId?.Trim() ?? string.Empty;
        if (!TimeZoneInfo.GetSystemTimeZones().Any(zone => zone.Id == request.TimeZoneId))
            ModelState.AddModelError("GroupForm.TimeZoneId", "请选择列表中的有效时区。");
        var duplicate = await db.OnCallGroups.AnyAsync(item => item.Name == request.Name && item.Id != request.Id, cancellationToken);
        if (duplicate) ModelState.AddModelError("GroupForm.Name", "值班组名称已存在。");
        if (!ModelState.IsValid)
        {
            var groups = await db.OnCallGroups.AsNoTracking().OrderBy(item => item.Name).ToListAsync(cancellationToken);
            var existingGroup = request.Id.HasValue ? groups.SingleOrDefault(item => item.Id == request.Id.Value) : null;
            if (request.Id.HasValue && existingGroup == null) return NotFound();
            var model = existingGroup == null ? new OnCallViewModel { Groups = groups } : await BuildManageModel(existingGroup, groups, cancellationToken);
            model.GroupForm = request;
            model.EditingGroupForm = request.Id.HasValue;
            return this.StackView(model, "Manage");
        }
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
        if (!await db.Users.AnyAsync(item => item.Id == request.UserId, cancellationToken))
            return ManageError(request.GroupId, "所选用户不存在。");
        if (!await db.OnCallGroupMembers.AnyAsync(item => item.GroupId == request.GroupId && item.UserId == request.UserId, cancellationToken))
        {
            db.OnCallGroupMembers.Add(new OnCallGroupMember { GroupId = request.GroupId, UserId = request.UserId });
            await db.SaveChangesAsync(cancellationToken);
        }
        return RedirectToAction(nameof(Members), new { id = request.GroupId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMember(Guid groupId, string userId, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        var member = await db.OnCallGroupMembers.SingleOrDefaultAsync(item => item.GroupId == groupId && item.UserId == userId, cancellationToken);
        if (member == null) return NotFound();
        var usedByTemplate = await db.OnCallShiftTemplateMembers.AnyAsync(item => item.UserId == userId &&
            db.OnCallShiftTemplates.Any(template => template.Id == item.TemplateId && template.GroupId == groupId), cancellationToken);
        if (usedByTemplate) return ManageError(groupId, "该成员仍在轮转模板中，请先移除模板成员。", nameof(Members));
        db.OnCallGroupMembers.Remove(member);
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Members), new { id = groupId });
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
        if (!ModelState.IsValid) return ManageError(request.GroupId, ValidationErrors("班次模板配置无效。"), nameof(Templates));
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
        return RedirectToAction(nameof(Templates), new { id = request.GroupId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTemplate(Guid id, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        var template = await db.OnCallShiftTemplates.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (template == null) return NotFound();
        var hasPublishedShifts = await db.OnCallShifts.AnyAsync(item => item.TemplateId == id, cancellationToken);
        if (hasPublishedShifts) return ManageError(template.GroupId, "已有已发布班次引用该模板，不能删除。", nameof(Templates));
        db.OnCallShiftTemplates.Remove(template);
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Templates), new { id = template.GroupId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(OnCallPublishRequest request, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        if (!ModelState.IsValid) return ManageError(request.GroupId, ValidationErrors("发布范围无效。"), nameof(Schedule));
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
            return ManageError(group.Id, exception.Message, nameof(Schedule));
        }
        var (coverageStartUtc, coverageEndUtc) = GetUtcDateRange(request.StartDate, request.EndDate.AddDays(1), timeZone);
        var result = await scheduleService.PublishAsync(group.Id, shifts, coverageStartUtc, coverageEndUtc, cancellationToken);
        if (!result.IsValid) return ManageError(group.Id, string.Join(" ", result.Errors), nameof(Schedule));
        return RedirectToAction(nameof(Schedule), new { id = group.Id });
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
            return OnCallError(group.Id, "请假用户不是该值班组成员。");
        if (!ModelState.IsValid) return OnCallError(group.Id, ValidationErrors("请假申请无效。"));
        if (!TryConvertToUtc(request.StartsAtLocal, group.TimeZoneId, out var startsAtUtc) ||
            !TryConvertToUtc(request.EndsAtLocal, group.TimeZoneId, out var endsAtUtc))
            return OnCallError(group.Id, "本地时间在该时区中无效或存在歧义，请选择其他时间。");
        db.OnCallLeaveRequests.Add(new OnCallLeaveRequest
        {
            GroupId = group.Id, UserId = request.UserId, StartsAtUtc = startsAtUtc, EndsAtUtc = endsAtUtc,
            Reason = request.Reason.Trim()
        });
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(applicationAccess.IsAdministrator(User) ? nameof(Requests) : nameof(Index),
            applicationAccess.IsAdministrator(User) ? new { id = group.Id } : new { groupId = group.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewLeave(OnCallLeaveReviewRequest request, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        var leave = await db.OnCallLeaveRequests.SingleOrDefaultAsync(item => item.Id == request.Id, cancellationToken);
        if (leave == null) return NotFound();
        if (leave.Status != OnCallLeaveRequest.Pending) return ManageError(leave.GroupId, "该请假申请已处理。", nameof(Requests));
        if (request.Approve && !await IsLeaveFullyCoveredAsync(leave, cancellationToken))
            return ManageError(leave.GroupId, "请假时段仍包含未覆盖的本人班次。请先安排完整代班后再批准请假。", nameof(Requests));
        leave.Status = request.Approve ? OnCallLeaveRequest.Approved : OnCallLeaveRequest.Rejected;
        leave.ReviewedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        leave.ReviewedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Requests), new { id = leave.GroupId });
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
            return OnCallError(shift.GroupId, "接班工程师必须是值班组成员。");
        if (!ModelState.IsValid) return OnCallError(shift.GroupId, ValidationErrors("交接申请无效。"));
        var group = await db.OnCallGroups.AsNoTracking().SingleAsync(item => item.Id == shift.GroupId, cancellationToken);
        if (!TryConvertToUtc(request.StartsAtLocal, group.TimeZoneId, out var startsAtUtc) ||
            !TryConvertToUtc(request.EndsAtLocal, group.TimeZoneId, out var endsAtUtc))
            return OnCallError(group.Id, "本地时间在该时区中无效或存在歧义，请选择其他时间。");
        if (startsAtUtc < shift.StartsAtUtc || endsAtUtc > shift.EndsAtUtc) return OnCallError(group.Id, "交接时间必须位于原班次内。");
        db.OnCallHandoverRequests.Add(new OnCallHandoverRequest
        {
            ShiftId = shift.Id, RequestedByUserId = shift.UserId, ReceivingUserId = request.ReceivingUserId,
            StartsAtUtc = startsAtUtc, EndsAtUtc = endsAtUtc, Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()
        });
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index), new { groupId = shift.GroupId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewHandover(OnCallHandoverReviewRequest request, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Forbid();
        var handover = await db.OnCallHandoverRequests.Include(item => item.Shift).SingleOrDefaultAsync(item => item.Id == request.Id, cancellationToken);
        if (handover == null) return NotFound();
        if (!applicationAccess.IsAdministrator(User) && handover.ReceivingUserId != userId) return Forbid();
        if (handover.Status != OnCallHandoverRequest.Pending) return HandoverError(handover.Shift.GroupId, "该交接申请已处理。");
        if (!request.Confirm)
        {
            handover.Status = OnCallHandoverRequest.Declined;
            handover.RespondedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return HandoverReturn(handover.Shift.GroupId);
        }
        var overrideItem = new OnCallShiftOverride
        {
            GroupId = handover.Shift.GroupId, OriginalShiftId = handover.ShiftId, CoveringUserId = handover.ReceivingUserId,
            StartsAtUtc = handover.StartsAtUtc, EndsAtUtc = handover.EndsAtUtc,
            Reason = $"交接确认：{handover.Note ?? "未填写说明"}", CreatedByUserId = userId
        };
        var result = await scheduleService.AddOverrideAsync(overrideItem, cancellationToken);
        if (!result.IsValid) return HandoverError(handover.Shift.GroupId, string.Join(" ", result.Errors));
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
        return HandoverReturn(handover.Shift.GroupId);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateOverride(OnCallOverrideRequest request, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        var shift = await db.OnCallShifts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.OriginalShiftId, cancellationToken);
        if (shift == null) return NotFound();
        if (!ModelState.IsValid) return ManageError(shift.GroupId, ValidationErrors("临时覆盖无效。"), nameof(Schedule));
        var group = await db.OnCallGroups.AsNoTracking().SingleAsync(item => item.Id == shift.GroupId, cancellationToken);
        if (!TryConvertToUtc(request.StartsAtLocal, group.TimeZoneId, out var startsAtUtc) ||
            !TryConvertToUtc(request.EndsAtLocal, group.TimeZoneId, out var endsAtUtc))
            return ManageError(group.Id, "本地时间在该时区中无效或存在歧义，请选择其他时间。", nameof(Schedule));
        var overrideItem = new OnCallShiftOverride
        {
            GroupId = group.Id, OriginalShiftId = shift.Id, CoveringUserId = request.CoveringUserId,
            StartsAtUtc = startsAtUtc, EndsAtUtc = endsAtUtc, Reason = request.Reason.Trim(),
            CreatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)
        };
        var result = await scheduleService.AddOverrideAsync(overrideItem, cancellationToken);
        if (!result.IsValid) return ManageError(group.Id, string.Join(" ", result.Errors), nameof(Schedule));
        db.OnCallCoverageDebts.Add(new OnCallCoverageDebt
        {
            GroupId = group.Id, OriginOverrideId = overrideItem.Id, OwingUserId = shift.UserId,
            CoveringUserId = overrideItem.CoveringUserId,
            DurationMinutes = (int)(overrideItem.EndsAtUtc - overrideItem.StartsAtUtc).TotalMinutes
        });
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Schedule), new { id = group.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SettleDebt(OnCallDebtSettlementRequest request, CancellationToken cancellationToken)
    {
        if (!applicationAccess.IsAdministrator(User)) return Forbid();
        var debt = await db.OnCallCoverageDebts.SingleOrDefaultAsync(item => item.Id == request.DebtId, cancellationToken);
        if (debt == null) return NotFound();
        if (debt.SettledAtUtc.HasValue) return ManageError(debt.GroupId, "该补班债务已偿还。", nameof(Debts));
        var settlement = await db.OnCallShiftOverrides.SingleOrDefaultAsync(item => item.Id == request.SettlementOverrideId, cancellationToken);
        if (settlement == null || settlement.GroupId != debt.GroupId || settlement.CoveringUserId != debt.OwingUserId)
            return ManageError(debt.GroupId, "补班覆盖必须由欠班工程师在同一值班组承担。", nameof(Debts));
        var duration = (int)(settlement.EndsAtUtc - settlement.StartsAtUtc).TotalMinutes;
        if (duration < debt.DurationMinutes) return ManageError(debt.GroupId, "补班时长不足以偿还该债务。", nameof(Debts));
        debt.SettlementOverrideId = settlement.Id;
        debt.SettledAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Debts), new { id = debt.GroupId });
    }

    private IActionResult HandoverReturn(Guid groupId) => applicationAccess.IsAdministrator(User)
        ? RedirectToAction(nameof(Requests), new { id = groupId })
        : RedirectToAction(nameof(Index), new { groupId });

    private IActionResult HandoverError(Guid groupId, string error)
    {
        TempData["OnCallError"] = error;
        return HandoverReturn(groupId);
    }

    private IActionResult ManageError(Guid groupId, string error, string action = nameof(Manage))
    {
        TempData["OnCallError"] = error;
        return RedirectToAction(action, new { id = groupId });
    }

    private IActionResult OnCallError(Guid groupId, string error)
    {
        TempData["OnCallError"] = error;
        return RedirectToAction(nameof(Index), new { groupId });
    }

    private string ValidationErrors(string fallback)
    {
        var errors = ModelState.Values.SelectMany(state => state.Errors)
            .Select(error => error.ErrorMessage).Where(error => !string.IsNullOrWhiteSpace(error)).Distinct().ToArray();
        return errors.Length == 0 ? fallback : string.Join(" ", errors);
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

    private async Task<OnCallViewModel> BuildManageModel(OnCallGroup group, List<OnCallGroup> groups,
        CancellationToken cancellationToken)
    {
        var timeZone = GetTimeZone(group.TimeZoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone));
        var (startUtc, endUtc) = GetUtcDateRange(today, today.AddDays(14), timeZone);
        var model = await BuildModel(group, groups, startUtc, endUtc, cancellationToken);
        model.GroupForm = new OnCallGroupRequest { Id = group.Id, Name = group.Name, TimeZoneId = group.TimeZoneId };
        return model;
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
            TemplateMembers = await db.OnCallShiftTemplateMembers.AsNoTracking().Include(item => item.User).Include(item => item.Template)
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
            PendingLeaveCount = await db.OnCallLeaveRequests.CountAsync(item => item.GroupId == group.Id &&
                item.Status == OnCallLeaveRequest.Pending, cancellationToken),
            PendingHandoverCount = await db.OnCallHandoverRequests.CountAsync(item => item.Shift.GroupId == group.Id &&
                item.Status == OnCallHandoverRequest.Pending, cancellationToken),
            UnsettledDebtCount = await db.OnCallCoverageDebts.CountAsync(item => item.GroupId == group.Id &&
                item.SettledAtUtc == null, cancellationToken),
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

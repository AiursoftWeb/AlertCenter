using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Aiursoft.AlertCenter.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class OnCallController(TemplateDbContext db, IOnCallScheduleGenerator generator) : Controller
{
    private readonly OnCallScheduleService schedules = new(db, generator);
    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "值班中心", CascadedLinksIcon = "calendar-clock", CascadedLinksOrder = 3,
        LinkText = "排班规则", LinkOrder = 1)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        this.StackView(await LoadModel(cancellationToken));

    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "值班中心", CascadedLinksIcon = "calendar-clock", CascadedLinksOrder = 3,
        LinkText = "团队管理", LinkOrder = 0)]
    public async Task<IActionResult> Groups(CancellationToken cancellationToken) =>
        this.StackView(await LoadModel(cancellationToken));

    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "值班中心", CascadedLinksIcon = "calendar-clock", CascadedLinksOrder = 3,
        LinkText = "值班排班", LinkOrder = 2)]
    public async Task<IActionResult> Schedule(CancellationToken cancellationToken) =>
        this.StackView(await LoadModel(cancellationToken));

    public async Task<IActionResult> GroupDetails(Guid id, CancellationToken cancellationToken)
    {
        var model = await LoadModel(cancellationToken);
        if (model.Groups.All(group => group.Id != id)) return NotFound();
        model.SelectedGroupId = id;
        model.GroupHasSavedSchedule = await db.OnCallShifts.AnyAsync(shift => shift.GroupId == id, cancellationToken);
        model.GroupHasBoundApplications = await db.SourceApplications.AnyAsync(application => application.OnCallGroupId == id, cancellationToken);
        return this.StackView(model);
    }

    [Authorize(Roles = "Administrators")]
    public async Task<IActionResult> GroupCreate(CancellationToken cancellationToken) =>
        this.StackView(await LoadModel(cancellationToken));

    [Authorize(Roles = "Administrators")]
    public async Task<IActionResult> GroupEdit(Guid id, CancellationToken cancellationToken)
    {
        var model = await LoadModel(cancellationToken);
        var group = model.Groups.SingleOrDefault(item => item.Id == id);
        if (group == null) return NotFound();
        model.SelectedGroupId = id;
        model.GroupHasSavedSchedule = await db.OnCallShifts.AnyAsync(shift => shift.GroupId == id, cancellationToken);
        model.GroupInput = new CreateOnCallGroupRequest
        {
            Name = group.Name, TimeZoneId = group.TimeZoneId, RuleId = group.RuleId,
            EmailCcRecipients = group.EmailCcRecipients,
            AnchorUtc = group.AnchorUtc, Version = group.ScheduleVersion,
            UserIds = group.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToList()
        };
        await PopulateSelectedUsers(model, cancellationToken);
        return this.StackView(model);
    }

    [Authorize(Roles = "Administrators")]
    public async Task<IActionResult> Create(CancellationToken cancellationToken) =>
        this.StackView(await LoadModel(cancellationToken));

    [Authorize(Roles = "Administrators")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var model = await LoadModel(cancellationToken);
        var rule = model.Rules.SingleOrDefault(item => item.Id == id);
        if (rule == null) return NotFound();
        model.RuleInput = new SaveOnCallRuleRequest
        {
            Id = rule.Id,
            Name = rule.Name,
            ShiftHours = rule.ShiftHours,
            RestHours = rule.RestHours,
            BalanceShiftTimes = rule.BalanceShiftTimes,
            MinimumStaff = rule.MinimumStaff, TargetStaff = rule.TargetStaff
        };
        return this.StackView(model);
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var model = await LoadModel(cancellationToken);
        if (model.Rules.All(rule => rule.Id != id)) return NotFound();
        model.SelectedRuleId = id;
        return this.StackView(model, "RuleDetails");
    }

    public async Task<IActionResult> ScheduleDetails(Guid id, CancellationToken cancellationToken, DateTime? startUtc = null)
    {
        var model = await LoadModel(cancellationToken);
        if (model.Groups.All(group => group.Id != id)) return NotFound();
        model.SelectedGroupId = id;
        await PopulatePreview(model, cancellationToken, startUtc);
        return this.StackView(model, "Details");
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = "Administrators")]
    public Task<IActionResult> GenerateSchedule([FromBody] SaveOnCallScheduleRequest? request, CancellationToken cancellationToken) =>
        SaveScheduleCore(request, true, cancellationToken);

    [HttpGet]
    [Authorize(Roles = "Administrators")]
    public async Task<IActionResult> GetSchedule(Guid id, DateTime? startUtc, CancellationToken cancellationToken)
    {
        var group = await db.OnCallGroups.AsNoTracking().Include(item => item.Rule).Include(item => item.Members)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (group == null) return NotFound();
        var model = new OnCallViewModel { SelectedGroupId = id, Groups = [group] };
        await PopulatePreview(model, cancellationToken, startUtc);
        if (model.WeekStartUtc == default) return BadRequest(new { errors = new[] { model.PreviewError } });
        var assignments = model.Preview.ToLookup(shift => shift.StartsAtUtc);
        return Json(new
        {
            groupId = id, version = group.ScheduleVersion, startUtc = model.WeekStartUtc, saved = model.ScheduleSaved,
            generationError = model.PreviewError,
            restHours = model.ScheduleRestHours, balanceShiftTimes = group.Rule?.BalanceShiftTimes ?? false,
            canGenerate = model.CanGenerateSchedule,
            minimumStaff = model.ScheduleMinimumStaff, targetStaff = group.Rule?.TargetStaff,
            warnings = model.StaffingWarnings,
            members = model.PreviewUsers.Select(user => new
            {
                id = user.Id, displayName = user.DisplayName, departed = group.Members.All(member => member.UserId != user.Id)
            }),
            slots = Enumerable.Range(0, 21).Select(index => new
            {
                startsAtUtc = model.WeekStartUtc.AddHours(index * 8), endsAtUtc = model.WeekStartUtc.AddHours((index + 1) * 8),
                userIds = assignments[model.WeekStartUtc.AddHours(index * 8)].Select(shift => shift.UserId)
            })
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = "Administrators")]
    public Task<IActionResult> SaveSchedule([FromBody] SaveOnCallScheduleRequest? request, CancellationToken cancellationToken) =>
        SaveScheduleCore(request, false, cancellationToken);

    private async Task<IActionResult> SaveScheduleCore(SaveOnCallScheduleRequest? request, bool generate, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || request == null) return BadRequest(new { errors = new[] { "排班请求格式不正确。" } });
        try
        {
            var errors = await schedules.Save(request, generate, cancellationToken);
            return errors.Count == 0 ? Ok(new { warnings = schedules.Warnings }) : BadRequest(new { errors });
        }
        catch (DbUpdateException)
        {
            return Conflict(new { errors = new[] { "排班已发生变化或保存冲突，请刷新页面后重试。" } });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { errors = new[] { ex.Message } });
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = "Administrators")]
    public async Task<IActionResult> CreateGroup([Bind(Prefix = "GroupInput")] CreateOnCallGroupRequest request,
        CancellationToken cancellationToken) => await SaveGroupCore(request, null, cancellationToken);

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = "Administrators")]
    public async Task<IActionResult> UpdateGroup(Guid id, [Bind(Prefix = "GroupInput")] CreateOnCallGroupRequest request,
        CancellationToken cancellationToken) => await SaveGroupCore(request, id, cancellationToken);

    private async Task<IActionResult> SaveGroupCore(CreateOnCallGroupRequest request, Guid? id, CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken) : null;
        var group = id.HasValue ? await db.OnCallGroups.Include(item => item.Members)
            .SingleOrDefaultAsync(item => item.Id == id.Value, cancellationToken) : null;
        if (id.HasValue && group == null) return NotFound();
        if (group != null && request.Version != group.ScheduleVersion)
            return Conflict("团队或排班已发生变化，请刷新页面后重试。");
        request.Name = request.Name?.Trim() ?? string.Empty;
        request.TimeZoneId = request.TimeZoneId?.Trim() ?? string.Empty;
        request.UserIds ??= [];
        if (string.IsNullOrWhiteSpace(request.Name)) ModelState.AddModelError("GroupInput.Name", "请输入团队名称。");
        if (!TimeZoneInfo.GetSystemTimeZones().Any(zone => zone.Id == request.TimeZoneId))
            ModelState.AddModelError("GroupInput.TimeZoneId", "请选择列表中的有效时区。");
        if (!EmailChannelAdapter.TryParseCcRecipients(request.EmailCcRecipients, out var emailCcRecipients, out var ccRecipientsError))
            ModelState.AddModelError("GroupInput.EmailCcRecipients", ccRecipientsError!);
        if (request.RuleId.HasValue && !await db.OnCallRules.AnyAsync(rule => rule.Id == request.RuleId, cancellationToken))
            ModelState.AddModelError("GroupInput.RuleId", "请选择现有排班规则。");
        if (request.AnchorUtc is not { } anchor || anchor.Kind == DateTimeKind.Local ||
            anchor < DateTime.MinValue.AddDays(28) || anchor > DateTime.MaxValue.AddDays(-28))
            ModelState.AddModelError("GroupInput.AnchorUtc", "请选择有效的 UTC 开始时间（需距离日期边界至少 28 天）。");
        if (request.UserIds.Count > 200 || request.UserIds.Any(string.IsNullOrWhiteSpace) ||
            request.UserIds.Distinct(StringComparer.Ordinal).Count() != request.UserIds.Count ||
            await db.Users.CountAsync(user => request.UserIds.Contains(user.Id), cancellationToken) != request.UserIds.Count)
            ModelState.AddModelError("GroupInput.UserIds", "所选员工不存在、重复或超过 200 人，请重新选择。");
        var hasSchedule = group != null && await db.OnCallShifts.AnyAsync(shift => shift.GroupId == group.Id, cancellationToken);
        if (hasSchedule && group!.AnchorUtc != request.AnchorUtc)
            ModelState.AddModelError("GroupInput.AnchorUtc", "已有保存排班，不能修改团队排班起点。");
        if (!ModelState.IsValid)
        {
            var model = await LoadModel(cancellationToken);
            model.GroupInput = request;
            model.SelectedGroupId = id;
            model.GroupHasSavedSchedule = hasSchedule;
            await PopulateSelectedUsers(model, cancellationToken);
            return this.StackView(model, id.HasValue ? "GroupEdit" : "GroupCreate");
        }
        if (group == null)
        {
            group = new OnCallGroup();
            db.OnCallGroups.Add(group);
        }
        var removedIds = group.Members.Select(member => member.UserId).Except(request.UserIds).ToArray();
        var now = DateTime.UtcNow;
        var futureAssignments = await db.OnCallShiftMembers
            .Where(member => member.Shift.GroupId == group.Id && member.Shift.StartsAtUtc > now && removedIds.Contains(member.UserId))
            .ToListAsync(cancellationToken);
        db.OnCallShiftMembers.RemoveRange(futureAssignments);
        group.Name = request.Name;
        group.TimeZoneId = request.TimeZoneId;
        group.EmailCcRecipients = emailCcRecipients.Count == 0 ? null : string.Join('\n', emailCcRecipients);
        group.RuleId = request.RuleId;
        group.AnchorUtc = DateTime.SpecifyKind(request.AnchorUtc!.Value, DateTimeKind.Utc);
        group.ScheduleVersion = Guid.NewGuid();
        try
        {
            // 先移除旧顺序，再写入新顺序，避免成员换序时触发唯一索引冲突。
            db.OnCallGroupMembers.RemoveRange(group.Members);
            await db.SaveChangesAsync(cancellationToken);
            group.Members.Clear();
            for (var index = 0; index < request.UserIds.Count; index++)
                group.Members.Add(new OnCallGroupMember { UserId = request.UserIds[index], RotationOrder = index });
            await db.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict("团队或排班已发生变化，请刷新页面后重试。");
        }
        TempData["OnCallGroupSuccess"] = id.HasValue ? "团队已更新。" : "团队创建成功。";
        if (futureAssignments.Count > 0)
            TempData["OnCallGroupSuccess"] += $" 已清除退出成员的 {futureAssignments.Count} 条未来值班安排，请检查并补齐空班。";
        return RedirectToAction(nameof(GroupDetails), new { id = group.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = "Administrators")]
    public async Task<IActionResult> DeleteGroup(Guid id, CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken) : null;
        var group = await db.OnCallGroups.FindAsync([id], cancellationToken);
        if (group == null) return NotFound();
        if (await db.OnCallShifts.AnyAsync(shift => shift.GroupId == id, cancellationToken))
            return Conflict("该团队有已保存的排班，不能删除团队及其历史记录。");
        if (await db.SourceApplications.AnyAsync(application => application.OnCallGroupId == id, cancellationToken))
            return Conflict("该团队已绑定接入应用，请先更换或解除应用的值班组绑定。");
        db.OnCallGroupMembers.RemoveRange(await db.OnCallGroupMembers.Where(member => member.GroupId == id).ToListAsync(cancellationToken));
        db.OnCallGroups.Remove(group);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict("团队或排班已发生变化，请刷新页面后重试。");
        }
        TempData["OnCallGroupSuccess"] = "团队已删除。";
        return RedirectToAction(nameof(Groups));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = "Administrators")]
    public async Task<IActionResult> SaveRule([Bind(Prefix = "RuleInput")] SaveOnCallRuleRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken) : null;
        request.Name = request.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(request.Name)) ModelState.AddModelError("RuleInput.Name", "请输入规则名称。");
        if (request.ShiftHours != 8) ModelState.AddModelError("RuleInput.ShiftHours", "固定每天三班，每班 8 小时。");
        if (request.RestHours is < 0 or > 168) ModelState.AddModelError("RuleInput.RestHours", "最短休息需为 0–168 小时。");
        if (request.MinimumStaff is < 1 or > 200) ModelState.AddModelError("RuleInput.MinimumStaff", "每班最低人数需为 1–200 人。");
        if (request.TargetStaff is < 1 or > 200 || request.TargetStaff < request.MinimumStaff)
            ModelState.AddModelError("RuleInput.TargetStaff", "目标人数不能低于最低人数，且需为 1–200 人；留空表示自动分配。");
        var rule = request.Id.HasValue ? await db.OnCallRules.FindAsync([request.Id.Value], cancellationToken) : null;
        if (request.Id.HasValue && rule == null) return NotFound();
        if (!ModelState.IsValid)
        {
            var model = await LoadModel(cancellationToken);
            model.RuleInput = request;
            return this.StackView(model, request.Id.HasValue ? "Edit" : "Create");
        }
        if (rule == null)
        {
            rule = new OnCallRule();
            db.OnCallRules.Add(rule);
        }
        rule.Name = request.Name;
        rule.ShiftHours = request.ShiftHours;
        rule.RestHours = request.RestHours;
        rule.BalanceShiftTimes = request.BalanceShiftTimes;
        rule.MinimumStaff = request.MinimumStaff;
        rule.TargetStaff = request.TargetStaff;
        var groups = await db.OnCallGroups.Where(group => group.RuleId == rule.Id).ToListAsync(cancellationToken);
        foreach (var group in groups) group.ScheduleVersion = Guid.NewGuid();
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict("规则或团队已发生变化，请刷新页面后重试。");
        }
        TempData["OnCallRuleSuccess"] = request.Id.HasValue ? "排班规则已更新。" : "排班规则创建成功。";
        return RedirectToAction(nameof(Details), new { id = rule.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = "Administrators")]
    public async Task<IActionResult> DeleteRule(Guid id, CancellationToken cancellationToken)
    {
        var rule = await db.OnCallRules.FindAsync([id], cancellationToken);
        if (rule == null) return NotFound();
        if (await db.OnCallGroups.AnyAsync(group => group.RuleId == id, cancellationToken))
            return Conflict("该规则仍被团队绑定，请先修改团队的规则绑定。");
        db.OnCallRules.Remove(rule);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict("该规则仍被团队绑定，请先修改团队的规则绑定。");
        }
        TempData["OnCallRuleSuccess"] = "排班规则已删除。";
        return RedirectToAction(nameof(Index));
    }

    private async Task<OnCallViewModel> LoadModel(CancellationToken cancellationToken) => new()
    {
        IsAdministrator = User.IsInRole("Administrators"),
        TimeZones = TimeZoneInfo.GetSystemTimeZones().Select(zone => new SelectListItem
        {
            Value = zone.Id,
            Text = $"{zone.DisplayName} ({zone.Id})"
        }).ToList(),
        Groups = await db.OnCallGroups.AsNoTracking().Include(group => group.Rule).Include(group => group.Members)
            .ThenInclude(member => member.User).OrderBy(group => group.Name).ToListAsync(cancellationToken),
        Rules = await db.OnCallRules.AsNoTracking().OrderBy(rule => rule.Name).ToListAsync(cancellationToken),
        Roles = await db.Roles.AsNoTracking().OrderBy(role => role.Name)
            .Select(role => new SelectListItem { Value = role.Id, Text = role.Name ?? role.Id })
            .ToListAsync(cancellationToken)
    };

    [HttpGet]
    [Authorize(Roles = "Administrators")]
    public async Task<IActionResult> SearchUsers(string? query, string? roleId, int page = 1,
        CancellationToken cancellationToken = default)
    {
        query = query?.Trim();
        if (query?.Length > 100 || page < 1 || page > 10000) return BadRequest();
        if (!string.IsNullOrWhiteSpace(roleId) &&
            !await db.Roles.AnyAsync(role => role.Id == roleId, cancellationToken)) return BadRequest();
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(roleId))
            return Json(new { users = Array.Empty<object>(), hasMore = false });

        const int pageSize = 20;
        var users = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(roleId))
            users = users.Where(user => db.UserRoles.Any(link => link.RoleId == roleId && link.UserId == user.Id));
        if (string.IsNullOrWhiteSpace(roleId) && !string.IsNullOrWhiteSpace(query))
            users = users.Where(user => user.UserName!.Contains(query) || user.DisplayName.Contains(query));
        var matches = await users.OrderBy(user => user.DisplayName).ThenBy(user => user.Id)
            .Skip((page - 1) * pageSize).Take(pageSize + 1)
            .Select(user => new { id = user.Id, name = user.DisplayName, username = user.UserName })
            .ToListAsync(cancellationToken);
        return Json(new { users = matches.Take(pageSize), hasMore = matches.Count > pageSize });
    }

    private async Task PopulateSelectedUsers(OnCallViewModel model, CancellationToken cancellationToken)
    {
        var ids = (model.GroupInput.UserIds ?? []).Distinct().ToArray();
        model.SelectedUsers = await db.Users.AsNoTracking().Where(user => ids.Contains(user.Id))
            .ToListAsync(cancellationToken);
    }

    private async Task PopulatePreview(OnCallViewModel model, CancellationToken cancellationToken, DateTime? requestedStart = null)
    {
        var group = model.Groups.SingleOrDefault(item => item.Id == model.SelectedGroupId);
        if (group == null) return;
        var userIds = group.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray();
        try
        {
            var startUtc = OnCallScheduleService.WeekStart(group, requestedStart);
            model.WeekStartUtc = startUtc;
            var endUtc = startUtc.AddDays(7);
            var saved = await db.OnCallShifts.AsNoTracking().Include(shift => shift.Members)
                .Where(shift => shift.GroupId == group.Id && shift.StartsAtUtc >= startUtc && shift.StartsAtUtc < endUtc)
                .OrderBy(shift => shift.StartsAtUtc).ToListAsync(cancellationToken);
            var previewIds = userIds.Concat(saved.SelectMany(shift => shift.Members.Select(member => member.UserId))).Distinct().ToArray();
            model.PreviewUsers = await db.Users.AsNoTracking().Where(user => previewIds.Contains(user.Id)).ToListAsync(cancellationToken);
            model.ScheduleSaved = saved.Count > 0;
            model.ScheduleRestHours = saved.Count > 0 ? saved[0].RestHours : group.Rule?.RestHours ?? 16;
            model.ScheduleMinimumStaff = saved.Count > 0 ? saved[0].MinimumStaff : group.Rule?.MinimumStaff ?? 1;
            model.CanGenerateSchedule = group.Rule != null && userIds.Length >=
                Math.Max(3, 1 + (int)Math.Ceiling(group.Rule.RestHours / 8.0)) * group.Rule.MinimumStaff;
            var zone = TimeZoneInfo.FindSystemTimeZoneById(group.TimeZoneId);
            var boundaries = Enumerable.Range(0, 21).Select(index => new GeneratedOnCallShift(string.Empty,
                startUtc.AddHours(index * 8), startUtc.AddHours((index + 1) * 8))).ToList();
            model.PreviewDays = OnCallPreviewTimeline.Build(boundaries, zone);
            if (model.ScheduleSaved)
            {
                model.Preview = saved.SelectMany(shift => shift.Members.OrderBy(member => member.UserId)
                    .Select(member => new GeneratedOnCallShift(member.UserId, DateTime.SpecifyKind(shift.StartsAtUtc, DateTimeKind.Utc),
                        DateTime.SpecifyKind(shift.EndsAtUtc, DateTimeKind.Utc), shift.RestHours))).ToList();
                model.StaffingWarnings = OnCallScheduleValidator.StaffingWarnings(startUtc, 21, model.ScheduleMinimumStaff, model.Preview);
                var covered = model.Preview.Select(shift => shift.StartsAtUtc).Distinct().Count();
                if (covered < 21) model.PreviewError = $"本周有 {21 - covered} 个空班，请手动补齐值班人员。";
                return;
            }
            if (!model.CanGenerateSchedule)
            {
                model.PreviewError = group.Rule == null ? "团队尚未绑定规则，可手动创建排班。"
                    : $"自动生成每班至少 {group.Rule.MinimumStaff} 人、休息 {group.Rule.RestHours} 小时的排班，需要至少 {Math.Max(3, 1 + (int)Math.Ceiling(group.Rule.RestHours / 8.0)) * group.Rule.MinimumStaff} 名成员；当前 {userIds.Length} 人，可增加成员或手动安排。";
                return;
            }
            model.Preview = generator.Generate(group.Rule!, DateTime.SpecifyKind(group.AnchorUtc, DateTimeKind.Utc),
                userIds, startUtc, 21, await schedules.Existing(group.Id, userIds, startUtc, cancellationToken));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeZoneNotFoundException or InvalidTimeZoneException or OverflowException)
        {
            model.PreviewError = $"无法生成预览：{ex.Message}";
        }
    }
}

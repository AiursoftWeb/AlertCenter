using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class OnCallController(TemplateDbContext db, IOnCallScheduleGenerator generator, IStringLocalizer<OnCallController> localizer, IStringLocalizerFactory localizerFactory) : Controller
{
    private readonly OnCallScheduleService schedules = new(db, generator, new StringLocalizer<OnCallScheduleService>(localizerFactory), localizerFactory);
    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "On-call", CascadedLinksIcon = "calendar-clock", CascadedLinksOrder = 3,
        LinkText = "Scheduling Rules", LinkOrder = 1)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        this.StackView(await LoadModel(cancellationToken));

    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "On-call", CascadedLinksIcon = "calendar-clock", CascadedLinksOrder = 3,
        LinkText = "Team management", LinkOrder = 0)]
    public async Task<IActionResult> Groups(CancellationToken cancellationToken) =>
        this.StackView(await LoadModel(cancellationToken));

    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "On-call", CascadedLinksIcon = "calendar-clock", CascadedLinksOrder = 3,
        LinkText = "On-call schedule", LinkOrder = 2)]
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
                id = user.Id, displayName = user.DisplayName, departed = group.Members.All(member => member.UserId != user.Id),
                otherGroups = model.OtherGroupMemberships.Where(member => member.UserId == user.Id)
                    .Select(member => new { id = member.GroupId, name = member.Group.Name })
            }),
            otherGroupShifts = model.OtherGroupShifts.Select(shift => new
            {
                groupId = shift.GroupId, groupName = shift.Group.Name,
                startsAtUtc = DateTime.SpecifyKind(shift.StartsAtUtc, DateTimeKind.Utc),
                endsAtUtc = DateTime.SpecifyKind(shift.EndsAtUtc, DateTimeKind.Utc),
                userIds = shift.Members.Select(member => member.UserId)
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
        if (!ModelState.IsValid || request == null) return BadRequest(new { errors = new[] { localizer["The shift request format is invalid."].Value } });
        try
        {
            var errors = await schedules.Save(request, generate, cancellationToken);
            return errors.Count == 0 ? Ok(new { warnings = schedules.Warnings }) : BadRequest(new { errors });
        }
        catch (DbUpdateException)
        {
            return Conflict(new { errors = new[] { localizer["The schedule has changed or a save conflict occurred. Please refresh the page and try again."].Value } });
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
        var attempt = 0;
        // Reuse the ID on retry so an uncertain commit cannot create a second record.
        var createdId = Guid.NewGuid();
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async retryToken =>
            {
                if (attempt++ > 0) db.ChangeTracker.Clear();
                await using var transaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, retryToken) : null;
                var group = id.HasValue ? await db.OnCallGroups.Include(item => item.Members)
                    .SingleOrDefaultAsync(item => item.Id == id.Value, retryToken) : null;
                if (id.HasValue && group == null) return NotFound();
                if (group != null && request.Version != group.ScheduleVersion)
                    return Conflict(localizer["The team or schedule has changed. Please refresh the page and try again."].Value);
                request.Name = request.Name?.Trim() ?? string.Empty;
                request.TimeZoneId = request.TimeZoneId?.Trim() ?? string.Empty;
                request.UserIds ??= [];
                if (string.IsNullOrWhiteSpace(request.Name)) ModelState.AddModelError("GroupInput.Name", localizer["Please enter the team name."].Value);
                if (!TimeZoneInfo.GetSystemTimeZones().Any(zone => zone.Id == request.TimeZoneId))
                    ModelState.AddModelError("GroupInput.TimeZoneId", localizer["Please select a valid time zone from the list."].Value);
                if (!EmailChannelAdapter.TryParseCcRecipients(request.EmailCcRecipients, out var emailCcRecipients, out var ccRecipientsError, localizer: localizerFactory.Create(typeof(EmailChannelAdapter))))
                    ModelState.AddModelError("GroupInput.EmailCcRecipients", ccRecipientsError!);
                if (request.RuleId.HasValue && !await db.OnCallRules.AnyAsync(rule => rule.Id == request.RuleId, retryToken))
                    ModelState.AddModelError("GroupInput.RuleId", localizer["Please select an existing on-call rule."].Value);
                if (request.AnchorUtc is not { } anchor || anchor.Kind == DateTimeKind.Local ||
                    anchor < DateTime.MinValue.AddDays(28) || anchor > DateTime.MaxValue.AddDays(-28))
                    ModelState.AddModelError("GroupInput.AnchorUtc", localizer["Please select a valid UTC start time (must be at least 28 days from the date boundary)."].Value);
                if (request.UserIds.Count > 200 || request.UserIds.Any(string.IsNullOrWhiteSpace) ||
                    request.UserIds.Distinct(StringComparer.Ordinal).Count() != request.UserIds.Count ||
                    await db.Users.CountAsync(user => request.UserIds.Contains(user.Id), retryToken) != request.UserIds.Count)
                    ModelState.AddModelError("GroupInput.UserIds", localizer["The selected employees do not exist, are duplicated, or exceed 200. Please select again."].Value);
                var hasSchedule = group != null && await db.OnCallShifts.AnyAsync(shift => shift.GroupId == group.Id, retryToken);
                if (hasSchedule && group!.AnchorUtc != request.AnchorUtc)
                    ModelState.AddModelError("GroupInput.AnchorUtc", localizer["A saved schedule exists; the team schedule start point cannot be modified."].Value);
                if (!ModelState.IsValid)
                {
                    var model = await LoadModel(retryToken);
                    model.GroupInput = request;
                    model.SelectedGroupId = id;
                    model.GroupHasSavedSchedule = hasSchedule;
                    await PopulateSelectedUsers(model, retryToken);
                    return this.StackView(model, id.HasValue ? "GroupEdit" : "GroupCreate");
                }
                if (group == null)
                {
                    group = new OnCallGroup { Id = createdId };
                    db.OnCallGroups.Add(group);
                }
                var removedIds = group.Members.Select(member => member.UserId).Except(request.UserIds).ToArray();
                var now = DateTime.UtcNow;
                var futureAssignments = await db.OnCallShiftMembers
                    .Where(member => member.Shift.GroupId == group.Id && member.Shift.StartsAtUtc > now && removedIds.Contains(member.UserId))
                    .ToListAsync(retryToken);
                db.OnCallShiftMembers.RemoveRange(futureAssignments);
                group.Name = request.Name;
                group.TimeZoneId = request.TimeZoneId;
                group.EmailCcRecipients = emailCcRecipients.Count == 0 ? null : string.Join('\n', emailCcRecipients);
                group.RuleId = request.RuleId;
                group.AnchorUtc = DateTime.SpecifyKind(request.AnchorUtc!.Value, DateTimeKind.Utc);
                group.ScheduleVersion = Guid.NewGuid();
                // Remove the old ordering before saving the new ordering to avoid unique-index conflicts.
                db.OnCallGroupMembers.RemoveRange(group.Members);
                await db.SaveChangesAsync(retryToken);
                group.Members.Clear();
                for (var index = 0; index < request.UserIds.Count; index++)
                    group.Members.Add(new OnCallGroupMember { UserId = request.UserIds[index], RotationOrder = index });
                await db.SaveChangesAsync(retryToken);
                if (transaction != null) await transaction.CommitAsync(retryToken);
                TempData["OnCallGroupSuccess"] = id.HasValue ? localizer["Team updated."].Value : localizer["Team created successfully."].Value;
                if (futureAssignments.Count > 0)
                    TempData["OnCallGroupSuccess"] += string.Format(localizer[" Cleared {0} future shift assignments for departed members. Please review and fill any gaps."].Value, futureAssignments.Count);
                return RedirectToAction(nameof(GroupDetails), new { id = group.Id });
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict(localizer["The team or schedule has changed. Please refresh the page and try again."].Value);
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = "Administrators")]
    public async Task<IActionResult> DeleteGroup(Guid id, CancellationToken cancellationToken)
    {
        var attempt = 0;
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async retryToken =>
            {
                if (attempt++ > 0) db.ChangeTracker.Clear();
                await using var transaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, retryToken) : null;
                var group = await db.OnCallGroups.FindAsync([id], retryToken);
                if (group == null) return NotFound();
                if (await db.OnCallShifts.AnyAsync(shift => shift.GroupId == id, retryToken))
                    return Conflict(localizer["This team has saved schedules and cannot be deleted along with its history."].Value);
                if (await db.SourceApplications.AnyAsync(application => application.OnCallGroupId == id, retryToken))
                    return Conflict(localizer["This team is bound to an integrated app. Please reassign or unbind the app's on-call group first."].Value);
                db.OnCallGroupMembers.RemoveRange(await db.OnCallGroupMembers.Where(member => member.GroupId == id).ToListAsync(retryToken));
                db.OnCallGroups.Remove(group);
                await db.SaveChangesAsync(retryToken);
                if (transaction != null) await transaction.CommitAsync(retryToken);
                TempData["OnCallGroupSuccess"] = localizer["Team deleted."].Value;
                return RedirectToAction(nameof(Groups));
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict(localizer["The team or schedule has changed. Please refresh the page and try again."].Value);
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = "Administrators")]
    public async Task<IActionResult> SaveRule([Bind(Prefix = "RuleInput")] SaveOnCallRuleRequest request,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        // Reuse the ID on retry so an uncertain commit cannot create a second record.
        var createdId = Guid.NewGuid();
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async retryToken =>
            {
                if (attempt++ > 0) db.ChangeTracker.Clear();
                await using var transaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, retryToken) : null;
                request.Name = request.Name?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(request.Name)) ModelState.AddModelError("RuleInput.Name", localizer["Please enter the rule name."].Value);
                if (request.ShiftHours != 8) ModelState.AddModelError("RuleInput.ShiftHours", localizer["Fixed three shifts per day, 8 hours each."].Value);
                if (request.RestHours is < 0 or > 168) ModelState.AddModelError("RuleInput.RestHours", localizer["Minimum rest must be 0–168 hours."].Value);
                if (request.MinimumStaff is < 1 or > 200) ModelState.AddModelError("RuleInput.MinimumStaff", localizer["Minimum staff per shift must be 1–200."].Value);
                if (request.TargetStaff is < 1 or > 200 || request.TargetStaff < request.MinimumStaff)
                    ModelState.AddModelError("RuleInput.TargetStaff", localizer["Target headcount cannot be lower than the minimum and must be between 1–200. Leave blank for automatic assignment."].Value);
                var rule = request.Id.HasValue ? await db.OnCallRules.FindAsync([request.Id.Value], retryToken) : null;
                if (request.Id.HasValue && rule == null) return NotFound();
                if (!ModelState.IsValid)
                {
                    var model = await LoadModel(retryToken);
                    model.RuleInput = request;
                    return this.StackView(model, request.Id.HasValue ? "Edit" : "Create");
                }
                if (rule == null)
                {
                    rule = new OnCallRule { Id = createdId };
                    db.OnCallRules.Add(rule);
                }
                rule.Name = request.Name;
                rule.ShiftHours = request.ShiftHours;
                rule.RestHours = request.RestHours;
                rule.BalanceShiftTimes = request.BalanceShiftTimes;
                rule.MinimumStaff = request.MinimumStaff;
                rule.TargetStaff = request.TargetStaff;
                var groups = await db.OnCallGroups.Where(group => group.RuleId == rule.Id).ToListAsync(retryToken);
                foreach (var group in groups) group.ScheduleVersion = Guid.NewGuid();
                await db.SaveChangesAsync(retryToken);
                if (transaction != null) await transaction.CommitAsync(retryToken);
                TempData["OnCallRuleSuccess"] = request.Id.HasValue ? localizer["Scheduling rule updated."].Value : localizer["Scheduling rule created successfully."].Value;
                return RedirectToAction(nameof(Details), new { id = rule.Id });
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict(localizer["The rule or team has changed. Refresh the page and try again."].Value);
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = "Administrators")]
    public async Task<IActionResult> DeleteRule(Guid id, CancellationToken cancellationToken)
    {
        var rule = await db.OnCallRules.FindAsync([id], cancellationToken);
        if (rule == null) return NotFound();
        if (await db.OnCallGroups.AnyAsync(group => group.RuleId == id, cancellationToken))
            return Conflict(localizer["This rule is still bound to a team. Please update the team's rule binding first."].Value);
        db.OnCallRules.Remove(rule);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict(localizer["This rule is still bound to a team. Please update the team's rule binding first."].Value);
        }
        TempData["OnCallRuleSuccess"] = localizer["Scheduling rule deleted."].Value;
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
            var startUtc = OnCallScheduleService.WeekStart(group, requestedStart, localizer: localizerFactory.Create(typeof(OnCallScheduleService)));
            model.WeekStartUtc = startUtc;
            var endUtc = startUtc.AddDays(7);
            var saved = await db.OnCallShifts.AsNoTracking().Include(shift => shift.Members)
                .Where(shift => shift.GroupId == group.Id && shift.StartsAtUtc >= startUtc && shift.StartsAtUtc < endUtc)
                .OrderBy(shift => shift.StartsAtUtc).ToListAsync(cancellationToken);
            model.AdjacentShifts = await db.OnCallShifts.AsNoTracking().Include(shift => shift.Members)
                .Where(shift => shift.GroupId == group.Id &&
                    ((shift.StartsAtUtc >= startUtc.AddDays(-7) && shift.StartsAtUtc < startUtc) ||
                     (shift.StartsAtUtc >= endUtc && shift.StartsAtUtc < endUtc.AddDays(7))))
                .OrderBy(shift => shift.StartsAtUtc).ToListAsync(cancellationToken);
            var previewIds = userIds.Concat(saved.Concat(model.AdjacentShifts)
                .SelectMany(shift => shift.Members.Select(member => member.UserId))).Distinct().ToArray();
            model.PreviewUsers = await db.Users.AsNoTracking().Where(user => previewIds.Contains(user.Id)).ToListAsync(cancellationToken);
            model.OtherGroupMemberships = await db.OnCallGroupMembers.AsNoTracking().Include(member => member.Group)
                .Where(member => member.GroupId != group.Id && previewIds.Contains(member.UserId))
                .OrderBy(member => member.Group.Name).ToListAsync(cancellationToken);
            model.OtherGroupShifts = await db.OnCallShifts.AsNoTracking().Include(shift => shift.Group)
                .Include(shift => shift.Members.Where(member => previewIds.Contains(member.UserId)))
                .Where(shift => shift.GroupId != group.Id && shift.StartsAtUtc < endUtc.AddDays(7) &&
                    shift.EndsAtUtc > startUtc.AddDays(-7) && shift.Members.Any(member => previewIds.Contains(member.UserId)))
                .OrderBy(shift => shift.StartsAtUtc).ThenBy(shift => shift.Group.Name).ToListAsync(cancellationToken);
            var sharedIds = model.OtherGroupMemberships.Select(member => member.UserId).ToHashSet(StringComparer.Ordinal);
            var automaticIds = userIds.Where(id => !sharedIds.Contains(id)).ToArray();
            model.ScheduleSaved = saved.Count > 0;
            model.ScheduleRestHours = saved.Count > 0 ? saved[0].RestHours : group.Rule?.RestHours ?? 16;
            model.ScheduleMinimumStaff = saved.Count > 0 ? saved[0].MinimumStaff : group.Rule?.MinimumStaff ?? 1;
            model.CanGenerateSchedule = group.Rule != null && automaticIds.Length >=
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
                model.StaffingWarnings = OnCallScheduleValidator.StaffingWarnings(startUtc, 21, model.ScheduleMinimumStaff, model.Preview, localizerFactory.Create(typeof(OnCallScheduleValidator)));
                var covered = model.Preview.Select(shift => shift.StartsAtUtc).Distinct().Count();
                if (covered < 21) model.PreviewError = string.Format(localizer["There are {0} unassigned shifts this week. Please fill in on-call personnel manually."].Value, 21 - covered);
                return;
            }
            if (!model.CanGenerateSchedule)
            {
                model.PreviewError = group.Rule == null ? localizer["No rules bound to the team yet. You can manually create a schedule."].Value
                    : string.Format(localizer["Auto-generation requires at least {0} staff per shift and {1} hours of rest, necessitating at least {2} members. After skipping multi-on-call group members, {3} can be auto-scheduled; please add single-group members or arrange manually."].Value, group.Rule.MinimumStaff, group.Rule.RestHours, Math.Max(3, 1 + (int)Math.Ceiling(group.Rule.RestHours / 8.0)) * group.Rule.MinimumStaff, automaticIds.Length);
                return;
            }
            model.Preview = generator.Generate(group.Rule!, DateTime.SpecifyKind(group.AnchorUtc, DateTimeKind.Utc),
                automaticIds, startUtc, 21, await schedules.Existing(group.Id, automaticIds, startUtc, cancellationToken));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeZoneNotFoundException or InvalidTimeZoneException or OverflowException)
        {
            model.PreviewError = string.Format(localizer["Unable to generate preview: {0}"].Value, ex.Message);
        }
    }
}

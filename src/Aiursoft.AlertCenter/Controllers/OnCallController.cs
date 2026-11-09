using Aiursoft.AlertCenter.Authorization;
using System.Security.Claims;
using Aiursoft.AlertCenter.Configuration;
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
        LinkText = "My on-call", LinkOrder = 3)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken, int page = 1) =>
        this.StackView(await new MyOnCallService(db, localizerFactory.Create(typeof(OnCallScheduleValidator)))
            .LoadAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, DateTime.UtcNow, page, cancellationToken));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetSubscription([FromServices] Microsoft.Extensions.Options.IOptions<EmailSettings> settings,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var user = await db.Users.SingleAsync(user => user.Id == userId, cancellationToken);
        var token = OnCallCalendar.NewToken();
        user.OnCallSubscriptionTokenHash = OnCallCalendar.Hash(token);
        await db.SaveChangesAsync(cancellationToken);
        var model = await new MyOnCallService(db, localizerFactory.Create(typeof(OnCallScheduleValidator)))
            .LoadAsync(userId, DateTime.UtcNow, 1, cancellationToken);
        model.SubscriptionUrl = settings.Value.PublicBaseUrl.TrimEnd('/') + "/OnCall/Calendar.ics?token=" + token;
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return this.StackView(model, "Mine");
    }

    [AllowAnonymous, HttpGet("/OnCall/Calendar.ics")]
    public async Task<IActionResult> Calendar(string? token,
        [FromServices] Microsoft.Extensions.Options.IOptions<EmailSettings> settings, CancellationToken cancellationToken)
    {
        if (token is null || token.Length != 64 || !token.All(Uri.IsHexDigit)) return NotFound();
        var hash = OnCallCalendar.Hash(token);
        var userId = await db.Users.AsNoTracking().Where(user => user.OnCallSubscriptionTokenHash == hash)
            .Select(user => user.Id).SingleOrDefaultAsync(cancellationToken);
        if (userId == null) return NotFound();
        var now = DateTime.UtcNow;
        var from = now.AddDays(-30);
        var until = now.AddDays(84);
        var shifts = await db.OnCallShifts.AsNoTracking().AsSingleQuery().Include(shift => shift.Group)
            .Include(shift => shift.Members).ThenInclude(member => member.User)
            .Where(shift => shift.Members.Any(member => member.UserId == userId) && shift.EndsAtUtc > from && shift.StartsAtUtc < until)
            .OrderBy(shift => shift.StartsAtUtc).ThenBy(shift => shift.Id).ToListAsync(cancellationToken);
        return File(System.Text.Encoding.UTF8.GetBytes(OnCallCalendar.Render(shifts, settings.Value.PublicBaseUrl, now)),
            "text/calendar; charset=utf-8", "oncall.ics");
    }
    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "On-call", CascadedLinksIcon = "calendar-clock", CascadedLinksOrder = 3,
        LinkText = "Scheduling Templates", LinkOrder = 1)]
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
        model.UpcomingIssues = await OnCallScheduleIssues.UpcomingAsync(db, DateTime.UtcNow, localizerFactory, cancellationToken, id);
        var now = DateTime.UtcNow;
        var counts = await db.OnCallShifts.Where(shift => shift.GroupId == id).GroupBy(shift => shift.EndsAtUtc > now)
            .Select(shifts => new { Future = shifts.Key, Count = shifts.Count() }).ToListAsync(cancellationToken);
        model.FutureShiftCount = counts.Where(count => count.Future).Sum(count => count.Count);
        model.HistoricalShiftCount = counts.Where(count => !count.Future).Sum(count => count.Count);
        model.BoundApplications = await db.SourceApplications.AsNoTracking().Where(application => application.OnCallGroupId == id)
            .OrderBy(application => application.Name).ToListAsync(cancellationToken);
        return this.StackView(model);
    }

    [Authorize(Policy = AppPermissionNames.CanManageOnCall)]
    public async Task<IActionResult> GroupCreate(CancellationToken cancellationToken)
    {
        var model = await LoadModel(cancellationToken);
        model.GroupInput.AnchorUtc = OnCallGroupFormService.NextWholeHour(TimeZoneInfo.Utc, DateTime.UtcNow);
        model.GroupInput.AnchorLocal = OnCallGroupFormService.ToLocal(model.GroupInput.AnchorUtc.Value, TimeZoneInfo.Utc);
        return this.StackView(model);
    }

    [Authorize(Policy = AppPermissionNames.CanManageOnCall)]
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
            WeekStartsOn = group.WeekStartsOn,
            EmailCcRecipients = group.EmailCcRecipients,
            MinimumRestHours = group.MinimumRestHours, MaximumContinuousHours = group.MaximumContinuousHours,
            MinimumShiftStaff = group.MinimumShiftStaff, AutoExtend = group.AutoExtend,
            FallbackUserIds = group.FallbackRecipients.Select(person => person.UserId).ToList(),
            AnchorUtc = group.AnchorUtc,
            AnchorLocal = OnCallGroupFormService.ToLocal(group.AnchorUtc, TimeZoneInfo.FindSystemTimeZoneById(group.TimeZoneId)),
            Version = group.ScheduleVersion,
            UserIds = group.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToList()
        };
        await PopulateSelectedUsers(model, cancellationToken);
        return this.StackView(model);
    }

    [Authorize(Policy = AppPermissionNames.CanManageOnCall)]
    public async Task<IActionResult> Create(CancellationToken cancellationToken) =>
        this.StackView(await LoadModel(cancellationToken));

    [Authorize(Policy = AppPermissionNames.CanManageOnCall)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var model = await LoadModel(cancellationToken);
        var rule = model.Rules.SingleOrDefault(item => item.Id == id);
        if (rule == null) return NotFound();
        model.RuleInput = new SaveOnCallRuleRequest
        {
            Id = rule.Id,
            SplitMode = rule.HandoverPoints.Count > 0 ? "weekly" : "fixed",
            Name = rule.Name,
            ShiftHours = rule.ShiftHours,
            RestHours = rule.RestHours,
            BalanceShiftTimes = rule.SwapAlternateRounds,
            MinimumStaff = rule.MinimumStaff, TargetStaff = rule.TargetStaff,
            ShiftMinutes = rule.ShiftMinutes, StaffPerShift = rule.StaffPerShift, SwapAlternateRounds = rule.SwapAlternateRounds,
            HandoverPoints = rule.HandoverPoints.Select(point => new OnCallHandoverPointRequest
                { DayOfWeek = point.DayOfWeek, MinuteOfDay = point.MinuteOfDay }).ToList()
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

    public async Task<IActionResult> ScheduleDetails(Guid id, CancellationToken cancellationToken, DateTime? startUtc = null,
        DateTime? endUtc = null, string? startLocal = null, string? endLocal = null)
    {
        var model = await LoadModel(cancellationToken);
        if (model.Groups.All(group => group.Id != id)) return NotFound();
        model.SelectedGroupId = id;
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(model.Groups.Single(group => group.Id == id).TimeZoneId);
            if (startLocal != null) startUtc = OnCallScheduleService.LocalToUtc(startLocal, zone);
            if (endLocal != null) endUtc = OnCallScheduleService.LocalToUtc(endLocal, zone);
            if (endUtc.HasValue && endUtc <= startUtc) return BadRequest(localizer["Shift end must be after its start."].Value);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return BadRequest(localizer["Invalid team local time."].Value);
        }
        await PopulatePreview(model, cancellationToken, startUtc, endUtc);
        return this.StackView(model, "Details");
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AppPermissionNames.CanManageOnCall)]
    public Task<IActionResult> GenerateSchedule([FromBody] SaveOnCallScheduleRequest? request, CancellationToken cancellationToken) =>
        SaveScheduleCore(request, true, cancellationToken);

    [HttpGet]
    [Authorize(Policy = AppPermissionNames.CanManageOnCall)]
    public async Task<IActionResult> GetSchedule(Guid id, DateTime? startUtc, CancellationToken cancellationToken, DateTime? endUtc = null,
        string? startLocal = null, string? endLocal = null)
    {
        var group = await db.OnCallGroups.AsNoTracking().Include(item => item.Rule).ThenInclude(rule => rule!.HandoverPoints).Include(item => item.Members)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (group == null) return NotFound();
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(group.TimeZoneId);
            if (startLocal != null) startUtc = OnCallScheduleService.LocalToUtc(startLocal, zone);
            if (endLocal != null) endUtc = OnCallScheduleService.LocalToUtc(endLocal, zone);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return BadRequest(new { errors = new[] { localizer["Invalid team local time."].Value } });
        }
        if (endUtc.HasValue && endUtc <= startUtc) return BadRequest();
        var model = new OnCallViewModel { SelectedGroupId = id, Groups = [group] };
        await PopulatePreview(model, cancellationToken, startUtc, endUtc);
        if (model.WeekStartUtc == default) return BadRequest(new { errors = new[] { model.PreviewError } });
        var slots = model.ScheduleSaved
            ? model.SavedShifts.Select(shift => new OnCallSlotRequest { Id = shift.Id,
                StartsAtUtc = DateTime.SpecifyKind(shift.StartsAtUtc, DateTimeKind.Utc),
                EndsAtUtc = DateTime.SpecifyKind(shift.EndsAtUtc, DateTimeKind.Utc), Source = shift.Source,
                RotationPosition = shift.RotationPosition, UserIds = shift.Members.Select(member => member.UserId).ToList() })
            : model.Preview.GroupBy(shift => new { shift.StartsAtUtc, shift.EndsAtUtc }).Select(batch => new OnCallSlotRequest {
                    StartsAtUtc = batch.Key.StartsAtUtc, EndsAtUtc = batch.Key.EndsAtUtc, Source = OnCallShiftSource.Generated,
                    UserIds = batch.Select(shift => shift.UserId).ToList() });
        return Json(new
        {
            groupId = id, version = group.ScheduleVersion, startUtc = model.WeekStartUtc, saved = model.ScheduleSaved,
            endUtc = model.WeekEndUtc, autoExtend = group.AutoExtend, lastShiftEndUtc = model.LastShiftEndUtc,
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
            slots = slots.Select(ScheduleSlot)
        });
    }

    private static object ScheduleSlot(OnCallSlotRequest slot) => new
    {
        id = slot.Id, startsAtUtc = DateTime.SpecifyKind(slot.StartsAtUtc, DateTimeKind.Utc),
        endsAtUtc = slot.EndsAtUtc.HasValue ? DateTime.SpecifyKind(slot.EndsAtUtc.Value, DateTimeKind.Utc) : (DateTime?)null,
        userIds = slot.UserIds, source = slot.Source, rotationPosition = slot.RotationPosition
    };

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AppPermissionNames.CanManageOnCall)]
    public Task<IActionResult> SaveSchedule([FromBody] SaveOnCallScheduleRequest? request, CancellationToken cancellationToken) =>
        SaveScheduleCore(request, false, cancellationToken);

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AppPermissionNames.CanManageOnCall)]
    public Task<IActionResult> PreviewSchedule([FromBody] SaveOnCallScheduleRequest? request, CancellationToken cancellationToken, bool generate = false, bool clear = false) =>
        SaveScheduleCore(request, generate, cancellationToken, true, clear);

    private async Task<IActionResult> SaveScheduleCore(SaveOnCallScheduleRequest? request, bool generate, CancellationToken cancellationToken, bool preview = false, bool clear = false)
    {
        if (!ModelState.IsValid || request == null) return BadRequest(new { errors = new[] { localizer["The shift request format is invalid."].Value } });
        try
        {
            var errors = await schedules.Save(request, generate, cancellationToken, preview, clear);
            return errors.Count == 0 ? Ok(new { version = schedules.Version, startUtc = request.StartUtc, endUtc = request.EndUtc,
                errors = schedules.PreviewErrors, warnings = schedules.Warnings, slots = schedules.PreviewSlots.Select(ScheduleSlot),
                replacedSlots = schedules.ReplacedSlots.Select(ScheduleSlot),
                clearRanges = request.ClearRanges.Select(range => new
                {
                    startUtc = range.StartUtc, endUtc = range.EndUtc, includeManual = range.IncludeManual,
                    includeHistory = range.IncludeHistory, confirmHistory = range.ConfirmHistory,
                    autoExtendAction = range.AutoExtendAction, clearedAtUtc = range.ClearedAtUtc
                }),
                timelineWarnings = schedules.TimelineWarnings.Select(warning => new
                {
                    startsAtUtc = DateTime.SpecifyKind(warning.StartsAtUtc, DateTimeKind.Utc),
                    endsAtUtc = DateTime.SpecifyKind(warning.EndsAtUtc, DateTimeKind.Utc), message = warning.Message
                }) }) : BadRequest(new { errors });
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
    [Authorize(Policy = AppPermissionNames.CanManageOnCall)]
    public async Task<IActionResult> GroupFormPreview([FromBody] OnCallGroupFormPreviewRequest? request,
        CancellationToken cancellationToken = default)
    {
        var result = new OnCallGroupFormPreviewResult();
        if (!ModelState.IsValid || request == null)
        {
            result.Errors.Add(localizer["The team preview request format is invalid."].Value);
            return GroupFormPreviewResponse(result, true);
        }
        var zone = TimeZoneInfo.GetSystemTimeZones().FirstOrDefault(zone => zone.Id == request.TimeZoneId);
        if (zone == null)
            result.Errors.Add(localizer["Please select a valid time zone from the list."].Value);
        var group = request.GroupId.HasValue ? await db.OnCallGroups.AsNoTracking()
            .SingleOrDefaultAsync(group => group.Id == request.GroupId, cancellationToken) : null;
        if (request.GroupId.HasValue && group == null)
            result.Errors.Add(localizer["On-call team does not exist."].Value);
        var rule = request.RuleId.HasValue ? await db.OnCallRules.AsNoTracking().Include(rule => rule.HandoverPoints)
            .SingleOrDefaultAsync(rule => rule.Id == request.RuleId, cancellationToken) : null;
        if (request.RuleId.HasValue && rule == null)
            result.Errors.Add(localizer["Please select an existing scheduling template."].Value);
        if (request.UserIds == null || request.UserIds.Count > 200 || request.UserIds.Any(string.IsNullOrWhiteSpace) ||
            request.UserIds.Distinct(StringComparer.Ordinal).Count() != request.UserIds.Count ||
            await db.Users.CountAsync(user => request.UserIds.Contains(user.Id), cancellationToken) != request.UserIds.Count)
            result.Errors.Add(localizer["The selected employees do not exist, are duplicated, or exceed 200. Please select again."].Value);
        if (result.Errors.Count > 0) return GroupFormPreviewResponse(result, true);
        var lastShift = group == null ? null : await db.OnCallShifts.AsNoTracking().Include(shift => shift.Members)
            .Where(shift => shift.GroupId == group.Id).OrderByDescending(shift => shift.EndsAtUtc)
            .ThenByDescending(shift => shift.StartsAtUtc).ThenBy(shift => shift.Id).FirstOrDefaultAsync(cancellationToken);
        var locked = lastShift != null;
        DateTime? anchor = locked ? DateTime.SpecifyKind(group!.AnchorUtc, DateTimeKind.Utc) : null;
        try
        {
            if (!locked && request.AnchorLocal != null)
                anchor = OnCallScheduleService.LocalToUtc(request.AnchorLocal, zone!);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            result.Errors.Add(localizer["Invalid team local time. During daylight saving transitions, skipped times are invalid and repeated times use standard time."].Value);
            return GroupFormPreviewResponse(result, true);
        }
        if (anchor.HasValue && !OnCallGroupFormService.IsValidAnchor(anchor.Value))
        {
            result.Errors.Add(localizer["Please select a valid team local start time (must be at least 28 days from the date boundary)."].Value);
            return GroupFormPreviewResponse(result, true);
        }
        result = new OnCallGroupFormService(generator, localizer).Preview(rule, zone!, anchor, request.UserIds!, DateTime.UtcNow, locked, lastShift);
        return GroupFormPreviewResponse(result);
    }

    private JsonResult GroupFormPreviewResponse(OnCallGroupFormPreviewResult result, bool badRequest = false) => new(new
    {
        anchorLocal = result.AnchorLocal, suggestedAnchorLocal = result.SuggestedAnchorLocal,
        anchorLocked = result.AnchorLocked, suggestions = result.Suggestions.Select(anchor => new { anchorLocal = anchor }),
        slots = result.Slots.Select(slot => new
        {
            startsAtUtc = slot.StartsAtUtc, endsAtUtc = slot.EndsAtUtc, startLocal = slot.StartLocal,
            endLocal = slot.EndLocal, userIds = slot.UserIds
        }),
        warnings = result.Warnings, errors = result.Errors
    }) { StatusCode = badRequest ? StatusCodes.Status400BadRequest : StatusCodes.Status200OK };

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AppPermissionNames.CanManageOnCall)]
    public async Task<IActionResult> CreateGroup([Bind(Prefix = "GroupInput")] CreateOnCallGroupRequest request,
        CancellationToken cancellationToken) => await SaveGroupCore(request, null, cancellationToken);

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AppPermissionNames.CanManageOnCall)]
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
                var group = id.HasValue ? await db.OnCallGroups.Include(item => item.Members).Include(item => item.FallbackRecipients)
                    .SingleOrDefaultAsync(item => item.Id == id.Value, retryToken) : null;
                if (id.HasValue && group == null) return NotFound();
                if (group != null && request.Version != group.ScheduleVersion)
                    return Conflict(localizer["The team or schedule has changed. Please refresh the page and try again."].Value);
                request.Name = request.Name?.Trim() ?? string.Empty;
                request.TimeZoneId = request.TimeZoneId?.Trim() ?? string.Empty;
                request.UserIds ??= [];
                if (request.FallbackSelectionSubmitted) request.FallbackUserIds ??= [];
                if (!Enum.IsDefined(request.WeekStartsOn)) ModelState.AddModelError("GroupInput.WeekStartsOn", localizer["Invalid week start."].Value);
                if (string.IsNullOrWhiteSpace(request.Name)) ModelState.AddModelError("GroupInput.Name", localizer["Please enter the team name."].Value);
                var zone = TimeZoneInfo.GetSystemTimeZones().FirstOrDefault(zone => zone.Id == request.TimeZoneId);
                if (zone == null)
                    ModelState.AddModelError("GroupInput.TimeZoneId", localizer["Please select a valid time zone from the list."].Value);
                if (!EmailChannelAdapter.TryParseCcRecipients(request.EmailCcRecipients, out var emailCcRecipients, out var ccRecipientsError, localizer: localizerFactory.Create(typeof(EmailChannelAdapter))))
                    ModelState.AddModelError("GroupInput.EmailCcRecipients", ccRecipientsError!);
                var rule = request.RuleId.HasValue ? await db.OnCallRules.AsNoTracking().Include(rule => rule.HandoverPoints)
                    .SingleOrDefaultAsync(rule => rule.Id == request.RuleId, retryToken) : null;
                if (request.RuleId.HasValue && rule == null)
                    ModelState.AddModelError("GroupInput.RuleId", localizer["Please select an existing scheduling template."].Value);
                if (request.FallbackUserIds != null && await db.Users.CountAsync(user => request.FallbackUserIds.Contains(user.Id), retryToken) != request.FallbackUserIds.Distinct().Count())
                    ModelState.AddModelError("GroupInput.FallbackUserIds", localizer["Fallback recipients must be existing system users."].Value);
                var hasSchedule = group != null && await db.OnCallShifts.AnyAsync(shift => shift.GroupId == group.Id, retryToken);
                var localAnchor = request.AnchorLocal != null || ModelState.ContainsKey("GroupInput.AnchorLocal");
                if (localAnchor)
                {
                    ModelState.Remove("GroupInput.AnchorUtc");
                    request.AnchorUtc = hasSchedule ? group!.AnchorUtc : null;
                    if (!hasSchedule && zone != null)
                    {
                        try
                        {
                            request.AnchorUtc = OnCallScheduleService.LocalToUtc(request.AnchorLocal!, zone);
                        }
                        catch (Exception ex) when (ex is FormatException or ArgumentException)
                        {
                            ModelState.AddModelError("GroupInput.AnchorLocal", localizer["Invalid team local time. During daylight saving transitions, skipped times are invalid and repeated times use standard time."].Value);
                        }
                    }
                }
                if (request.AnchorUtc is not { } anchor || !OnCallGroupFormService.IsValidAnchor(anchor))
                    ModelState.AddModelError(localAnchor ? "GroupInput.AnchorLocal" : "GroupInput.AnchorUtc", localAnchor
                        ? localizer["Please select a valid team local start time (must be at least 28 days from the date boundary)."].Value
                        : localizer["Please select a valid UTC start time (must be at least 28 days from the date boundary)."].Value);
                if (request.UserIds.Count > 200 || request.UserIds.Any(string.IsNullOrWhiteSpace) ||
                    request.UserIds.Distinct(StringComparer.Ordinal).Count() != request.UserIds.Count ||
                    await db.Users.CountAsync(user => request.UserIds.Contains(user.Id), retryToken) != request.UserIds.Count)
                    ModelState.AddModelError("GroupInput.UserIds", localizer["The selected employees do not exist, are duplicated, or exceed 200. Please select again."].Value);
                if (hasSchedule && group!.AnchorUtc != request.AnchorUtc)
                    ModelState.AddModelError("GroupInput.AnchorUtc", localizer["A saved schedule exists; the team schedule start point cannot be modified."].Value);
                if (hasSchedule && zone != null)
                {
                    var lockedLocal = OnCallGroupFormService.ToLocal(group!.AnchorUtc, zone);
                    var changedLocal = localAnchor && request.AnchorLocal != lockedLocal;
                    request.AnchorLocal = lockedLocal;
                    ModelState.Remove("GroupInput.AnchorLocal");
                    if (changedLocal)
                        ModelState.AddModelError("GroupInput.AnchorLocal", localizer["A saved schedule exists; the team schedule start point cannot be modified."].Value);
                }
                if (!ModelState.IsValid)
                {
                    if (!localAnchor && !hasSchedule && request.AnchorUtc.HasValue && zone != null && OnCallGroupFormService.IsValidAnchor(request.AnchorUtc.Value))
                        request.AnchorLocal = OnCallGroupFormService.ToLocal(request.AnchorUtc.Value, zone);
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
                group.Name = request.Name;
                group.TimeZoneId = request.TimeZoneId;
                group.WeekStartsOn = request.WeekStartsOn;
                group.EmailCcRecipients = emailCcRecipients.Count == 0 ? null : string.Join('\n', emailCcRecipients);
                group.RuleId = request.RuleId;
                group.MinimumRestHours = request.MinimumRestHours;
                group.MaximumContinuousHours = request.MaximumContinuousHours;
                group.MinimumShiftStaff = request.MinimumShiftStaff;
                group.AutoExtend = request.RuleId.HasValue && (request.AutoExtend ?? group.AutoExtend);
                if (request.FallbackUserIds != null)
                {
                    var selectedFallback = request.FallbackUserIds.ToHashSet(StringComparer.Ordinal);
                    foreach (var person in group.FallbackRecipients.Where(person => !selectedFallback.Contains(person.UserId)).ToList())
                        db.Remove(person);
                    foreach (var userId in selectedFallback.Except(group.FallbackRecipients.Select(person => person.UserId)))
                        group.FallbackRecipients.Add(new OnCallFallbackRecipient { UserId = userId });
                }
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
                if (OnCallGroupFormService.IsUnaligned(rule, zone!, group.AnchorUtc))
                    TempData["OnCallGroupWarning"] = localizer["The start time does not match a weekly handover point; the first shift will be a partial segment."].Value;
                return RedirectToAction(nameof(GroupDetails), new { id = group.Id });
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict(localizer["The team or schedule has changed. Please refresh the page and try again."].Value);
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AppPermissionNames.CanManageOnCall)]
    public async Task<IActionResult> DeleteGroup(Guid id, string? confirmationName, CancellationToken cancellationToken)
    {
        var attempt = 0;
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async retryToken =>
            {
                if (attempt++ > 0) db.ChangeTracker.Clear();
                await using var transaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, retryToken) : null;
                await OnCallScheduleService.LockGroupAsync(db, id, retryToken);
                var group = await db.OnCallGroups.Include(item => item.Members).Include(item => item.FallbackRecipients)
                    .SingleOrDefaultAsync(item => item.Id == id, retryToken);
                if (group == null) return NotFound();
                if (!ModelState.IsValid || !string.Equals(group.Name, confirmationName, StringComparison.Ordinal))
                    return BadRequest(localizer["The team name does not match."].Value);
                var applications = await db.SourceApplications.Where(application => application.OnCallGroupId == id)
                    .OrderBy(application => application.Name).Select(application => new { application.Id, application.Name }).ToListAsync(retryToken);
                if (applications.Count > 0)
                    return Conflict(new
                    {
                        message = localizer["This team is bound to an integrated app. Please reassign or unbind the app's on-call group first."].Value +
                            " " + string.Join(", ", applications.Select(application => application.Name)),
                        links = applications.Select(application => new { text = application.Name, url = $"/Applications/OnCall/{application.Id}" })
                    });
                var shiftIds = db.OnCallShifts.Where(shift => shift.GroupId == id).Select(shift => shift.Id);
                var snapshotShiftIds = db.OnCallEmailSnapshots.Where(snapshot => snapshot.GroupId == id).Select(snapshot => snapshot.ShiftId);
                db.RemoveRange(await db.Set<OnCallHandoverReceipt>()
                    .Where(receipt => shiftIds.Contains(receipt.ShiftId) || snapshotShiftIds.Contains(receipt.ShiftId)).ToListAsync(retryToken));
                if (!db.Database.IsRelational())
                    db.OnCallShifts.RemoveRange(await db.OnCallShifts.Include(shift => shift.Members).Where(shift => shift.GroupId == id).ToListAsync(retryToken));
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
    [Authorize(Policy = AppPermissionNames.CanManageOnCall)]
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
                if (string.IsNullOrWhiteSpace(request.Name)) ModelState.AddModelError("RuleInput.Name", localizer["Please enter the template name."].Value);
                if (request.ShiftHours <= 0) ModelState.AddModelError("RuleInput.ShiftHours", localizer["Shift duration must be positive."].Value);
                if (request.RestHours is < 0 or > 168) ModelState.AddModelError("RuleInput.RestHours", localizer["Minimum rest must be 0–168 hours."].Value);
                if (request.MinimumStaff is < 1 or > 200) ModelState.AddModelError("RuleInput.MinimumStaff", localizer["Minimum staff per shift must be 1–200."].Value);
                if (request.TargetStaff is < 1 or > 200)
                    ModelState.AddModelError("RuleInput.TargetStaff", localizer["Target headcount cannot be lower than the minimum and must be between 1–200. Leave blank for automatic assignment."].Value);
                if (request.HandoverPoints?.Any(point => !Enum.IsDefined(point.DayOfWeek) || point.MinuteOfDay is < 0 or >= 1440) == true)
                    ModelState.AddModelError("RuleInput.HandoverPoints", localizer["Invalid weekly handover point."].Value);
                if (request.SplitMode == "weekly" && (request.HandoverPoints?.Count ?? 0) == 0)
                    ModelState.AddModelError("RuleInput.HandoverPoints", localizer["Add at least one weekly handover point."].Value);
                if (request.SplitMode == "fixed") request.HandoverPoints = [];
                var rule = request.Id.HasValue ? await db.OnCallRules.Include(item => item.HandoverPoints)
                    .SingleOrDefaultAsync(item => item.Id == request.Id.Value, retryToken) : null;
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
                rule.BalanceShiftTimes = request.SwapAlternateRounds ?? request.BalanceShiftTimes;
                rule.MinimumStaff = request.MinimumStaff;
                rule.TargetStaff = request.StaffPerShift ?? request.TargetStaff;
                rule.StaffPerShift = request.StaffPerShift ?? request.TargetStaff ?? 1;
                rule.ShiftMinutes = request.ShiftMinutes;
                rule.SwapAlternateRounds = request.SwapAlternateRounds ?? request.BalanceShiftTimes;
                if (request.HandoverPoints != null)
                {
                    db.RemoveRange(rule.HandoverPoints);
                    rule.HandoverPoints = request.HandoverPoints.DistinctBy(point => new { point.DayOfWeek, point.MinuteOfDay })
                        .Select(point => new OnCallHandoverPoint { DayOfWeek = point.DayOfWeek, MinuteOfDay = point.MinuteOfDay }).ToList();
                    db.AddRange(rule.HandoverPoints);
                }
                var groups = await db.OnCallGroups.Where(group => group.RuleId == rule.Id).ToListAsync(retryToken);
                foreach (var group in groups) group.ScheduleVersion = Guid.NewGuid();
                await db.SaveChangesAsync(retryToken);
                if (transaction != null) await transaction.CommitAsync(retryToken);
                TempData["OnCallRuleSuccess"] = request.Id.HasValue ? localizer["Scheduling template updated."].Value : localizer["Scheduling template created successfully."].Value;
                return RedirectToAction(nameof(Details), new { id = rule.Id });
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict(localizer["The template or team has changed. Refresh the page and try again."].Value);
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AppPermissionNames.CanManageOnCall)]
    public async Task<IActionResult> DeleteRule(Guid id, CancellationToken cancellationToken)
    {
        var rule = await db.OnCallRules.FindAsync([id], cancellationToken);
        if (rule == null) return NotFound();
        if (await db.OnCallGroups.AnyAsync(group => group.RuleId == id, cancellationToken))
            return Conflict(localizer["This template is still bound to a team. Please update the team's template binding first."].Value);
        db.OnCallRules.Remove(rule);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict(localizer["This template is still bound to a team. Please update the team's template binding first."].Value);
        }
        TempData["OnCallRuleSuccess"] = localizer["Scheduling template deleted."].Value;
        return RedirectToAction(nameof(Index));
    }

    private async Task<OnCallViewModel> LoadModel(CancellationToken cancellationToken) => new()
    {
        CanManageOnCall = User.HasClaim(AppPermissions.Type, AppPermissionNames.CanManageOnCall),
        TimeZones = TimeZoneInfo.GetSystemTimeZones().Select(zone => new SelectListItem
        {
            Value = zone.Id,
            Text = $"{zone.DisplayName} ({zone.Id})"
        }).ToList(),
        Groups = await db.OnCallGroups.AsNoTracking().Include(group => group.FallbackRecipients).ThenInclude(person => person.User)
            .Include(group => group.Rule).ThenInclude(rule => rule!.HandoverPoints).Include(group => group.Members)
            .ThenInclude(member => member.User).OrderBy(group => group.Name).ToListAsync(cancellationToken),
        Rules = await db.OnCallRules.AsNoTracking().Include(rule => rule.HandoverPoints).OrderBy(rule => rule.Name).ToListAsync(cancellationToken),
        Roles = await db.Roles.AsNoTracking().OrderBy(role => role.Name)
            .Select(role => new SelectListItem { Value = role.Id, Text = role.Name ?? role.Id })
            .ToListAsync(cancellationToken)
    };

    [HttpGet]
    [Authorize(Policy = AppPermissionNames.CanManageOnCall)]
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
        var ids = (model.GroupInput.UserIds ?? []).Concat(model.GroupInput.FallbackUserIds ?? []).Distinct().ToArray();
        model.SelectedUsers = await db.Users.AsNoTracking().Where(user => ids.Contains(user.Id))
            .ToListAsync(cancellationToken);
    }

    private async Task PopulatePreview(OnCallViewModel model, CancellationToken cancellationToken, DateTime? requestedStart = null, DateTime? requestedEnd = null)
    {
        var group = model.Groups.SingleOrDefault(item => item.Id == model.SelectedGroupId);
        if (group == null) return;
        var lastEnd = await db.OnCallShifts.Where(shift => shift.GroupId == group.Id)
            .MaxAsync(shift => (DateTime?)shift.EndsAtUtc, cancellationToken);
        model.LastShiftEndUtc = lastEnd.HasValue ? DateTime.SpecifyKind(lastEnd.Value, DateTimeKind.Utc) : null;
        var userIds = group.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray();
        try
        {
            var startUtc = OnCallScheduleService.WeekStart(group, requestedStart, localizer: localizerFactory.Create(typeof(OnCallScheduleService)));
            model.WeekStartUtc = startUtc;
            var zone = TimeZoneInfo.FindSystemTimeZoneById(group.TimeZoneId);
            var endUtc = requestedEnd ?? OnCallScheduleService.MoveLocalDays(startUtc, zone, 7);
            model.WeekEndUtc = endUtc;
            model.PreviousWeekUtc = OnCallScheduleService.MoveLocalDays(startUtc, zone, -7);
            model.NextWeekUtc = OnCallScheduleService.MoveLocalDays(startUtc, zone, 7);
            var saved = await db.OnCallShifts.AsNoTracking().Include(shift => shift.Members)
                .Where(shift => shift.GroupId == group.Id && shift.EndsAtUtc > startUtc && shift.StartsAtUtc < endUtc)
                .OrderBy(shift => shift.StartsAtUtc).ToListAsync(cancellationToken);
            model.AdjacentShifts = await db.OnCallShifts.AsNoTracking().Include(shift => shift.Members)
                .Where(shift => shift.GroupId == group.Id &&
                    !(shift.EndsAtUtc > startUtc && shift.StartsAtUtc < endUtc) &&
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
            var automaticIds = userIds;
            model.ScheduleSaved = saved.Count > 0;
            model.SavedShifts = saved;
            model.ScheduleRestHours = saved.Count > 0 ? saved[0].RestHours : group.Rule?.RestHours ?? 16;
            model.ScheduleMinimumStaff = group.MinimumShiftStaff ?? 1;
            model.CanGenerateSchedule = group.Rule != null && automaticIds.Length >= group.Rule.StaffPerShift;
            model.PreviewDays = OnCallPreviewTimeline.Build([new GeneratedOnCallShift(string.Empty, startUtc, endUtc)], zone);
            if (model.ScheduleSaved)
            {
                model.Preview = saved.SelectMany(shift => shift.Members.OrderBy(member => member.UserId)
                    .Select(member => new GeneratedOnCallShift(member.UserId, DateTime.SpecifyKind(shift.StartsAtUtc, DateTimeKind.Utc),
                        DateTime.SpecifyKind(shift.EndsAtUtc, DateTimeKind.Utc)))).ToList();
                var history = await schedules.Existing(group.Id, previewIds, startUtc, cancellationToken, endUtc,
                    Math.Max(group.MinimumRestHours ?? 0, group.MaximumContinuousHours ?? 0));
                model.StaffingWarnings = OnCallScheduleValidator.Warnings(group, startUtc, endUtc, saved, history,
                    localizerFactory.Create(typeof(OnCallScheduleValidator)));
                var overlapErrors = OnCallScheduleValidator.OverlapErrors(saved, [],
                    localizerFactory.Create(typeof(OnCallScheduleValidator)), model.PreviewUsers.ToDictionary(user => user.Id, user => user.DisplayName));
                model.StaffingWarnings = model.StaffingWarnings.Concat(overlapErrors.Select(error => error.Message)).Distinct().ToList();
                model.TimelineWarnings = OnCallScheduleValidator.TimelineWarnings(group, startUtc, endUtc, saved, history,
                    localizerFactory.Create(typeof(OnCallScheduleValidator))).Concat(overlapErrors).ToList();
                return;
            }
            if (!model.CanGenerateSchedule)
            {
                model.PreviewError = group.Rule == null ? localizer["No template bound to the team yet. You can manually create a schedule."].Value
                    : localizer["Staff per shift must not exceed the rotation queue size."].Value;
                return;
            }
            var count = group.Rule!.HandoverPoints.Count == 0
                ? (int)Math.Ceiling((endUtc - startUtc).TotalMinutes / (group.Rule.ShiftMinutes ?? group.Rule.ShiftHours * 60))
                : ((int)Math.Ceiling((endUtc - startUtc).TotalDays / 7) + 2) * group.Rule.HandoverPoints.Count;
            model.Preview = generator.GenerateRange(group.Rule, DateTime.SpecifyKind(group.AnchorUtc, DateTimeKind.Utc), automaticIds, startUtc, count, zone)
                .Where(shift => shift.StartsAtUtc < endUtc)
                .Select(shift => shift.EndsAtUtc > endUtc ? shift with { EndsAtUtc = endUtc } : shift).ToList();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeZoneNotFoundException or InvalidTimeZoneException or OverflowException)
        {
            model.PreviewError = string.Format(localizer["Unable to generate preview: {0}"].Value, ex.Message);
        }
    }
}

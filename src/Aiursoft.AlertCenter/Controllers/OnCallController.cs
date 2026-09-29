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
    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "告警中心", CascadedLinksIcon = "bell", CascadedLinksOrder = 1,
        LinkText = "自动排班", LinkOrder = 4)]
    public async Task<IActionResult> Index(Guid? ruleId, Guid? editRuleId, CancellationToken cancellationToken)
    {
        var model = await LoadModel(cancellationToken);
        if (ruleId.HasValue && model.Rules.All(rule => rule.Id != ruleId.Value)) return NotFound();
        if (editRuleId.HasValue)
        {
            if (!model.IsAdministrator) return Forbid();
            var editRule = model.Rules.SingleOrDefault(rule => rule.Id == editRuleId.Value);
            if (editRule == null) return NotFound();
            model.EditingRuleId = editRule.Id;
            model.RuleInput = new SaveOnCallRuleRequest
            {
                Id = editRule.Id,
                GroupId = editRule.GroupId,
                Name = editRule.Name,
                ShiftHours = editRule.ShiftHours,
                RestHours = editRule.RestHours,
                AnchorUtc = editRule.AnchorUtc,
                Members = editRule.Members.OrderBy(member => member.RotationOrder)
                    .Select(member => new OnCallMemberInput
                    {
                        UserId = member.UserId, Selected = true, RotationOrder = member.RotationOrder + 1
                    }).ToList()
            };
        }
        else
        {
            var now = DateTime.UtcNow;
            model.RuleInput.AnchorUtc = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
        }
        model.SelectedRuleId = ruleId ?? editRuleId ?? model.Rules.FirstOrDefault()?.Id;
        PrepareMembers(model);
        PopulatePreview(model);
        return this.StackView(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = "Administrators")]
    public async Task<IActionResult> CreateGroup([Bind(Prefix = "GroupInput")] CreateOnCallGroupRequest request,
        CancellationToken cancellationToken)
    {
        request.Name = request.Name?.Trim() ?? string.Empty;
        request.TimeZoneId = request.TimeZoneId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(request.Name)) ModelState.AddModelError("GroupInput.Name", "请输入团队名称。");
        if (!TimeZoneInfo.GetSystemTimeZones().Any(zone => zone.Id == request.TimeZoneId))
            ModelState.AddModelError("GroupInput.TimeZoneId", "请选择列表中的有效时区。");
        if (!ModelState.IsValid)
        {
            var model = await LoadModel(cancellationToken);
            model.GroupInput = request;
            PrepareMembers(model);
            model.SelectedRuleId = model.Rules.FirstOrDefault()?.Id;
            PopulatePreview(model);
            return this.StackView(model, "Index");
        }
        db.OnCallGroups.Add(new OnCallGroup { Name = request.Name, TimeZoneId = request.TimeZoneId });
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = "Administrators")]
    public async Task<IActionResult> SaveRule([Bind(Prefix = "RuleInput")] SaveOnCallRuleRequest request,
        CancellationToken cancellationToken)
    {
        request.Name = request.Name?.Trim() ?? string.Empty;
        request.Members ??= [];
        if (string.IsNullOrWhiteSpace(request.Name)) ModelState.AddModelError("RuleInput.Name", "请输入规则名称。");
        if (request.GroupId == null || request.GroupId == Guid.Empty ||
            !await db.OnCallGroups.AnyAsync(group => group.Id == request.GroupId, cancellationToken))
            ModelState.AddModelError("RuleInput.GroupId", "请选择现有团队。");
        if (request.AnchorUtc is not { } anchor || anchor.Kind == DateTimeKind.Local ||
            anchor < DateTime.MinValue.AddDays(7) || anchor > DateTime.MaxValue.AddDays(-7))
            ModelState.AddModelError("RuleInput.AnchorUtc", "请选择有效的 UTC 开始时间（需距离日期边界至少七天）。");
        var knownIds = await db.Users.Select(user => user.Id).ToListAsync(cancellationToken);
        if (request.Members.Count != knownIds.Count ||
            request.Members.Select(member => member.UserId).Distinct(StringComparer.Ordinal).Count() != knownIds.Count ||
            request.Members.Any(member => !knownIds.Contains(member.UserId)))
            ModelState.AddModelError("RuleInput.Members", "员工列表已变化，请刷新后重新选择。");
        var selectedMembers = request.Members.Where(member => member.Selected).ToList();
        if (selectedMembers.Count == 0) ModelState.AddModelError("RuleInput.Members", "请至少选择一名员工。");
        if (selectedMembers.Any(member => string.IsNullOrWhiteSpace(member.UserId)) ||
            selectedMembers.Select(member => member.UserId).Distinct(StringComparer.Ordinal).Count() != selectedMembers.Count ||
            selectedMembers.Any(member => member.RotationOrder < 1) ||
            selectedMembers.Select(member => member.RotationOrder).Distinct().Count() != selectedMembers.Count)
            ModelState.AddModelError("RuleInput.Members", "轮换顺序必须是每位员工唯一的正整数。");
        if (request.ShiftHours is >= 1 and <= 24 && request.RestHours is >= 0 and <= 168 &&
            selectedMembers.Count < 1 + (int)Math.Ceiling((double)request.RestHours / request.ShiftHours))
            ModelState.AddModelError("RuleInput.Members",
                $"班次 {request.ShiftHours} 小时、至少休息 {request.RestHours} 小时需要至少 {1 + (int)Math.Ceiling((double)request.RestHours / request.ShiftHours)} 名员工。请增加员工或调整时间。");
        OnCallRule? rule = null;
        if (request.Id.HasValue)
        {
            rule = await db.OnCallRules.Include(item => item.Members)
                .SingleOrDefaultAsync(item => item.Id == request.Id.Value, cancellationToken);
            if (rule == null) return NotFound();
        }
        if (request.GroupId.HasValue && !string.IsNullOrWhiteSpace(request.Name) &&
            await db.OnCallRules.AnyAsync(item => item.GroupId == request.GroupId &&
                item.Name == request.Name && item.Id != request.Id, cancellationToken))
            ModelState.AddModelError("RuleInput.Name", "该团队已有同名规则，请使用其他名称。");
        if (!ModelState.IsValid)
        {
            var model = await LoadModel(cancellationToken);
            model.RuleInput = request;
            model.EditingRuleId = request.Id;
            model.SelectedRuleId = request.Id;
            PrepareMembers(model);
            PopulatePreview(model);
            return this.StackView(model, "Index");
        }
        var orderedUserIds = selectedMembers.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToList();
        if (rule == null)
        {
            rule = new OnCallRule();
            db.OnCallRules.Add(rule);
        }
        rule.GroupId = request.GroupId!.Value;
        rule.Name = request.Name;
        rule.ShiftHours = request.ShiftHours;
        rule.RestHours = request.RestHours;
        rule.AnchorUtc = DateTime.SpecifyKind(request.AnchorUtc!.Value, DateTimeKind.Utc);
        if (request.Id.HasValue)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            db.OnCallRuleMembers.RemoveRange(rule.Members);
            await db.SaveChangesAsync(cancellationToken);
            rule.Members.Clear();
            AddMembers(rule, orderedUserIds);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            AddMembers(rule, orderedUserIds);
            await db.SaveChangesAsync(cancellationToken);
        }
        return RedirectToAction(nameof(Index), new { ruleId = rule.Id });
    }

    private static void AddMembers(OnCallRule rule, IReadOnlyList<string> orderedUserIds)
    {
        for (var index = 0; index < orderedUserIds.Count; index++)
            rule.Members.Add(new OnCallRuleMember { RuleId = rule.Id, UserId = orderedUserIds[index], RotationOrder = index });
    }

    private async Task<OnCallViewModel> LoadModel(CancellationToken cancellationToken) => new()
    {
        IsAdministrator = User.IsInRole("Administrators"),
        TimeZones = TimeZoneInfo.GetSystemTimeZones().Select(zone => new SelectListItem
        {
            Value = zone.Id,
            Text = $"{zone.DisplayName} ({zone.Id})"
        }).ToList(),
        Groups = await db.OnCallGroups.AsNoTracking().OrderBy(group => group.Name).ToListAsync(cancellationToken),
        Rules = await db.OnCallRules.AsNoTracking().Include(rule => rule.Group).Include(rule => rule.Members)
            .OrderBy(rule => rule.Group.Name).ThenBy(rule => rule.Name).ToListAsync(cancellationToken),
        Staff = await db.Users.AsNoTracking().OrderBy(user => user.DisplayName).ThenBy(user => user.Id)
            .ToListAsync(cancellationToken)
    };

    private static void PrepareMembers(OnCallViewModel model)
    {
        var byId = model.RuleInput.Members.GroupBy(member => member.UserId)
            .ToDictionary(group => group.Key, group => group.First());
        model.RuleInput.Members = model.Staff.Select((user, index) =>
            byId.GetValueOrDefault(user.Id) ?? new OnCallMemberInput
            {
                UserId = user.Id, RotationOrder = index + 1
            }).ToList();
    }

    private void PopulatePreview(OnCallViewModel model)
    {
        var rule = model.Rules.SingleOrDefault(item => item.Id == model.SelectedRuleId);
        if (rule == null) return;
        var userIds = rule.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToList();
        try
        {
            rule.AnchorUtc = DateTime.SpecifyKind(rule.AnchorUtc, DateTimeKind.Utc);
            var duration = TimeSpan.FromHours(rule.ShiftHours);
            var now = DateTime.UtcNow;
            var offset = now > rule.AnchorUtc ? (now - rule.AnchorUtc).Ticks / duration.Ticks : 0;
            var startUtc = rule.AnchorUtc.AddTicks(offset * duration.Ticks);
            var count = (int)Math.Ceiling(168.0 / rule.ShiftHours);
            model.Preview = generator.Generate(rule, userIds, startUtc, count);
            _ = TimeZoneInfo.FindSystemTimeZoneById(rule.Group.TimeZoneId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeZoneNotFoundException or InvalidTimeZoneException or OverflowException)
        {
            model.PreviewError = $"无法生成预览：{ex.Message}";
        }
    }
}

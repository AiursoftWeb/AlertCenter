using Aiursoft.AlertCenter.Authorization;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Controllers;

[Authorize(Policy = AppPermissionNames.CanManagePush)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class SeverityPolicyController(TemplateDbContext db, IEnumerable<INotificationChannelAdapter> adapters, IStringLocalizer<SeverityPolicyController> localizer) : Controller
{
    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "Notifications", CascadedLinksIcon = "send", CascadedLinksOrder = 2,
        LinkText = "Severity policy", LinkOrder = 3)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        this.StackView(new SeverityPolicyViewModel
        {
            SmsAvailable = adapters.Any(adapter => adapter.Definition.Key == "sms"),
            Rules = await db.SeverityRules.AsNoTracking().OrderBy(rule => rule.Level).ToListAsync(cancellationToken)
        });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(SeverityPolicyViewModel model, CancellationToken cancellationToken)
    {
        var rules = await db.SeverityRules.OrderBy(rule => rule.Level).ToListAsync(cancellationToken);
        if (model.Rules.Count != rules.Count ||
            !model.Rules.Select(rule => rule.Level).Order().SequenceEqual(rules.Select(rule => rule.Level)))
            ModelState.AddModelError(string.Empty, localizer["Submit exactly one rule for every severity."].Value);
        if (!ModelState.IsValid)
        {
            model.SmsAvailable = adapters.Any(adapter => adapter.Definition.Key == "sms");
            return this.StackView(model, nameof(Index));
        }
        foreach (var rule in rules)
        {
            var submitted = model.Rules.Single(item => item.Level == rule.Level);
            rule.Email = submitted.Email;
            rule.Sms = submitted.Sms;
            rule.Webhook = submitted.Webhook;
            rule.InApp = submitted.InApp;
            rule.ShowInLists = submitted.ShowInLists;
        }
        // Save the entire matrix atomically. Each subsequent operation loads the current policy.
        await db.SaveChangesAsync(cancellationToken);
        TempData["PolicySaved"] = true;
        return RedirectToAction(nameof(Index));
    }
}

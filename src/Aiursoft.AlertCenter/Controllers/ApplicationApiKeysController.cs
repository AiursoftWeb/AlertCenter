using System.Security.Cryptography;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ApplicationApiKeysController(TemplateDbContext db, IApplicationAccessService access,
    IStringLocalizer<ApplicationApiKeysController> localizer) : Controller
{
    public async Task<IActionResult> Index(Guid applicationId, CancellationToken cancellationToken)
    {
        if (!await CanManage(applicationId, cancellationToken)) return Forbid();
        var application = await db.SourceApplications.AsNoTracking().SingleAsync(item => item.Id == applicationId, cancellationToken);
        return this.StackView(new ApplicationApiKeysViewModel
        {
            Application = application,
            Keys = await db.ApplicationApiKeys.AsNoTracking().Where(key => key.ApplicationId == applicationId)
                .OrderByDescending(key => key.CreatedAtUtc).ToListAsync(cancellationToken),
            NewKey = TempData[$"NewApiKey:{applicationId}"] as string
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create(Guid applicationId, CancellationToken cancellationToken)
    {
        if (!await CanManage(applicationId, cancellationToken)) return Forbid();
        return this.StackView(new ApplicationApiKeyEditViewModel { ApplicationId = applicationId }, "Edit");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ApplicationApiKeyEditViewModel model, CancellationToken cancellationToken)
    {
        if (!await CanManage(model.ApplicationId, cancellationToken)) return Forbid();
        if (model.Id.HasValue) return BadRequest();
        if (!await CanGrant(model, null, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return this.StackView(model, "Edit");
        var key = new ApplicationApiKey { ApplicationId = model.ApplicationId };
        Apply(model, key);
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        key.SecretHash = new PasswordHasher<ApplicationApiKey>().HashPassword(key, secret);
        db.ApplicationApiKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken);
        TempData[$"NewApiKey:{model.ApplicationId}"] = $"{model.ApplicationId}.{key.Id}.{secret}";
        return RedirectToAction(nameof(Index), new { applicationId = model.ApplicationId });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid applicationId, Guid id, CancellationToken cancellationToken)
    {
        if (!await CanManage(applicationId, cancellationToken)) return Forbid();
        var key = await db.ApplicationApiKeys.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && item.ApplicationId == applicationId, cancellationToken);
        if (key == null) return NotFound();
        return this.StackView(new ApplicationApiKeyEditViewModel
        {
            ApplicationId = applicationId, Id = key.Id, Name = key.Name, IsEnabled = key.IsEnabled,
            CanQueryIncidents = key.CanQueryIncidents, CanCreateIncidents = key.CanCreateIncidents,
            CanPatchIncidentMetadata = key.CanPatchIncidentMetadata, CanMitigateIncidents = key.CanMitigateIncidents, CanResolveIncidents = key.CanResolveIncidents, CanChangeIncidentSeverity = key.CanChangeIncidentSeverity
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(ApplicationApiKeyEditViewModel model, CancellationToken cancellationToken)
    {
        if (!await CanManage(model.ApplicationId, cancellationToken)) return Forbid();
        var key = await db.ApplicationApiKeys.SingleOrDefaultAsync(item => item.Id == model.Id && item.ApplicationId == model.ApplicationId, cancellationToken);
        if (key == null) return NotFound();
        if (!await CanGrant(model, key, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return this.StackView(model);
        Apply(model, key);
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index), new { applicationId = model.ApplicationId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid applicationId, Guid id, string? confirmationName, CancellationToken cancellationToken)
    {
        if (!await CanManage(applicationId, cancellationToken)) return Forbid();
        var key = await db.ApplicationApiKeys.SingleOrDefaultAsync(item => item.Id == id && item.ApplicationId == applicationId, cancellationToken);
        if (key == null) return NotFound();
        if (!string.Equals(key.Name, confirmationName, StringComparison.Ordinal))
            return BadRequest(localizer["Enter the exact key name to confirm deletion."].Value);
        db.ApplicationApiKeys.Remove(key);
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index), new { applicationId });
    }

    private Task<bool> CanManage(Guid applicationId, CancellationToken cancellationToken) =>
        access.CanAccessAsync(User, applicationId, ApplicationPermission.ManageApiKeys, cancellationToken);

    private async Task<bool> CanGrant(ApplicationApiKeyEditViewModel model, ApplicationApiKey? existing, CancellationToken cancellationToken) =>
        (!model.CanQueryIncidents || existing?.CanQueryIncidents == true || await access.CanAccessAsync(User, model.ApplicationId, ApplicationPermission.ViewIncidents, cancellationToken)) &&
        (!model.CanCreateIncidents || existing?.CanCreateIncidents == true || await access.CanAccessAsync(User, model.ApplicationId, ApplicationPermission.CreateIncidents, cancellationToken)) &&
        (!model.CanChangeIncidentSeverity || existing?.CanChangeIncidentSeverity == true || await access.CanAccessAsync(User, model.ApplicationId, ApplicationPermission.UpdateIncidents, cancellationToken)) &&
        (!model.CanPatchIncidentMetadata || existing?.CanPatchIncidentMetadata == true || await access.CanAccessAsync(User, model.ApplicationId, ApplicationPermission.UpdateIncidents, cancellationToken)) &&
        (!model.CanMitigateIncidents || existing?.CanMitigateIncidents == true || await access.CanAccessAsync(User, model.ApplicationId, ApplicationPermission.UpdateIncidents, cancellationToken)) &&
        (!model.CanResolveIncidents || existing?.CanResolveIncidents == true || await access.CanAccessAsync(User, model.ApplicationId, ApplicationPermission.UpdateIncidents, cancellationToken));

    private static void Apply(ApplicationApiKeyEditViewModel model, ApplicationApiKey key)
    {
        key.Name = model.Name.Trim();
        key.IsEnabled = model.IsEnabled;
        key.CanQueryIncidents = model.CanQueryIncidents;
        key.CanCreateIncidents = model.CanCreateIncidents;
        key.CanMitigateIncidents = model.CanMitigateIncidents;
        key.CanPatchIncidentMetadata = model.CanPatchIncidentMetadata;
        key.CanResolveIncidents = model.CanResolveIncidents;
        key.CanChangeIncidentSeverity = model.CanChangeIncidentSeverity;
    }
}

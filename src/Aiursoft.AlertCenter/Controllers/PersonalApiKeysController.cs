using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Services.Authentication;
using Aiursoft.UiStack.Navigation;
using Aiursoft.WebTools.Attributes;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Controllers;

[Authorize]
[LimitPerMin]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class PersonalApiKeysController(TemplateDbContext db) : Controller
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [RenderInNavBar(NavGroupName = "Settings", NavGroupOrder = 9998,
        CascadedLinksGroupName = "Personal", CascadedLinksIcon = "user-circle", CascadedLinksOrder = 1,
        LinkText = "API Keys", LinkOrder = 4)]
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken token) =>
        this.StackView(await LoadAsync(token));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind(Prefix = "Input")] PersonalApiKeyInput input, CancellationToken token)
    {
        if (!ModelState.IsValid)
            return this.StackView(await LoadAsync(token, input), nameof(Index));

        var now = DateTime.UtcNow;
        var key = new PersonalApiKey
        {
            UserId = UserId, Name = input.Name.Trim(), CanHandleIncidents = input.CanHandleIncidents,
            CreatedAtUtc = now, ExpiresAtUtc = now.AddDays(input.LifetimeDays)
        };
        var raw = PersonalApiKeyAuthenticationHandler.Issue(key.Id);
        key.SecretHash = PersonalApiKeyAuthenticationHandler.Hash(raw);
        db.PersonalApiKeys.Add(key);
        await db.SaveChangesAsync(token);
        ModelState.Clear();
        // Render the secret in this response only, without putting it in TempData or a cookie.
        return this.StackView(await LoadAsync(token, newKey: raw), nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken token)
    {
        var key = await db.PersonalApiKeys.NotCacheable().SingleOrDefaultAsync(item => item.Id == id && item.UserId == UserId, token);
        if (key == null) return NotFound();
        db.PersonalApiKeys.Remove(key);
        try
        {
            await db.SaveChangesAsync(token);
        }
        catch (DbUpdateConcurrencyException)
        {
            return NotFound();
        }
        return RedirectToAction(nameof(Index));
    }

    private async Task<PersonalApiKeysViewModel> LoadAsync(CancellationToken token, PersonalApiKeyInput? input = null, string? newKey = null) => new()
    {
        Input = input ?? new(), NewKey = newKey,
        Keys = await db.PersonalApiKeys.AsNoTracking().NotCacheable().Where(key => key.UserId == UserId)
            .OrderByDescending(key => key.CreatedAtUtc)
            .Select(key => new PersonalApiKeySummary(key.Id, key.Name, key.CanHandleIncidents, key.ExpiresAtUtc)).ToListAsync(token)
    };
}

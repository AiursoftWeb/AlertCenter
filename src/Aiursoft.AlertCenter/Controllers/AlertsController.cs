using System.Security.Cryptography;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Controllers;

[Authorize(Roles = "Administrators")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class AlertsController(TemplateDbContext db) : Controller
{
    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "告警中心", CascadedLinksIcon = "bell", CascadedLinksOrder = 0,
        LinkText = "消息列表", LinkOrder = 0)]
    public async Task<IActionResult> Index(int page = 1)
    {
        page = Math.Clamp(page, 1, 1000000);
        var messages = await db.ReportedMessages.AsNoTracking().Include(m => m.Application)
            .OrderByDescending(m => m.ReceivedAtUtc).ThenByDescending(m => m.Id)
            .Skip((page - 1) * 20).Take(21).ToListAsync();
        return this.StackView(new AlertsViewModel
        {
            Messages = messages.Take(20).ToList(), Page = page, HasNextPage = messages.Count > 20
        });
    }

    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "告警中心", CascadedLinksIcon = "bell", CascadedLinksOrder = 0,
        LinkText = "接入应用", LinkOrder = 1)]
    public async Task<IActionResult> Applications() => this.StackView(await ApplicationsModel());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateApplication(CreateApplicationRequest request)
    {
        if (!ModelState.IsValid) return this.StackView(await ApplicationsModel(), "Applications");
        var application = new SourceApplication { Name = request.Name.Trim() };
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        application.SecretHash = new PasswordHasher<SourceApplication>().HashPassword(application, secret);
        db.SourceApplications.Add(application);
        await db.SaveChangesAsync();
        var model = await ApplicationsModel();
        model.ApiKey = $"{application.Id}.{secret}";
        return this.StackView(model, "Applications");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetEnabled(Guid id, bool enabled)
    {
        var application = await db.SourceApplications.FindAsync(id);
        if (application == null) return NotFound();
        application.IsEnabled = enabled;
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Applications));
    }

    public async Task<IActionResult> Details(Guid id)
    {
        var message = await db.ReportedMessages.AsNoTracking().Include(m => m.Application)
            .SingleOrDefaultAsync(m => m.Id == id);
        if (message == null) return NotFound();
        return this.StackView(new AlertsViewModel { Message = message });
    }

    private async Task<AlertsViewModel> ApplicationsModel() => new()
    {
        Applications = await db.SourceApplications.AsNoTracking().OrderByDescending(a => a.CreatedAtUtc).ToListAsync()
    };
}

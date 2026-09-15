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
    private const string CreatedApplicationApiKey = "CreatedApplicationApiKey";
    private const string ApiKeyWasReset = "ApiKeyWasReset";

    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "告警中心", CascadedLinksIcon = "bell", CascadedLinksOrder = 1,
        LinkText = "消息列表", LinkOrder = 0)]
    public async Task<IActionResult> Index([FromQuery] AlertFilter filter)
    {
        filter.Normalize();
        var model = new AlertsViewModel
        {
            AlertFilter = filter,
            Page = filter.Page,
            Applications = await db.SourceApplications.AsNoTracking().Where(application =>
                db.ReportedMessages.Any(message => message.ApplicationId == application.Id))
                .OrderBy(application => application.Name).ToListAsync()
        };
        if (!ModelState.IsValid) return this.StackView(model);
        var query = FilterMessages(db.ReportedMessages.AsNoTracking(), filter);
        var messages = await query.Include(message => message.Application).Include(message => message.Incident)
            .OrderByDescending(message => message.ReceivedAtUtc).ThenByDescending(message => message.Id)
            .Skip((filter.Page - 1) * 20).Take(21).ToListAsync();
        model.Messages = messages.Take(20).ToList();
        model.HasNextPage = messages.Count > 20;
        return this.StackView(model);
    }

    private static IQueryable<ReportedMessage> FilterMessages(IQueryable<ReportedMessage> query, AlertFilter filter)
    {
        if (filter.ApplicationId.HasValue)
            query = query.Where(message => message.ApplicationId == filter.ApplicationId.Value);
        if (filter.Level != null)
            query = query.Where(message => message.Level == filter.Level);
        if (filter.Keyword != null)
        {
            var keyword = filter.Keyword.ToLowerInvariant();
            query = query.Where(message => message.Title.ToLower().Contains(keyword) ||
                (message.Content != null && message.Content.ToLower().Contains(keyword)));
        }
        if (filter.FromDate.HasValue)
        {
            var start = filter.FromDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(message => message.ReceivedAtUtc >= start);
        }
        if (filter.ToDate.HasValue && filter.ToDate.Value < DateOnly.MaxValue)
        {
            var end = filter.ToDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(message => message.ReceivedAtUtc < end);
        }
        return query;
    }

    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "告警中心", CascadedLinksIcon = "bell", CascadedLinksOrder = 1,
        LinkText = "接入应用", LinkOrder = 1)]
    public async Task<IActionResult> Applications(string? keyword)
    {
        keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        var model = await ApplicationsModel(keyword);
        model.ApiKey = TempData[CreatedApplicationApiKey] as string;
        model.ApiKeyWasReset = TempData[ApiKeyWasReset] is true;
        return this.StackView(model);
    }

    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "告警中心", CascadedLinksIcon = "bell", CascadedLinksOrder = 1,
        LinkText = "接入指南", LinkOrder = 2)]
    public IActionResult GettingStarted() => this.StackView(new AlertsViewModel { PageTitle = "接入指南" });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateApplication(CreateApplicationRequest request)
    {
        if (!ModelState.IsValid) return this.StackView(await ApplicationsModel(), "Applications");
        var application = new SourceApplication { Name = request.Name.Trim() };
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        application.SecretHash = new PasswordHasher<SourceApplication>().HashPassword(application, secret);
        db.SourceApplications.Add(application);
        await db.SaveChangesAsync();
        TempData[CreatedApplicationApiKey] = $"{application.Id}.{secret}";
        TempData[ApiKeyWasReset] = false;
        return RedirectToAction(nameof(Applications));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetApiKey(Guid id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || id == Guid.Empty) return BadRequest("请选择有效的应用。");
        var application = await db.SourceApplications.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (application == null) return NotFound();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        application.SecretHash = new PasswordHasher<SourceApplication>().HashPassword(application, secret);
        await db.SaveChangesAsync(cancellationToken);
        TempData[CreatedApplicationApiKey] = $"{application.Id}.{secret}";
        TempData[ApiKeyWasReset] = true;
        return RedirectToAction(nameof(Applications));
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

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteApplication(Guid id, string? confirmationName, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var applicationName = await db.SourceApplications.AsNoTracking()
            .Where(application => application.Id == id)
            .Select(application => application.Name).SingleOrDefaultAsync(cancellationToken);
        if (applicationName == null) return NotFound();
        if (!ModelState.IsValid || string.IsNullOrEmpty(confirmationName) ||
            !string.Equals(applicationName, confirmationName, StringComparison.Ordinal))
        {
            return BadRequest("请输入完全一致的应用名称以确认删除。");
        }
        // 在同一事务中批量删除历史消息和应用，避免部分删除或逐条加载消息。
        await db.Incidents.Where(incident => incident.ApplicationId == id)
            .ExecuteDeleteAsync(cancellationToken);
        await db.ReportedMessages.Where(message => message.ApplicationId == id)
            .ExecuteDeleteAsync(cancellationToken);
        var deleted = await db.SourceApplications.Where(application => application.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted == 0 ? NotFound() : RedirectToAction(nameof(Applications));
    }

    public async Task<IActionResult> Details(Guid id)
    {
        var message = await db.ReportedMessages.AsNoTracking().Include(m => m.Application).Include(m => m.Incident)
            .SingleOrDefaultAsync(m => m.Id == id);
        if (message == null) return NotFound();
        return this.StackView(new AlertsViewModel { Message = message });
    }

    private async Task<AlertsViewModel> ApplicationsModel(string? keyword = null)
    {
        var applications = db.SourceApplications.AsNoTracking();
        if (!string.IsNullOrEmpty(keyword)) applications = applications.Where(application => application.Name.Contains(keyword));
        return new AlertsViewModel
        {
            Applications = await applications.OrderByDescending(application => application.CreatedAtUtc).ToListAsync(),
            ApplicationSearchKeyword = keyword
        };
    }
}

using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.WebTools.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[LimitPerMin(8)]
public class SearchController(
    TemplateDbContext db,
    IApplicationAccessService applicationAccess,
    IncidentVectorSearchService vectorSearch,
    IStringLocalizer<SearchController> localizer) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string? q, int page = 1, CancellationToken cancellationToken = default)
    {
        var model = new IncidentSearchViewModel
        {
            Query = q?.Trim() ?? string.Empty,
            Page = Math.Clamp(page, 1, 1000000)
        };
        if (model.Query.Length > 200)
            ModelState.AddModelError(nameof(q), localizer["Search queries must not exceed 200 characters."].Value);
        if (!ModelState.IsValid || model.Query.Length == 0)
            return this.StackView(model);

        // 告警搜索必须同时遵守应用访问权限和严重级别可见性策略。
        var applicationIds = await applicationAccess.GetAccessibleApplicationIdsAsync(User, cancellationToken: cancellationToken);
        var policy = await new SeverityPolicyStore(db).LoadAsync(cancellationToken);
        var baseQuery = db.Incidents.AsNoTracking().Include(incident => incident.Application)
            .Where(incident => applicationIds.Contains(incident.ApplicationId)).Where(policy.VisibleIncidents);
        var (usedAi, results, totalCount) = await vectorSearch.SearchAsync(
            baseQuery, model.Query, model.Page, IncidentSearchViewModel.PageSize, cancellationToken);
        model.UsedAiSearch = usedAi;
        if (usedAi)
        {
            model.Incidents = results;
            model.TotalCount = totalCount;
        }
        else
        {
            var keyword = model.Query.ToLowerInvariant();
            var matches = baseQuery.Where(incident => incident.Title.ToLower().Contains(keyword) ||
                (incident.Content != null && incident.Content.ToLower().Contains(keyword)) ||
                incident.Application.Name.ToLower().Contains(keyword));
            model.TotalCount = await matches.CountAsync(cancellationToken);
            model.Incidents = await matches.OrderByDescending(incident => incident.ReceivedAtUtc)
                .Skip((model.Page - 1) * IncidentSearchViewModel.PageSize).Take(IncidentSearchViewModel.PageSize)
                .ToListAsync(cancellationToken);
        }
        return this.StackView(model);
    }
}

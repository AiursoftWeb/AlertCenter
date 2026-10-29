using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Aiursoft.WebTools.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[LimitPerMin(8)]
[ServiceFilter(typeof(IncidentSearchRequestFilter))]
public class SearchController(IncidentSearchService searchService, IStringLocalizer<SearchController> localizer) : Controller
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

        return this.StackView(await searchService.SearchAsync(User, model.Query, model.Page, cancellationToken));
    }
}

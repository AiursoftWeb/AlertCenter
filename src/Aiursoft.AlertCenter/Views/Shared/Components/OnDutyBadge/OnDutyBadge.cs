using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Views.Shared.Components.OnDutyBadge;

public class OnDutyBadge(TemplateDbContext db) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var userId = UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
        var now = DateTime.UtcNow;
        var active = userId != null && await db.OnCallShifts.AsNoTracking().AnyAsync(shift =>
            shift.StartsAtUtc <= now && shift.EndsAtUtc > now && shift.Members.Any(member => member.UserId == userId), HttpContext.RequestAborted);
        return View(active);
    }
}

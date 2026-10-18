using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models.DashboardViewModels;
using Aiursoft.AlertCenter.Services;
using Aiursoft.UiStack.Navigation;
using Aiursoft.WebTools.Attributes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Controllers;

[LimitPerMin]
public class DashboardController(TemplateDbContext db, IApplicationAccessService applicationAccess) : Controller
{
    [RenderInNavBar(
        NavGroupName = "Features",
        NavGroupOrder = 1,
        CascadedLinksGroupName = "Home",
        CascadedLinksIcon = "home",
        CascadedLinksOrder = 0,
        LinkText = "Index",
        LinkOrder = 1)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var model = new IndexViewModel
        {
            IsAuthenticated = User.Identity?.IsAuthenticated == true,
            CanManageApplications = applicationAccess.CanManageApplications(User)
        };

        if (model.IsAuthenticated)
        {
            var accessibleIds = await applicationAccess.GetAccessibleApplicationIdsAsync(User, cancellationToken: cancellationToken);
            model.TotalApplications = await db.SourceApplications.AsNoTracking()
                .CountAsync(a => accessibleIds.Contains(a.Id), cancellationToken);
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var messagesQuery = db.Incidents.AsNoTracking().Where(incident => accessibleIds.Contains(incident.ApplicationId));
            model.TotalIncidents = await messagesQuery.CountAsync(cancellationToken);
            model.Sev0Count = await messagesQuery.CountAsync(m => m.Level == "sev0", cancellationToken);
            model.Sev1Count = await messagesQuery.CountAsync(m => m.Level == "sev1", cancellationToken);
            model.Sev2Count = await messagesQuery.CountAsync(m => m.Level == "sev2", cancellationToken);
            model.Sev3Count = await messagesQuery.CountAsync(m => m.Level == "sev3", cancellationToken);
            model.Sev4Count = await messagesQuery.CountAsync(m => m.Level == "sev4", cancellationToken);

            model.ActiveIncidents = await messagesQuery
                .CountAsync(i => i.Status != Incident.Resolved, cancellationToken);

            var deliveriesQuery = db.PushDeliveries.AsNoTracking()
                .Where(d => d.Incident != null && accessibleIds.Contains(d.Incident.ApplicationId));
            model.TotalDeliveries = await deliveriesQuery.CountAsync(cancellationToken);
            model.SuccessfulDeliveries = await deliveriesQuery.CountAsync(d => d.Status == PushDelivery.Succeeded, cancellationToken);

            if (userId != null)
            {
                model.UnreadNotifications = await db.InAppNotifications.AsNoTracking()
                    .CountAsync(n => n.UserId == userId && n.ReadAtUtc == null, cancellationToken);
            }

            model.ChannelsCount = await db.PushChannels.AsNoTracking().CountAsync(c => c.IsEnabled, cancellationToken);

            model.LatestIncidents = await messagesQuery
                .Include(m => m.Application)
                .OrderByDescending(m => m.ReceivedAtUtc)
                .Take(6)
                .ToListAsync(cancellationToken);

            model.RecentIncidents = await messagesQuery
                .Where(i => i.Status != Incident.Resolved)
                .Include(i => i.Application)
                .OrderByDescending(i => i.ReceivedAtUtc)
                .Take(4)
                .ToListAsync(cancellationToken);
        }
        else
        {
            model.TotalApplications = await db.SourceApplications.AsNoTracking().CountAsync(cancellationToken);
            model.TotalIncidents = await db.Incidents.AsNoTracking().CountAsync(cancellationToken);
            model.ActiveIncidents = await db.Incidents.AsNoTracking().CountAsync(i => i.Status != Incident.Resolved, cancellationToken);
            model.ChannelsCount = await db.PushChannels.AsNoTracking().CountAsync(c => c.IsEnabled, cancellationToken);
        }

        return this.StackView(model);
    }
}

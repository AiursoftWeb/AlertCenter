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
            IsAdministrator = applicationAccess.IsAdministrator(User)
        };

        if (model.IsAuthenticated)
        {
            var accessibleIds = await applicationAccess.GetAccessibleApplicationIdsAsync(User, cancellationToken: cancellationToken);
            model.TotalApplications = await db.SourceApplications.AsNoTracking()
                .CountAsync(a => accessibleIds.Contains(a.Id), cancellationToken);

            var messagesQuery = db.ReportedMessages.AsNoTracking().Where(m => accessibleIds.Contains(m.ApplicationId));
            model.TotalMessages = await messagesQuery.CountAsync(cancellationToken);
            model.Sev1Count = await messagesQuery.CountAsync(m => m.Level == "sev1", cancellationToken);
            model.Sev2Count = await messagesQuery.CountAsync(m => m.Level == "sev2", cancellationToken);
            model.Sev3Count = await messagesQuery.CountAsync(m => m.Level == "sev3", cancellationToken);
            model.Sev4Count = await messagesQuery.CountAsync(m => m.Level == "sev4", cancellationToken);

            model.ActiveIncidents = await db.Incidents.AsNoTracking()
                .CountAsync(i => accessibleIds.Contains(i.ApplicationId) && i.Status != Incident.Resolved, cancellationToken);

            var deliveriesQuery = db.PushDeliveries.AsNoTracking()
                .Where(d => d.Message != null && accessibleIds.Contains(d.Message.ApplicationId));
            model.TotalDeliveries = await deliveriesQuery.CountAsync(cancellationToken);
            model.SuccessfulDeliveries = await deliveriesQuery.CountAsync(d => d.Status == PushDelivery.Succeeded, cancellationToken);

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId != null)
            {
                model.UnreadNotifications = await db.InAppNotifications.AsNoTracking()
                    .CountAsync(n => n.UserId == userId && n.ReadAtUtc == null, cancellationToken);
            }

            model.ChannelsCount = await db.PushChannels.AsNoTracking().CountAsync(c => c.IsEnabled, cancellationToken);

            model.RecentMessages = await messagesQuery
                .Include(m => m.Application)
                .Include(m => m.Incident)
                .OrderByDescending(m => m.ReceivedAtUtc)
                .Take(6)
                .ToListAsync(cancellationToken);

            model.RecentIncidents = await db.Incidents.AsNoTracking()
                .Where(i => accessibleIds.Contains(i.ApplicationId) && i.Status != Incident.Resolved)
                .Include(i => i.Application)
                .OrderByDescending(i => i.TriggeredAtUtc)
                .Take(4)
                .ToListAsync(cancellationToken);
        }
        else
        {
            model.TotalApplications = await db.SourceApplications.AsNoTracking().CountAsync(cancellationToken);
            model.TotalMessages = await db.ReportedMessages.AsNoTracking().CountAsync(cancellationToken);
            model.ActiveIncidents = await db.Incidents.AsNoTracking().CountAsync(i => i.Status != Incident.Resolved, cancellationToken);
            model.ChannelsCount = await db.PushChannels.AsNoTracking().CountAsync(c => c.IsEnabled, cancellationToken);
        }

        return this.StackView(model);
    }
}

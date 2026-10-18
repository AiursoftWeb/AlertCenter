using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;

namespace Aiursoft.AlertCenter.Services;

public class IncidentAccessService(IApplicationAccessService applicationAccess)
{
    public Task<bool> CanManageAsync(ClaimsPrincipal user, Incident incident, DateTime atUtc,
        CancellationToken cancellationToken = default) =>
        applicationAccess.CanAccessAsync(user, incident.ApplicationId, ApplicationPermission.UpdateIncidents, cancellationToken);

    public Task<bool> CanViewAsync(ClaimsPrincipal user, Incident incident, DateTime atUtc,
        CancellationToken cancellationToken = default) =>
        applicationAccess.CanAccessAsync(user, incident.ApplicationId, cancellationToken: cancellationToken);
}

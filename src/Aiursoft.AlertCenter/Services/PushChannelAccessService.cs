using System.Security.Claims;
using Aiursoft.AlertCenter.Authorization;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public interface IPushChannelAccessService
{
    Task<bool> CanBindAsync(ClaimsPrincipal user, Guid channelId, CancellationToken cancellationToken = default);
    Task<List<Guid>> GetBindableChannelIdsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
}

public sealed class PushChannelAccessService(TemplateDbContext db)
    : IPushChannelAccessService
{
    public async Task<bool> CanBindAsync(ClaimsPrincipal user, Guid channelId, CancellationToken cancellationToken = default)
    {
        if (user.HasClaim(AppPermissions.Type, AppPermissionNames.CanManagePush)) return true;
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return false;
        if (await db.PushChannelBindingUsers.AnyAsync(link =>
                link.ChannelId == channelId && link.UserId == userId, cancellationToken))
            return true;
        return await db.PushChannelBindingRoles.AnyAsync(link => link.ChannelId == channelId &&
            db.UserRoles.Any(membership => membership.UserId == userId && membership.RoleId == link.RoleId),
            cancellationToken);
    }

    public async Task<List<Guid>> GetBindableChannelIdsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        if (user.HasClaim(AppPermissions.Type, AppPermissionNames.CanManagePush))
            return await db.PushChannels.Select(channel => channel.Id).ToListAsync(cancellationToken);
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return [];
        var direct = db.PushChannelBindingUsers.Where(link => link.UserId == userId).Select(link => link.ChannelId);
        var inherited = from link in db.PushChannelBindingRoles
            join membership in db.UserRoles on link.RoleId equals membership.RoleId
            where membership.UserId == userId
            select link.ChannelId;
        return await direct.Concat(inherited).Distinct().ToListAsync(cancellationToken);
    }
}

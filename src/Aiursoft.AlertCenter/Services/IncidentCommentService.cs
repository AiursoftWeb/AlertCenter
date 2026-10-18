using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public sealed record IncidentCommentResult(int StatusCode, string? Error = null);

public class IncidentCommentService(TemplateDbContext db, IApplicationAccessService applicationAccess, IStringLocalizer<IncidentCommentService> localizer)
{
    public async Task<IncidentCommentResult> AddAsync(ClaimsPrincipal user, AddIncidentCommentRequest request,
        CancellationToken cancellationToken = default)
    {
        var incident = await db.Incidents.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.IncidentId, cancellationToken);
        if (incident == null) return new(404);
        if (!await new IncidentAccessService(applicationAccess).CanManageAsync(user, incident, DateTime.UtcNow, cancellationToken)) return new(403);
        if (string.IsNullOrWhiteSpace(request.Markdown) || request.Markdown.Length > 4000)
            return new(400, localizer["Comment cannot be empty and must not exceed 4,000 characters."].Value);
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var author = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (author == null) return new(403);
        db.IncidentComments.Add(new IncidentComment
        {
            IncidentId = incident.Id, AuthorId = author.Id, AuthorName = author.DisplayName, Markdown = request.Markdown
        });
        await db.SaveChangesAsync(cancellationToken);
        return new(200);
    }

    public async Task<IncidentCommentResult> DeleteAsync(ClaimsPrincipal user, DeleteIncidentCommentRequest request,
        CancellationToken cancellationToken = default)
    {
        var comment = await db.IncidentComments.Include(item => item.Incident)
            .SingleOrDefaultAsync(item => item.Id == request.CommentId && item.IncidentId == request.IncidentId, cancellationToken);
        if (comment == null) return new(404);
        if (!await new IncidentAccessService(applicationAccess).CanManageAsync(user, comment.Incident, DateTime.UtcNow, cancellationToken)) return new(403);
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return new(403);
        if (comment.DeletedAtUtc.HasValue) return new(409, localizer["This comment has been deleted. Please refresh the page."].Value);
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (actor == null) return new(403);
        comment.DeletedAtUtc = DateTime.UtcNow;
        comment.DeletedById = actor.Id;
        comment.DeletedByName = actor.DisplayName;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new(409, localizer["This comment was deleted by another user. Please refresh the page."].Value);
        }
        return new(200);
    }
}

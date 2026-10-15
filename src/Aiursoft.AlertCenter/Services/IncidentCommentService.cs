using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Services;

public sealed record IncidentCommentResult(int StatusCode, string? Error = null);

public class IncidentCommentService(TemplateDbContext db, IApplicationAccessService applicationAccess)
{
    public async Task<IncidentCommentResult> AddAsync(ClaimsPrincipal user, AddIncidentCommentRequest request,
        CancellationToken cancellationToken = default)
    {
        var incident = await db.Incidents.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.IncidentId, cancellationToken);
        if (incident == null) return new(404);
        if (!await new IncidentAccessService(db, applicationAccess).CanViewAsync(user, incident, DateTime.UtcNow, cancellationToken)) return new(403);
        if (string.IsNullOrWhiteSpace(request.Markdown) || request.Markdown.Length > 4000)
            return new(400, "评论不能为空，且不能超过 4000 个字符。");
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
        if (!await new IncidentAccessService(db, applicationAccess).CanViewAsync(user, comment.Incident, DateTime.UtcNow, cancellationToken)) return new(403);
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null || (comment.AuthorId != userId &&
            !await applicationAccess.CanAccessAsync(user, comment.Incident.ApplicationId, true, cancellationToken))) return new(403);
        if (comment.DeletedAtUtc.HasValue) return new(409, "评论已被删除，请刷新页面。");
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
            return new(409, "评论已被其他用户删除，请刷新页面。");
        }
        return new(200);
    }
}

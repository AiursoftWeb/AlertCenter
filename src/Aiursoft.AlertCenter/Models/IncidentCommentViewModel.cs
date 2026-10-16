using System.ComponentModel.DataAnnotations;
using Aiursoft.AlertCenter.Entities;

namespace Aiursoft.AlertCenter.Models;

public class IncidentCommentViewModel
{
    public Guid Id { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public string AuthorAvatarRelativePath { get; set; } = User.DefaultAvatarPath;
    public DateTime CreatedAtUtc { get; set; }
    public string? Markdown { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedByName { get; set; }
    public bool CanDelete { get; set; }
}

public class AddIncidentCommentRequest
{
    public Guid IncidentId { get; set; }
    [Required(ErrorMessage = "Comment cannot be empty."), StringLength(4000, ErrorMessage = "Comment must not exceed 4,000 characters.")]
    public string Markdown { get; set; } = string.Empty;
}

public class DeleteIncidentCommentRequest
{
    public Guid IncidentId { get; set; }
    public Guid CommentId { get; set; }
    public int CommentsPage { get; set; } = 1;
}

using Aiursoft.AiurProtocol.Models;

namespace Aiursoft.AlertCenter.SDK.Models;

public sealed class CreateIncidentCommentResponse : AiurResponse
{
    public Guid IncidentId { get; set; }
    public Guid CommentId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

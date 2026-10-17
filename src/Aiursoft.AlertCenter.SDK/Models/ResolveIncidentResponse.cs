using Aiursoft.AiurProtocol.Models;

namespace Aiursoft.AlertCenter.SDK.Models;

public sealed class ResolveIncidentResponse : AiurResponse
{
    public Guid IncidentId { get; set; }
    public Guid Version { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
}

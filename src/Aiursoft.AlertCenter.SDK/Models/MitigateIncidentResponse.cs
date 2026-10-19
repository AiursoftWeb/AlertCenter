using Aiursoft.AiurProtocol.Models;

namespace Aiursoft.AlertCenter.SDK.Models;

public sealed class MitigateIncidentResponse : AiurResponse
{
    public Guid IncidentId { get; set; }
    public Guid Version { get; set; }
    public DateTimeOffset? MitigatedAtUtc { get; set; }
}

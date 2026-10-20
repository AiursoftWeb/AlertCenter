using Aiursoft.AiurProtocol.Models;

namespace Aiursoft.AlertCenter.SDK.Models;

public sealed class ReopenIncidentResponse : AiurResponse
{
    public Guid IncidentId { get; set; }
    public Guid Version { get; set; }
}

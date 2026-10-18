using Aiursoft.AiurProtocol.Models;

namespace Aiursoft.AlertCenter.SDK.Models;

public sealed class ChangeIncidentSeverityResponse : AiurResponse
{
    public Guid IncidentId { get; set; }
    public Guid Version { get; set; }
    public string Level { get; set; } = string.Empty;
}

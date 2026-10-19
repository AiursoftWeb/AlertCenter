using Aiursoft.AiurProtocol.Models;

namespace Aiursoft.AlertCenter.SDK.Models;

public sealed class GetIncidentResponse : AiurResponse
{
    public IncidentDetails Incident { get; set; } = new();
}

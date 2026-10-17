using Aiursoft.AiurProtocol.Models;

namespace Aiursoft.AlertCenter.SDK.Models;

public sealed class CreateIncidentResponse : AiurResponse
{
    public Guid ReportId { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public Guid IncidentId { get; set; }
    public bool Duplicate { get; set; }
}

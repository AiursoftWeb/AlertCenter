using Aiursoft.AiurProtocol.Models;

namespace Aiursoft.AlertCenter.SDK.Models;

public sealed class ReportMessageResponse : AiurResponse
{
    public Guid Id { get; set; }
    public Guid ReportId { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public Guid? IncidentId { get; set; }
    public bool Archived { get; set; }
    public bool Duplicate { get; set; }
}

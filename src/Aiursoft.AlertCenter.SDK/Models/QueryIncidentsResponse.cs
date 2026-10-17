using Aiursoft.AiurProtocol.Models;
using Newtonsoft.Json.Linq;

namespace Aiursoft.AlertCenter.SDK.Models;

public sealed class QueryIncidentsResponse : AiurResponse
{
    public List<IncidentDetails> Items { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
}

public sealed class IncidentDetails
{
    public Guid IncidentId { get; set; }
    public Guid ReportId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Content { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public JObject? Context { get; set; }
}

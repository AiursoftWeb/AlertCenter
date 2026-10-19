using System.CommandLine;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.SDK;
using Aiursoft.AlertCenter.SDK.Models;
using Newtonsoft.Json.Linq;

namespace Aiursoft.AlertCenter.Client;

public sealed class CreateHandler : IncidentCommandHandler
{
    protected override string Name => "create";
    protected override string Description => "Report an incident. Retain the report ID and occurrence time when retrying.";
    private readonly Option<Guid> _reportId = new("--report-id") { Required = true, Description = "Stable report ID for idempotent retries." };
    private readonly Option<string> _title = new("--title") { Required = true };
    private readonly Option<string> _level = new("--level") { Required = true, Description = "Severity: sev0 through sev4." };
    private readonly Option<DateTimeOffset> _occurredAt = new("--occurred-at") { Required = true, Description = "Occurrence time in ISO 8601 with a UTC offset." };
    private readonly Option<string?> _content = new("--content") { Description = "Markdown incident content." };
    private readonly Option<string?> _context = new("--context") { Description = "Context as a JSON object." };
    private readonly Option<string?> _metadata = new("--metadata") { Description = "Initial incident metadata as a JSON object." };
    protected override IEnumerable<Option> IncidentOptions() => [_reportId, _title, _level, _occurredAt, _content, _context, _metadata];
    protected override async Task<AiurResponse> InvokeAsync(AlertCenterApiClient client, ParseResult context)
    {
        var json = context.GetValue(_context);
        return await client.CreateIncidentAsync(new CreateIncidentRequest
        {
            ReportId = context.GetValue(_reportId), Title = context.GetValue(_title)!, Level = context.GetValue(_level)!,
            OccurredAtUtc = context.GetValue(_occurredAt), Content = context.GetValue(_content),
            Context = json == null ? null : JObject.Parse(json),
            Metadata = context.GetValue(_metadata) is { } metadata ? IncidentMetadata.Read(metadata) : null
        });
    }
}

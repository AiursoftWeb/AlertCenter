using System.CommandLine;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.SDK;
using Aiursoft.AlertCenter.SDK.Models;

namespace Aiursoft.AlertCenter.Client;

public sealed class SeverityHandler : IncidentCommandHandler
{
    protected override string Name => "severity";
    protected override string Description => "Change an active incident severity.";
    private readonly Option<Guid> _id = new("--id") { Required = true, Description = "Incident ID." };
    private readonly Option<Guid> _version = new("--version") { Required = true, Description = "Incident Version returned by list or get." };
    private readonly Option<string> _reason = new("--reason") { Required = true };
    private readonly Option<string> _level = new("--level") { Required = true, Description = "Severity: sev0 through sev4." };
    protected override IEnumerable<Option> IncidentOptions() => [_id, _version, _reason, _level];
    protected override async Task<AiurResponse> InvokeAsync(AlertCenterApiClient client, ParseResult context) =>
        await client.ChangeIncidentSeverityAsync(context.GetValue(_id), new ChangeIncidentSeverityRequest
        {
            Level = context.GetValue(_level)!,
            Version = context.GetValue(_version), Reason = context.GetValue(_reason)!
        });
}

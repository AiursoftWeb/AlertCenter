using System.CommandLine;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.SDK;
using Aiursoft.AlertCenter.SDK.Models;

namespace Aiursoft.AlertCenter.Client;

public sealed class MitigateHandler : IncidentCommandHandler
{
    protected override string Name => "mitigate";
    protected override string Description => "Mitigate an incident after confirming recovery.";
    private readonly Option<Guid> _id = new("--id") { Required = true, Description = "Incident ID." };
    private readonly Option<Guid> _version = new("--version") { Required = true, Description = "Incident Version returned by list or get." };
    private readonly Option<string> _reason = new("--reason") { Required = true };
    protected override IEnumerable<Option> IncidentOptions() => [_id, _version, _reason];
    protected override async Task<AiurResponse> InvokeAsync(AlertCenterApiClient client, ParseResult context) =>
        await client.MitigateIncidentAsync(context.GetValue(_id), new MitigateIncidentRequest
        {
            Version = context.GetValue(_version), Reason = context.GetValue(_reason)!
        });
}

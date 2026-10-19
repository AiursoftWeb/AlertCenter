using System.CommandLine;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.SDK;

namespace Aiursoft.AlertCenter.Client;

public sealed class GetHandler : IncidentCommandHandler
{
    protected override string Name => "get";
    protected override string Description => "Get one incident belonging to the API key's application.";
    private readonly Option<Guid> _id = new("--id") { Required = true, Description = "Incident ID." };
    protected override IEnumerable<Option> IncidentOptions() => [_id];
    protected override async Task<AiurResponse> InvokeAsync(AlertCenterApiClient client, ParseResult context) =>
        await client.GetIncidentAsync(context.GetValue(_id));
}

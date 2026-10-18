using System.CommandLine;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.SDK;
using Aiursoft.AlertCenter.SDK.Models;

namespace Aiursoft.AlertCenter.Client;

public sealed class ListHandler : IncidentCommandHandler
{
    protected override string Name => "list";
    protected override string Description => "Query incidents belonging to the API key's application.";
    private readonly Option<int> _page = new("--page") { DefaultValueFactory = _ => 1 };
    private readonly Option<int> _pageSize = new("--page-size") { DefaultValueFactory = _ => 50 };
    private readonly Option<bool> _activeOnly = new("--active-only") { Description = "Exclude resolved incidents." };
    protected override IEnumerable<Option> IncidentOptions() => [_page, _pageSize, _activeOnly];
    protected override async Task<AiurResponse> InvokeAsync(AlertCenterApiClient client, ParseResult context) =>
        await client.QueryIncidentsAsync(new QueryIncidentsRequest
        {
            Page = context.GetValue(_page), PageSize = context.GetValue(_pageSize), ActiveOnly = context.GetValue(_activeOnly)
        });
}

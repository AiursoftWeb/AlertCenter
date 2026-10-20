using System.CommandLine;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.SDK;
using Aiursoft.AlertCenter.SDK.Models;

namespace Aiursoft.AlertCenter.Client;

public sealed class CommentHandler : IncidentCommandHandler
{
    protected override string Name => "comment";
    protected override string Description => "Append a Markdown comment without changing the incident state or sending notifications.";
    private readonly Option<Guid> _id = new("--id") { Required = true, Description = "Incident ID." };
    private readonly Option<string> _markdown = new("--markdown") { Required = true, Description = "Comment Markdown, up to 4,000 characters." };
    protected override IEnumerable<Option> IncidentOptions() => [_id, _markdown];
    protected override async Task<AiurResponse> InvokeAsync(AlertCenterApiClient client, ParseResult context) =>
        await client.CreateIncidentCommentAsync(context.GetValue(_id), new CreateIncidentCommentRequest
        {
            Markdown = context.GetValue(_markdown)!
        });
}

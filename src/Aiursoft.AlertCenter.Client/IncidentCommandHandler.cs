using System.CommandLine;
using Aiursoft.AiurProtocol;
using Aiursoft.AiurProtocol.Exceptions;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.SDK;
using Aiursoft.AlertCenter.SDK.Models;
using Aiursoft.CommandFramework.Framework;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

namespace Aiursoft.AlertCenter.Client;

public abstract class IncidentCommandHandler : ExecutableCommandHandlerBuilder
{
    private readonly Option<string> _server = new("--server")
    {
        Description = "AlertCenter server base URL.", Required = true
    };
    private readonly Option<string> _apiKey = new("--api-key")
    {
        Description = "Application API key for this invocation. Never saved.", Required = true
    };
    private readonly Option<bool> _json = new("--json")
    {
        Description = "Write the complete AiurProtocol response as JSON."
    };

    protected override IEnumerable<Option> GetCommandOptions() => [_server, _apiKey, _json, .. IncidentOptions()];
    protected abstract IEnumerable<Option> IncidentOptions();
    protected abstract Task<AiurResponse> InvokeAsync(AlertCenterApiClient client, ParseResult context);

    protected override async Task Execute(ParseResult context)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlertCenterSdk(context.GetValue(_server)!, context.GetValue(_apiKey)!);
        // Match the S360 CLI: credentials must not follow HTTP redirects.
        services.AddHttpClient(string.Empty).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false
        });
        using var provider = services.BuildServiceProvider();
        AiurResponse response;
        try
        {
            response = await InvokeAsync(provider.GetRequiredService<AlertCenterApiClient>(), context);
        }
        catch (AiurUnexpectedServerResponseException exception)
        {
            var reasons = exception is AiurBadApiInputException input ? " " + string.Join("; ", input.Reasons) : string.Empty;
            throw new InvalidOperationException($"{exception.Response.Code} ({(int)exception.Response.Code}): {exception.Message}{reasons}");
        }
        var output = context.InvocationConfiguration.Output;
        if (context.GetValue(_json))
        {
            output.WriteLine(JsonConvert.SerializeObject(response, Formatting.Indented, ProtocolSettings.JsonSettings));
            return;
        }
        output.WriteLine(response.Message);
        switch (response)
        {
            case QueryIncidentsResponse list:
                output.WriteLine($"Page {list.Page}, page size {list.PageSize}, total {list.TotalCount}");
                foreach (var item in list.Items)
                    output.WriteLine($"{item.IncidentId}  {item.Level}  {item.Status}  {item.Title}  Version: {item.Version}");
                break;
            case GetIncidentResponse details:
                var incident = details.Incident;
                output.WriteLine($"{incident.IncidentId}  {incident.Level}  {incident.Status}  {incident.Title}");
                output.WriteLine($"Version: {incident.Version}\nReport ID: {incident.ReportId}");
                output.WriteLine($"Occurred: {incident.OccurredAtUtc:O}\nReceived: {incident.ReceivedAtUtc:O}");
                output.WriteLine(incident.Content);
                if (incident.Context != null) output.WriteLine(incident.Context.ToString(Formatting.Indented));
                output.WriteLine("Metadata:");
                output.WriteLine(incident.Metadata.ToString(Formatting.Indented));
                break;
            case PatchIncidentMetadataResponse patched:
                output.WriteLine($"Incident: {patched.IncidentId}\nVersion: {patched.Version}");
                output.WriteLine(patched.Metadata.ToString(Formatting.Indented));
                break;
            case CreateIncidentCommentResponse commented:
                output.WriteLine($"Incident: {commented.IncidentId}\nComment: {commented.CommentId}\nCreated: {commented.CreatedAtUtc:O}");
                break;
            case CreateIncidentResponse created:
                output.WriteLine($"Incident: {created.IncidentId}\nReport ID: {created.ReportId}\nDuplicate: {created.Duplicate}");
                break;
            case MitigateIncidentResponse mitigated:
                output.WriteLine($"Incident: {mitigated.IncidentId}\nVersion: {mitigated.Version}\nMitigated: {mitigated.MitigatedAtUtc:O}");
                break;
            case ResolveIncidentResponse resolved:
                output.WriteLine($"Incident: {resolved.IncidentId}\nVersion: {resolved.Version}\nResolved: {resolved.ResolvedAtUtc:O}");
                break;
            case ChangeIncidentSeverityResponse changed:
                output.WriteLine($"Incident: {changed.IncidentId}\nVersion: {changed.Version}\nSeverity: {changed.Level}");
                break;
        }
    }
}

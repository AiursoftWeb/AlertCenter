using System.CommandLine;
using Aiursoft.AiurProtocol;
using Aiursoft.AiurProtocol.Exceptions;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AiurProtocol.Services;
using Aiursoft.AlertCenter.SDK;
using Aiursoft.AlertCenter.SDK.Models;
using Aiursoft.CommandFramework.Framework;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

namespace Aiursoft.AlertCenter.Client;

public sealed class PersonalHandler : NavigationCommandHandlerBuilder
{
    protected override string Name => "personal";
    protected override string Description => "View and handle incidents across applications with a personal API key.";
    protected override CommandHandlerBuilder[] GetSubCommandHandlers() =>
        [new PersonalListHandler(), new PersonalGetHandler(), new PersonalActionHandler("ack", "Acknowledge and take ownership."),
            new PersonalActionHandler("mitigate", "Mark an acknowledged incident as mitigated."),
            new PersonalActionHandler("resolve", "Mark a mitigated incident as resolved.")];
}

internal abstract class PersonalCommandHandler : ExecutableCommandHandlerBuilder
{
    private readonly Option<string> _server = new("--server") { Required = true, Description = "AlertCenter server base URL." };
    private readonly Option<string?> _apiKey = new("--api-key") { Description = "Personal key. Defaults to ALERTCENTER_PERSONAL_API_KEY. Never saved." };
    private readonly Option<bool> _json = new("--json") { Description = "Write the complete response as JSON." };

    protected override IEnumerable<Option> GetCommandOptions() => [_server, _apiKey, _json, .. PersonalOptions()];
    protected abstract IEnumerable<Option> PersonalOptions();
    protected abstract Task<AiurResponse> InvokeAsync(AlertCenterPersonalClient client, ParseResult context);

    protected override async Task Execute(ParseResult context)
    {
        var server = context.GetValue(_server)!;
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("--server must be an HTTPS base URL (HTTP is allowed only on loopback), without user info, query or fragment.");
        var key = context.GetValue(_apiKey) ?? Environment.GetEnvironmentVariable("ALERTCENTER_PERSONAL_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Supply a personal key with --api-key or ALERTCENTER_PERSONAL_API_KEY.");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiurProtocolClient();
        services.AddHttpClient(string.Empty).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        using var provider = services.BuildServiceProvider();
        var client = new AlertCenterPersonalClient(provider.GetRequiredService<AiurProtocolClient>(), server, key);
        AiurResponse response;
        try
        {
            response = await InvokeAsync(client, context);
        }
        catch (AiurUnexpectedServerResponseException exception)
        {
            var reasons = exception is AiurBadApiInputException input ? " " + string.Join("; ", input.Reasons) : string.Empty;
            throw new InvalidOperationException($"{exception.Response.Code}: {SafeLine(exception.Message + reasons)}");
        }
        var output = context.InvocationConfiguration.Output;
        if (context.GetValue(_json)) output.WriteLine(JsonConvert.SerializeObject(response, Formatting.Indented, ProtocolSettings.JsonSettings));
        else WriteHuman(output, response);
    }

    private static void WriteHuman(TextWriter output, AiurResponse response)
    {
        switch (response)
        {
            case MobileIncidentsResponse list:
                output.WriteLine($"Page {list.Page}. {(list.Incidents.Count == 0 ? "No matching incidents." : $"{list.Incidents.Count} incidents.")}");
                foreach (var item in list.Incidents)
                    output.WriteLine($"{item.Level}  {item.Status}  {(item.AcknowledgedAtUtc.HasValue ? "ACK" : "---")}  {SafeLine(item.ApplicationName)}  {item.Id}  {SafeLine(item.Title)}");
                if (list.HasNextPage) output.WriteLine($"Next page: --page {list.Page + 1}");
                break;
            case MobileIncidentResponse details:
                var incident = details.Incident;
                output.WriteLine($"{incident.Id}  {incident.Level}  {incident.Status}  {SafeLine(incident.Title)}");
                output.WriteLine($"Application: {SafeLine(incident.ApplicationName)}\nVersion: {incident.Version}\nOccurred: {incident.OccurredAtUtc:O}");
                output.WriteLine($"Owner: {SafeLine(incident.OwnerName)}\nAcknowledged: {incident.AcknowledgedAtUtc:O}\nCan handle: {incident.CanManage}");
                WriteField(output, "Content", incident.Content);
                WriteField(output, "Investigate URL", incident.InvestigateUrl);
                WriteField(output, "Context", incident.ContextJson);
                WriteField(output, "Metadata", incident.MetadataJson);
                output.WriteLine($"Timeline (page {details.Page}):");
                foreach (var entry in details.Activities)
                    output.WriteLine($"  {entry.CreatedAtUtc:O}  {SafeLine(entry.Type)}  {SafeLine(entry.ActorName)}  {SafeLine(entry.Note)}");
                if (details.HasNextPage) output.WriteLine($"Next timeline page: --page {details.Page + 1}");
                break;
            default:
                output.WriteLine(SafeLine(response.Message));
                output.WriteLine("Run personal get to fetch the updated version before another change.");
                break;
        }
    }

    private static void WriteField(TextWriter output, string caption, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) output.WriteLine($"{caption}: {SafeLine(value)}");
    }

    private static string SafeLine(string? value) => value is null ? string.Empty :
        new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
}

internal sealed class PersonalListHandler : PersonalCommandHandler
{
    private readonly Option<int> _page = new("--page") { DefaultValueFactory = _ => 1 };
    private readonly Option<string> _status = new("--status") { DefaultValueFactory = _ => "active", Description = "active (default), all, new, mitigated, or resolved." };
    private readonly Option<Guid?> _applicationId = new("--application-id");
    private readonly Option<string?> _level = new("--level") { Description = "Filter by sev0 through sev4." };
    private readonly Option<string?> _keyword = new("--keyword");
    private readonly Option<bool> _mine = new("--mine") { Description = "Show only incidents assigned to you." };
    private readonly Option<bool> _allVisible = new("--all-visible") { Description = "Also include applications where you only have view access." };
    protected override string Name => "list";
    protected override string Description => "List incidents across applications you can handle.";
    protected override IEnumerable<Option> PersonalOptions() => [_page, _status, _applicationId, _level, _keyword, _mine, _allVisible];
    protected override async Task<AiurResponse> InvokeAsync(AlertCenterPersonalClient client, ParseResult context) =>
        await client.ListAsync(new PersonalIncidentQuery
        {
            Page = context.GetValue(_page), Status = context.GetValue(_status)!, ApplicationId = context.GetValue(_applicationId),
            Level = context.GetValue(_level), Keyword = context.GetValue(_keyword), OnlyMine = context.GetValue(_mine), AllVisible = context.GetValue(_allVisible)
        });
}

internal sealed class PersonalGetHandler : PersonalCommandHandler
{
    private readonly Option<Guid> _id = new("--id") { Required = true, Description = "Incident ID." };
    private readonly Option<int> _page = new("--page") { DefaultValueFactory = _ => 1, Description = "Timeline page." };
    protected override string Name => "get";
    protected override string Description => "Get incident details, current version and timeline.";
    protected override IEnumerable<Option> PersonalOptions() => [_id, _page];
    protected override async Task<AiurResponse> InvokeAsync(AlertCenterPersonalClient client, ParseResult context) =>
        await client.GetAsync(context.GetValue(_id), context.GetValue(_page));
}

internal sealed class PersonalActionHandler(string name, string description) : PersonalCommandHandler
{
    private readonly Option<Guid> _id = new("--id") { Required = true, Description = "Incident ID." };
    private readonly Option<Guid> _version = new("--version") { Required = true, Description = "Current version from personal get." };
    private readonly Option<string?> _reason = new("--reason") { Required = name != "ack", Description = "Reason or progress note." };
    protected override string Name => name;
    protected override string Description => description;
    protected override IEnumerable<Option> PersonalOptions() => [_id, _version, _reason];
    protected override Task<AiurResponse> InvokeAsync(AlertCenterPersonalClient client, ParseResult context)
    {
        var id = context.GetValue(_id);
        var request = new MobileIncidentAction { Version = context.GetValue(_version), Note = context.GetValue(_reason) };
        return name switch
        {
            "ack" => client.AcknowledgeAsync(id, request), "mitigate" => client.MitigateAsync(id, request),
            "resolve" => client.ResolveAsync(id, request), _ => throw new InvalidOperationException("Unknown personal action.")
        };
    }
}

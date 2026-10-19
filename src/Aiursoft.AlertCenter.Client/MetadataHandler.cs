using System.CommandLine;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.SDK;
using Aiursoft.AlertCenter.SDK.Models;
using Aiursoft.CommandFramework.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aiursoft.AlertCenter.Client;

public sealed class MetadataHandler : NavigationCommandHandlerBuilder
{
    protected override string Name => "metadata";
    protected override string Description => "Update incident metadata properties.";
    protected override CommandHandlerBuilder[] GetSubCommandHandlers() => [new MetadataPatchHandler()];
}

public sealed class MetadataPatchHandler : IncidentCommandHandler
{
    protected override string Name => "patch";
    protected override string Description => "Replace one top-level metadata property using the current incident version.";
    private readonly Option<Guid> _id = new("--id") { Required = true, Description = "Incident ID." };
    private readonly Option<Guid> _version = new("--version") { Required = true, Description = "Incident Version returned by list or get." };
    private readonly Option<string> _property = new("--property") { Required = true, Description = "Literal top-level property name, not a JSON path." };
    private readonly Option<string> _value = new("--value") { Required = true, Description = "A JSON value. null sets JSON null; it does not delete the property." };
    protected override IEnumerable<Option> IncidentOptions() => [_id, _version, _property, _value];
    protected override async Task<AiurResponse> InvokeAsync(AlertCenterApiClient client, ParseResult context)
    {
        using var reader = new JsonTextReader(new StringReader(context.GetValue(_value)!));
        reader.DateParseHandling = DateParseHandling.None;
        var value = JToken.Load(reader);
        if (reader.Read()) throw new JsonReaderException("Supply exactly one JSON value.");
        return await client.PatchIncidentMetadataAsync(context.GetValue(_id), new PatchIncidentMetadataRequest
        {
            Version = context.GetValue(_version), Property = context.GetValue(_property)!, Value = value
        });
    }
}

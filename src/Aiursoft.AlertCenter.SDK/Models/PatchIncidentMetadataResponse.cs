using Newtonsoft.Json;
using Aiursoft.AiurProtocol.Models;
using Newtonsoft.Json.Linq;

namespace Aiursoft.AlertCenter.SDK.Models;

public sealed class PatchIncidentMetadataResponse : AiurResponse
{
    public Guid IncidentId { get; set; }
    public Guid Version { get; set; }
    [JsonConverter(typeof(IncidentMetadataConverter))]
    public JObject Metadata { get; set; } = new();
}

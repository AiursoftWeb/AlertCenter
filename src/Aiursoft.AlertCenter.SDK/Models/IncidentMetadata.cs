using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aiursoft.AlertCenter.SDK.Models;

public static class IncidentMetadata
{
    public static string? Validate(JObject metadata)
    {
        var json = metadata.ToString(Formatting.None);
        if (Encoding.UTF8.GetByteCount(json) > 16384)
            return "Incident metadata must not exceed 16 KiB of UTF-8 JSON.";
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (System.Text.Json.JsonException)
        {
            return "Incident metadata must contain valid JSON with at most 16 levels of nesting.";
        }
        foreach (var property in metadata.Properties())
            if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 128 || property.Name.Any(char.IsControl))
                return "Metadata property names must contain 1 to 128 characters without control characters.";
        return null;
    }

    public static JObject Read(string? json)
    {
        if (json == null) return new JObject();
        using var reader = new JsonTextReader(new StringReader(json));
        reader.DateParseHandling = DateParseHandling.None;
        return JObject.Load(reader);
    }
}

public sealed class IncidentMetadataConverter : JsonConverter<JObject>
{
    public override bool CanWrite => false;

    public override JObject? ReadJson(JsonReader reader, Type objectType, JObject? existingValue,
        bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null) return null;
        var previous = reader.DateParseHandling;
        try
        {
            reader.DateParseHandling = DateParseHandling.None;
            return JObject.Load(reader);
        }
        finally
        {
            reader.DateParseHandling = previous;
        }
    }

    public override void WriteJson(JsonWriter writer, JObject? value, JsonSerializer serializer) =>
        throw new NotSupportedException();
}

using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aiursoft.AlertCenter.SDK.Models;

[JsonConverter(typeof(PatchIncidentMetadataRequestConverter))]
public sealed class PatchIncidentMetadataRequest : IValidatableObject
{
    public Guid Version { get; set; }
    [Required, StringLength(128)] public string Property { get; set; } = string.Empty;
    [JsonProperty(Required = Required.AllowNull)] public JToken? Value { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Version == Guid.Empty)
            yield return new ValidationResult("A non-empty incident version is required.", [nameof(Version)]);
        if (!string.IsNullOrWhiteSpace(Property) && IncidentMetadata.Validate(new JObject { [Property] = Value?.DeepClone() ?? JValue.CreateNull() }) is { } error)
            yield return new ValidationResult(error, [nameof(Property), nameof(Value)]);
    }
}

public sealed class PatchIncidentMetadataRequestConverter : JsonConverter<PatchIncidentMetadataRequest>
{
    public override bool CanWrite => false;

    public override PatchIncidentMetadataRequest? ReadJson(JsonReader reader, Type objectType,
        PatchIncidentMetadataRequest? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null) return null;
        var previous = reader.DateParseHandling;
        try
        {
            reader.DateParseHandling = DateParseHandling.None;
            var request = new PatchIncidentMetadataRequest();
            serializer.Populate(reader, request);
            return request;
        }
        finally
        {
            reader.DateParseHandling = previous;
        }
    }

    public override void WriteJson(JsonWriter writer, PatchIncidentMetadataRequest? value, JsonSerializer serializer) =>
        throw new NotSupportedException();
}

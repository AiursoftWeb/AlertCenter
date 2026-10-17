using System.Text.Encodings.Web;
using System.Text.Json;

namespace Aiursoft.AlertCenter.Services;

public static class IncidentContextPresentation
{
    private static readonly JsonSerializerOptions DisplayOptions = new()
    {
        WriteIndented = true,
        // The result is text, encoded by Razor when rendered, never executable markup.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IReadOnlyList<KeyValuePair<string, string>> ScalarFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
            return document.RootElement.EnumerateObject()
                .Where(property => property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                .Select(property => new KeyValuePair<string, string>(property.Name,
                    property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText()))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Format(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, DisplayOptions);
        }
        catch (JsonException)
        {
            // Preserve unexpected stored data for inspection instead of failing the page.
            return json;
        }
    }
}

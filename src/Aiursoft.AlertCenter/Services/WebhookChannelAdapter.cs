using System.Text.Json;
using System.Text.Json.Nodes;
using Aiursoft.AlertCenter.Entities;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public class WebhookChannelAdapter(IHttpClientFactory clients, WebhookConfigurationProtector webhookConfigurationProtector, IStringLocalizer<WebhookChannelAdapter> localizer) : INotificationChannelAdapter
{
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Length", "Transfer-Encoding", "Host", "Connection", "Expect", "TE", "Trailer", "Upgrade",
        "Idempotency-Key"
    };

    private static readonly HashSet<string> PayloadPlaceholders = new(StringComparer.Ordinal)
    {
        "{{deliveryId}}", "{{messageId}}", "{{title}}", "{{content}}", "{{level}}", "{{occurredAtUtc}}", "{{context}}"
    };

    public ChannelTypeDefinition Definition { get; } = new("webhook", "Webhook",
        localizer["Send JSON messages to a specified HTTP address, suitable for connecting to existing systems."].Value, "webhook", true);

    public static bool IsValidEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);

    public static bool TryValidateCustomHeaders(string? customHeadersJson, out string? error, IStringLocalizer? localizer = null) =>
        TryParseCustomHeaders(customHeadersJson, out _, out error, localizer: localizer);

    public static bool TryValidatePayloadTemplate(string? payloadTemplate, out string? error, IStringLocalizer? localizer = null)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(payloadTemplate)) return true;
        try
        {
            var template = JsonNode.Parse(payloadTemplate);
            if (template is not JsonObject)
            {
                error = (localizer is null ? "Custom request body must be a JSON object." : localizer["Custom request body must be a JSON object."].Value);
                return false;
            }
            return TryValidatePlaceholders(template, out error, localizer: localizer);
        }
        catch (JsonException)
        {
            error = (localizer is null ? "Custom request body must be valid JSON." : localizer["Custom request body must be valid JSON."].Value);
            return false;
        }
    }

    public async Task<NotificationDeliveryResult> DeliverAsync(PushDelivery delivery, CancellationToken cancellationToken = default)
    {
        if (delivery.Channel == null || !IsValidEndpoint(delivery.Channel.Endpoint))
            return new(false, ErrorMessage: localizer["Invalid Webhook URL. Please check channel configuration."].Value);
        if (!webhookConfigurationProtector.TryUnprotect(delivery.Channel.WebhookHeadersProtected, out var customHeadersJson) ||
            !TryParseCustomHeaders(customHeadersJson, out var headers, out _, localizer: localizer) || !TryCreatePayload(delivery, out var payload, localizer: localizer))
            return new(false, ErrorMessage: localizer["Invalid Webhook custom configuration. Please check channel configuration."].Value);

        using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Channel.Endpoint);
        request.Content = JsonContent.Create(payload);
        request.Headers.Add("Idempotency-Key", delivery.Id.ToString());
        foreach (var header in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                return new(false, ErrorMessage: localizer["Invalid Webhook custom headers. Please check channel configuration."].Value);
        }
        try
        {
            using var client = clients.CreateClient("PushWebhook");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode
                ? new(true)
                : new(false, ErrorMessage: string.Format(localizer["Webhook returned HTTP {0}. Please check the receiver and retry manually."].Value, (int)response.StatusCode));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, ErrorMessage: localizer["Webhook request timed out. The receiver may have received the message. Please verify and retry."].Value);
        }
        catch (HttpRequestException)
        {
            return new(false, ErrorMessage: localizer["Unable to connect to Webhook. Please check the address, network, or receiving service."].Value);
        }
    }

    private static bool TryParseCustomHeaders(string? customHeadersJson, out Dictionary<string, string> headers, out string? error, IStringLocalizer? localizer = null)
    {
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;
        if (string.IsNullOrWhiteSpace(customHeadersJson)) return true;
        try
        {
            using var document = JsonDocument.Parse(customHeadersJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = (localizer is null ? "Custom request headers must be a JSON object." : localizer["Custom request headers must be a JSON object."].Value);
                return false;
            }
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!IsValidHeaderName(property.Name) || IsReservedHeader(property.Name))
                {
                    error = (localizer is null ? "Custom request headers contain disallowed names." : localizer["Custom request headers contain disallowed names."].Value);
                    return false;
                }
                if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    error = (localizer is null ? "Custom request header values must be non-empty strings." : localizer["Custom request header values must be non-empty strings."].Value);
                    return false;
                }
                var value = property.Value.GetString()!;
                if (value.Length > 2048 || value.Any(char.IsControl) || !headers.TryAdd(property.Name, value))
                {
                    error = (localizer is null ? "Custom request headers contain invalid or duplicate values." : localizer["Custom request headers contain invalid or duplicate values."].Value);
                    return false;
                }
            }
            return true;
        }
        catch (JsonException)
        {
            error = (localizer is null ? "Custom request headers must be valid JSON." : localizer["Custom request headers must be valid JSON."].Value);
            return false;
        }
    }

    private static bool IsReservedHeader(string name) =>
        ReservedHeaders.Contains(name) || name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidHeaderName(string name) => name.Length is > 0 and <= 128 && name.All(character =>
        (character is >= 'A' and <= 'Z') || (character is >= 'a' and <= 'z') || (character is >= '0' and <= '9') ||
        "!#$%&'*+-.^_`|~".Contains(character));

    private static bool TryValidatePlaceholders(JsonNode? node, out string? error, IStringLocalizer? localizer = null)
    {
        error = null;
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (!TryValidatePlaceholders(property.Value, out error, localizer: localizer)) return false;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (!TryValidatePlaceholders(item, out error, localizer: localizer)) return false;
            }
        }
        else if (node is JsonValue value && value.TryGetValue<string>(out var text) &&
                 text.Contains("{{", StringComparison.Ordinal) && !PayloadPlaceholders.Contains(text))
        {
            error = (localizer is null ? "Custom request body contains unknown or incompletely used placeholders." : localizer["Custom request body contains unknown or incompletely used placeholders."].Value);
            return false;
        }
        return true;
    }

    private static bool TryCreatePayload(PushDelivery delivery, out JsonNode? payload, IStringLocalizer? localizer = null)
    {
        payload = null;
        try
        {
            var context = JsonNode.Parse(delivery.Message?.ContextJson ?? "null");
            var message = delivery.Message;
            var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["{{deliveryId}}"] = JsonValue.Create(delivery.Id.ToString()),
                ["{{messageId}}"] = delivery.MessageId.HasValue ? JsonValue.Create(delivery.MessageId.Value.ToString()) : null,
                ["{{title}}"] = JsonValue.Create(message?.Title ?? (localizer is null ? "Channel test notification" : localizer["Channel test notification"].Value)),
                ["{{content}}"] = message == null ? JsonValue.Create(delivery.TestContent ?? (localizer is null ? "This is a test message for the push channel." : localizer["This is a test message for the push channel."].Value)) : JsonValue.Create(message.Content),
                ["{{level}}"] = JsonValue.Create(message?.Level ?? "sev3"),
                ["{{occurredAtUtc}}"] = JsonValue.Create(message?.OccurredAtUtc ?? delivery.CreatedAtUtc),
                ["{{context}}"] = context
            };
            if (string.IsNullOrWhiteSpace(delivery.Channel?.WebhookPayloadTemplate))
            {
                payload = new JsonObject
                {
                    ["deliveryId"] = values["{{deliveryId}}"], ["messageId"] = values["{{messageId}}"],
                    ["title"] = values["{{title}}"], ["content"] = values["{{content}}"],
                    ["level"] = values["{{level}}"], ["occurredAtUtc"] = values["{{occurredAtUtc}}"],
                    ["context"] = values["{{context}}"]
                };
                return true;
            }
            var template = JsonNode.Parse(delivery.Channel!.WebhookPayloadTemplate!);
            if (template is not JsonObject || !TryResolvePayload(template, values, out payload)) return false;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryResolvePayload(JsonNode? node, IReadOnlyDictionary<string, JsonNode?> values, out JsonNode? resolved)
    {
        resolved = null;
        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var property in obj)
            {
                if (!TryResolvePayload(property.Value, values, out var resolvedProperty)) return false;
                result[property.Key] = resolvedProperty;
            }
            resolved = result;
            return true;
        }
        if (node is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var item in array)
            {
                if (!TryResolvePayload(item, values, out var resolvedItem)) return false;
                result.Add(resolvedItem);
            }
            resolved = result;
            return true;
        }
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (values.TryGetValue(text, out var replacement))
            {
                resolved = replacement?.DeepClone();
                return true;
            }
            if (text.Contains("{{", StringComparison.Ordinal)) return false;
        }
        resolved = node?.DeepClone();
        return true;
    }
}

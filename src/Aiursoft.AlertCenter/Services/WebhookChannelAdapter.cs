using System.Text.Json;
using System.Text.Json.Nodes;
using Aiursoft.AlertCenter.Entities;

namespace Aiursoft.AlertCenter.Services;

public class WebhookChannelAdapter(IHttpClientFactory clients, WebhookConfigurationProtector webhookConfigurationProtector) : INotificationChannelAdapter
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
        "向指定 HTTP 地址发送 JSON 消息，适合连接已有系统。", "webhook", true);

    public static bool IsValidEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);

    public static bool TryValidateCustomHeaders(string? customHeadersJson, out string? error) =>
        TryParseCustomHeaders(customHeadersJson, out _, out error);

    public static bool TryValidatePayloadTemplate(string? payloadTemplate, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(payloadTemplate)) return true;
        try
        {
            var template = JsonNode.Parse(payloadTemplate);
            if (template is not JsonObject)
            {
                error = "自定义请求体必须是 JSON 对象。";
                return false;
            }
            return TryValidatePlaceholders(template, out error);
        }
        catch (JsonException)
        {
            error = "自定义请求体必须是有效 JSON。";
            return false;
        }
    }

    public async Task<NotificationDeliveryResult> DeliverAsync(PushDelivery delivery, CancellationToken cancellationToken = default)
    {
        if (delivery.Channel == null || !IsValidEndpoint(delivery.Channel.Endpoint))
            return new(false, ErrorMessage: "Webhook 地址无效，请检查渠道配置。");
        if (!webhookConfigurationProtector.TryUnprotect(delivery.Channel.WebhookHeadersProtected, out var customHeadersJson) ||
            !TryParseCustomHeaders(customHeadersJson, out var headers, out _) || !TryCreatePayload(delivery, out var payload))
            return new(false, ErrorMessage: "Webhook 自定义配置无效，请检查渠道配置。");

        using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Channel.Endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("Idempotency-Key", delivery.Id.ToString());
        foreach (var header in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                return new(false, ErrorMessage: "Webhook 自定义请求头无效，请检查渠道配置。");
        }
        try
        {
            using var client = clients.CreateClient("PushWebhook");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode
                ? new(true)
                : new(false, ErrorMessage: $"Webhook 返回 HTTP {(int)response.StatusCode}。请检查接收端后手动重试。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, ErrorMessage: "Webhook 请求超时，接收端可能已收到消息，请核实后重试。");
        }
        catch (HttpRequestException)
        {
            return new(false, ErrorMessage: "无法连接 Webhook，请检查地址、网络或接收端服务。");
        }
    }

    private static bool TryParseCustomHeaders(string? customHeadersJson, out Dictionary<string, string> headers, out string? error)
    {
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;
        if (string.IsNullOrWhiteSpace(customHeadersJson)) return true;
        try
        {
            using var document = JsonDocument.Parse(customHeadersJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "自定义请求头必须是 JSON 对象。";
                return false;
            }
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!IsValidHeaderName(property.Name) || IsReservedHeader(property.Name))
                {
                    error = "自定义请求头包含不允许的名称。";
                    return false;
                }
                if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    error = "自定义请求头的值必须是非空字符串。";
                    return false;
                }
                var value = property.Value.GetString()!;
                if (value.Length > 2048 || value.Any(char.IsControl) || !headers.TryAdd(property.Name, value))
                {
                    error = "自定义请求头包含无效或重复的值。";
                    return false;
                }
            }
            return true;
        }
        catch (JsonException)
        {
            error = "自定义请求头必须是有效 JSON。";
            return false;
        }
    }

    private static bool IsReservedHeader(string name) =>
        ReservedHeaders.Contains(name) || name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidHeaderName(string name) => name.Length is > 0 and <= 128 && name.All(character =>
        (character is >= 'A' and <= 'Z') || (character is >= 'a' and <= 'z') || (character is >= '0' and <= '9') ||
        "!#$%&'*+-.^_`|~".Contains(character));

    private static bool TryValidatePlaceholders(JsonNode? node, out string? error)
    {
        error = null;
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (!TryValidatePlaceholders(property.Value, out error)) return false;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (!TryValidatePlaceholders(item, out error)) return false;
            }
        }
        else if (node is JsonValue value && value.TryGetValue<string>(out var text) && text != null &&
                 text.Contains("{{", StringComparison.Ordinal) && !PayloadPlaceholders.Contains(text))
        {
            error = "自定义请求体包含未知或未完整使用的占位符。";
            return false;
        }
        return true;
    }

    private static bool TryCreatePayload(PushDelivery delivery, out JsonNode? payload)
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
                ["{{title}}"] = JsonValue.Create(message?.Title ?? "渠道测试通知"),
                ["{{content}}"] = message == null ? JsonValue.Create(delivery.TestContent ?? PushDelivery.DefaultTestContent) : JsonValue.Create(message.Content),
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
        if (node is JsonValue value && value.TryGetValue<string>(out var text) && text != null)
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

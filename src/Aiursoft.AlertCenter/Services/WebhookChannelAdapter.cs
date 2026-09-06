using System.Text.Json;
using Aiursoft.AlertCenter.Entities;

namespace Aiursoft.AlertCenter.Services;

public class WebhookChannelAdapter(IHttpClientFactory clients) : INotificationChannelAdapter
{
    public ChannelTypeDefinition Definition { get; } = new("webhook", "Webhook",
        "向指定 HTTP 地址发送 JSON 消息，适合连接已有系统。", "webhook", true);

    public static bool IsValidEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);

    public async Task<NotificationDeliveryResult> DeliverAsync(PushDelivery delivery, CancellationToken cancellationToken = default)
    {
        if (!IsValidEndpoint(delivery.Channel.Endpoint))
            return new(false, ErrorMessage: "Webhook 地址无效，请检查渠道配置。");
        var message = delivery.Message;
        using var context = JsonDocument.Parse(message?.ContextJson ?? "null");
        using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Channel.Endpoint)
        {
            Content = JsonContent.Create(new
            {
                deliveryId = delivery.Id, messageId = delivery.MessageId,
                title = message?.Title ?? "渠道测试通知", content = message == null ? "这是一条推送渠道测试消息。" : message.Content,
                level = message?.Level ?? "sev3", occurredAtUtc = message?.OccurredAtUtc ?? delivery.CreatedAtUtc,
                context = context.RootElement
            })
        };
        request.Headers.Add("Idempotency-Key", delivery.Id.ToString());
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
}

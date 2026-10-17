using Aiursoft.AiurProtocol;
using Aiursoft.AiurProtocol.Services;
using Aiursoft.AlertCenter.SDK.Models;
using Microsoft.Extensions.Options;

namespace Aiursoft.AlertCenter.SDK;

public sealed class AlertCenterApiClient(
    AiurProtocolClient http,
    IOptions<AlertCenterApiOptions> options,
    IAlertCenterApiKeyProvider keyProvider)
{
    public async Task<ReportMessageResponse> ReportAsync(ReportMessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = await keyProvider.GetApiKeyAsync();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("An AlertCenter application API key is required.");

        return await http.Post<ReportMessageResponse>(
            new AiurApiEndpoint(options.Value.Endpoint.TrimEnd('/'), "/api/v1/messages", new { }),
            new AiurApiPayload(request), BodyFormat.HttpJsonBody,
            autoRetry: false,
            headers: new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" });
    }
}

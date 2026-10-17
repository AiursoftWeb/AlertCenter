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
    public async Task<QueryIncidentsResponse> QueryIncidentsAsync(QueryIncidentsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await http.Get<QueryIncidentsResponse>(
            new AiurApiEndpoint(options.Value.Endpoint.TrimEnd('/'), "/api/v1/incidents", request),
            autoRetry: false, headers: await AuthorizationHeadersAsync());
    }

    public async Task<ResolveIncidentResponse> ResolveIncidentAsync(Guid incidentId, ResolveIncidentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await http.Post<ResolveIncidentResponse>(
            new AiurApiEndpoint(options.Value.Endpoint.TrimEnd('/'), $"/api/v1/incidents/{incidentId}/resolve", new { }),
            new AiurApiPayload(request), BodyFormat.HttpJsonBody,
            autoRetry: false, headers: await AuthorizationHeadersAsync());
    }

    private async Task<Dictionary<string, string>> AuthorizationHeadersAsync()
    {
        var key = await keyProvider.GetApiKeyAsync();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("An AlertCenter application API key is required.");
        return new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" };
    }

    public async Task<CreateIncidentResponse> CreateIncidentAsync(CreateIncidentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await http.Post<CreateIncidentResponse>(
            new AiurApiEndpoint(options.Value.Endpoint.TrimEnd('/'), "/api/v1/incidents", new { }),
            new AiurApiPayload(request), BodyFormat.HttpJsonBody,
            autoRetry: false,
            headers: await AuthorizationHeadersAsync());
    }
}

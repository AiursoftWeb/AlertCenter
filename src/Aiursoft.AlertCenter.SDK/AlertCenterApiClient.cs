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
            new IncidentQueryEndpoint(options.Value.Endpoint.TrimEnd('/'), request),
            autoRetry: false, headers: await AuthorizationHeadersAsync());
    }

    public async Task<CreateIncidentCommentResponse> CreateIncidentCommentAsync(Guid incidentId, CreateIncidentCommentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await http.Post<CreateIncidentCommentResponse>(
            new AiurApiEndpoint(options.Value.Endpoint.TrimEnd('/'), $"/api/v1/incidents/{incidentId}/comments", new { }),
            new AiurApiPayload(request), BodyFormat.HttpJsonBody,
            autoRetry: false, headers: await AuthorizationHeadersAsync());
    }

    public async Task<GetIncidentResponse> GetIncidentAsync(Guid incidentId)
    {
        return await http.Get<GetIncidentResponse>(
            new AiurApiEndpoint(options.Value.Endpoint.TrimEnd('/'), $"/api/v1/incidents/{incidentId}", new { }),
            autoRetry: false, headers: await AuthorizationHeadersAsync());
    }

    public async Task<PatchIncidentMetadataResponse> PatchIncidentMetadataAsync(Guid incidentId, PatchIncidentMetadataRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await http.Patch<PatchIncidentMetadataResponse>(
            new AiurApiEndpoint(options.Value.Endpoint.TrimEnd('/'), $"/api/v1/incidents/{incidentId}/metadata", new { }),
            new AiurApiPayload(request), BodyFormat.HttpJsonBody,
            autoRetry: false, headers: await AuthorizationHeadersAsync());
    }

    public async Task<MitigateIncidentResponse> MitigateIncidentAsync(Guid incidentId, MitigateIncidentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await http.Post<MitigateIncidentResponse>(
            new AiurApiEndpoint(options.Value.Endpoint.TrimEnd('/'), $"/api/v1/incidents/{incidentId}/mitigate", new { }),
            new AiurApiPayload(request), BodyFormat.HttpJsonBody,
            autoRetry: false, headers: await AuthorizationHeadersAsync());
    }

    public async Task<ReopenIncidentResponse> ReopenIncidentAsync(Guid incidentId, ReopenIncidentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await http.Post<ReopenIncidentResponse>(
            new AiurApiEndpoint(options.Value.Endpoint.TrimEnd('/'), $"/api/v1/incidents/{incidentId}/reopen", new { }),
            new AiurApiPayload(request), BodyFormat.HttpJsonBody,
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

    public async Task<ChangeIncidentSeverityResponse> ChangeIncidentSeverityAsync(Guid incidentId, ChangeIncidentSeverityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await http.Post<ChangeIncidentSeverityResponse>(
            new AiurApiEndpoint(options.Value.Endpoint.TrimEnd('/'), $"/api/v1/incidents/{incidentId}/severity", new { }),
            new AiurApiPayload(request), BodyFormat.HttpJsonBody,
            autoRetry: false, headers: await AuthorizationHeadersAsync());
    }

    private sealed class IncidentQueryEndpoint(string host, QueryIncidentsRequest request)
        : AiurApiEndpoint(host, "/api/v1/incidents", request)
    {
        public override string ToString()
        {
            var url = base.ToString();
            // AiurProtocol omits whitespace query values. An exact metadata filter must preserve them.
            return request.MetadataValue != null && string.IsNullOrWhiteSpace(request.MetadataValue)
                ? url + (url.Contains('?') ? "&" : "?") + "metadatavalue=" + Uri.EscapeDataString(request.MetadataValue)
                : url;
        }
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

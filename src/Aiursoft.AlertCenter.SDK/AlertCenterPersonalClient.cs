using Aiursoft.AiurProtocol;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AiurProtocol.Services;
using Aiursoft.AlertCenter.SDK.Models;

namespace Aiursoft.AlertCenter.SDK;

public sealed class AlertCenterPersonalClient(AiurProtocolClient http, string endpoint, string apiKey)
{
    private const string Path = "/api/v1/personal/incidents";
    private AiurApiEndpoint Endpoint(string path, object? query = null) =>
        new(endpoint.TrimEnd('/'), path, query ?? new { });

    private Dictionary<string, string> Headers() => new() { ["Authorization"] = "Bearer " + apiKey };

    public Task<MobileIncidentsResponse> ListAsync(PersonalIncidentQuery query) =>
        http.Get<MobileIncidentsResponse>(Endpoint(Path, query), autoRetry: false, headers: Headers());

    public Task<MobileIncidentResponse> GetAsync(Guid id, int page = 1) =>
        http.Get<MobileIncidentResponse>(Endpoint($"{Path}/{id}", new { page }), autoRetry: false, headers: Headers());

    public Task<AiurResponse> AcknowledgeAsync(Guid id, MobileIncidentAction request) => ExecuteAsync(id, "acknowledge", request);
    public Task<AiurResponse> MitigateAsync(Guid id, MobileIncidentAction request) => ExecuteAsync(id, "mitigate", request);
    public Task<AiurResponse> ResolveAsync(Guid id, MobileIncidentAction request) => ExecuteAsync(id, "resolve", request);

    private Task<AiurResponse> ExecuteAsync(Guid id, string action, MobileIncidentAction request) =>
        http.Post<AiurResponse>(Endpoint($"{Path}/{id}/{action}"), new AiurApiPayload(request),
            BodyFormat.HttpJsonBody, autoRetry: false, headers: Headers());
}

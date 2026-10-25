using Aiursoft.AiurProtocol;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AiurProtocol.Services;
using Aiursoft.AlertCenter.SDK.Models;

namespace Aiursoft.AlertCenter.SDK;

public interface IAlertCenterAccessTokenProvider
{
    ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

public sealed class AlertCenterMobileClient(
    AiurProtocolClient http, string endpoint, IAlertCenterAccessTokenProvider tokens)
{
    private AiurApiEndpoint Endpoint(string path, object? query = null) =>
        new(endpoint.TrimEnd('/'), path, query ?? new { });

    private async Task<Dictionary<string, string>> Headers() => new()
    {
        ["Authorization"] = "Bearer " + await tokens.GetAccessTokenAsync()
    };

    public Task<MobileConfigurationResponse> GetConfigurationAsync() =>
        http.Get<MobileConfigurationResponse>(Endpoint("/api/v1/config"), autoRetry: false);

    public Task<LocalAuthenticationResponse> LoginLocalAsync(LocalLoginRequest request) =>
        http.Post<LocalAuthenticationResponse>(Endpoint("/api/v1/auth/local/login"),
            new AiurApiPayload(request), BodyFormat.HttpJsonBody, autoRetry: false);

    public Task<LocalAuthenticationResponse> RegisterLocalAsync(LocalRegistrationRequest request) =>
        http.Post<LocalAuthenticationResponse>(Endpoint("/api/v1/auth/local/register"),
            new AiurApiPayload(request), BodyFormat.HttpJsonBody, autoRetry: false);

    public async Task<MobileUserResponse> GetUserAsync() =>
        await http.Get<MobileUserResponse>(Endpoint("/api/v1/mobile/me"), autoRetry: false, headers: await Headers());

    public async Task<MobileApplicationsResponse> GetApplicationsAsync() =>
        await http.Get<MobileApplicationsResponse>(Endpoint("/api/v1/mobile/applications"), autoRetry: false, headers: await Headers());

    public async Task<MobileIncidentsResponse> GetIncidentsAsync(MobileIncidentQuery query) =>
        await http.Get<MobileIncidentsResponse>(Endpoint("/api/v1/mobile/incidents", query), autoRetry: false, headers: await Headers());

    public async Task<MobileIncidentResponse> GetIncidentAsync(Guid id, int page = 1) =>
        await http.Get<MobileIncidentResponse>(Endpoint($"/api/v1/mobile/incidents/{id}", new { page }), autoRetry: false, headers: await Headers());

    public async Task<AiurResponse> ExecuteAsync(Guid id, string action, MobileIncidentAction request) =>
        await http.Post<AiurResponse>(Endpoint($"/api/v1/mobile/incidents/{id}/{action}"),
            new AiurApiPayload(request), BodyFormat.HttpJsonBody, autoRetry: false, headers: await Headers());

    public async Task<AiurResponse> CommentAsync(Guid id, string markdown) =>
        await http.Post<AiurResponse>(Endpoint($"/api/v1/mobile/incidents/{id}/comments"),
            new AiurApiPayload(new MobileCommentRequest { Markdown = markdown }), BodyFormat.HttpJsonBody,
            autoRetry: false, headers: await Headers());

    public async Task<MobileCreatedIncidentResponse> CreateIncidentAsync(MobileCreateIncidentRequest request) =>
        await http.Post<MobileCreatedIncidentResponse>(Endpoint("/api/v1/mobile/incidents"),
            new AiurApiPayload(request), BodyFormat.HttpJsonBody, autoRetry: false, headers: await Headers());
}

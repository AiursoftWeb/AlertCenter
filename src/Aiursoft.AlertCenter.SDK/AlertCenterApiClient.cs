using System.Net;
using Aiursoft.AiurProtocol;
using Aiursoft.AiurProtocol.Exceptions;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AiurProtocol.Services;
using Aiursoft.AlertCenter.SDK.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiursoft.AlertCenter.SDK;

public sealed class AlertCenterApiClient(
    AiurProtocolClient http,
    IOptions<AlertCenterApiOptions> options,
    IAlertCenterApiKeyProvider keyProvider,
    ILogger<AlertCenterApiClient> logger)
{
    public AlertCenterApiClient(AiurProtocolClient http, IOptions<AlertCenterApiOptions> options,
        IAlertCenterApiKeyProvider keyProvider) : this(http, options, keyProvider, NullLogger<AlertCenterApiClient>.Instance)
    {
    }

    public async Task<QueryIncidentsResponse> QueryIncidentsAsync(QueryIncidentsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await SendAsync(async () => await http.Get<QueryIncidentsResponse>(
            new IncidentQueryEndpoint(options.Value.Endpoint.TrimEnd('/'), request),
            autoRetry: false, headers: await AuthorizationHeadersAsync()));
    }

    public async Task<CreateIncidentCommentResponse> CreateIncidentCommentAsync(Guid incidentId, CreateIncidentCommentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Appending has no idempotency key: a lost response must not duplicate a comment.
        return await http.Post<CreateIncidentCommentResponse>(
            new AiurApiEndpoint(options.Value.Endpoint.TrimEnd('/'), $"/api/v1/incidents/{incidentId}/comments", new { }),
            new AiurApiPayload(request), BodyFormat.HttpJsonBody,
            autoRetry: false, headers: await AuthorizationHeadersAsync());
    }

    public async Task<GetIncidentResponse> GetIncidentAsync(Guid incidentId)
    {
        return await SendAsync(async () => await http.Get<GetIncidentResponse>(
            new AiurApiEndpoint(options.Value.Endpoint.TrimEnd('/'), $"/api/v1/incidents/{incidentId}", new { }),
            autoRetry: false, headers: await AuthorizationHeadersAsync()));
    }

    public async Task<PatchIncidentMetadataResponse> PatchIncidentMetadataAsync(Guid incidentId, PatchIncidentMetadataRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await SendAsync(async () => await http.Patch<PatchIncidentMetadataResponse>(
            new AiurApiEndpoint(options.Value.Endpoint.TrimEnd('/'), $"/api/v1/incidents/{incidentId}/metadata", new { }),
            new AiurApiPayload(request), BodyFormat.HttpJsonBody,
            autoRetry: false, headers: await AuthorizationHeadersAsync()));
    }

    public async Task<MitigateIncidentResponse> MitigateIncidentAsync(Guid incidentId, MitigateIncidentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await SendAsync(async () => await http.Post<MitigateIncidentResponse>(
            new AiurApiEndpoint(options.Value.Endpoint.TrimEnd('/'), $"/api/v1/incidents/{incidentId}/mitigate", new { }),
            new AiurApiPayload(request), BodyFormat.HttpJsonBody,
            autoRetry: false, headers: await AuthorizationHeadersAsync()));
    }

    public async Task<ReopenIncidentResponse> ReopenIncidentAsync(Guid incidentId, ReopenIncidentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await SendAsync(async () => await http.Post<ReopenIncidentResponse>(
            new AiurApiEndpoint(options.Value.Endpoint.TrimEnd('/'), $"/api/v1/incidents/{incidentId}/reopen", new { }),
            new AiurApiPayload(request), BodyFormat.HttpJsonBody,
            autoRetry: false, headers: await AuthorizationHeadersAsync()));
    }

    public async Task<ResolveIncidentResponse> ResolveIncidentAsync(Guid incidentId, ResolveIncidentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await SendAsync(async () => await http.Post<ResolveIncidentResponse>(
            new AiurApiEndpoint(options.Value.Endpoint.TrimEnd('/'), $"/api/v1/incidents/{incidentId}/resolve", new { }),
            new AiurApiPayload(request), BodyFormat.HttpJsonBody,
            autoRetry: false, headers: await AuthorizationHeadersAsync()));
    }

    public async Task<ChangeIncidentSeverityResponse> ChangeIncidentSeverityAsync(Guid incidentId, ChangeIncidentSeverityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await SendAsync(async () => await http.Post<ChangeIncidentSeverityResponse>(
            new AiurApiEndpoint(options.Value.Endpoint.TrimEnd('/'), $"/api/v1/incidents/{incidentId}/severity", new { }),
            new AiurApiPayload(request), BodyFormat.HttpJsonBody,
            autoRetry: false, headers: await AuthorizationHeadersAsync()));
    }

    private static bool IsTransientStatus(HttpStatusCode status) => status is
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static bool IsTransient(Exception exception) => exception switch
    {
        AiurHttpResponseException response => IsTransientStatus(response.StatusCode) &&
            response.RetryAfter is not { TotalSeconds: > 30 },
        HttpRequestException request => request.StatusCode is { } status ? IsTransientStatus(status) :
            request.HttpRequestError is not (HttpRequestError.UserAuthenticationError or HttpRequestError.ConfigurationLimitExceeded),
        // HttpClient timeouts wrap TimeoutException. Caller cancellation must not be retried.
        OperationCanceledException { InnerException: TimeoutException } => true,
        AiurUnexpectedServerResponseException protocol when protocol.Response.Code is
            Code.WrongKey or Code.Unauthorized or Code.NotFound or Code.Conflict => false,
        AiurUnexpectedServerResponseException { InnerException: AiurHttpResponseException response } => IsTransient(response),
        AiurUnexpectedServerResponseException protocol => protocol.Response.Code is
            Code.RemoteNotAccessible or Code.Timeout or Code.TooManyRequests,
        _ => false
    };

    private async Task<T> SendAsync<T>(Func<Task<T>> send)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await send();
            }
            catch (Exception exception) when (attempt < 3 && IsTransient(exception))
            {
                var delay = TimeSpan.FromSeconds(attempt);
                var response = exception as AiurHttpResponseException ?? exception.InnerException as AiurHttpResponseException;
                if (response?.RetryAfter is { } retryAfter && retryAfter > delay)
                    delay = retryAfter;
                logger.LogWarning("AlertCenter request failed with {ExceptionType} on attempt {Attempt}/3; retrying in {Delay}.",
                    exception.GetType().Name, attempt, delay);
                await Task.Delay(delay);
            }
        }
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
        return await SendAsync(async () => await http.Post<CreateIncidentResponse>(
            new AiurApiEndpoint(options.Value.Endpoint.TrimEnd('/'), "/api/v1/incidents", new { }),
            new AiurApiPayload(request), BodyFormat.HttpJsonBody,
            autoRetry: false,
            headers: await AuthorizationHeadersAsync()));
    }
}

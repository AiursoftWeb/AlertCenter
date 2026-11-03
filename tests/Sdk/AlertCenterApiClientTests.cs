using System.Net;
using System.Text;
using Aiursoft.AiurProtocol.Exceptions;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AiurProtocol.Services;
using Microsoft.Extensions.Options;
using Aiursoft.AlertCenter.SDK;
using Aiursoft.AlertCenter.SDK.Models;
using Newtonsoft.Json.Linq;

namespace Aiursoft.AlertCenter.Tests.Sdk;

[TestClass]
public sealed class AlertCenterApiClientTests
{
    [TestMethod]
    public async Task CommentUsesJsonCurrentCredentialAndDoesNotRetryAppends()
    {
        using var handler = new RecordingHandler();
        var keys = new RotatingKeyProvider();
        await using var services = BuildProvider(handler, keys);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var id = Guid.NewGuid();
        var request = new CreateIncidentCommentRequest { Markdown = "**Investigation:** CI CPU contention" };
        await client.CreateIncidentCommentAsync(id, request);
        Assert.AreEqual($"https://alerts.example/center/api/v1/incidents/{id}/comments", handler.RequestUri?.ToString());
        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual("application/json", handler.ContentType);
        Assert.AreEqual(request.Markdown, JObject.Parse(handler.Body!)["markdown"]!.Value<string>());
        keys.Key = "rotated-key";
        await client.CreateIncidentCommentAsync(id, request);
        Assert.AreEqual("Bearer rotated-key", handler.Authorization);
        using var failedHandler = new RecordingHandler { Fail = true };
        await using var failedServices = BuildProvider(failedHandler, keys);
        await Assert.ThrowsAsync<HttpRequestException>(() => failedServices.GetRequiredService<AlertCenterApiClient>().CreateIncidentCommentAsync(id, request));
        Assert.AreEqual(1, failedHandler.CallCount);
    }

    [TestMethod]
    public async Task MitigationUsesVersionedJsonAndCurrentCredentialWithBoundedRetries()
    {
        using var handler = new RecordingHandler();
        var keys = new RotatingKeyProvider();
        await using var services = BuildProvider(handler, keys);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var id = Guid.NewGuid();
        var request = new MitigateIncidentRequest { Version = Guid.NewGuid(), Reason = "Build recovered" };
        await client.MitigateIncidentAsync(id, request);
        Assert.AreEqual($"https://alerts.example/center/api/v1/incidents/{id}/mitigate", handler.RequestUri?.ToString());
        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual("application/json", handler.ContentType);
        Assert.AreEqual(request.Version, JObject.Parse(handler.Body!)["version"]!.ToObject<Guid>());
        Assert.AreEqual(request.Reason, JObject.Parse(handler.Body!)["reason"]!.Value<string>());
        keys.Key = "rotated-key";
        await client.MitigateIncidentAsync(id, request);
        Assert.AreEqual("Bearer rotated-key", handler.Authorization);
        using var failedHandler = new RecordingHandler { Fail = true };
        await using var failedServices = BuildProvider(failedHandler, keys);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            failedServices.GetRequiredService<AlertCenterApiClient>().MitigateIncidentAsync(id, request));
        Assert.AreEqual(3, failedHandler.CallCount);
    }

    [TestMethod]
    public async Task DetailQueriesUseConfiguredBasePathAndCurrentKeyWithBoundedRetries()
    {
        using var handler = new RecordingHandler();
        var keys = new RotatingKeyProvider();
        await using var services = BuildProvider(handler, keys);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var incidentId = Guid.NewGuid();
        await client.GetIncidentAsync(incidentId);
        Assert.AreEqual($"https://alerts.example/center/api/v1/incidents/{incidentId}", handler.RequestUri?.ToString());
        Assert.AreEqual(HttpMethod.Get, handler.Method);
        Assert.AreEqual("Bearer first-key", handler.Authorization);
        keys.Key = "rotated-key";
        await client.GetIncidentAsync(incidentId);
        Assert.AreEqual("Bearer rotated-key", handler.Authorization);
        Assert.AreEqual(2, handler.CallCount);
        using var failedHandler = new RecordingHandler { Fail = true };
        await using var failedServices = BuildProvider(failedHandler, keys);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            failedServices.GetRequiredService<AlertCenterApiClient>().GetIncidentAsync(incidentId));
        Assert.AreEqual(3, failedHandler.CallCount);
    }

    [TestMethod]
    public async Task ReportsUseConfiguredBasePathAndFetchCurrentCredentialForEveryCall()
    {
        using var handler = new RecordingHandler();
        var keys = new RotatingKeyProvider();
        await using var services = BuildProvider(handler, keys);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var request = ValidRequest();
        await client.CreateIncidentAsync(request);
        Assert.AreEqual("https://alerts.example/center/api/v1/incidents", handler.RequestUri?.ToString());
        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual("application/json", handler.ContentType);
        Assert.AreEqual("Bearer first-key", handler.Authorization);
        Assert.AreEqual(request.ReportId, JObject.Parse(handler.Body!)["reportId"]!.ToObject<Guid>());

        keys.Key = "rotated-key";
        await client.CreateIncidentAsync(request);
        Assert.AreEqual("Bearer rotated-key", handler.Authorization);
        Assert.AreEqual(2, handler.CallCount);
    }

    [TestMethod]
    [DataRow("reportId")]
    [DataRow("occurredAtUtc")]
    [DataRow("title")]
    [DataRow("level")]
    public async Task InvalidReportsNeverReachTheNetwork(string field)
    {
        using var handler = new RecordingHandler();
        await using var services = BuildProvider(handler, new RotatingKeyProvider());
        var request = ValidRequest();
        switch (field)
        {
            case "reportId": request.ReportId = Guid.Empty; break;
            case "occurredAtUtc": request.OccurredAtUtc = default; break;
            case "title": request.Title = " "; break;
            case "level": request.Level = "sev5"; break;
        }
        await Assert.ThrowsAsync<AiurBadApiInputException>(() => services.GetRequiredService<AlertCenterApiClient>().CreateIncidentAsync(request));
        Assert.AreEqual(0, handler.CallCount);
    }

    [TestMethod]
    public async Task TransportFailuresStopAfterThreeAttempts()
    {
        using var handler = new RecordingHandler { Fail = true };
        await using var services = BuildProvider(handler, new RotatingKeyProvider());
        await Assert.ThrowsAsync<HttpRequestException>(() => services.GetRequiredService<AlertCenterApiClient>().CreateIncidentAsync(ValidRequest()));
        Assert.AreEqual(3, handler.CallCount);
    }

    [TestMethod]
    public async Task MetadataPatchUsesPatchJsonVersionAndCurrentKeyWithBoundedRetries()
    {
        using var handler = new RecordingHandler();
        var keys = new RotatingKeyProvider();
        await using var services = BuildProvider(handler, keys);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var id = Guid.NewGuid();
        var request = new PatchIncidentMetadataRequest { Version = Guid.NewGuid(), Property = "ci", Value = new JObject { ["count"] = 3 } };
        await client.PatchIncidentMetadataAsync(id, request);
        Assert.AreEqual(HttpMethod.Patch, handler.Method);
        Assert.AreEqual($"https://alerts.example/center/api/v1/incidents/{id}/metadata", handler.RequestUri?.ToString());
        Assert.AreEqual("application/json", handler.ContentType);
        Assert.AreEqual(request.Version, JObject.Parse(handler.Body!)["version"]!.ToObject<Guid>());
        Assert.AreEqual(3, JObject.Parse(handler.Body!)["value"]!.Value<int>("count"));
        keys.Key = "rotated-key";
        await client.PatchIncidentMetadataAsync(id, request);
        Assert.AreEqual("Bearer rotated-key", handler.Authorization);
        using var failing = new RecordingHandler { Fail = true };
        await using var failed = BuildProvider(failing, keys);
        await Assert.ThrowsAsync<HttpRequestException>(() => failed.GetRequiredService<AlertCenterApiClient>().PatchIncidentMetadataAsync(id, request));
        Assert.AreEqual(3, failing.CallCount);
    }

    [TestMethod]
    public async Task ManuallyConstructedClientsKeepTheExistingConstructorAndRetryQueries()
    {
        using var handler = new RecordingHandler
        {
            FailuresBeforeSuccess = 1, FailureException = new HttpRequestException("Connection reset")
        };
        var keys = new RotatingKeyProvider();
        await using var services = BuildProvider(handler, keys);
        var client = new AlertCenterApiClient(services.GetRequiredService<AiurProtocolClient>(),
            Options.Create(new AlertCenterApiOptions { Endpoint = "https://alerts.example/center" }), keys);
        await client.QueryIncidentsAsync(new QueryIncidentsRequest());
        Assert.AreEqual(2, handler.CallCount);
    }

    [TestMethod]
    [DataRow("query")]
    [DataRow("detail")]
    [DataRow("metadata")]
    [DataRow("mitigate")]
    [DataRow("reopen")]
    [DataRow("resolve")]
    [DataRow("severity")]
    public async Task IncidentOperationsRecoverFromATransportFailure(string operation)
    {
        using var handler = new RecordingHandler
        {
            FailuresBeforeSuccess = 1, FailureException = new HttpRequestException("Connection reset")
        };
        await using var services = BuildProvider(handler, new RotatingKeyProvider());
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var id = Guid.NewGuid();
        var version = Guid.NewGuid();
        await (operation switch
        {
            "query" => (Task)client.QueryIncidentsAsync(new QueryIncidentsRequest()),
            "detail" => client.GetIncidentAsync(id),
            "metadata" => client.PatchIncidentMetadataAsync(id, new PatchIncidentMetadataRequest { Version = version, Property = "ci" }),
            "mitigate" => client.MitigateIncidentAsync(id, new MitigateIncidentRequest { Version = version, Reason = "Recovered" }),
            "reopen" => client.ReopenIncidentAsync(id, new ReopenIncidentRequest { Version = version, Reason = "Recurred" }),
            "resolve" => client.ResolveIncidentAsync(id, new ResolveIncidentRequest { Version = version, Reason = "Fixed" }),
            "severity" => client.ChangeIncidentSeverityAsync(id, new ChangeIncidentSeverityRequest { Version = version, Level = "sev2", Reason = "Escalated" }),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });
        Assert.AreEqual(2, handler.CallCount);
        Assert.AreEqual(1, handler.Bodies.Distinct().Count());
        if (operation is not ("query" or "detail"))
            Assert.AreEqual(version, JObject.Parse(handler.Bodies[0])["version"]!.ToObject<Guid>());
    }

    [TestMethod]
    [DataRow(408)]
    [DataRow(429)]
    [DataRow(500)]
    [DataRow(502)]
    [DataRow(503)]
    [DataRow(504)]
    public async Task TemporaryHttpFailureRecoversOnThirdAttempt(int status)
    {
        using var handler = new RecordingHandler { FailuresBeforeSuccess = 2, FailureStatus = (HttpStatusCode)status };
        await using var services = BuildProvider(handler, new RotatingKeyProvider());
        var request = ValidRequest();
        await services.GetRequiredService<AlertCenterApiClient>().CreateIncidentAsync(request);
        Assert.AreEqual(3, handler.CallCount);
        Assert.AreEqual(1, handler.Bodies.Distinct().Count());
        Assert.AreEqual(request.ReportId, JObject.Parse(handler.Bodies[0])["reportId"]!.ToObject<Guid>());
    }

    [TestMethod]
    [DataRow(400)]
    [DataRow(401)]
    [DataRow(403)]
    [DataRow(404)]
    [DataRow(409)]
    [DataRow(501)]
    public async Task PermanentHttpFailureIsNotRetried(int status)
    {
        using var handler = new RecordingHandler { FailuresBeforeSuccess = 5, FailureStatus = (HttpStatusCode)status };
        await using var services = BuildProvider(handler, new RotatingKeyProvider());
        var exception = await Assert.ThrowsAsync<AiurHttpResponseException>(() =>
            services.GetRequiredService<AlertCenterApiClient>().QueryIncidentsAsync(new QueryIncidentsRequest()));
        Assert.AreEqual((HttpStatusCode)status, exception.StatusCode);
        Assert.AreEqual(1, handler.CallCount);
    }

    [TestMethod]
    [DataRow(200, -11, 2)]
    [DataRow(429, -11, 2)]
    [DataRow(503, -5, 2)]
    [DataRow(503, -1, 1)]
    [DataRow(409, -7, 1)]
    [DataRow(401, -1, 1)]
    public async Task ProtocolErrorsRespectHttpContextAndPermanentApiFailures(int status, int code, int attempts)
    {
        using var handler = new RecordingHandler
        {
            FailuresBeforeSuccess = 1, FailureStatus = (HttpStatusCode)status,
            FailureBody = $$"""{"Code":{{code}},"Message":"API failed","ProtocolVersion":"10.0.3"}"""
        };
        await using var services = BuildProvider(handler, new RotatingKeyProvider());
        var client = services.GetRequiredService<AlertCenterApiClient>();
        if (attempts == 1)
            await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.QueryIncidentsAsync(new QueryIncidentsRequest()));
        else
            await client.QueryIncidentsAsync(new QueryIncidentsRequest());
        Assert.AreEqual(attempts, handler.CallCount);
    }

    [TestMethod]
    public async Task ProtocolRateLimitDoesNotLoseLongRetryAfter()
    {
        using var handler = new RecordingHandler
        {
            FailuresBeforeSuccess = 5, FailureStatus = HttpStatusCode.TooManyRequests,
            FailureBody = """{"Code":-11,"Message":"Rate limited","ProtocolVersion":"10.0.3"}""",
            RetryAfter = TimeSpan.FromMinutes(2)
        };
        await using var services = BuildProvider(handler, new RotatingKeyProvider());
        var exception = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() =>
            services.GetRequiredService<AlertCenterApiClient>().QueryIncidentsAsync(new QueryIncidentsRequest()));
        Assert.AreEqual(Code.TooManyRequests, exception.Response.Code);
        Assert.AreEqual(TimeSpan.FromMinutes(2), ((AiurHttpResponseException)exception.InnerException!).RetryAfter);
        Assert.AreEqual(1, handler.CallCount);
    }

    [TestMethod]
    public async Task LongRetryAfterIsReturnedToCallerWithoutRetryingEarly()
    {
        using var handler = new RecordingHandler
        {
            FailuresBeforeSuccess = 5, FailureStatus = HttpStatusCode.TooManyRequests,
            RetryAfter = TimeSpan.FromMinutes(2)
        };
        await using var services = BuildProvider(handler, new RotatingKeyProvider());
        var exception = await Assert.ThrowsAsync<AiurHttpResponseException>(() =>
            services.GetRequiredService<AlertCenterApiClient>().GetIncidentAsync(Guid.NewGuid()));
        Assert.AreEqual(TimeSpan.FromMinutes(2), exception.RetryAfter);
        Assert.AreEqual(1, handler.CallCount);
    }

    [TestMethod]
    public async Task HttpTimeoutRecoversButCallerCancellationIsNotRetried()
    {
        using var timeout = new RecordingHandler
        {
            FailuresBeforeSuccess = 1, FailureException = new TaskCanceledException("HTTP timeout", new TimeoutException())
        };
        await using var services = BuildProvider(timeout, new RotatingKeyProvider());
        await services.GetRequiredService<AlertCenterApiClient>().GetIncidentAsync(Guid.NewGuid());
        Assert.AreEqual(2, timeout.CallCount);
        using var cancelled = new RecordingHandler
        {
            FailuresBeforeSuccess = 5, FailureException = new OperationCanceledException(new CancellationToken(true))
        };
        await using var cancelledServices = BuildProvider(cancelled, new RotatingKeyProvider());
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            cancelledServices.GetRequiredService<AlertCenterApiClient>().GetIncidentAsync(Guid.NewGuid()));
        Assert.AreEqual(1, cancelled.CallCount);
    }

    [TestMethod]
    public async Task ConflictAfterUncertainWriteDoesNotRefreshVersionOrReplayIntent()
    {
        using var handler = new RecordingHandler
        {
            FailuresBeforeSuccess = 2, LoseFirstResponse = true,
            FailureBody = """{"Code":-7,"Message":"Refresh the incident before deciding again","ProtocolVersion":"10.0.3"}"""
        };
        await using var services = BuildProvider(handler, new RotatingKeyProvider());
        var request = new PatchIncidentMetadataRequest { Version = Guid.NewGuid(), Property = "ci", Value = new JObject { ["count"] = 1 } };
        var exception = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() =>
            services.GetRequiredService<AlertCenterApiClient>().PatchIncidentMetadataAsync(Guid.NewGuid(), request));
        Assert.AreEqual(Code.Conflict, exception.Response.Code);
        Assert.AreEqual(2, handler.CallCount);
        Assert.AreEqual(1, handler.Bodies.Distinct().Count());
        Assert.AreEqual(request.Version, JObject.Parse(handler.Bodies[0])["version"]!.ToObject<Guid>());
    }

    private static CreateIncidentRequest ValidRequest() => new()
    {
        ReportId = Guid.NewGuid(), Title = "Database unavailable", OccurredAtUtc = DateTimeOffset.UtcNow
    };

    private static ServiceProvider BuildProvider(RecordingHandler handler, RotatingKeyProvider keys)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlertCenterSdk("https://alerts.example/center/", "configured-key");
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(handler));
        services.AddScoped<IAlertCenterApiKeyProvider>(_ => keys);
        return services.BuildServiceProvider();
    }

    private sealed class RotatingKeyProvider : IAlertCenterApiKeyProvider
    {
        public string Key { get; set; } = "first-key";
        public ValueTask<string> GetApiKeyAsync() => ValueTask.FromResult(Key);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public bool Fail { get; init; }
        public int FailuresBeforeSuccess { get; init; }
        public HttpStatusCode FailureStatus { get; init; } = HttpStatusCode.ServiceUnavailable;
        public string FailureBody { get; init; } = "upstream unavailable";
        public Exception? FailureException { get; init; }
        public bool LoseFirstResponse { get; init; }
        public TimeSpan? RetryAfter { get; init; }
        public List<string> Bodies { get; } = [];
        public int CallCount { get; private set; }
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? ContentType { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (Fail) throw new HttpRequestException("Connection lost.");
            RequestUri = request.RequestUri;
            Method = request.Method;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(Body);
            if (LoseFirstResponse && CallCount == 1) throw new HttpRequestException("Response lost after commit.");
            if (CallCount <= FailuresBeforeSuccess)
            {
                if (FailureException != null) throw FailureException;
                var response = new HttpResponseMessage(FailureStatus) { Content = new StringContent(FailureBody) };
                if (RetryAfter is { } delay)
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"Code":2,"Message":"Received","ProtocolVersion":"10.0.34"}""", Encoding.UTF8, "application/json")
            };
        }
    }
}

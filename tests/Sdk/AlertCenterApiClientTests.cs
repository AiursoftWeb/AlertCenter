using System.Net;
using System.Text;
using Aiursoft.AiurProtocol.Exceptions;
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
    public async Task MitigationUsesVersionedJsonAndCurrentCredentialWithoutRetries()
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
        Assert.AreEqual(1, failedHandler.CallCount);
    }

    [TestMethod]
    public async Task DetailQueriesUseConfiguredBasePathAndCurrentKeyWithoutRetries()
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
        Assert.AreEqual(1, failedHandler.CallCount);
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
    public async Task TransportFailuresAreNotAutomaticallyRetried()
    {
        using var handler = new RecordingHandler { Fail = true };
        await using var services = BuildProvider(handler, new RotatingKeyProvider());
        await Assert.ThrowsAsync<HttpRequestException>(() => services.GetRequiredService<AlertCenterApiClient>().CreateIncidentAsync(ValidRequest()));
        Assert.AreEqual(1, handler.CallCount);
    }

    [TestMethod]
    public async Task MetadataPatchUsesPatchJsonVersionAndCurrentKeyWithoutRetries()
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
        Assert.AreEqual(1, failing.CallCount);
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
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"Code":2,"Message":"Received","ProtocolVersion":"10.0.34"}""", Encoding.UTF8, "application/json")
            };
        }
    }
}

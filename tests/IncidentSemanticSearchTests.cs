using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Aiursoft.AlertCenter.Configuration;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Services.BackgroundJobs;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class IncidentSemanticSearchTests
{
    private SqliteContext _db = null!;
    private IConfiguration _configuration = null!;
    private MemoryCache _cache = null!;
    private FakeEmbeddingHandler _handler = null!;
    private EmbeddingClient _client = null!;
    private IncidentEmbeddingJobGate _gate = null!;
    private SourceApplication _application = null!;
    private readonly ClaimsPrincipal _user = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "reader")], "test"));

    [TestInitialize]
    public async Task Initialize()
    {
        _db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite("DataSource=:memory:").Options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.EnsureCreatedAsync();
        _db.Users.Add(new User { Id = "reader", DisplayName = "Reader" });
        _application = new SourceApplication { Name = "Payments", OwnerId = "reader" };
        _db.SourceApplications.Add(_application);
        await _db.SaveChangesAsync();
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [SettingsMap.EnableEmbeddingBasedSearch] = "True",
            [SettingsMap.EmbeddingOllamaInstance] = "https://embedding.invalid/gateway/",
            [SettingsMap.EmbeddingModel] = "test-model",
            [SettingsMap.EmbeddingApiToken] = "test-token",
            [SettingsMap.EmbeddingQueryCacheLimit] = "2"
        }).Build();
        _cache = new MemoryCache(new MemoryCacheOptions());
        var settings = new GlobalSettingsService(_db, _configuration, null!, _cache, new EphemeralDataProtectionProvider(),
            NullLogger<GlobalSettingsService>.Instance);
        _handler = new FakeEmbeddingHandler();
        _client = new EmbeddingClient(settings, new FakeClientFactory(_handler));
        _gate = new IncidentEmbeddingJobGate();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        _handler.Dispose();
        _cache.Dispose();
        _gate.Dispose();
        await _db.DisposeAsync();
    }

    private IncidentSearchService Search() => new(_db, new ApplicationAccessService(_db), _client, NullLogger<IncidentSearchService>.Instance);
    private GenerateIncidentEmbeddingsJob Index() => new(_db, _client, _gate, NullLogger<GenerateIncidentEmbeddingsJob>.Instance);
    private Incident Add(string title = "Database unavailable", string? content = "Connection pool exhausted", SourceApplication? application = null)
    {
        var incident = new Incident { Application = application ?? _application, Title = title, Content = content };
        _db.Incidents.Add(incident);
        return incident;
    }

    [TestMethod]
    public async Task VectorRankingFiltersPermissionsAndSeverityBeforeCountingAndPaging()
    {
        var privateApp = new SourceApplication { Name = "Private application" };
        for (var index = 0; index < 12; index++)
        {
            Add($"Allowed outage {index}");
            Add($"Private outage {index}", application: privateApp);
        }
        var hidden = Add("Hidden severity");
        hidden.Level = "sev4";
        (await _db.SeverityRules.SingleAsync(rule => rule.Level == "sev4")).ShowInLists = false;
        await _db.SaveChangesAsync();
        await Index().ExecuteAsync();
        var first = await Search().SearchAsync(_user, "system failure", 1, default);
        Assert.IsTrue(first.UsedAiSearch);
        Assert.AreEqual(12, first.TotalCount);
        Assert.AreEqual(10, first.Incidents.Count);
        Assert.IsTrue(first.Incidents.All(incident => incident.Title.StartsWith("Allowed outage")));
        var requestCount = _handler.Inputs.Count;
        var second = await Search().SearchAsync(_user, "system failure", 2, default);
        Assert.AreEqual(2, second.Incidents.Count);
        Assert.IsFalse(second.Incidents.Any(incident => first.Incidents.Any(other => other.Id == incident.Id)));
        Assert.AreEqual(requestCount, _handler.Inputs.Count, "The query vector should be cached across pages.");
        var anonymous = await Search().SearchAsync(new ClaimsPrincipal(), "system failure", 1, default);
        Assert.AreEqual(0, anonymous.TotalCount);
        Assert.AreEqual(requestCount, _handler.Inputs.Count);
    }

    [TestMethod]
    [DataRow("disabled")]
    [DataRow("unconfigured")]
    [DataRow("empty-index")]
    [DataRow("http")]
    [DataRow("timeout")]
    [DataRow("invalid")]
    public async Task UnavailableEmbeddingsFallBackToPermissionScopedKeywordSearch(string mode)
    {
        Add();
        Add(application: new SourceApplication { Name = "Private" });
        await _db.SaveChangesAsync();
        if (mode != "empty-index") await Index().ExecuteAsync();
        if (mode == "disabled") _configuration[SettingsMap.EnableEmbeddingBasedSearch] = "False";
        if (mode == "unconfigured") _configuration[SettingsMap.EmbeddingModel] = "";
        _handler.Failure = mode;
        var result = await Search().SearchAsync(_user, "database", 1, default);
        Assert.IsFalse(result.UsedAiSearch);
        Assert.AreEqual(1, result.TotalCount);
        Assert.AreEqual(_application.Id, result.Incidents.Single().ApplicationId);
    }

    [TestMethod]
    public async Task SemanticMatchesAreRankedBySimilarityWithoutRequiringKeywordMatches()
    {
        var weak = Add("Weak match");
        var best = Add("Best match");
        Add("Unrelated match");
        await _db.SaveChangesAsync();
        _handler.VectorFor = text => text.Contains("Weak match") ? [0.1f, 1f] : text.Contains("Unrelated match") ? [-1f, 0f] : [1f, 0f];
        await Index().ExecuteAsync();
        var result = await Search().SearchAsync(_user, "an entirely different wording", 1, default);
        Assert.IsTrue(result.UsedAiSearch);
        Assert.AreEqual(2, result.TotalCount);
        CollectionAssert.AreEqual(new[] { best.Id, weak.Id }, result.Incidents.Select(incident => incident.Id).ToArray());
        var laterPage = await Search().SearchAsync(_user, "an entirely different wording", 1000000, default);
        Assert.IsTrue(laterPage.UsedAiSearch);
        Assert.AreEqual(0, laterPage.Incidents.Count);
    }

    [TestMethod]
    public async Task IndexIsIncrementalAndRegeneratesOnlyWhenSearchableContentOrModelChanges()
    {
        var incident = Add();
        await _db.SaveChangesAsync();
        await Index().ExecuteAsync();
        Assert.AreEqual(1, _handler.Inputs.Count);
        Assert.AreEqual("https://embedding.invalid/gateway/api/embed?keep_alive=-1", _handler.LastUri);
        Assert.AreEqual("Bearer test-token", _handler.LastAuthorization);
        await Index().ExecuteAsync();
        incident.Status = Incident.Resolved;
        incident.Version = Guid.NewGuid();
        await _db.SaveChangesAsync();
        await Index().ExecuteAsync();
        Assert.AreEqual(1, _handler.Inputs.Count);
        incident.Content = "New searchable evidence";
        await _db.SaveChangesAsync();
        var beforeIndex = await Search().SearchAsync(_user, "evidence", 1, default);
        Assert.IsFalse(beforeIndex.UsedAiSearch, "Outdated vectors must not be searched.");
        await Index().ExecuteAsync();
        Assert.AreEqual(2, _handler.Inputs.Count);
        var indexed = await Search().SearchAsync(_user, "similar failure", 1, default);
        Assert.IsTrue(indexed.UsedAiSearch);
        _application.Name = "Renamed app";
        await _db.SaveChangesAsync();
        await Index().ExecuteAsync();
        Assert.Contains("Application: Renamed app", _handler.Inputs.Last());
        var oldKey = (await _db.IncidentEmbeddings.SingleAsync()).ModelKey;
        _configuration[SettingsMap.EmbeddingModel] = "new-model-same-dimensions";
        Assert.IsFalse((await Search().SearchAsync(_user, "evidence", 1, default)).UsedAiSearch);
        await Index().ExecuteAsync();
        Assert.AreNotEqual(oldKey, (await _db.IncidentEmbeddings.SingleAsync()).ModelKey);
        var count = _handler.Inputs.Count;
        Assert.IsTrue((await Search().SearchAsync(_user, "similar failure", 1, default)).UsedAiSearch);
        Assert.AreEqual(count + 1, _handler.Inputs.Count, "Changing the model must invalidate query vectors too.");
    }

    [TestMethod]
    public async Task DeletedIncidentsAndRevokedGrantsDisappearWithoutWaitingForAnIndexRefresh()
    {
        var shared = new SourceApplication { Name = "Shared application" };
        var incident = Add(application: shared);
        var grant = new ApplicationUserAccess { Application = shared, UserId = "reader", CanViewIncidents = true };
        _db.ApplicationUserAccesses.Add(grant);
        await _db.SaveChangesAsync();
        await Index().ExecuteAsync();
        Assert.AreEqual(1, (await Search().SearchAsync(_user, "failure", 1, default)).TotalCount);
        _db.ApplicationUserAccesses.Remove(grant);
        await _db.SaveChangesAsync();
        Assert.AreEqual(0, (await Search().SearchAsync(_user, "failure", 1, default)).TotalCount);
        await _db.Incidents.Where(item => item.Id == incident.Id).ExecuteDeleteAsync();
        Assert.AreEqual(0, await _db.IncidentEmbeddings.CountAsync());
        Add();
        await _db.SaveChangesAsync();
        await Index().ExecuteAsync();
        await _db.Incidents.Where(item => item.ApplicationId == _application.Id).ExecuteDeleteAsync();
        await _db.SourceApplications.Where(item => item.Id == _application.Id).ExecuteDeleteAsync();
        Assert.AreEqual(0, await _db.IncidentEmbeddings.CountAsync());
        Assert.AreEqual(0, (await Search().SearchAsync(_user, "failure", 1, default)).TotalCount);
    }

    [TestMethod]
    public async Task QueryCacheEvictsOldEntriesAndCallerCancellationIsNotSwallowed()
    {
        Add();
        await _db.SaveChangesAsync();
        await Index().ExecuteAsync();
        foreach (var query in new[] { "one", "two", "three" })
            await Search().SearchAsync(_user, query, 1, default);
        Assert.AreEqual(2, await _db.SearchEmbeddings.CountAsync());
        _handler.Block = true;
        using var cancellation = new CancellationTokenSource();
        var search = Search().SearchAsync(_user, "cancelled", 1, cancellation.Token);
        await _handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => search);
    }

    [TestMethod]
    public async Task FailedIndexRunCanBeRetriedWithoutBlockingIncidentPersistence()
    {
        var incident = Add();
        await _db.SaveChangesAsync();
        _handler.Failure = "http";
        await Assert.ThrowsAsync<HttpRequestException>(() => Index().ExecuteAsync());
        Assert.IsTrue(await _db.Incidents.AnyAsync(item => item.Id == incident.Id));
        Assert.AreEqual(0, await _db.IncidentEmbeddings.CountAsync());
        _handler.Failure = null;
        await Index().ExecuteAsync();
        Assert.AreEqual(1, await _db.IncidentEmbeddings.CountAsync());
    }

    [TestMethod]
    public async Task IncompatibleDimensionsFallBackAndCorruptQueryCacheIsRegenerated()
    {
        Add();
        await _db.SaveChangesAsync();
        await Index().ExecuteAsync();
        _handler.VectorFor = _ => [1f, 0f, 0f];
        Assert.IsFalse((await Search().SearchAsync(_user, "database", 1, default)).UsedAiSearch);
        var cached = await _db.SearchEmbeddings.SingleAsync();
        cached.Vector = [1, 2, 3];
        await _db.SaveChangesAsync();
        _handler.VectorFor = _ => [1f, 0f];
        Assert.IsTrue((await Search().SearchAsync(_user, "database", 1, default)).UsedAiSearch);
        Assert.AreEqual(8, (await _db.SearchEmbeddings.SingleAsync()).Vector.Length);
    }

    [TestMethod]
    public async Task IndexBatchesAreBoundedAndNextRunContinuesTheBacklog()
    {
        for (var index = 0; index < 55; index++) Add($"Backlog incident {index}");
        await _db.SaveChangesAsync();
        await Index().ExecuteAsync();
        Assert.AreEqual(50, await _db.IncidentEmbeddings.CountAsync());
        Assert.AreEqual(5, _handler.BatchSizes.Count);
        Assert.IsTrue(_handler.BatchSizes.All(size => size == 10));
        await Index().ExecuteAsync();
        Assert.AreEqual(55, await _db.IncidentEmbeddings.CountAsync());
        Assert.AreEqual(5, _handler.BatchSizes.Last());
    }

    [TestMethod]
    public async Task ContentChangedDuringEmbeddingIsNotPublishedAndNextRunRetries()
    {
        var incident = Add();
        await _db.SaveChangesAsync();
        _handler.BeforeResponse = async () =>
        {
            incident.Content = "Changed during model call";
            await _db.SaveChangesAsync();
            _handler.BeforeResponse = null;
        };
        await Index().ExecuteAsync();
        Assert.AreEqual(0, await _db.IncidentEmbeddings.CountAsync());
        await Index().ExecuteAsync();
        Assert.AreEqual(1, await _db.IncidentEmbeddings.CountAsync());
        Assert.Contains("Changed during model call", _handler.Inputs.Last());
    }

    private sealed class FakeClientFactory(FakeEmbeddingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeEmbeddingHandler : HttpMessageHandler
    {
        public List<string> Inputs { get; } = [];
        public List<int> BatchSizes { get; } = [];
        public string? LastUri { get; private set; }
        public string? LastAuthorization { get; private set; }
        public string? Failure { get; set; }
        public bool Block { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<Task>? BeforeResponse { get; set; }
        public Func<string, float[]> VectorFor { get; set; } = _ => [1f, 0f];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri!.AbsoluteUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var inputs = body.RootElement.GetProperty("input").EnumerateArray().Select(value => value.GetString()!).ToArray();
            Inputs.AddRange(inputs);
            BatchSizes.Add(inputs.Length);
            if (Block)
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (Failure == "timeout") throw new TaskCanceledException("Simulated embedding timeout");
            if (Failure == "http") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (Failure == "invalid") return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { embeddings = new[] { new[] { 0f, 0f } } }) };
            if (BeforeResponse != null) await BeforeResponse();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { embeddings = inputs.Select(VectorFor).ToArray() })
            };
        }
    }
}

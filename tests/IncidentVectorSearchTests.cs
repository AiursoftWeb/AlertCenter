using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aiursoft.AlertCenter.Configuration;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.InMemory;
using Aiursoft.AlertCenter.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using static Aiursoft.WebTools.EmbeddingHelper;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class IncidentVectorSearchTests
{
    private const int VectorDimension = 1024;

    private InMemoryContext _db = null!;
    private IncidentEmbeddingCache _cache = null!;
    private IConfiguration _config = null!;
    private IMemoryCache _memoryCache = null!;

    // ─────────────────────────────────────────────────────────────────────────
    // Test setup / helpers
    // ─────────────────────────────────────────────────────────────────────────

    [TestInitialize]
    public void Initialize()
    {
        var dbName = "VectorSearchTest_" + Guid.NewGuid();
        var dbOptions = new DbContextOptionsBuilder<InMemoryContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        _db = new InMemoryContext(dbOptions);
        _cache = new IncidentEmbeddingCache(new LoggerFactory().CreateLogger<IncidentEmbeddingCache>());
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
    }

    private async Task SeedGlobalSettingsAsync(
        bool useAiSearch = false,
        string ollamaInstance = "",
        string embeddingModel = "",
        int cacheLimit = 2000)
    {
        var settings = new[]
        {
            new GlobalSetting { Key = SettingsMap.EnableEmbeddingBasedSearch, Value = useAiSearch ? "True" : "False" },
            new GlobalSetting { Key = SettingsMap.EmbeddingOllamaInstance, Value = ollamaInstance },
            new GlobalSetting { Key = SettingsMap.EmbeddingModel, Value = embeddingModel },
            new GlobalSetting { Key = SettingsMap.EmbeddingQueryCacheLimit, Value = cacheLimit.ToString() }
        };

        foreach (var setting in settings)
        {
            var existing = await _db.GlobalSettings.FirstOrDefaultAsync(s => s.Key == setting.Key);
            if (existing != null)
            {
                existing.Value = setting.Value;
            }
            else
            {
                _db.GlobalSettings.Add(setting);
            }
        }

        await _db.SaveChangesAsync();
    }

    private GlobalSettingsService CreateSettingsService()
    {
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        return new GlobalSettingsService(_db, _config, null!, _memoryCache, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), Microsoft.Extensions.Logging.Abstractions.NullLogger<GlobalSettingsService>.Instance);
    }

    /// <summary>
    /// Seeds incidents with known embeddings so the cache has data for vector search.
    /// Incident "Noodle Soup" gets a vector close to the query vector.
    /// Incident "Bread" gets a vector far from the query vector.
    /// </summary>
    private async Task SeedIncidentsWithEmbeddingsAsync()
    {
        // Vector close to noodle query.
        var noodleSoupVector = EncodeVector(v => { v[0] = 0.9f; v[1] = 0.9f; });
        // Vector far from noodle query (orthogonal-ish).
        var breadVector = EncodeVector(v => { v[2] = 1.0f; v[3] = 1.0f; });

        _db.Incidents.Add(new Incident
        {
            Title = "牛肉面",
            Content = "一碗香喷喷的牛肉面",
            Embedding = noodleSoupVector,
            LastEmbeddedAt = DateTime.UtcNow
        });

        _db.Incidents.Add(new Incident
        {
            Title = "面包",
            Content = "自制手工面包",
            Embedding = breadVector,
            LastEmbeddedAt = DateTime.UtcNow
        });

        _db.Incidents.Add(new Incident
        {
            Title = "未向量化的菜",
            Content = "这道菜还没有生成向量",
            Embedding = null,
            LastEmbeddedAt = DateTime.MinValue
        });

        await _db.SaveChangesAsync();
        await _cache.LoadAsync(_db);
    }

    private static byte[] EncodeVector(Action<float[]> initialize)
    {
        var vector = new float[VectorDimension];
        initialize(vector);
        Aiursoft.WebTools.EmbeddingHelper.Normalize(vector);
        return Aiursoft.WebTools.EmbeddingHelper.Serialize(vector);
    }

    private static string HashKey(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var sb = new StringBuilder(40);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
            if (sb.Length >= 40) break;
        }
        return sb.ToString();
    }

    private IncidentVectorSearchService CreateSearchService(HttpMessageHandler? handler = null)
    {
        var settings = CreateSettingsService();
        var httpClientFactory = handler != null
            ? new TestHttpClientFactory(handler)
            : (IHttpClientFactory)new TestHttpClientFactory(new FakeOllamaEmbedHandler());
        return new IncidentVectorSearchService(_db, _cache, settings, httpClientFactory, new LoggerFactory().CreateLogger<IncidentVectorSearchService>());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tests: three conditions for AI vector search
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Condition1_UseAiSearchDisabled_ReturnsUsedAiFalse()
    {
        await SeedGlobalSettingsAsync(useAiSearch: false, ollamaInstance: "http://localhost:11434", embeddingModel: "bge-m3");
        await SeedIncidentsWithEmbeddingsAsync();

        var service = CreateSearchService();
        var (usedAi, results, _) = await service.SearchAsync(_db.Incidents.AsNoTracking(), "牛肉面", 1, 10);

        Assert.IsFalse(usedAi, "Should NOT use AI search when UseAiSearch is false.");
        Assert.AreEqual(0, results.Count, "Results should be empty when AI search is skipped.");
    }

    [TestMethod]
    public async Task Condition2_OllamaInstanceNotConfigured_ReturnsUsedAiFalse()
    {
        await SeedGlobalSettingsAsync(useAiSearch: true, ollamaInstance: "", embeddingModel: "bge-m3");
        await SeedIncidentsWithEmbeddingsAsync();

        var service = CreateSearchService();
        var (usedAi, results, _) = await service.SearchAsync(_db.Incidents.AsNoTracking(), "牛肉面", 1, 10);

        Assert.IsFalse(usedAi, "Should NOT use AI search when OllamaInstance is empty.");
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task Condition3_EmbeddingModelNotConfigured_ReturnsUsedAiFalse()
    {
        await SeedGlobalSettingsAsync(useAiSearch: true, ollamaInstance: "http://localhost:11434", embeddingModel: "");
        await SeedIncidentsWithEmbeddingsAsync();

        var service = CreateSearchService();
        var (usedAi, results, _) = await service.SearchAsync(_db.Incidents.AsNoTracking(), "牛肉面", 1, 10);

        Assert.IsFalse(usedAi, "Should NOT use AI search when EmbeddingModel is empty.");
        Assert.AreEqual(0, results.Count);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tests: successful vector search
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task AllConditionsMet_VectorSearchSucceeds()
    {
        await SeedGlobalSettingsAsync(useAiSearch: true, ollamaInstance: "http://localhost:11434", embeddingModel: "bge-m3");
        await SeedIncidentsWithEmbeddingsAsync();

        // Fake Ollama that returns a vector close to "noodle" incidents.
        var fakeHandler = new FakeOllamaEmbedHandler(v =>
        {
            v[0] = 1.0f;
            v[1] = 1.0f;
        });

        var service = CreateSearchService(fakeHandler);
        var (usedAi, results, _) = await service.SearchAsync(_db.Incidents.AsNoTracking(), "面", 1, 10);

        Assert.IsTrue(usedAi, "Should use AI search when all conditions are met.");
        Assert.IsTrue(results.Count > 0, "Should return at least one result.");
        // "牛肉面" (noodle soup) should rank higher than "面包" (bread) for query "面" (noodles).
        if (results.Count >= 2)
        {
            Assert.AreEqual("牛肉面", results[0].Title,
                "Semantic search should rank noodle soup above bread for query '面' (noodles).");
        }
    }

    [TestMethod]
    public async Task CallerCancellation_DoesNotTriggerKeywordFallback()
    {
        await SeedGlobalSettingsAsync(useAiSearch: true, ollamaInstance: "http://localhost:11434", embeddingModel: "bge-m3");
        await SeedIncidentsWithEmbeddingsAsync();
        using var cancellation = new CancellationTokenSource();
        var service = CreateSearchService(new CancelCallerHandler(cancellation));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.SearchAsync(_db.Incidents.AsNoTracking(), "cancel search", 1, 10, cancellation.Token));
        Assert.AreEqual(0, await _db.SearchEmbeddings.CountAsync());
    }

    private sealed class CancelCallerHandler(CancellationTokenSource caller) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            caller.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
        }
    }

    [TestMethod]
    public async Task OllamaTimeout_FallsBackToUsedAiFalse()
    {
        await SeedGlobalSettingsAsync(useAiSearch: true, ollamaInstance: "http://localhost:11434", embeddingModel: "bge-m3");
        await SeedIncidentsWithEmbeddingsAsync();

        // Handler that delays 15 seconds (exceeds 10s embed timeout).
        var slowHandler = new SlowOllamaHandler(TimeSpan.FromSeconds(15));

        var service = CreateSearchService(slowHandler);
        var (usedAi, results, _) = await service.SearchAsync(_db.Incidents.AsNoTracking(), "面", 1, 10);

        Assert.IsFalse(usedAi, "Should NOT use AI search when Ollama does not respond within 10 seconds.");
        Assert.AreEqual(0, results.Count, "Should return empty results (caller should fall back to keyword search).");
    }

    [TestMethod]
    public async Task EmptyCache_VectorSearchSkipped()
    {
        await SeedGlobalSettingsAsync(useAiSearch: true, ollamaInstance: "http://localhost:11434", embeddingModel: "bge-m3");
        // Don't seed any embeddings — cache is empty.

        var service = CreateSearchService();
        var (usedAi, results, _) = await service.SearchAsync(_db.Incidents.AsNoTracking(), "牛肉面", 1, 10);

        Assert.IsFalse(usedAi, "Should skip AI search when cache has no embeddings.");
        Assert.AreEqual(0, results.Count);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tests: search embedding database caching (issue #25)
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task EmbeddingIsCachedInDatabase_AfterSuccessfulSearch()
    {
        await SeedGlobalSettingsAsync(useAiSearch: true, ollamaInstance: "http://localhost:11434", embeddingModel: "bge-m3");
        await SeedIncidentsWithEmbeddingsAsync();

        var countingHandler = new CountingOllamaHandler();
        var service = CreateSearchService(countingHandler);
        var (usedAi, _, _) = await service.SearchAsync(_db.Incidents.AsNoTracking(), "牛肉面", 1, 10);

        Assert.IsTrue(usedAi);
        Assert.AreEqual(1, countingHandler.CallCount, "Ollama should be called exactly once for a new query.");

        // Verify the embedding was cached in the database.
        var cachedEntry = await _db.SearchEmbeddings
            .FirstOrDefaultAsync(e => e.QueryText == HashKey("牛肉面"));
        Assert.IsNotNull(cachedEntry, "Search embedding should be persisted to SearchEmbeddings table.");
        Assert.IsTrue(cachedEntry.Embedding.Length == VectorDimension * 4,
            "Cached embedding should be 1024 floats = 4096 bytes.");
    }

    [TestMethod]
    public async Task SecondIdenticalSearch_UsesDatabaseCache_DoesNotCallOllama()
    {
        await SeedGlobalSettingsAsync(useAiSearch: true, ollamaInstance: "http://localhost:11434", embeddingModel: "bge-m3");
        await SeedIncidentsWithEmbeddingsAsync();

        var countingHandler = new CountingOllamaHandler();
        var service = CreateSearchService(countingHandler);

        // First search — Ollama must be called.
        var (usedAi1, _, _) = await service.SearchAsync(_db.Incidents.AsNoTracking(), "红烧肉", 1, 10);
        Assert.IsTrue(usedAi1);
        Assert.AreEqual(1, countingHandler.CallCount);

        // Second search with same query — must use DB cache, NOT call Ollama again.
        var (usedAi2, _, _) = await service.SearchAsync(_db.Incidents.AsNoTracking(), "红烧肉", 1, 10);
        Assert.IsTrue(usedAi2);
        Assert.AreEqual(1, countingHandler.CallCount,
            "Ollama should NOT be called again for a previously cached query.");

        // Different query — Ollama must be called.
        var (usedAi3, _, _) = await service.SearchAsync(_db.Incidents.AsNoTracking(), "鱼香肉丝", 1, 10);
        Assert.IsTrue(usedAi3);
        Assert.AreEqual(2, countingHandler.CallCount,
            "Ollama should be called for a new, uncached query.");
    }

    [TestMethod]
    public async Task CacheTrim_LRUEviction_RemovesLeastRecentlyAccessed()
    {
        await SeedGlobalSettingsAsync(useAiSearch: true, ollamaInstance: "http://localhost:11434", embeddingModel: "bge-m3", cacheLimit: 5);
        await SeedIncidentsWithEmbeddingsAsync();

        var now = DateTime.UtcNow;

        // Seed 5 entries with staggered LastAccessedAt.
        // oldest has been accessed least recently, newest most recently.
        for (var i = 0; i < 5; i++)
        {
            _db.SearchEmbeddings.Add(new SearchEmbedding
            {
                QueryText = HashKey($"query_{i}"),
                Embedding = new byte[VectorDimension * 4],
                CreatedAt = now.AddDays(-10),
                LastAccessedAt = now.AddDays(-10 + i) // 0=distant, 4=recent
            });
        }
        await _db.SaveChangesAsync();

        var countingHandler = new CountingOllamaHandler();
        var service = CreateSearchService(countingHandler);

        // A new search adds one more entry, total becomes 6 > limit 5, triggers trim.
        var (usedAi, _, _) = await service.SearchAsync(_db.Incidents.AsNoTracking(), "新查询", 1, 10);
        Assert.IsTrue(usedAi);

        // The oldest entry (query_0, least recently accessed) should be evicted.
        var allQueries = await _db.SearchEmbeddings.Select(e => e.QueryText).ToListAsync();
        Assert.AreEqual(5, allQueries.Count, "Cache should be trimmed to exactly 5 entries.");
        Assert.IsFalse(allQueries.Contains(HashKey("query_0")), "Least-recently-accessed entry should be evicted.");
        Assert.IsTrue(allQueries.Contains(HashKey("query_4")), "Most-recently-accessed entry should survive.");
        Assert.IsTrue(allQueries.Contains(HashKey("新查询")), "Newly searched query should be cached.");
    }

    [TestMethod]
    public async Task PreCachedQuery_ReturnsImmediatelyWithoutCallingOllama()
    {
        await SeedGlobalSettingsAsync(useAiSearch: true, ollamaInstance: "http://localhost:11434", embeddingModel: "bge-m3");
        await SeedIncidentsWithEmbeddingsAsync();

        // Pre-cache a query embedding in the database (simulating a prior search).
        var queryVector = new float[VectorDimension];
        queryVector[0] = 0.8f;
        queryVector[1] = 0.8f;
        Normalize(queryVector);
        var bytes = Serialize(queryVector);

        _db.SearchEmbeddings.Add(new SearchEmbedding
        {
            QueryText = HashKey("预缓存查询"),
            Embedding = bytes,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var countingHandler = new CountingOllamaHandler();
        var service = CreateSearchService(countingHandler);

        var (usedAi, _, _) = await service.SearchAsync(_db.Incidents.AsNoTracking(), "预缓存查询", 1, 10);
        Assert.IsTrue(usedAi);
        Assert.AreEqual(0, countingHandler.CallCount,
            "Ollama should NOT be called when the query is already cached in the database.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tests: LRU cache behavior
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task CacheHit_UpdatesLastAccessedAt_WhenPastThrottle()
    {
        await SeedGlobalSettingsAsync(useAiSearch: true, ollamaInstance: "http://localhost:11434", embeddingModel: "bge-m3");
        await SeedIncidentsWithEmbeddingsAsync();

        var oldDate = DateTime.UtcNow.AddHours(-2); // older than AccessThrottle (1 hour)

        // Pre-cache a query with an old LastAccessedAt.
        var queryVector = new float[VectorDimension];
        queryVector[0] = 0.8f;
        queryVector[1] = 0.8f;
        Normalize(queryVector);
        var bytes = Serialize(queryVector);

        _db.SearchEmbeddings.Add(new SearchEmbedding
        {
            QueryText = HashKey("旧查询"),
            Embedding = bytes,
            CreatedAt = oldDate,
            LastAccessedAt = oldDate
        });
        await _db.SaveChangesAsync();

        var service = CreateSearchService();
        var (usedAi, _, _) = await service.SearchAsync(_db.Incidents.AsNoTracking(), "旧查询", 1, 10);
        Assert.IsTrue(usedAi);

        // LastAccessedAt should have been bumped to near-now.
        var cached = await _db.SearchEmbeddings
            .AsNoTracking()
            .FirstAsync(e => e.QueryText == HashKey("旧查询"));
        Assert.IsTrue(cached.LastAccessedAt > oldDate.AddHours(1),
            "LastAccessedAt should be updated when past the access throttle window.");
    }

    [TestMethod]
    public async Task CacheHit_SkipsLastAccessedAtUpdate_WithinThrottleWindow()
    {
        await SeedGlobalSettingsAsync(useAiSearch: true, ollamaInstance: "http://localhost:11434", embeddingModel: "bge-m3");
        await SeedIncidentsWithEmbeddingsAsync();

        var justNow = DateTime.UtcNow;

        var queryVector = new float[VectorDimension];
        queryVector[0] = 0.8f;
        queryVector[1] = 0.8f;
        Normalize(queryVector);
        var bytes = Serialize(queryVector);

        _db.SearchEmbeddings.Add(new SearchEmbedding
        {
            QueryText = HashKey("刚缓存"),
            Embedding = bytes,
            CreatedAt = justNow,
            LastAccessedAt = justNow
        });
        await _db.SaveChangesAsync();

        var service = CreateSearchService();
        var (usedAi, _, _) = await service.SearchAsync(_db.Incidents.AsNoTracking(), "刚缓存", 1, 10);
        Assert.IsTrue(usedAi);

        // LastAccessedAt should NOT have been updated — still within the throttle window.
        var cached = await _db.SearchEmbeddings
            .AsNoTracking()
            .FirstAsync(e => e.QueryText == HashKey("刚缓存"));
        Assert.AreEqual(justNow, cached.LastAccessedAt,
            "LastAccessedAt should NOT be updated when still within the access throttle window.");
    }

    [TestMethod]
    public async Task CacheLimit_RespectsConfiguredValue()
    {
        // Set limit to 2 — only 2 query embeddings should survive after a new search.
        await SeedGlobalSettingsAsync(useAiSearch: true, ollamaInstance: "http://localhost:11434", embeddingModel: "bge-m3", cacheLimit: 2);
        await SeedIncidentsWithEmbeddingsAsync();

        var countingHandler = new CountingOllamaHandler();
        var service = CreateSearchService(countingHandler);

        // Search for 3 different queries; each should be cached, but only 2 survive.
        await service.SearchAsync(_db.Incidents.AsNoTracking(), "查询A", 1, 10);
        await service.SearchAsync(_db.Incidents.AsNoTracking(), "查询B", 1, 10);
        await service.SearchAsync(_db.Incidents.AsNoTracking(), "查询C", 1, 10);

        var count = await _db.SearchEmbeddings.CountAsync();
        Assert.AreEqual(2, count, "Cache should be trimmed to exactly the configured limit of 2.");

        // The first query (least recently accessed) should be evicted.
        var existsA = await _db.SearchEmbeddings.AnyAsync(e => e.QueryText == HashKey("查询A"));
        Assert.IsFalse(existsA, "Least-recently-accessed query should be evicted when limit exceeded.");
    }

    [TestMethod]
    public async Task InvisibleIncidentsNeverAffectResultsCountsOrPagination()
    {
        await SeedGlobalSettingsAsync(useAiSearch: true, ollamaInstance: "http://localhost:11434", embeddingModel: "bge-m3");
        var allowedApplication = Guid.NewGuid();
        var visible = Enumerable.Range(0, 3).Select(index => new Incident
        {
            ApplicationId = allowedApplication, Title = $"Visible {index}", Level = "sev1",
            Embedding = EncodeVector(v => { v[0] = 0.8f; v[1] = 0.6f; })
        }).ToList();
        _db.Incidents.AddRange(visible);
        _db.Incidents.AddRange(
            new Incident { ApplicationId = Guid.NewGuid(), Title = "Inaccessible app", Level = "sev1",
                Embedding = EncodeVector(v => { v[0] = 1; v[1] = 1; }) },
            new Incident { ApplicationId = allowedApplication, Title = "Hidden severity", Level = "sev4",
                Embedding = EncodeVector(v => { v[0] = 1; v[1] = 1; }) });
        await _db.SaveChangesAsync();
        await _cache.LoadAsync(_db);
        var policy = new IncidentSeverityPolicy([new SeverityRule { Level = "sev1", ShowInLists = true }]);
        var baseQuery = _db.Incidents.AsNoTracking().Where(r => r.ApplicationId == allowedApplication)
            .Where(policy.VisibleIncidents);
        var service = CreateSearchService();

        var first = await service.SearchAsync(baseQuery, "outage", 1, 2);
        var second = await service.SearchAsync(baseQuery, "outage", 2, 2);
        var third = await service.SearchAsync(baseQuery, "outage", 3, 2);

        Assert.IsTrue(first.UsedAi && second.UsedAi && third.UsedAi);
        Assert.AreEqual(3, first.TotalCount);
        Assert.AreEqual(3, second.TotalCount);
        Assert.AreEqual(3, third.TotalCount);
        Assert.AreEqual(2, first.Results.Count);
        Assert.AreEqual(1, second.Results.Count);
        Assert.AreEqual(0, third.Results.Count);
        CollectionAssert.AreEquivalent(visible.Select(r => r.Id).ToArray(),
            first.Results.Concat(second.Results).Select(r => r.Id).ToArray());
        var none = await service.SearchAsync(baseQuery.Where(r => false), "outage", 1, 2);
        Assert.AreEqual(0, none.TotalCount);
        Assert.AreEqual(0, none.Results.Count);
    }

    [TestMethod]
    public async Task CacheRetainsLatestTenThousandIncidentsAndSkipsInvalidVectors()
    {
        var start = DateTime.UtcNow;
        var incidents = Enumerable.Range(0, 10002).Select(index => new Incident
        {
            Title = $"Incident {index}", ReceivedAtUtc = start.AddSeconds(index),
            Embedding = Serialize([1f, 0f])
        }).ToArray();
        _db.Incidents.AddRange(incidents);
        await _db.SaveChangesAsync();
        await _cache.LoadAsync(_db);
        Assert.AreEqual(10000, _cache.Count);
        var snapshot = _cache.Snapshot();
        Assert.IsFalse(snapshot.ContainsKey(incidents[0].Id));
        Assert.IsFalse(snapshot.ContainsKey(incidents[1].Id));
        Assert.IsTrue(snapshot.ContainsKey(incidents[^1].Id));
        incidents[^1].Embedding = [1, 2, 3];
        await _db.SaveChangesAsync();
        await _cache.LoadAsync(_db);
        Assert.AreEqual(9999, _cache.Count);
        Assert.IsFalse(_cache.Snapshot().ContainsKey(incidents[^1].Id));
    }

    [TestMethod]
    public async Task LongQueriesWithSharedPrefixHaveDistinctCacheKeys()
    {
        await SeedGlobalSettingsAsync(useAiSearch: true, ollamaInstance: "http://localhost:11434", embeddingModel: "bge-m3");
        await SeedIncidentsWithEmbeddingsAsync();
        var handler = new CountingOllamaHandler();
        var service = CreateSearchService(handler);
        var prefix = new string('a', 100);
        await service.SearchAsync(_db.Incidents.AsNoTracking(), prefix + "one", 1, 10);
        await service.SearchAsync(_db.Incidents.AsNoTracking(), prefix + "two", 1, 10);
        Assert.AreEqual(2, handler.CallCount);
        Assert.AreEqual(2, await _db.SearchEmbeddings.CountAsync());
        Assert.IsTrue(await _db.SearchEmbeddings.AllAsync(e => e.QueryText.Length == 40));
    }

    private class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private class FakeOllamaEmbedHandler : HttpMessageHandler
    {
        private readonly Action<float[]>? _initVector;

        /// <param name="initVector">
        /// Optional: custom initializer for the returned 1024-dim vector.
        /// Defaults to the noodle-query vector (dims 0,1 = 1.0).
        /// </param>
        public FakeOllamaEmbedHandler(Action<float[]>? initVector = null)
        {
            _initVector = initVector;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var vector = new float[VectorDimension];
            if (_initVector != null)
            {
                _initVector(vector);
            }
            else
            {
                vector[0] = 1.0f;
                vector[1] = 1.0f;
            }

            var response = new
            {
                embeddings = new[] { vector }
            };

            var json = JsonConvert.SerializeObject(response);
            var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(httpResponse);
        }
    }

    private class SlowOllamaHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;

        public SlowOllamaHandler(TimeSpan delay) => _delay = delay;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            try
            {
                await Task.Delay(_delay, ct);
            }
            catch (OperationCanceledException)
            {
                // The caller's timeout CTS cancelled us — that's the expected path.
            }

            // If we reach here, return OK (the timeout already fired upstream).
            var response = new { embeddings = new[] { new float[VectorDimension] } };
            var json = JsonConvert.SerializeObject(response);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>
    /// Fake Ollama handler that counts how many times it was invoked.
    /// Used to verify that repeated queries hit the database cache instead of calling Ollama.
    /// </summary>
    private class CountingOllamaHandler : HttpMessageHandler
    {
        public int CallCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref CallCount);

            var vector = new float[VectorDimension];
            vector[0] = 1.0f;
            vector[1] = 1.0f;

            var response = new
            {
                embeddings = new[] { vector }
            };

            var json = JsonConvert.SerializeObject(response);
            var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(httpResponse);
        }
    }
}

using System.Net;
using System.Text.Json;
using Aiursoft.Canon;
using Aiursoft.AlertCenter.Configuration;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.InMemory;
using Aiursoft.AlertCenter.Sqlite;
using Aiursoft.AlertCenter.MySql;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Services.BackgroundJobs;
using Aiursoft.WebTools;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class EmbeddingRetryTests
{
    private InMemoryContext _db = null!;
    private MemoryCache _memory = null!;
    private GlobalSettingsService _settings = null!;
    private IncidentEmbeddingCache _cache = null!;
    private IncidentVectorSearchService _service = null!;
    private Incident _item = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _db = new InMemoryContext(new DbContextOptionsBuilder<InMemoryContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _memory = new MemoryCache(new MemoryCacheOptions());
        _settings = new GlobalSettingsService(_db, new ConfigurationBuilder().Build(), null!, _memory,
            new EphemeralDataProtectionProvider(), NullLogger<GlobalSettingsService>.Instance);
        _db.GlobalSettings.AddRange(
            new GlobalSetting { Key = SettingsMap.EnableEmbeddingBasedSearch, Value = "True" },
            new GlobalSetting { Key = SettingsMap.EmbeddingOllamaInstance, Value = "https://embedding.example.com/ignored/path" },
            new GlobalSetting { Key = SettingsMap.EmbeddingApiToken, Value = "test-token" },
            new GlobalSetting { Key = SettingsMap.EmbeddingModel, Value = "test-embedding-model" });
        _item = new Incident
        {
            Title = "Matching incident", Content = new string('a', 10000),
            Embedding = EmbeddingHelper.Serialize([1f, 0f])
        };
        _db.Incidents.Add(_item);
        await _db.SaveChangesAsync();
        _cache = new IncidentEmbeddingCache(NullLogger<IncidentEmbeddingCache>.Instance);
        await _cache.LoadAsync(_db);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        _memory.Dispose();
    }

    [TestMethod]
    [DataRow(HttpStatusCode.BadRequest)]
    [DataRow(HttpStatusCode.RequestEntityTooLarge)]
    public async Task QueryShrinksContextErrorsAndCachesOnlyTheSuccessfulVector(HttpStatusCode status)
    {
        using var handler = new EmbeddingHandler { ErrorStatus = status };
        _service = new IncidentVectorSearchService(_db, _cache, _settings, handler, NullLogger<IncidentVectorSearchService>.Instance);
        var query = "Introduction " + new string('a', 10000) + " Conclusion";

        var result = await _service.SearchAsync(_db.Incidents.AsNoTracking(), query, 1, 10);

        Assert.IsTrue(result.UsedAi);
        Assert.AreEqual(1, result.TotalCount);
        CollectionAssert.AreEqual(new[] { 8000, 4000, 2000 }, handler.Inputs.Select(input => input.Length).ToArray());
        foreach (var input in handler.Inputs) Assert.AreEqual(query[..input.Length], input);
        Assert.AreEqual(1, await _db.SearchEmbeddings.CountAsync());
        await _service.SearchAsync(_db.Incidents.AsNoTracking(), query, 1, 10);
        Assert.AreEqual(3, handler.Inputs.Count, "Successful query embeddings must be reused.");
    }

    [TestMethod]
    [DataRow(HttpStatusCode.BadRequest, "Request body length exceeds the limit")]
    [DataRow(HttpStatusCode.RequestEntityTooLarge, "Request body length exceeds the limit")]
    [DataRow(HttpStatusCode.RequestEntityTooLarge, "Context service unavailable")]
    [DataRow(HttpStatusCode.InternalServerError, "context length exceeded")]
    public async Task UnrelatedQueryErrorsFallBackWithoutShrinking(HttpStatusCode status, string error)
    {
        using var handler = new EmbeddingHandler { ErrorStatus = status, ErrorMessage = error, ContextLimit = 0 };
        _service = new IncidentVectorSearchService(_db, _cache, _settings, handler, NullLogger<IncidentVectorSearchService>.Instance);
        var query = new string('a', 10000);

        var result = await _service.SearchAsync(_db.Incidents.AsNoTracking(), query, 1, 10);

        Assert.IsFalse(result.UsedAi);
        Assert.AreEqual(1, handler.Inputs.Count);
        Assert.AreEqual(8000, handler.Inputs[0].Length);
        Assert.AreEqual(0, await _db.SearchEmbeddings.CountAsync());
    }

    [TestMethod]
    public async Task RepeatedContextErrorsStopAtTheMinimumBudget()
    {
        using var handler = new EmbeddingHandler { ContextLimit = 0 };
        _service = new IncidentVectorSearchService(_db, _cache, _settings, handler, NullLogger<IncidentVectorSearchService>.Instance);
        var query = new string('a', 10000);

        var result = await _service.SearchAsync(_db.Incidents.AsNoTracking(), query, 1, 10);

        Assert.IsFalse(result.UsedAi);
        CollectionAssert.AreEqual(new[] { 8000, 4000, 2000, 1000, 500 }, handler.Inputs.Select(input => input.Length).ToArray());
        Assert.AreEqual(0, await _db.SearchEmbeddings.CountAsync());
    }

    [TestMethod]
    [DataRow(HttpStatusCode.BadRequest, "Request body length exceeds the limit")]
    [DataRow(HttpStatusCode.RequestEntityTooLarge, "Request body length exceeds the limit")]
    [DataRow(HttpStatusCode.InternalServerError, "context length exceeded")]
    public async Task BackgroundErrorsNeverRetryWithUnrelatedTruncation(HttpStatusCode status, string error)
    {
        _item.Embedding = null;
        await _db.SaveChangesAsync();
        using var handler = new EmbeddingHandler { ErrorStatus = status, ErrorMessage = error, ContextLimit = 0 };
        var retry = new RetryEngine(NullLogger<RetryEngine>.Instance);
        var job = new GenerateEmbeddingsJob(_db, _settings, handler, retry, NullLogger<GenerateEmbeddingsJob>.Instance);

        await job.ExecuteAsync();

        Assert.IsTrue(handler.Inputs.Count > 0);
        Assert.IsTrue(handler.Inputs.All(input => input.Length == 8000), "Transient job retries must not shrink unrelated failures.");
        Assert.IsNull(_item.Embedding);
    }

    [TestMethod]
    public async Task BackgroundContextRetryPreservesHeadAndTail()
    {
        using var handler = new EmbeddingHandler();
        var job = new GenerateEmbeddingsJob(_db, _settings, handler,
            new RetryEngine(NullLogger<RetryEngine>.Instance), NullLogger<GenerateEmbeddingsJob>.Instance);
        var text = "Introduction " + new string('a', 10000) + " Conclusion";
        var vector = await job.CallEmbedApiAsync("https://embedding.example.com/ignored", "test-embedding-model", "test-token", text, "incident");
        CollectionAssert.AreEqual(new[] { 8000, 4000, 2000 }, handler.Inputs.Select(input => input.Length).ToArray());
        foreach (var input in handler.Inputs)
        {
            Assert.StartsWith("Introduction ", input);
            Assert.EndsWith(" Conclusion", input);
            Assert.Contains("\n...\n", input);
        }
        CollectionAssert.AreEqual(new[] { 1f, 0f }, vector);
    }

    [TestMethod]
    public async Task SqliteGenerationProcessesFiftyDocumentsOnceWithoutApplicationNames()
    {
        await using var db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GlobalSettings:EnableEmbeddingBasedSearch"] = "True",
            ["GlobalSettings:EmbeddingOllamaInstance"] = "https://embedding.example.com/ignored",
            ["GlobalSettings:EmbeddingApiToken"] = "test-token",
            ["GlobalSettings:EmbeddingModel"] = "test-embedding-model"
        }).Build();
        var settings = new GlobalSettingsService(db, config, null!, memory,
            new EphemeralDataProtectionProvider(), NullLogger<GlobalSettingsService>.Instance);
        var application = new SourceApplication { Name = "Do not embed application names" };
        db.Incidents.AddRange(Enumerable.Range(0, 55).Select(index => new Incident
        {
            Application = application, Title = $"Incident {index}", Content = "Evidence"
        }));
        await db.SaveChangesAsync();
        using var handler = new EmbeddingHandler();
        var job = new GenerateEmbeddingsJob(db, settings, handler,
            new RetryEngine(NullLogger<RetryEngine>.Instance), NullLogger<GenerateEmbeddingsJob>.Instance);

        await job.ExecuteAsync();
        Assert.AreEqual(GenerateEmbeddingsJob.MaxDocumentsPerRun, handler.Inputs.Count);
        Assert.AreEqual(50, await db.Incidents.CountAsync(r => r.Embedding != null && r.LastEmbeddedAt > DateTime.MinValue));
        Assert.IsTrue(handler.Inputs.All(input => input.StartsWith("Incident ") && input.EndsWith("Evidence" + Environment.NewLine)));
        Assert.IsTrue(handler.Inputs.All(input => !input.Contains(application.Name)));

        application.Name = "Renamed application";
        await db.SaveChangesAsync();
        await job.ExecuteAsync();
        await job.ExecuteAsync();
        Assert.AreEqual(55, handler.Inputs.Count);
        Assert.AreEqual(55, await db.Incidents.CountAsync(r => r.Embedding != null));
        db.ChangeTracker.Clear();
        var deleted = await db.Incidents.FirstAsync();
        db.Incidents.Remove(deleted);
        await db.SaveChangesAsync();
        Assert.IsFalse(await GenerateEmbeddingsJob.TrySaveEmbeddingIfIncidentUnchangedAsync(db, deleted, [1, 2, 3, 4]));
    }

    [TestMethod]
    public void MySqlPendingGuidCursorTranslatesToSql()
    {
        using var db = new MySqlContext(new DbContextOptionsBuilder<MySqlContext>()
            .UseMySql("Server=localhost;Database=test;Uid=root;Pwd=password;", new MySqlServerVersion(new Version(8, 0, 31))).Options);
        var lastId = Guid.NewGuid();
        var sql = db.Incidents.Where(r => r.Id.CompareTo(lastId) > 0 &&
                (r.Embedding == null || r.LastEmbeddedAt == DateTime.MinValue))
            .OrderBy(r => r.Id).Take(10).ToQueryString();
        Assert.Contains("LIMIT", sql);
        Assert.Contains("ORDER BY", sql);
    }

    private sealed class EmbeddingHandler : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Inputs { get; } = [];
        public HttpStatusCode ErrorStatus { get; init; } = HttpStatusCode.RequestEntityTooLarge;
        public string ErrorMessage { get; init; } = "{\"error\":\"the input length exceeds the context length\"}";
        public int ContextLimit { get; init; } = 2000;

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("/api/embed", request.RequestUri!.AbsolutePath);
            Assert.AreEqual("?keep_alive=-1", request.RequestUri.Query);
            Assert.AreEqual("embedding.example.com", request.RequestUri.Host);
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("test-token", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.AreEqual("test-embedding-model", body.RootElement.GetProperty("model").GetString());
            var input = body.RootElement.GetProperty("input").GetString()!;
            Inputs.Add(input);
            return input.Length > ContextLimit
                ? new HttpResponseMessage(ErrorStatus) { Content = new StringContent(ErrorMessage) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { embeddings = new[] { new[] { 1f, 0f } } }) };
        }
    }
}

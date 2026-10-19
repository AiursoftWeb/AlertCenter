using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Aiursoft.AiurProtocol.Exceptions;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.SDK;
using Aiursoft.AlertCenter.SDK.Models;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class IncidentPagesTests
{
    [TestMethod]
    public async Task MetadataPatchPreservesOtherPropertiesAndLifecycleAndUsesStrictVersions()
    {
        var original = await CreateApplication();
        var app = Guid.Parse(original.Split('.')[0]);
        var key = await CreateApplicationKey(app, metadata: true);
        await using var services = ReportingClient(key);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var receipt = await client.CreateIncidentAsync(new CreateIncidentRequest
        {
            ReportId = Guid.NewGuid(), Title = "Metadata", OccurredAtUtc = DateTimeOffset.UtcNow,
            Context = new JObject { ["original"] = true },
            Metadata = IncidentMetadata.Read("""{"source":"gitlab:550:master","ci":{"count":1},"Keep":"unchanged","timestamp":"2026-10-19T00:00:00+08:00"}""")
        });
        var before = (await client.GetIncidentAsync(receipt.IncidentId)).Incident;
        var patch = new PatchIncidentMetadataRequest { Version = before.Version, Property = "ci", Value = JObject.Parse("""{"count":2,"pipeline":42}""") };
        var result = await client.PatchIncidentMetadataAsync(receipt.IncidentId, patch);
        Assert.AreEqual(Code.JobDone, result.Code);
        Assert.AreNotEqual(before.Version, result.Version);
        Assert.AreEqual("unchanged", result.Metadata.Value<string>("Keep"));
        Assert.AreEqual(JTokenType.String, result.Metadata["timestamp"]!.Type);
        Assert.AreEqual("2026-10-19T00:00:00+08:00", result.Metadata.Value<string>("timestamp"));
        Assert.AreEqual("gitlab:550:master", result.Metadata.Value<string>("source"));
        Assert.AreEqual(2, result.Metadata["ci"]!.Value<int>("count"));
        var stale = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.PatchIncidentMetadataAsync(receipt.IncidentId, patch));
        Assert.AreEqual(Code.Conflict, stale.Response.Code);
        patch.Version = result.Version;
        var noChange = await client.PatchIncidentMetadataAsync(receipt.IncidentId, patch);
        Assert.AreEqual(Code.NoActionTaken, noChange.Code);
        Assert.AreEqual(result.Version, noChange.Version);
        patch.Property = "ci.count";
        patch.Value = JValue.CreateNull();
        var literal = await client.PatchIncidentMetadataAsync(receipt.IncidentId, patch);
        Assert.IsTrue(literal.Metadata.ContainsKey("ci.count"));
        Assert.AreEqual(JTokenType.Null, literal.Metadata["ci.count"]!.Type);
        Assert.AreEqual(2, literal.Metadata["ci"]!.Value<int>("count"));
        var after = (await client.GetIncidentAsync(receipt.IncidentId)).Incident;
        Assert.AreEqual(before.Status, after.Status);
        Assert.AreEqual(before.Level, after.Level);
        Assert.AreEqual(before.OwnerId, after.OwnerId);
        Assert.AreEqual(before.AcknowledgedAtUtc, after.AcknowledgedAtUtc);
        Assert.IsTrue(JToken.DeepEquals(before.Context, after.Context));
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var entries = await db.IncidentTimelineEntries.Where(e => e.Type == IncidentTimelineEntry.Updated).ToListAsync();
        Assert.HasCount(2, entries);
        Assert.IsTrue(entries.All(e => e.ActorIdSnapshot == $"application:{app}" && e.ActorName == "Application"));
        Assert.AreEqual(0, await db.PushDeliveries.CountAsync());
    }

    [TestMethod]
    public async Task MetadataQueriesAreExactTypedScopedAndPaginatedAfterFiltering()
    {
        var key = await CreateApplication();
        await using var services = ReportingClient(key);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        foreach (var value in new JToken[] { new JValue("Alpha"), new JValue("Alpha"), new JValue("alpha"), new JValue("Alpha "), new JValue(42), new JValue("42"), JValue.CreateNull(), new JObject { ["value"] = "Alpha" }, new JArray("Alpha"), new JValue(""), new JValue(" ") })
            await client.CreateIncidentAsync(new CreateIncidentRequest
            {
                ReportId = Guid.NewGuid(), Title = "Metadata search", OccurredAtUtc = DateTimeOffset.UtcNow,
                Metadata = new JObject { ["source"] = value, ["odd.\"key"] = "literal" }
            });
        var query = new QueryIncidentsRequest { MetadataKey = "source", MetadataValue = "Alpha", PageSize = 1 };
        var first = await client.QueryIncidentsAsync(query);
        Assert.AreEqual(2, first.TotalCount);
        Assert.HasCount(1, first.Items);
        query.Page = 2;
        var second = await client.QueryIncidentsAsync(query);
        Assert.AreNotEqual(first.Items[0].IncidentId, second.Items[0].IncidentId);
        query.MetadataKey = "Source";
        Assert.AreEqual(0, (await client.QueryIncidentsAsync(query)).TotalCount);
        query.MetadataKey = "source"; query.MetadataValue = "42"; query.Page = 1;
        Assert.AreEqual(1, (await client.QueryIncidentsAsync(query)).TotalCount);
        query.MetadataValue = "";
        Assert.AreEqual(1, (await client.QueryIncidentsAsync(query)).TotalCount);
        query.MetadataValue = " ";
        Assert.AreEqual(1, (await client.QueryIncidentsAsync(query)).TotalCount);
        query.MetadataKey = "odd.\"key"; query.MetadataValue = "literal";
        Assert.AreEqual(11, (await client.QueryIncidentsAsync(query)).TotalCount);
        query.MetadataKey = "source') OR 1=1 --";
        Assert.AreEqual(0, (await client.QueryIncidentsAsync(query)).TotalCount);
        await using var foreign = ReportingClient(await CreateApplication());
        Assert.AreEqual(0, (await foreign.GetRequiredService<AlertCenterApiClient>().QueryIncidentsAsync(new QueryIncidentsRequest { MetadataKey = "source", MetadataValue = "Alpha" })).TotalCount);
        query.MetadataKey = "source"; query.MetadataValue = null;
        await Assert.ThrowsAsync<AiurBadApiInputException>(() => client.QueryIncidentsAsync(query));
    }

    [TestMethod]
    public async Task MetadataPatchEnforcesIndependentScopeAndHidesForeignIncidents()
    {
        var original = await CreateApplication();
        var app = Guid.Parse(original.Split('.')[0]);
        var key = await CreateApplicationKey(app, query: false, create: false, resolve: false, metadata: true);
        await using var originalServices = ReportingClient(original);
        var client = originalServices.GetRequiredService<AlertCenterApiClient>();
        var receipt = await client.CreateIncidentAsync(new CreateIncidentRequest { ReportId = Guid.NewGuid(), Title = "Scope", OccurredAtUtc = DateTimeOffset.UtcNow });
        var before = (await client.GetIncidentAsync(receipt.IncidentId)).Incident;
        var request = new PatchIncidentMetadataRequest { Version = before.Version, Property = "count", Value = new JValue(1) };
        var denied = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.PatchIncidentMetadataAsync(receipt.IncidentId, request));
        Assert.AreEqual(Code.Unauthorized, denied.Response.Code);
        await using var services = ReportingClient(key);
        var patchClient = services.GetRequiredService<AlertCenterApiClient>();
        await patchClient.PatchIncidentMetadataAsync(receipt.IncidentId, request);
        var queryDenied = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => patchClient.GetIncidentAsync(receipt.IncidentId));
        Assert.AreEqual(Code.Unauthorized, queryDenied.Response.Code);
        var foreignKey = await CreateApplicationKey(Guid.Parse((await CreateApplication()).Split('.')[0]), metadata: true);
        await using var other = ReportingClient(foreignKey);
        var foreign = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => other.GetRequiredService<AlertCenterApiClient>().PatchIncidentMetadataAsync(receipt.IncidentId, request));
        var missing = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => patchClient.PatchIncidentMetadataAsync(Guid.NewGuid(), request));
        Assert.AreEqual(Code.NotFound, foreign.Response.Code);
        Assert.AreEqual(missing.Response.Message, foreign.Response.Message);
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        (await db.ApplicationApiKeys.SingleAsync(k => k.Id == Guid.Parse(key.Split('.')[1]))).IsEnabled = false;
        await db.SaveChangesAsync();
        var disabled = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => patchClient.PatchIncidentMetadataAsync(receipt.IncidentId, request));
        Assert.AreEqual(Code.WrongKey, disabled.Response.Code);
    }

    [TestMethod]
    public async Task ConcurrentMetadataUpdatesHaveOneWinnerWithoutLostPropertiesOrAudit()
    {
        var full = await CreateApplication();
        var key = await CreateApplicationKey(Guid.Parse(full.Split('.')[0]), metadata: true);
        await using var services = ReportingClient(key);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var receipt = await client.CreateIncidentAsync(new CreateIncidentRequest { ReportId = Guid.NewGuid(), Title = "Concurrent patch", OccurredAtUtc = DateTimeOffset.UtcNow });
        var version = (await client.GetIncidentAsync(receipt.IncidentId)).Incident.Version;
        async Task<Code> Patch(string property)
        {
            try { return (await client.PatchIncidentMetadataAsync(receipt.IncidentId, new PatchIncidentMetadataRequest { Version = version, Property = property, Value = new JValue(1) })).Code; }
            catch (AiurUnexpectedServerResponseException e) { return e.Response.Code; }
        }
        var results = await Task.WhenAll(Patch("first"), Patch("second"));
        Assert.AreEqual(1, results.Count(c => c == Code.JobDone));
        Assert.AreEqual(1, results.Count(c => c == Code.Conflict));
        var current = (await client.GetIncidentAsync(receipt.IncidentId)).Incident;
        Assert.AreEqual(1, current.Metadata.Count);
        var missing = current.Metadata.ContainsKey("first") ? "second" : "first";
        await client.PatchIncidentMetadataAsync(receipt.IncidentId, new PatchIncidentMetadataRequest { Version = current.Version, Property = missing, Value = new JValue(1) });
        Assert.AreEqual(2, (await client.GetIncidentAsync(receipt.IncidentId)).Incident.Metadata.Count);
        using var scope = Server!.Services.CreateScope();
        Assert.AreEqual(2, await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().IncidentTimelineEntries.CountAsync(e => e.Type == IncidentTimelineEntry.Updated));
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{broken")]
    [DataRow("missing")]
    [DataRow("property-null")]
    [DataRow("oversized")]
    public async Task MetadataPatchRejectsInvalidBodiesWithoutWriting(string input)
    {
        var original = await CreateApplication();
        var key = await CreateApplicationKey(Guid.Parse(original.Split('.')[0]), metadata: true);
        await using var services = ReportingClient(key);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var receipt = await client.CreateIncidentAsync(new CreateIncidentRequest { ReportId = Guid.NewGuid(), Title = "Invalid metadata", OccurredAtUtc = DateTimeOffset.UtcNow });
        var version = (await client.GetIncidentAsync(receipt.IncidentId)).Incident.Version;
        var body = input switch
        {
            "missing" => new JObject { ["Version"] = version, ["Property"] = "ci" }.ToString(),
            "property-null" => new JObject { ["Version"] = version, ["Property"] = null, ["Value"] = 1 }.ToString(),
            "oversized" => new JObject { ["Version"] = version, ["Property"] = "ci", ["Value"] = new string('x', 17000) }.ToString(),
            _ => input
        };
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/incidents/{receipt.IncidentId}/metadata");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var protocol = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual((int)Code.InvalidInput, protocol.Value<int>("Code"));
        Assert.IsTrue(protocol["Items"] is JArray { Count: > 0 });
        Assert.AreEqual(version, (await client.GetIncidentAsync(receipt.IncidentId)).Incident.Version);
    }

    [TestMethod]
    public async Task CliCreatesFindsAndPatchesMetadataWithRequiredCredentialsAndVersion()
    {
        var full = await CreateApplication();
        var key = await CreateApplicationKey(Guid.Parse(full.Split('.')[0]), metadata: true);
        var created = CliJson(await RunIncidentCli(key, "create", "--report-id", Guid.NewGuid().ToString(), "--title", "CLI metadata", "--level", "sev3", "--occurred-at", "2026-10-19T00:00:00Z", "--metadata", """{"source":"gitlab:550:master"}""", "--json"));
        var id = created.Value<string>("incidentId")!;
        var found = CliJson(await RunIncidentCli(key, "list", "--metadata-key", "source", "--metadata-value", "gitlab:550:master", "--json"));
        Assert.AreEqual(1, found.Value<int>("totalCount"));
        var version = found["items"]![0]!.Value<string>("version")!;
        string[] args = ["metadata", "patch", "--server", Http.BaseAddress!.ToString(), "--api-key", key, "--id", id, "--version", version, "--property", "ci", "--value", """{"count":1}"""];
        var patched = await IncidentCli().TestRunAsync([.. args, "--json"]);
        Assert.AreEqual(1, CliJson(patched)["metadata"]!["ci"]!.Value<int>("count"));
        var stale = await IncidentCli().TestRunAsync(args);
        Assert.AreNotEqual(0, stale.ProgramReturn);
        Assert.Contains("Conflict", stale.StdErr);
        var missingKey = await IncidentCli().TestRunAsync(["metadata", "patch", "--server", Http.BaseAddress!.ToString(), "--id", id, "--version", version, "--property", "ci", "--value", "1"]);
        Assert.AreNotEqual(0, missingKey.ProgramReturn);
        Assert.Contains("--api-key", missingKey.StdErr);
        var invalid = await IncidentCli().TestRunAsync([.. args[..^1], "{broken"]);
        Assert.AreNotEqual(0, invalid.ProgramReturn);
        Assert.DoesNotContain(key, patched.StdOut + patched.StdErr + invalid.StdErr);
        var details = await RunIncidentCli(key, "get", "--id", id);
        Assert.Contains("Metadata:", details.StdOut);
    }
    [TestMethod]
    [DataRow("1")]
    [DataRow("true")]
    [DataRow("null")]
    [DataRow("[1,2]")]
    [DataRow("\"2026-10-19T00:00:00+08:00\"")]
    public async Task MetadataPatchPreservesJsonValueTypesAndWorksAfterResolution(string json)
    {
        var full = await CreateApplication();
        var key = await CreateApplicationKey(Guid.Parse(full.Split('.')[0]), metadata: true);
        await using var services = ReportingClient(key);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var receipt = await client.CreateIncidentAsync(new CreateIncidentRequest { ReportId = Guid.NewGuid(), Title = "JSON types", OccurredAtUtc = DateTimeOffset.UtcNow });
        var before = (await client.GetIncidentAsync(receipt.IncidentId)).Incident;
        var resolved = await client.ResolveIncidentAsync(receipt.IncidentId, new ResolveIncidentRequest { Version = before.Version, Reason = "Closed" });
        var value = IncidentMetadata.Read("{\"value\":" + json + "}")["value"]!;
        var patched = await client.PatchIncidentMetadataAsync(receipt.IncidentId, new PatchIncidentMetadataRequest { Version = resolved.Version, Property = "value", Value = value });
        Assert.IsTrue(JToken.DeepEquals(value, patched.Metadata["value"]));
        var saved = (await client.GetIncidentAsync(receipt.IncidentId)).Incident;
        Assert.AreEqual(Incident.Resolved, saved.Status);
        Assert.IsTrue(JToken.DeepEquals(value, saved.Metadata["value"]));
    }

    [TestMethod]
    public async Task MetadataPatchChecksCombinedSizeAndDoesNotInvalidateVersionOnRejection()
    {
        var full = await CreateApplication();
        var key = await CreateApplicationKey(Guid.Parse(full.Split('.')[0]), metadata: true);
        await using var services = ReportingClient(key);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var receipt = await client.CreateIncidentAsync(new CreateIncidentRequest
        {
            ReportId = Guid.NewGuid(), Title = "Metadata limits", OccurredAtUtc = DateTimeOffset.UtcNow,
            Metadata = new JObject { ["keep"] = new string('x', 9000) }
        });
        var before = (await client.GetIncidentAsync(receipt.IncidentId)).Incident;
        await Assert.ThrowsAsync<AiurBadApiInputException>(() => client.PatchIncidentMetadataAsync(receipt.IncidentId,
            new PatchIncidentMetadataRequest { Version = before.Version, Property = "another", Value = new JValue(new string('x', 9000)) }));
        var after = (await client.GetIncidentAsync(receipt.IncidentId)).Incident;
        Assert.AreEqual(before.Version, after.Version);
        Assert.IsTrue(JToken.DeepEquals(before.Metadata, after.Metadata));
        var patch = await client.PatchIncidentMetadataAsync(receipt.IncidentId,
            new PatchIncidentMetadataRequest { Version = after.Version, Property = "keep", Value = new JValue("short") });
        var staleResolution = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.ResolveIncidentAsync(receipt.IncidentId,
            new ResolveIncidentRequest { Version = before.Version, Reason = "Stale" }));
        Assert.AreEqual(Code.Conflict, staleResolution.Response.Code);
        Assert.AreEqual(patch.Version, (await client.GetIncidentAsync(receipt.IncidentId)).Incident.Version);
    }

}

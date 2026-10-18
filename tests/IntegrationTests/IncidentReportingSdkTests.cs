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
    private ServiceProvider ReportingClient(string key)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlertCenterSdk(Http.BaseAddress!.ToString(), key);
        return services.BuildServiceProvider();
    }

    [TestMethod]
    [DataRow("", 400, false)]
    [DataRow("{broken", 400, false)]
    [DataRow("oversized", 413, false)]
    [DataRow("oversized", 413, true)]
    public async Task ResolutionBodyErrorsUseAiurProtocol(string body, int expectedStatus, bool chunked)
    {
        if (body == "oversized") body = "{\"Reason\":\"" + new string('x', 66000) + "\"}";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/incidents/{Guid.NewGuid()}/resolve");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.TransferEncodingChunked = chunked;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await CreateApplication());
        using var response = await Http.SendAsync(request);
        Assert.AreEqual((HttpStatusCode)expectedStatus, response.StatusCode);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        var error = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual((int)Code.InvalidInput, error.Value<int>("Code"));
        Assert.IsTrue(error["Items"] is JArray { Count: > 0 });
    }

    [TestMethod]
    [DataRow(Incident.New)]
    [DataRow(Incident.Acked)]
    [DataRow(Incident.Mitigated)]
    public async Task SdkResolvesActiveIncidentsOnceWithApplicationAttribution(string status)
    {
        var key = await CreateApplication();
        await using var services = ReportingClient(key);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var receipt = await client.CreateIncidentAsync(new CreateIncidentRequest
        {
            ReportId = Guid.NewGuid(), Title = "Service recovery", OccurredAtUtc = DateTimeOffset.UtcNow
        });
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var incident = await db.Incidents.SingleAsync();
            incident.Status = status;
            await db.SaveChangesAsync();
        }
        var before = (await client.QueryIncidentsAsync(new QueryIncidentsRequest())).Items.Single();
        var request = new ResolveIncidentRequest { Version = before.Version, Reason = "  Audit run 42 confirmed recovery.  " };
        var resolved = await client.ResolveIncidentAsync(receipt.IncidentId, request);
        Assert.AreEqual(Code.JobDone, resolved.Code);
        Assert.IsNotNull(resolved.ProtocolVersion);
        Assert.AreNotEqual(before.Version, resolved.Version);
        Assert.IsNotNull(resolved.ResolvedAtUtc);
        Assert.AreEqual(TimeSpan.Zero, resolved.ResolvedAtUtc.Value.Offset);
        var retry = await client.ResolveIncidentAsync(receipt.IncidentId, request);
        Assert.AreEqual(Code.NoActionTaken, retry.Code);
        Assert.AreEqual(resolved.Version, retry.Version);
        Assert.AreEqual(resolved.ResolvedAtUtc, retry.ResolvedAtUtc);
        Assert.IsEmpty((await client.QueryIncidentsAsync(new QueryIncidentsRequest { ActiveOnly = true })).Items);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var incident = await db.Incidents.SingleAsync();
            Assert.AreEqual(Incident.Resolved, incident.Status);
            var entry = await db.IncidentTimelineEntries.SingleAsync(item => item.Type == IncidentTimelineEntry.Resolved);
            Assert.IsNull(entry.ActorId);
            Assert.AreEqual("Application", entry.ActorName);
            Assert.AreEqual($"application:{incident.ApplicationId}", entry.ActorIdSnapshot);
            Assert.AreEqual("Audit run 42 confirmed recovery.", entry.Note);
            Assert.AreEqual(status, entry.PreviousStatus);
            Assert.AreEqual(Incident.Resolved, entry.NewStatus);
            Assert.AreEqual(incident.ResolvedAtUtc, entry.CreatedAtUtc);
            Assert.AreEqual(2, await db.IncidentTimelineEntries.CountAsync());
            // A delayed retry must not close an incident that was reopened meanwhile.
            incident.Status = Incident.New;
            incident.ResolvedAtUtc = null;
            incident.Version = Guid.NewGuid();
            await db.SaveChangesAsync();
        }
        var stale = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.ResolveIncidentAsync(receipt.IncidentId, request));
        Assert.AreEqual(Code.Conflict, stale.Response.Code);
        Assert.HasCount(1, (await client.QueryIncidentsAsync(new QueryIncidentsRequest { ActiveOnly = true })).Items);
    }

    [TestMethod]
    public async Task SdkResolutionEnforcesScopeKeysVersionAndReason()
    {
        var key = await CreateApplication();
        await using var services = ReportingClient(key);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var receipt = await client.CreateIncidentAsync(new CreateIncidentRequest
        {
            ReportId = Guid.NewGuid(), Title = "Scoped recovery", OccurredAtUtc = DateTimeOffset.UtcNow
        });
        var current = (await client.QueryIncidentsAsync(new QueryIncidentsRequest())).Items.Single();
        foreach (var request in new[]
                 {
                     new ResolveIncidentRequest { Version = current.Version, Reason = " " },
                     new ResolveIncidentRequest { Version = current.Version, Reason = new string('x', 4001) },
                     new ResolveIncidentRequest { Reason = "Recovered" }
                 })
        {
            var invalid = await Assert.ThrowsAsync<AiurBadApiInputException>(() => client.ResolveIncidentAsync(receipt.IncidentId, request));
            Assert.IsNotEmpty(invalid.Reasons);
        }
        var valid = new ResolveIncidentRequest { Version = current.Version, Reason = "Recovered" };
        await using var otherServices = ReportingClient(await CreateApplication());
        var other = otherServices.GetRequiredService<AlertCenterApiClient>();
        var foreign = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => other.ResolveIncidentAsync(receipt.IncidentId, valid));
        Assert.AreEqual(Code.NotFound, foreign.Response.Code);
        var missing = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.ResolveIncidentAsync(Guid.NewGuid(), valid));
        Assert.AreEqual(Code.NotFound, missing.Response.Code);
        var stale = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.ResolveIncidentAsync(receipt.IncidentId,
            new ResolveIncidentRequest { Version = Guid.NewGuid(), Reason = "Recovered" }));
        Assert.AreEqual(Code.Conflict, stale.Response.Code);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var app = await db.SourceApplications.SingleAsync(item => item.Id == Guid.Parse(key.Split('.')[0]));
            app.IsEnabled = false;
            await db.SaveChangesAsync();
        }
        var disabled = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.ResolveIncidentAsync(receipt.IncidentId, valid));
        Assert.AreEqual(Code.WrongKey, disabled.Response.Code);
        using var finalScope = Server!.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(Incident.New, (await finalDb.Incidents.SingleAsync()).Status);
        Assert.AreEqual(1, await finalDb.IncidentTimelineEntries.CountAsync());
    }

    [TestMethod]
    public async Task SdkQueriesOwnIncidentsWithFullContextAndActivePagination()
    {
        var key = await CreateApplication();
        var otherKey = await CreateApplication();
        await using var services = ReportingClient(key);
        await using var otherServices = ReportingClient(otherKey);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var otherClient = otherServices.GetRequiredService<AlertCenterApiClient>();
        var context = JObject.Parse("""
            {"service":"payment-api","request":{"traceId":"abc123"},"nodes":["global-01"],"checks":[{"healthy":false}]}
            """);
        var request = new CreateIncidentRequest
        {
            ReportId = Guid.NewGuid(), Title = "Service unavailable", Content = "Probe failed.",
            Level = "sev2", OccurredAtUtc = DateTimeOffset.UtcNow, Context = context
        };
        var first = await client.CreateIncidentAsync(request);
        await otherClient.CreateIncidentAsync(request);
        var ids = new List<Guid> { first.IncidentId };
        foreach (var status in new[] { Incident.Acked, Incident.Mitigated, Incident.Resolved })
        {
            request.ReportId = Guid.NewGuid();
            var receipt = await client.CreateIncidentAsync(request);
            ids.Add(receipt.IncidentId);
            using var scope = Server!.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var incident = await db.Incidents.SingleAsync(item => item.Id == receipt.IncidentId);
            incident.Status = status;
            // Equal timestamps exercise the stable secondary ordering across pages.
            incident.ReceivedAtUtc = first.ReceivedAtUtc.UtcDateTime;
            await db.SaveChangesAsync();
        }

        var active = await client.QueryIncidentsAsync(new QueryIncidentsRequest { ActiveOnly = true, PageSize = 2 });
        Assert.AreEqual(Code.JobDone, active.Code);
        Assert.IsNotNull(active.ProtocolVersion);
        Assert.AreEqual(3, active.TotalCount);
        Assert.AreEqual(1, active.Page);
        Assert.AreEqual(2, active.PageSize);
        Assert.HasCount(2, active.Items);
        var second = await client.QueryIncidentsAsync(new QueryIncidentsRequest { ActiveOnly = true, PageSize = 2, Page = 2 });
        Assert.HasCount(1, second.Items);
        var combined = active.Items.Concat(second.Items).ToList();
        Assert.HasCount(3, combined.Select(item => item.IncidentId).Distinct().ToList());
        CollectionAssert.AreEquivalent(new[] { Incident.New, Incident.Acked, Incident.Mitigated }, combined.Select(item => item.Status).ToArray());
        foreach (var item in combined)
        {
            Assert.Contains(item.IncidentId, ids);
            Assert.AreEqual(request.Title, item.Title);
            Assert.AreEqual(request.Content, item.Content);
            Assert.AreEqual(request.Level, item.Level);
            Assert.AreEqual(TimeSpan.Zero, item.OccurredAtUtc.Offset);
            Assert.AreEqual(TimeSpan.Zero, item.ReceivedAtUtc.Offset);
            Assert.IsTrue(JToken.DeepEquals(context, item.Context));
        }
        var all = await client.QueryIncidentsAsync(new QueryIncidentsRequest());
        Assert.AreEqual(4, all.TotalCount);
        Assert.HasCount(4, all.Items);
        var empty = await client.QueryIncidentsAsync(new QueryIncidentsRequest { Page = 3, PageSize = 2 });
        Assert.AreEqual(4, empty.TotalCount);
        Assert.IsEmpty(empty.Items);
        var other = await otherClient.QueryIncidentsAsync(new QueryIncidentsRequest());
        Assert.AreEqual(1, other.TotalCount);
        Assert.IsFalse(ids.Contains(other.Items.Single().IncidentId));
    }

    [TestMethod]
    public async Task SdkQueryRejectsInvalidPagingAndRevokedOrDisabledKeys()
    {
        var key = await CreateApplication();
        await using var services = ReportingClient(key);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        foreach (var request in new[]
                 {
                     new QueryIncidentsRequest { Page = 0 }, new QueryIncidentsRequest { Page = int.MaxValue },
                     new QueryIncidentsRequest { PageSize = 0 }, new QueryIncidentsRequest { PageSize = 101 }
                 })
        {
            var invalid = await Assert.ThrowsAsync<AiurBadApiInputException>(() => client.QueryIncidentsAsync(request));
            Assert.IsNotEmpty(invalid.Reasons);
        }
        var empty = await client.QueryIncidentsAsync(new QueryIncidentsRequest());
        Assert.AreEqual(0, empty.TotalCount);
        Assert.IsEmpty(empty.Items);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var app = await db.SourceApplications.SingleAsync();
            app.IsEnabled = false;
            await db.SaveChangesAsync();
        }
        var disabled = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.QueryIncidentsAsync(new QueryIncidentsRequest()));
        Assert.AreEqual(Code.WrongKey, disabled.Response.Code);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var app = await db.SourceApplications.SingleAsync();
            app.IsEnabled = true;
            await db.SaveChangesAsync();
        }
        await ReplaceApplicationKey(Guid.Parse(key.Split('.')[0]));
        var revoked = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.QueryIncidentsAsync(new QueryIncidentsRequest()));
        Assert.AreEqual(Code.WrongKey, revoked.Response.Code);
    }

    [TestMethod]
    [DataRow("?Page=invalid", true, Code.InvalidInput)]
    [DataRow("?ActiveOnly=invalid", true, Code.InvalidInput)]
    [DataRow("", false, Code.WrongKey)]
    public async Task QueryFailuresUseAiurProtocol(string query, bool authenticated, Code expectedCode)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/incidents" + query);
        if (authenticated)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await CreateApplication());
        using var response = await Http.SendAsync(request);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        var error = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual((int)expectedCode, error.Value<int>("Code"));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error.Value<string>("ProtocolVersion")));
        if (expectedCode == Code.InvalidInput)
            Assert.IsTrue(error["Items"] is JArray { Count: > 0 });
    }

    [TestMethod]
    public async Task SdkPersistsStructuredReportAndReturnsOriginalReceiptForRetries()
    {
        var key = await CreateApplication();
        await using var services = ReportingClient(key);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var request = new CreateIncidentRequest
        {
            ReportId = Guid.NewGuid(), Title = "Payment provider timed out", Content = "Retry later.",
            Level = "sev1",
            OccurredAtUtc = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.FromHours(8)),
            Context = new JObject { ["orderId"] = "ORDER-42", ["attempts"] = 3 }
        };
        var first = await client.CreateIncidentAsync(request);
        Assert.AreEqual(Code.JobDone, first.Code);
        Assert.IsFalse(first.Duplicate);
        Assert.AreNotEqual(Guid.Empty, first.IncidentId);
        Assert.AreEqual(request.ReportId, first.ReportId);
        Assert.AreNotEqual(default, first.ReceivedAtUtc);
        Assert.AreEqual(TimeSpan.Zero, first.ReceivedAtUtc.Offset);
        Assert.IsNotNull(first.ProtocolVersion);

        request.Title = "Must not replace the original";
        var duplicate = await client.CreateIncidentAsync(request);
        Assert.AreEqual(Code.NoActionTaken, duplicate.Code);
        Assert.IsTrue(duplicate.Duplicate);
        Assert.AreEqual(first.IncidentId, duplicate.IncidentId);
        Assert.AreEqual(first.ReceivedAtUtc, duplicate.ReceivedAtUtc);
        Assert.AreEqual(first.IncidentId, duplicate.IncidentId);

        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var saved = await db.Incidents.SingleAsync();
        Assert.AreEqual("Payment provider timed out", saved.Title);
        Assert.AreEqual(request.OccurredAtUtc.UtcDateTime, saved.OccurredAtUtc);
        Assert.IsTrue(JToken.DeepEquals(request.Context, JObject.Parse(saved.ContextJson!)));

        var otherKey = await CreateApplication();
        await using var otherServices = ReportingClient(otherKey);
        var other = await otherServices.GetRequiredService<AlertCenterApiClient>().CreateIncidentAsync(request);
        Assert.AreEqual(Code.JobDone, other.Code);
        Assert.AreNotEqual(first.IncidentId, other.IncidentId);
        Assert.AreEqual(2, await db.Incidents.CountAsync());
    }

    [TestMethod]
    public async Task SdkRejectsInvalidInputAndRevokedApplicationKeys()
    {
        var key = await CreateApplication();
        await using var services = ReportingClient(key);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var invalid = await Assert.ThrowsAsync<AiurBadApiInputException>(() => client.CreateIncidentAsync(new CreateIncidentRequest()));
        Assert.IsNotEmpty(invalid.Reasons);

        var request = new CreateIncidentRequest
        {
            ReportId = Guid.NewGuid(), Title = "SDK authorization", OccurredAtUtc = DateTimeOffset.UtcNow
        };
        await ReplaceApplicationKey(Guid.Parse(key.Split('.')[0]));
        var rejected = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.CreateIncidentAsync(request));
        Assert.AreEqual(Code.WrongKey, rejected.Response.Code);
        using var scope = Server!.Services.CreateScope();
        Assert.AreEqual(0, await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().Incidents.CountAsync());
    }

    [TestMethod]
    public async Task SdkReportsServerSideBodyLimitAsInputError()
    {
        var key = await CreateApplication();
        await using var services = ReportingClient(key);
        var error = await Assert.ThrowsAsync<AiurBadApiInputException>(() =>
            services.GetRequiredService<AlertCenterApiClient>().CreateIncidentAsync(new CreateIncidentRequest
            {
                ReportId = Guid.NewGuid(), Title = "Oversized context", OccurredAtUtc = DateTimeOffset.UtcNow,
                Context = new JObject { ["detail"] = new string('x', 66000) }
            }));
        Assert.IsTrue(error.Reasons.Any(reason => reason.Contains("64 KiB")));
    }

    [TestMethod]
    [DataRow("{}", 400)]
    [DataRow("", 400)]
    [DataRow("unsupported", 400)]
    [DataRow("null", 400)]
    [DataRow("{broken", 400)]
    [DataRow("oversized", 413)]
    [DataRow("chunked", 413)]
    public async Task ReportingFailuresReturnAiurProtocolJson(string body, int expectedStatus)
    {
        var key = await CreateApplication();
        var chunked = body == "chunked";
        var contentType = body == "unsupported" ? "text/plain" : "application/json";
        if (body is "oversized" or "chunked") body = "{\"title\":\"" + new string('a', 66000) + "\"}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/incidents");
        request.Content = new StringContent(body, Encoding.UTF8, contentType);
        request.Headers.TransferEncodingChunked = chunked;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await Http.SendAsync(request);
        Assert.AreEqual((HttpStatusCode)expectedStatus, response.StatusCode);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        var error = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual((int)Code.InvalidInput, error.Value<int>("Code"));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error.Value<string>("ProtocolVersion")));
        Assert.IsTrue(error["Items"] is JArray { Count: > 0 });
    }
}

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
    public async Task SdkRecoversLostCreationReceiptWithoutCreatingAnotherIncidentOrTimelineEntry()
    {
        var key = await CreateApplication();
        using var handler = new LostReceiptHandler("/api/v1/incidents");
        await using var services = ReportingClientWithLostReceipt(key, handler);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var report = new CreateIncidentRequest
        {
            ReportId = Guid.NewGuid(), Title = "A committed report lost its response", OccurredAtUtc = DateTimeOffset.UtcNow
        };
        var receipt = await client.CreateIncidentAsync(report);
        Assert.IsTrue(receipt.Duplicate);
        Assert.AreEqual(report.ReportId, receipt.ReportId);
        Assert.AreEqual(2, handler.MatchingCalls);
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(receipt.IncidentId, (await db.Incidents.SingleAsync()).Id);
        Assert.AreEqual(1, await db.IncidentTimelineEntries.CountAsync());
    }

    [TestMethod]
    public async Task SdkReturnsConflictAfterLostPatchReceiptAndPreservesTheCommittedVersion()
    {
        var originalKey = await CreateApplication();
        var key = await CreateApplicationKey(Guid.Parse(originalKey.Split('.')[0]), metadata: true);
        await using var originalServices = ReportingClient(key);
        var originalClient = originalServices.GetRequiredService<AlertCenterApiClient>();
        var receipt = await originalClient.CreateIncidentAsync(new CreateIncidentRequest
        {
            ReportId = Guid.NewGuid(), Title = "Uncertain metadata update", OccurredAtUtc = DateTimeOffset.UtcNow
        });
        var before = (await originalClient.GetIncidentAsync(receipt.IncidentId)).Incident;
        using var handler = new LostReceiptHandler($"/api/v1/incidents/{receipt.IncidentId}/metadata");
        await using var services = ReportingClientWithLostReceipt(key, handler);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var request = new PatchIncidentMetadataRequest
        {
            Version = before.Version, Property = "ci", Value = new JObject { ["count"] = 1 }
        };
        var conflict = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() =>
            client.PatchIncidentMetadataAsync(receipt.IncidentId, request));
        Assert.AreEqual(Code.Conflict, conflict.Response.Code);
        Assert.AreEqual(2, handler.MatchingCalls);
        Assert.AreEqual(before.Version, request.Version);
        var after = (await originalClient.GetIncidentAsync(receipt.IncidentId)).Incident;
        Assert.AreNotEqual(before.Version, after.Version);
        Assert.AreEqual(1, after.Metadata["ci"]!.Value<int>("count"));
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(1, await db.IncidentTimelineEntries.CountAsync(item => item.Type == IncidentTimelineEntry.Updated));
    }

    private ServiceProvider ReportingClientWithLostReceipt(string key, HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlertCenterSdk(Http.BaseAddress!.ToString(), key);
        services.AddSingleton<IHttpClientFactory>(new LostReceiptClientFactory(handler));
        return services.BuildServiceProvider();
    }

    private sealed class LostReceiptClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class LostReceiptHandler(string path) : DelegatingHandler(new HttpClientHandler())
    {
        public int MatchingCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri!.AbsolutePath != path) return response;
            MatchingCalls++;
            if (MatchingCalls != 1) return response;
            Assert.IsTrue(response.IsSuccessStatusCode);
            response.Dispose();
            throw new HttpRequestException("The server committed the operation but its response was lost.");
        }
    }
}

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
        await ResetApplicationKey(Guid.Parse(key.Split('.')[0]));
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

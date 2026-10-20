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
    [DataRow(Incident.Mitigated)]
    [DataRow(Incident.Resolved)]
    public async Task SdkReopensWithIndependentScopePreservingResponseAndRecoveryHistory(string status)
    {
        var original = await CreateApplication();
        var app = Guid.Parse(original.Split('.')[0]);
        await using var originalServices = ReportingClient(original);
        var originalClient = originalServices.GetRequiredService<AlertCenterApiClient>();
        var receipt = await originalClient.CreateIncidentAsync(new CreateIncidentRequest
        {
            ReportId = Guid.NewGuid(), Title = "Recurring performance issue", Level = "sev3", OccurredAtUtc = DateTimeOffset.UtcNow
        });
        var recoveredAt = new DateTime(2026, 10, 20, 1, 0, 0, DateTimeKind.Utc);
        string ownerId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var incident = await db.Incidents.SingleAsync();
            var owner = await db.Users.FirstAsync();
            ownerId = incident.OwnerId = owner.Id;
            incident.OwnerDisplayName = owner.DisplayName;
            incident.AcknowledgedAtUtc = recoveredAt.AddMinutes(-10);
            incident.Status = status;
            incident.MitigatedAtUtc = recoveredAt;
            incident.ResolvedAtUtc = status == Incident.Resolved ? recoveredAt.AddMinutes(5) : null;
            db.ApplicationPushChannels.Add(new ApplicationPushChannel
            {
                ApplicationId = app,
                Channel = new PushChannel { Name = "Recurrence", Type = "email", EmailRecipient = "test@example.com" }
            });
            await db.SaveChangesAsync();
        }
        var before = (await originalClient.GetIncidentAsync(receipt.IncidentId)).Incident;
        Assert.AreEqual(new DateTimeOffset(recoveredAt), before.MitigatedAtUtc);
        Assert.AreEqual(status == Incident.Resolved ? new DateTimeOffset(recoveredAt.AddMinutes(5)) : null, before.ResolvedAtUtc);
        var listed = (await originalClient.QueryIncidentsAsync(new QueryIncidentsRequest())).Items.Single();
        Assert.AreEqual(before.MitigatedAtUtc, listed.MitigatedAtUtc);
        Assert.AreEqual(before.ResolvedAtUtc, listed.ResolvedAtUtc);
        var request = new ReopenIncidentRequest { Version = before.Version, Reason = "  Server pressure returned.  " };
        var denied = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => originalClient.ReopenIncidentAsync(receipt.IncidentId, request));
        Assert.AreEqual(Code.Unauthorized, denied.Response.Code);
        var key = await CreateApplicationKey(app, reopen: true);
        await using var services = ReportingClient(key);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var stale = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.ReopenIncidentAsync(receipt.IncidentId,
            new ReopenIncidentRequest { Version = Guid.NewGuid(), Reason = "Stale observation" }));
        Assert.AreEqual(Code.Conflict, stale.Response.Code);
        foreach (var invalid in new[]
                 {
                     new ReopenIncidentRequest { Reason = "Returned" },
                     new ReopenIncidentRequest { Version = before.Version, Reason = " " },
                     new ReopenIncidentRequest { Version = before.Version, Reason = new string('x', 4001) }
                 })
            await Assert.ThrowsAsync<AiurBadApiInputException>(() => client.ReopenIncidentAsync(receipt.IncidentId, invalid));
        var otherKey = await CreateApplicationKey(Guid.Parse((await CreateApplication()).Split('.')[0]), reopen: true);
        await using var otherServices = ReportingClient(otherKey);
        var foreign = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() =>
            otherServices.GetRequiredService<AlertCenterApiClient>().ReopenIncidentAsync(receipt.IncidentId, request));
        var missing = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.ReopenIncidentAsync(Guid.NewGuid(), request));
        Assert.AreEqual(Code.NotFound, foreign.Response.Code);
        Assert.AreEqual(missing.Response.Message, foreign.Response.Message);
        var reopened = await client.ReopenIncidentAsync(receipt.IncidentId, request);
        Assert.AreEqual(Code.JobDone, reopened.Code);
        Assert.AreNotEqual(before.Version, reopened.Version);
        var retry = await client.ReopenIncidentAsync(receipt.IncidentId, request);
        Assert.AreEqual(Code.NoActionTaken, retry.Code);
        Assert.AreEqual(reopened.Version, retry.Version);
        var after = (await client.GetIncidentAsync(receipt.IncidentId)).Incident;
        Assert.AreEqual(Incident.New, after.Status);
        Assert.AreEqual(before.AcknowledgedAtUtc, after.AcknowledgedAtUtc);
        Assert.AreEqual(ownerId, after.OwnerId);
        Assert.AreEqual(before.Level, after.Level);
        Assert.IsNull(after.MitigatedAtUtc);
        Assert.IsNull(after.ResolvedAtUtc);
        using var finalScope = Server!.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var entry = await finalDb.IncidentTimelineEntries.SingleAsync(item => item.Type == IncidentTimelineEntry.Reopened);
        Assert.AreEqual(status, entry.PreviousStatus);
        Assert.AreEqual(Incident.New, entry.NewStatus);
        Assert.AreEqual($"application:{app}", entry.ActorIdSnapshot);
        Assert.AreEqual("Server pressure returned.", entry.Note);
        Assert.AreEqual(ownerId, entry.NewOwnerId);
        Assert.AreEqual(1, await finalDb.PushDeliveries.CountAsync(item => item.IncidentEventId == entry.Id));
        (await finalDb.ApplicationApiKeys.SingleAsync(item => item.Id == Guid.Parse(key.Split('.')[1]))).IsEnabled = false;
        await finalDb.SaveChangesAsync();
        var disabled = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.ReopenIncidentAsync(receipt.IncidentId, request));
        Assert.AreEqual(Code.WrongKey, disabled.Response.Code);
    }

    [TestMethod]
    public async Task ConcurrentApplicationReopenCommitsOneEventAndEmail()
    {
        var original = await CreateApplication();
        var app = Guid.Parse(original.Split('.')[0]);
        var key = await CreateApplicationKey(app, reopen: true);
        await using var services = ReportingClient(key);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        var receipt = await client.CreateIncidentAsync(new CreateIncidentRequest
        {
            ReportId = Guid.NewGuid(), Title = "Recurring failure", OccurredAtUtc = DateTimeOffset.UtcNow
        });
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var incident = await db.Incidents.SingleAsync();
            incident.Status = Incident.Mitigated;
            incident.MitigatedAtUtc = DateTime.UtcNow;
            db.ApplicationPushChannels.Add(new ApplicationPushChannel
            {
                ApplicationId = app,
                Channel = new PushChannel { Name = "Reopen concurrency", Type = "email", EmailRecipient = "test@example.com" }
            });
            await db.SaveChangesAsync();
        }
        var version = (await client.GetIncidentAsync(receipt.IncidentId)).Incident.Version;
        async Task<Code> Reopen()
        {
            try
            {
                return (await client.ReopenIncidentAsync(receipt.IncidentId,
                    new ReopenIncidentRequest { Version = version, Reason = "Failure returned" })).Code;
            }
            catch (AiurUnexpectedServerResponseException exception) { return exception.Response.Code; }
        }
        var results = await Task.WhenAll(Reopen(), Reopen());
        Assert.AreEqual(1, results.Count(code => code == Code.JobDone));
        Assert.IsTrue(results.All(code => code is Code.JobDone or Code.NoActionTaken or Code.Conflict));
        using var finalScope = Server!.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var entry = await finalDb.IncidentTimelineEntries.SingleAsync(item => item.Type == IncidentTimelineEntry.Reopened);
        Assert.AreEqual(1, await finalDb.PushDeliveries.CountAsync(item => item.IncidentEventId == entry.Id));
    }

    [TestMethod]
    [DataRow(Incident.New)]
    [DataRow(Incident.Acked)]
    public async Task SdkMitigatesWithoutResolvingAndPreservesHumanResponseState(string status)
    {
        var original = await CreateApplication();
        var applicationId = Guid.Parse(original.Split('.')[0]);
        var key = await CreateApplicationKey(applicationId, query: true, create: true, resolve: false, mitigate: true);
        await using var services = ReportingClient(key);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.ApplicationPushChannels.Add(new ApplicationPushChannel
            {
                ApplicationId = applicationId,
                Channel = new PushChannel { Name = "Mitigation test", Type = "email", EmailRecipient = "test@example.com" }
            });
            await db.SaveChangesAsync();
        }
        var receipt = await client.CreateIncidentAsync(new CreateIncidentRequest
        {
            ReportId = Guid.NewGuid(), Title = "Main branch build failed", Level = "sev3", OccurredAtUtc = DateTimeOffset.UtcNow
        });
        string? ownerId = null;
        DateTime? acknowledgedAt = null;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var incident = await db.Incidents.SingleAsync();
            incident.Status = status == Incident.Acked ? Incident.New : status;
            incident.AcknowledgedAtUtc = status == Incident.Acked ? DateTime.UtcNow : null;
            if (status == Incident.Acked)
            {
                var owner = await db.Users.FirstAsync();
                ownerId = incident.OwnerId = owner.Id;
                incident.OwnerDisplayName = owner.DisplayName;
                acknowledgedAt = incident.AcknowledgedAtUtc = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
            }
            await db.SaveChangesAsync();
        }
        var before = (await client.GetIncidentAsync(receipt.IncidentId)).Incident;
        var request = new MitigateIncidentRequest { Version = before.Version, Reason = "  Main branch build recovered.  " };
        var mitigated = await client.MitigateIncidentAsync(receipt.IncidentId, request);
        Assert.AreEqual(Code.JobDone, mitigated.Code);
        Assert.IsNotNull(mitigated.ProtocolVersion);
        Assert.IsNotNull(mitigated.MitigatedAtUtc);
        Assert.AreEqual(TimeSpan.Zero, mitigated.MitigatedAtUtc.Value.Offset);
        Assert.AreNotEqual(before.Version, mitigated.Version);
        var repeated = await client.MitigateIncidentAsync(receipt.IncidentId, request);
        Assert.AreEqual(Code.NoActionTaken, repeated.Code);
        Assert.AreEqual(mitigated.Version, repeated.Version);
        Assert.AreEqual(mitigated.MitigatedAtUtc, repeated.MitigatedAtUtc);
        Assert.AreEqual(Incident.Mitigated, (await client.GetIncidentAsync(receipt.IncidentId)).Incident.Status);
        Assert.AreEqual(1, (await client.QueryIncidentsAsync(new QueryIncidentsRequest { ActiveOnly = true })).TotalCount);
        var resolveDenied = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.ResolveIncidentAsync(receipt.IncidentId,
            new ResolveIncidentRequest { Version = mitigated.Version, Reason = "Cannot resolve with mitigation scope" }));
        Assert.AreEqual(Code.Unauthorized, resolveDenied.Response.Code);
        using var finalScope = Server!.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var saved = await finalDb.Incidents.SingleAsync();
        Assert.IsNull(saved.ResolvedAtUtc);
        Assert.AreEqual(acknowledgedAt, saved.AcknowledgedAtUtc);
        Assert.AreEqual(ownerId, saved.OwnerId);
        Assert.AreEqual("sev3", saved.Level);
        var entry = await finalDb.IncidentTimelineEntries.SingleAsync(item => item.Type == IncidentTimelineEntry.Mitigated);
        Assert.IsNull(entry.ActorId);
        Assert.AreEqual("Application", entry.ActorName);
        Assert.AreEqual($"application:{applicationId}", entry.ActorIdSnapshot);
        Assert.AreEqual("Main branch build recovered.", entry.Note);
        Assert.AreEqual(status == Incident.Acked ? Incident.New : status, entry.PreviousStatus);
        Assert.AreEqual(Incident.Mitigated, entry.NewStatus);
        Assert.AreEqual(ownerId, entry.PreviousOwnerId);
        Assert.AreEqual(ownerId, entry.NewOwnerId);
        Assert.AreEqual(saved.MitigatedAtUtc, entry.CreatedAtUtc);
        Assert.AreEqual(2, await finalDb.IncidentTimelineEntries.CountAsync());
        Assert.AreEqual(1, await finalDb.PushDeliveries.CountAsync(item => item.IncidentEventId == entry.Id));
        Assert.AreEqual(0, await finalDb.IncidentTimelineEntries.CountAsync(item => item.Type == IncidentTimelineEntry.Resolved));
    }

    [TestMethod]
    public async Task MitigationEnforcesIndependentScopeApplicationKeyStatusAndVersion()
    {
        var originalKey = await CreateApplication();
        var app = Guid.Parse(originalKey.Split('.')[0]);
        await using var originalServices = ReportingClient(originalKey);
        var original = originalServices.GetRequiredService<AlertCenterApiClient>();
        var receipt = await original.CreateIncidentAsync(new CreateIncidentRequest
        {
            ReportId = Guid.NewGuid(), Title = "API mitigation", OccurredAtUtc = DateTimeOffset.UtcNow
        });
        var before = (await original.GetIncidentAsync(receipt.IncidentId)).Incident;
        var request = new MitigateIncidentRequest { Version = before.Version, Reason = "Build recovered" };
        var denied = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => original.MitigateIncidentAsync(receipt.IncidentId, request));
        Assert.AreEqual(Code.Unauthorized, denied.Response.Code);
        var key = await CreateApplicationKey(app, mitigate: true);
        await using var services = ReportingClient(key);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        foreach (var invalid in new[]
                 {
                     new MitigateIncidentRequest { Reason = "Recovered" },
                     new MitigateIncidentRequest { Version = before.Version, Reason = " " },
                     new MitigateIncidentRequest { Version = before.Version, Reason = new string('x', 4001) }
                 })
            await Assert.ThrowsAsync<AiurBadApiInputException>(() => client.MitigateIncidentAsync(receipt.IncidentId, invalid));
        var otherKey = await CreateApplicationKey(Guid.Parse((await CreateApplication()).Split('.')[0]), mitigate: true);
        await using var otherServices = ReportingClient(otherKey);
        var foreign = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() =>
            otherServices.GetRequiredService<AlertCenterApiClient>().MitigateIncidentAsync(receipt.IncidentId, request));
        var missing = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.MitigateIncidentAsync(Guid.NewGuid(), request));
        Assert.AreEqual(Code.NotFound, foreign.Response.Code);
        Assert.AreEqual(missing.Response.Code, foreign.Response.Code);
        Assert.AreEqual(missing.Response.Message, foreign.Response.Message);
        var stale = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.MitigateIncidentAsync(receipt.IncidentId,
            new MitigateIncidentRequest { Version = Guid.NewGuid(), Reason = "Stale result" }));
        Assert.AreEqual(Code.Conflict, stale.Response.Code);
        await client.MitigateIncidentAsync(receipt.IncidentId, request);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var incident = await db.Incidents.SingleAsync();
            incident.Status = Incident.New;
            incident.MitigatedAtUtc = null;
            incident.Version = Guid.NewGuid();
            await db.SaveChangesAsync();
        }
        var reopened = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.MitigateIncidentAsync(receipt.IncidentId, request));
        Assert.AreEqual(Code.Conflict, reopened.Response.Code);
        var current = (await client.GetIncidentAsync(receipt.IncidentId)).Incident;
        await client.ResolveIncidentAsync(receipt.IncidentId, new ResolveIncidentRequest { Version = current.Version, Reason = "Human confirmation" });
        var resolvedVersion = (await client.GetIncidentAsync(receipt.IncidentId)).Incident.Version;
        var resolved = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.MitigateIncidentAsync(receipt.IncidentId,
            new MitigateIncidentRequest { Version = resolvedVersion, Reason = "Late build result" }));
        Assert.AreEqual(Code.Conflict, resolved.Response.Code);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            (await db.ApplicationApiKeys.SingleAsync(item => item.Id == Guid.Parse(key.Split('.')[1]))).IsEnabled = false;
            await db.SaveChangesAsync();
        }
        var disabled = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.MitigateIncidentAsync(receipt.IncidentId, request));
        Assert.AreEqual(Code.WrongKey, disabled.Response.Code);
    }

    [TestMethod]
    public async Task ConcurrentMitigationsCommitOnlyOneTimelineEventAndEmail()
    {
        var original = await CreateApplication();
        var app = Guid.Parse(original.Split('.')[0]);
        var key = await CreateApplicationKey(app, mitigate: true);
        await using var services = ReportingClient(key);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.ApplicationPushChannels.Add(new ApplicationPushChannel
            {
                ApplicationId = app,
                Channel = new PushChannel { Name = "Concurrent mitigation", Type = "email", EmailRecipient = "test@example.com" }
            });
            await db.SaveChangesAsync();
        }
        var receipt = await client.CreateIncidentAsync(new CreateIncidentRequest
        {
            ReportId = Guid.NewGuid(), Title = "Concurrent recovery", OccurredAtUtc = DateTimeOffset.UtcNow
        });
        var version = (await client.GetIncidentAsync(receipt.IncidentId)).Incident.Version;
        async Task<Code> Mitigate()
        {
            try
            {
                return (await client.MitigateIncidentAsync(receipt.IncidentId,
                    new MitigateIncidentRequest { Version = version, Reason = "Recovered" })).Code;
            }
            catch (AiurUnexpectedServerResponseException exception)
            {
                return exception.Response.Code;
            }
        }
        var results = await Task.WhenAll(Mitigate(), Mitigate());
        Assert.AreEqual(1, results.Count(code => code == Code.JobDone));
        Assert.IsTrue(results.All(code => code is Code.JobDone or Code.NoActionTaken or Code.Conflict));
        using var finalScope = Server!.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var entry = await finalDb.IncidentTimelineEntries.SingleAsync(item => item.Type == IncidentTimelineEntry.Mitigated);
        Assert.AreEqual(1, await finalDb.PushDeliveries.CountAsync(item => item.IncidentEventId == entry.Id));
        Assert.AreEqual(Incident.Mitigated, (await finalDb.Incidents.SingleAsync()).Status);
    }

    [TestMethod]
    [DataRow("{}", 400, false)]
    [DataRow("", 400, false)]
    [DataRow("{broken", 400, false)]
    [DataRow("oversized", 413, false)]
    [DataRow("oversized", 413, true)]
    public async Task MitigationBodyErrorsUseAiurProtocol(string body, int expectedStatus, bool chunked)
    {
        if (body == "oversized") body = "{\"Reason\":\"" + new string('x', 66000) + "\"}";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/incidents/{Guid.NewGuid()}/mitigate");
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
}

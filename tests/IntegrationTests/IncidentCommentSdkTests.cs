using Aiursoft.AiurProtocol.Exceptions;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.SDK;
using Aiursoft.AlertCenter.SDK.Models;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class IncidentPagesTests
{
    [TestMethod]
    public async Task ApplicationCommentsAppendAcrossAllStatesWithoutLifecycleChangesOrNotifications()
    {
        var original = await CreateApplication();
        var app = Guid.Parse(original.Split('.')[0]);
        var key = await CreateApplicationKey(app, create: false, resolve: false, comment: true);
        await using var creator = ReportingClient(original);
        var receipt = await creator.GetRequiredService<AlertCenterApiClient>().CreateIncidentAsync(new CreateIncidentRequest
        {
            ReportId = Guid.NewGuid(), Title = "CPU contention", OccurredAtUtc = DateTimeOffset.UtcNow
        });
        await using var services = ReportingClient(key);
        var client = services.GetRequiredService<AlertCenterApiClient>();
        foreach (var state in new[] { Incident.New, Incident.Mitigated, Incident.Resolved })
        {
            using var scope = Server!.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var incident = await db.Incidents.SingleAsync(item => item.Id == receipt.IncidentId);
            incident.Status = state;
            await db.SaveChangesAsync();
            var version = incident.Version;
            var timelineCount = await db.IncidentTimelineEntries.CountAsync();
            var deliveryCount = await db.PushDeliveries.CountAsync();
            var comment = await client.CreateIncidentCommentAsync(receipt.IncidentId, new CreateIncidentCommentRequest
            {
                Markdown = $"**Investigation** for {state}: shared CI CPU pool applied."
            });
            Assert.AreEqual(Code.JobDone, comment.Code);
            var stored = await db.IncidentComments.SingleAsync(item => item.Id == comment.CommentId);
            Assert.AreEqual(receipt.IncidentId, stored.IncidentId);
            Assert.AreEqual("Application", stored.AuthorName);
            Assert.IsNull(stored.AuthorId);
            Assert.AreEqual(comment.CreatedAtUtc.UtcDateTime, stored.CreatedAtUtc);
            await db.Entry(incident).ReloadAsync();
            Assert.AreEqual(version, incident.Version);
            Assert.AreEqual(state, incident.Status);
            Assert.AreEqual(timelineCount, await db.IncidentTimelineEntries.CountAsync());
            Assert.AreEqual(deliveryCount, await db.PushDeliveries.CountAsync());
        }
    }

    [TestMethod]
    public async Task CommentScopeIsIndependentAndForeignAndMissingIncidentsAreHidden()
    {
        var original = await CreateApplication();
        var app = Guid.Parse(original.Split('.')[0]);
        await using var creator = ReportingClient(original);
        var client = creator.GetRequiredService<AlertCenterApiClient>();
        var receipt = await client.CreateIncidentAsync(new CreateIncidentRequest { ReportId = Guid.NewGuid(), Title = "Scope", OccurredAtUtc = DateTimeOffset.UtcNow });
        var request = new CreateIncidentCommentRequest { Markdown = "Investigation note" };
        var denied = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => client.CreateIncidentCommentAsync(receipt.IncidentId, request));
        Assert.AreEqual(Code.Unauthorized, denied.Response.Code);
        var key = await CreateApplicationKey(app, query: false, create: false, resolve: false, comment: true);
        await using var services = ReportingClient(key);
        var commenter = services.GetRequiredService<AlertCenterApiClient>();
        await commenter.CreateIncidentCommentAsync(receipt.IncidentId, request);
        var query = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => commenter.GetIncidentAsync(receipt.IncidentId));
        Assert.AreEqual(Code.Unauthorized, query.Response.Code);
        var missing = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => commenter.CreateIncidentCommentAsync(Guid.NewGuid(), request));
        Assert.AreEqual(Code.NotFound, missing.Response.Code);
        var foreignKey = await CreateApplicationKey(Guid.Parse((await CreateApplication()).Split('.')[0]), comment: true);
        await using var foreign = ReportingClient(foreignKey);
        var hidden = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => foreign.GetRequiredService<AlertCenterApiClient>().CreateIncidentCommentAsync(receipt.IncidentId, request));
        Assert.AreEqual(Code.NotFound, hidden.Response.Code);
    }

    [TestMethod]
    public async Task CommentCliValidatesMarkdownAndReturnsStoredCommentReceipt()
    {
        var original = await CreateApplication();
        var key = await CreateApplicationKey(Guid.Parse(original.Split('.')[0]), comment: true);
        await using var services = ReportingClient(original);
        var receipt = await services.GetRequiredService<AlertCenterApiClient>().CreateIncidentAsync(new CreateIncidentRequest { ReportId = Guid.NewGuid(), Title = "CLI", OccurredAtUtc = DateTimeOffset.UtcNow });
        var result = CliJson(await RunIncidentCli(key, "comment", "--id", receipt.IncidentId.ToString(), "--markdown", "**Root cause:** CPU contention", "--json"));
        Assert.AreEqual(2, result.Value<int>("code"));
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var id = result["commentId"]!.ToObject<Guid>();
        Assert.AreEqual("**Root cause:** CPU contention", (await db.IncidentComments.SingleAsync(item => item.Id == id)).Markdown);
        var denied = await RunIncidentCli(original, "comment", "--id", receipt.IncidentId.ToString(), "--markdown", "Denied");
        Assert.AreNotEqual(0, denied.ProgramReturn);
        foreach (var markdown in new[] { "", "   ", new string('x', 4001) })
        {
            var invalid = await RunIncidentCli(key, "comment", "--id", receipt.IncidentId.ToString(), "--markdown", markdown);
            Assert.AreNotEqual(0, invalid.ProgramReturn);
        }
        Assert.AreEqual(1, await db.IncidentComments.CountAsync());
        var text = await RunIncidentCli(key, "comment", "--id", receipt.IncidentId.ToString(), "--markdown", "Follow-up");
        Assert.AreEqual(0, text.ProgramReturn, text.StdErr);
        Assert.Contains("Comment:", text.StdOut);
        Assert.DoesNotContain(key, text.StdOut + text.StdErr);
    }
}

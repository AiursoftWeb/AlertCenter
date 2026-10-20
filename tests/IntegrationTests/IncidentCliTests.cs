using Aiursoft.AlertCenter.Client;
using Aiursoft.CommandFramework;
using TestResult = Aiursoft.CommandFramework.Models.TestResult;
using Newtonsoft.Json.Linq;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class IncidentPagesTests
{
    private static NestedCommandApp IncidentCli() => new NestedCommandApp()
        .WithFeature(new MetadataHandler())
        .WithFeature(new ListHandler())
        .WithFeature(new GetHandler())
        .WithFeature(new CreateHandler())
        .WithFeature(new CommentHandler())
        .WithFeature(new MitigateHandler())
        .WithFeature(new ResolveHandler())
        .WithFeature(new SeverityHandler());

    private Task<TestResult> RunIncidentCli(string key, string command, params string[] arguments) =>
        IncidentCli().TestRunAsync([command, "--server", Http.BaseAddress!.ToString(), "--api-key", key, .. arguments]);

    private static JObject CliJson(TestResult result)
    {
        Assert.AreEqual(0, result.ProgramReturn, result.StdErr);
        Assert.AreEqual(string.Empty, result.StdErr);
        return JObject.Parse(result.StdOut);
    }

    [TestMethod]
    public async Task CliMitigatesWithoutResolvingAndRequiresExplicitScopeAndVersion()
    {
        var full = await CreateApplication();
        var key = await CreateApplicationKey(Guid.Parse(full.Split('.')[0]), mitigate: true);
        var report = CliJson(await RunIncidentCli(key, "create", "--report-id", Guid.NewGuid().ToString(), "--title", "Build failed",
            "--level", "sev3", "--occurred-at", "2026-10-19T00:00:00Z", "--json"));
        var id = report.Value<string>("incidentId")!;
        var incident = CliJson(await RunIncidentCli(key, "get", "--id", id, "--json"))["incident"]!;
        var version = incident.Value<string>("version")!;
        var denied = await RunIncidentCli(full, "mitigate", "--id", id, "--version", version, "--reason", "Recovered");
        Assert.AreNotEqual(0, denied.ProgramReturn);
        Assert.Contains("Unauthorized", denied.StdErr);
        var missingKey = await IncidentCli().TestRunAsync(["mitigate", "--server", Http.BaseAddress!.ToString(), "--id", id, "--version", version, "--reason", "Recovered"]);
        Assert.AreNotEqual(0, missingKey.ProgramReturn);
        Assert.Contains("--api-key", missingKey.StdErr);
        var missingVersion = await RunIncidentCli(key, "mitigate", "--id", id, "--reason", "Recovered");
        Assert.AreNotEqual(0, missingVersion.ProgramReturn);
        Assert.Contains("--version", missingVersion.StdErr);
        var stale = await RunIncidentCli(key, "mitigate", "--id", id, "--version", Guid.NewGuid().ToString(), "--reason", "Stale result");
        Assert.AreNotEqual(0, stale.ProgramReturn);
        Assert.Contains("Conflict", stale.StdErr);
        var result = CliJson(await RunIncidentCli(key, "mitigate", "--id", id, "--version", version, "--reason", "Build recovered", "--json"));
        Assert.AreEqual(2, result.Value<int>("code"));
        Assert.IsNotNull(result.Value<string>("mitigatedAtUtc"));
        var repeated = await RunIncidentCli(key, "mitigate", "--id", id, "--version", version, "--reason", "Build recovered");
        Assert.AreEqual(0, repeated.ProgramReturn, repeated.StdErr);
        Assert.Contains("already mitigated", repeated.StdOut);
        Assert.Contains("Mitigated:", repeated.StdOut);
        var after = CliJson(await RunIncidentCli(key, "get", "--id", id, "--json"))["incident"]!;
        Assert.AreEqual("mitigated", after.Value<string>("status"));
        Assert.AreEqual(1, CliJson(await RunIncidentCli(key, "list", "--active-only", "--json")).Value<int>("totalCount"));
        Assert.DoesNotContain(key, repeated.StdOut + repeated.StdErr);
    }

    [TestMethod]
    public async Task CliReportsQueriesChangesAndResolvesOnlyItsApplicationIncidents()
    {
        var originalKey = await CreateApplication();
        var key = await CreateApplicationKey(Guid.Parse(originalKey.Split('.')[0]), severity: true);
        var reportId = Guid.NewGuid().ToString();
        string[] report = ["--report-id", reportId, "--title", "Main branch build failed", "--level", "sev2",
            "--occurred-at", "2026-10-18T12:00:00Z", "--content", "Compiler failure", "--context", "{\"pipeline\":42}", "--json"];
        var created = CliJson(await RunIncidentCli(key, "create", report));
        var id = created.Value<string>("incidentId")!;
        Assert.AreEqual(2, created.Value<int>("code"));
        var duplicate = CliJson(await RunIncidentCli(key, "create", report));
        Assert.AreEqual(id, duplicate.Value<string>("incidentId"));
        Assert.IsTrue(duplicate.Value<bool>("duplicate"));
        Assert.AreEqual(1, duplicate.Value<int>("code"));
        var list = CliJson(await RunIncidentCli(key, "list", "--page-size", "1", "--active-only", "--json"));
        Assert.AreEqual(1, list.Value<int>("totalCount"));
        Assert.AreEqual(1, list.Value<int>("pageSize"));
        Assert.AreEqual(id, list["items"]![0]!.Value<string>("incidentId"));
        var details = CliJson(await RunIncidentCli(key, "get", "--id", id, "--json"))["incident"]!;
        Assert.AreEqual("Compiler failure", details.Value<string>("content"));
        Assert.AreEqual(42, details["context"]!.Value<int>("pipeline"));
        Assert.AreEqual(reportId, details.Value<string>("reportId"));
        var version = details.Value<string>("version")!;

        var otherKey = await CreateApplication();
        var otherList = CliJson(await RunIncidentCli(otherKey, "list", "--json"));
        Assert.AreEqual(0, otherList.Value<int>("totalCount"));
        var foreign = await RunIncidentCli(otherKey, "get", "--id", id);
        Assert.AreNotEqual(0, foreign.ProgramReturn);
        Assert.Contains("NotFound", foreign.StdErr);
        var missing = await RunIncidentCli(key, "get", "--id", Guid.NewGuid().ToString());
        Assert.AreNotEqual(0, missing.ProgramReturn);
        Assert.Contains("NotFound", missing.StdErr);
        var foreignResolve = await RunIncidentCli(otherKey, "resolve", "--id", id, "--version", version, "--reason", "Other app");
        Assert.AreNotEqual(0, foreignResolve.ProgramReturn);
        Assert.Contains("NotFound", foreignResolve.StdErr);

        var changed = CliJson(await RunIncidentCli(key, "severity", "--id", id, "--version", version, "--level", "sev1", "--reason", "Escalated", "--json"));
        Assert.AreEqual("sev1", changed.Value<string>("level"));
        var conflict = await RunIncidentCli(key, "resolve", "--id", id, "--version", version, "--reason", "Stale recovery");
        Assert.AreNotEqual(0, conflict.ProgramReturn);
        Assert.Contains("Conflict", conflict.StdErr);
        var stillActive = CliJson(await RunIncidentCli(key, "get", "--id", id, "--json"))["incident"]!;
        Assert.AreEqual("new", stillActive.Value<string>("status"));
        var resolved = CliJson(await RunIncidentCli(key, "resolve", "--id", id, "--version", changed.Value<string>("version")!, "--reason", "Build recovered", "--json"));
        Assert.AreEqual(2, resolved.Value<int>("code"));
        var retried = CliJson(await RunIncidentCli(key, "resolve", "--id", id, "--version", changed.Value<string>("version")!, "--reason", "Build recovered", "--json"));
        Assert.AreEqual(1, retried.Value<int>("code"));
        Assert.AreEqual(0, CliJson(await RunIncidentCli(key, "list", "--active-only", "--json")).Value<int>("totalCount"));
        var text = await RunIncidentCli(key, "get", "--id", id);
        Assert.AreEqual(0, text.ProgramReturn, text.StdErr);
        Assert.Contains("Main branch build failed", text.StdOut);
        Assert.Contains("resolved", text.StdOut);
        Assert.Contains("Compiler failure", text.StdOut);
        Assert.DoesNotContain(key, text.StdOut + text.StdErr + foreign.StdOut + foreign.StdErr + conflict.StdErr);
    }

    [TestMethod]
    public async Task CliRejectsMissingCredentialsInvalidInputAndInsufficientScopes()
    {
        var key = await CreateApplication();
        var id = Guid.NewGuid().ToString();
        string[][] commands =
        [
            ["list"], ["get", "--id", id],
            ["create", "--report-id", id, "--title", "Failure", "--level", "sev2", "--occurred-at", "2026-10-18T12:00:00Z"],
            ["resolve", "--id", id, "--version", id, "--reason", "Recovered"],
            ["severity", "--id", id, "--version", id, "--reason", "Escalated", "--level", "sev1"]
        ];
        foreach (var command in commands)
        {
            var missingKey = await IncidentCli().TestRunAsync([.. command, "--server", Http.BaseAddress!.ToString()]);
            Assert.AreNotEqual(0, missingKey.ProgramReturn);
            Assert.Contains("--api-key", missingKey.StdErr);
            var missingServer = await IncidentCli().TestRunAsync([.. command, "--api-key", key]);
            Assert.AreNotEqual(0, missingServer.ProgramReturn);
            Assert.Contains("--server", missingServer.StdErr);
        }
        var limited = await CreateApplicationKey(Guid.Parse(key.Split('.')[0]), query: false, create: false, resolve: false);
        foreach (var command in commands)
        {
            var denied = await RunIncidentCli(limited, command[0], command[1..]);
            Assert.AreNotEqual(0, denied.ProgramReturn);
            Assert.Contains("Unauthorized", denied.StdErr);
            Assert.DoesNotContain(limited, denied.StdOut + denied.StdErr);
        }
        var badKey = await RunIncidentCli("invalid-key", "list");
        Assert.AreNotEqual(0, badKey.ProgramReturn);
        Assert.Contains("WrongKey", badKey.StdErr);
        Assert.DoesNotContain("invalid-key", badKey.StdErr);
        var invalidPage = await RunIncidentCli(key, "list", "--page-size", "101");
        Assert.AreNotEqual(0, invalidPage.ProgramReturn);
        Assert.Contains("InvalidInput", invalidPage.StdErr);
        var invalidContext = await RunIncidentCli(key, "create", "--report-id", id, "--title", "Bad context", "--level", "sev2",
            "--occurred-at", "2026-10-18T12:00:00Z", "--context", "[]");
        Assert.AreNotEqual(0, invalidContext.ProgramReturn);
        Assert.AreEqual(0, CliJson(await RunIncidentCli(key, "list", "--json")).Value<int>("totalCount"));
    }
}

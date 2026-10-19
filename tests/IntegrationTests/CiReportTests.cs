using Aiursoft.AlertCenter.Client;
using Aiursoft.CommandFramework;
using Newtonsoft.Json.Linq;
using TestResult = Aiursoft.CommandFramework.Models.TestResult;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class IncidentPagesTests
{
    private Task<TestResult> ReportPipeline(string key, long id, string result, string project = "550",
        string branch = "master", string? missing = null)
    {
        var environment = new Dictionary<string, string>
        {
            ["CI_SERVER_URL"] = "https://gitlab.example.com", ["CI_PROJECT_ID"] = project,
            ["CI_PROJECT_PATH"] = "aiursoft/example", ["CI_COMMIT_BRANCH"] = branch,
            ["CI_PIPELINE_ID"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CI_PIPELINE_URL"] = $"https://gitlab.example.com/aiursoft/example/-/pipelines/{id}",
            ["CI_COMMIT_SHA"] = "0123456789abcdef0123456789abcdef01234567",
            ["CI_PIPELINE_CREATED_AT"] = "2026-10-19T00:00:00Z"
        };
        if (missing != null) environment.Remove(missing);
        return new NestedCommandApp().WithFeature(new CiReportHandler(name => environment.GetValueOrDefault(name)))
            .TestRunAsync(["report", "--server", Http.BaseAddress!.ToString(), "--api-key", key, "--result", result, "--json"]);
    }

    private Task<TestResult> RunCiMetadata(string key, params string[] arguments) =>
        IncidentCli().TestRunAsync(["metadata", "patch", "--server", Http.BaseAddress!.ToString(), "--api-key", key, .. arguments]);

    private async Task<string> ReportingKey()
    {
        var original = await CreateApplication();
        return await CreateApplicationKey(Guid.Parse(original.Split('.')[0]), severity: true, mitigate: true, metadata: true);
    }

    private async Task<JArray> ReportedIncidents(string key) =>
        (JArray)CliJson(await RunIncidentCli(key, "list", "--json"))["items"]!;

    [TestMethod]
    public async Task CiReportCountsEscalatesRecoversAndStartsANewCycle()
    {
        var key = await ReportingKey();
        CliJson(await ReportPipeline(key, 1, "success"));
        Assert.AreEqual(0, (await ReportedIncidents(key)).Count);
        for (var id = 2; id <= 4; id++)
        {
            CliJson(await ReportPipeline(key, id, "failed"));
            var current = (await ReportedIncidents(key)).Single();
            Assert.AreEqual(id - 1, current["metadata"]!["ci"]!.Value<int>("consecutiveFailures"));
            Assert.AreEqual(id == 4 ? "sev2" : "sev3", current.Value<string>("level"));
            Assert.IsTrue(current["metadata"]!["ci"]!.Value<bool>("applied"));
        }
        CliJson(await ReportPipeline(key, 5, "success"));
        var recovered = (await ReportedIncidents(key)).Single();
        Assert.AreEqual("mitigated", recovered.Value<string>("status"));
        Assert.AreEqual(0, recovered["metadata"]!["ci"]!.Value<int>("consecutiveFailures"));
        CliJson(await ReportPipeline(key, 6, "failed"));
        var incidents = await ReportedIncidents(key);
        Assert.AreEqual(2, incidents.Count);
        Assert.AreNotEqual(recovered.Value<string>("incidentId"), incidents[0]!.Value<string>("incidentId"));
        Assert.AreEqual("sev3", incidents[0]!.Value<string>("level"));
        Assert.AreEqual(1, incidents[0]!["metadata"]!["ci"]!.Value<int>("consecutiveFailures"));
    }

    [TestMethod]
    public async Task CiReportDeduplicatesConcurrentReportsAndIgnoresStaleFailures()
    {
        var key = await ReportingKey();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ReportPipeline(key, 10, "failed")));
        foreach (var result in results) CliJson(result);
        var incident = (await ReportedIncidents(key)).Single();
        Assert.AreEqual(1, incident["metadata"]!["ci"]!.Value<int>("consecutiveFailures"));
        // This exact UUID comes from the previous Python uuid5 implementation.
        Assert.AreEqual("bea5b68b-9fa0-55f4-9bf0-df3e95719b94", incident.Value<string>("reportId"));
        CliJson(await ReportPipeline(key, 10, "success"));
        CliJson(await ReportPipeline(key, 10, "failed"));
        CliJson(await ReportPipeline(key, 9, "failed"));
        incident = (await ReportedIncidents(key)).Single();
        Assert.AreEqual("mitigated", incident.Value<string>("status"));
        Assert.AreEqual(10, incident["metadata"]!["ci"]!.Value<int>("pipelineId"));
        Assert.AreEqual(0, incident["metadata"]!["ci"]!.Value<int>("consecutiveFailures"));
    }

    [TestMethod]
    public async Task CiReportCompletesPendingRecoveryBeforeAcceptingANewerFailure()
    {
        var key = await ReportingKey();
        CliJson(await ReportPipeline(key, 10, "failed"));
        var current = (await ReportedIncidents(key)).Single();
        var id = current.Value<string>("incidentId")!;
        var pending = new JObject
        {
            ["pipelineId"] = 11, ["pipelineUrl"] = "https://gitlab.example.com/pipelines/11",
            ["result"] = "success", ["consecutiveFailures"] = 0, ["applied"] = false
        };
        CliJson(await RunCiMetadata(key, "--id", id, "--version", current.Value<string>("version")!,
            "--property", "ci", "--value", pending.ToString(), "--json"));
        CliJson(await ReportPipeline(key, 12, "failed"));
        var incidents = await ReportedIncidents(key);
        Assert.AreEqual(2, incidents.Count);
        Assert.AreEqual("mitigated", incidents.Single(i => i.Value<string>("incidentId") == id).Value<string>("status"));
        Assert.AreEqual("sev3", incidents[0]!.Value<string>("level"));
        Assert.AreEqual(1, incidents[0]!["metadata"]!["ci"]!.Value<int>("consecutiveFailures"));
    }

    [TestMethod]
    public async Task CiReportPreservesOtherMetadataAndManualSeverityAndIsolatesStreams()
    {
        var key = await ReportingKey();
        CliJson(await ReportPipeline(key, 1, "failed"));
        var incident = (await ReportedIncidents(key)).Single();
        var id = incident.Value<string>("incidentId")!;
        var patched = CliJson(await RunCiMetadata(key, "--id", id, "--version", incident.Value<string>("version")!,
            "--property", "ownerNote", "--value", "{\"keep\":true}", "--json"));
        CliJson(await RunIncidentCli(key, "severity", "--id", id, "--version", patched.Value<string>("version")!,
            "--level", "sev1", "--reason", "Manual escalation", "--json"));
        CliJson(await ReportPipeline(key, 2, "failed"));
        CliJson(await ReportPipeline(key, 3, "failed"));
        CliJson(await ReportPipeline(key, 4, "failed", project: "551"));
        CliJson(await ReportPipeline(key, 5, "failed", branch: "release"));
        var incidents = await ReportedIncidents(key);
        Assert.AreEqual(3, incidents.Count);
        var original = incidents.Single(i => i.Value<string>("incidentId") == id);
        Assert.AreEqual("sev1", original.Value<string>("level"));
        Assert.IsTrue(original["metadata"]!["ownerNote"]!.Value<bool>("keep"));
        var otherKey = await ReportingKey();
        CliJson(await ReportPipeline(otherKey, 6, "success"));
        Assert.AreEqual(0, (await ReportedIncidents(otherKey)).Count);
        Assert.AreEqual("new", (await ReportedIncidents(key)).Single(i => i.Value<string>("incidentId") == id).Value<string>("status"));
    }

    [TestMethod]
    public async Task CiReportRejectsInvalidInputAndMetadataAndRequiresPermissions()
    {
        var key = await ReportingKey();
        foreach (var missing in new[] { "CI_SERVER_URL", "CI_PROJECT_ID", "CI_PROJECT_PATH", "CI_COMMIT_BRANCH", "CI_PIPELINE_ID", "CI_PIPELINE_URL", "CI_COMMIT_SHA", "CI_PIPELINE_CREATED_AT" })
        {
            var result = await ReportPipeline(key, 1, "failed", missing: missing);
            Assert.AreNotEqual(0, result.ProgramReturn);
            Assert.Contains(missing, result.StdErr);
            Assert.DoesNotContain(key, result.StdErr);
        }
        Assert.AreNotEqual(0, (await ReportPipeline(key, 1, "unknown")).ProgramReturn);
        Assert.AreNotEqual(0, (await ReportPipeline(key, -1, "failed")).ProgramReturn);
        Assert.AreEqual(0, (await ReportedIncidents(key)).Count);
        var limited = await CreateApplicationKey(Guid.Parse(key.Split('.')[0]), query: false);
        var denied = await ReportPipeline(limited, 1, "failed");
        Assert.AreNotEqual(0, denied.ProgramReturn);
        Assert.Contains("Unauthorized", denied.StdErr);
        CliJson(await ReportPipeline(key, 1, "failed"));
        var incident = (await ReportedIncidents(key)).Single();
        CliJson(await RunCiMetadata(key, "--id", incident.Value<string>("incidentId")!,
            "--version", incident.Value<string>("version")!, "--property", "ci", "--value", "{}", "--json"));
        var invalid = await ReportPipeline(key, 2, "success");
        Assert.AreNotEqual(0, invalid.ProgramReturn);
        Assert.Contains("Invalid CI metadata", invalid.StdErr);
        Assert.AreEqual("new", (await ReportedIncidents(key)).Single().Value<string>("status"));
    }
}

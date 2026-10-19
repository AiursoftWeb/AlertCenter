using System.Net;
using Aiursoft.AiurProtocol.Exceptions;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.SDK;
using Aiursoft.AlertCenter.SDK.Models;
using Newtonsoft.Json.Linq;

namespace Aiursoft.AlertCenter.Client;

internal sealed class CiReporter(AlertCenterApiClient client)
{
    public async Task<AiurResponse> ReportAsync(GitLabPipeline pipeline)
    {
        var transportFailures = 0;
        for (var attempt = 0; attempt < 32; attempt++)
        {
            try
            {
                var response = await client.QueryIncidentsAsync(new QueryIncidentsRequest
                {
                    MetadataKey = "source", MetadataValue = pipeline.Source, PageSize = 1
                });
                var incident = response.Items.FirstOrDefault();
                if (incident == null)
                {
                    if (pipeline.Result == "success")
                        return Done("No incident exists; successful pipelines need no incident.");
                    await CreateAsync(pipeline, State(pipeline, 1));
                    continue;
                }
                var current = ReadState(incident);
                var terminal = incident.Status is "mitigated" or "resolved";
                var previousId = current.Value<long>("pipelineId");
                var previousResult = current.Value<string>("result")!;
                var failures = current.Value<long>("consecutiveFailures");
                if (!current.Value<bool>("applied"))
                {
                    // Finish the persisted intent before accepting any newer pipeline result.
                    var reason = $"GitLab pipeline {previousId}: {current.Value<string>("pipelineUrl")}";
                    if (previousResult == "success" && !terminal)
                    {
                        await client.MitigateIncidentAsync(incident.IncidentId, new MitigateIncidentRequest
                        {
                            Version = incident.Version, Reason = reason
                        });
                        continue;
                    }
                    if (previousResult == "failed" && failures >= 3 && !terminal && incident.Level is "sev3" or "sev4")
                    {
                        await client.ChangeIncidentSeverityAsync(incident.IncidentId, new ChangeIncidentSeverityRequest
                        {
                            Version = incident.Version, Level = "sev2", Reason = reason
                        });
                        continue;
                    }
                    current["applied"] = true;
                    await PatchAsync(incident, current);
                    continue;
                }
                if (previousId > pipeline.Id)
                    return Done("Ignoring an older pipeline result.");
                if (previousId == pipeline.Id && previousResult == "success" && pipeline.Result == "failed")
                    return Done("Ignoring a failure already superseded by success for this pipeline.");
                if (previousId < pipeline.Id || previousResult != pipeline.Result)
                {
                    var next = State(pipeline, pipeline.Result == "failed" ? (terminal ? 1 : checked(failures + 1)) : 0);
                    if (pipeline.Result == "failed" && terminal)
                        await CreateAsync(pipeline, next);
                    else
                        await PatchAsync(incident, next);
                    continue;
                }
                return Done($"Pipeline result applied: {pipeline.Result} with {failures} consecutive failures.");
            }
            catch (AiurUnexpectedServerResponseException exception) when (exception.Response.Code == Code.Conflict)
            {
                // Another writer won. Re-read the version and recompute instead of replaying a stale patch.
                await Task.Delay(250);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or WebException)
            {
                // A write may have committed before its response was lost. Re-read persisted state.
                if (++transportFailures >= 5)
                    throw new InvalidOperationException("AlertCenter remained unavailable after 5 attempts; retry this reporting job.");
                await Task.Delay(1000);
            }
        }
        throw new InvalidOperationException("Pipeline reporting did not converge after 32 attempts; retry this reporting job.");
    }

    private static AiurResponse Done(string message) => new() { Code = Code.JobDone, Message = message };

    private static JObject State(GitLabPipeline pipeline, long failures) => new()
    {
        ["pipelineId"] = pipeline.Id, ["pipelineUrl"] = pipeline.Url, ["result"] = pipeline.Result,
        ["consecutiveFailures"] = failures, ["applied"] = false
    };

    private static JObject ReadState(IncidentDetails incident)
    {
        if (incident.Metadata["ci"] is not JObject value || value["pipelineId"]?.Type != JTokenType.Integer ||
            value["consecutiveFailures"]?.Type != JTokenType.Integer || value["applied"]?.Type != JTokenType.Boolean ||
            value["pipelineUrl"]?.Type != JTokenType.String || value.Value<long>("pipelineId") <= 0 ||
            value.Value<long>("consecutiveFailures") < 0 || value.Value<string>("result") is not ("success" or "failed"))
            throw new InvalidOperationException("Invalid CI metadata; refusing to reset existing state.");
        return (JObject)value.DeepClone();
    }

    private Task<PatchIncidentMetadataResponse> PatchAsync(IncidentDetails incident, JObject state) =>
        client.PatchIncidentMetadataAsync(incident.IncidentId, new PatchIncidentMetadataRequest
        {
            Version = incident.Version, Property = "ci", Value = state
        });

    private Task<CreateIncidentResponse> CreateAsync(GitLabPipeline pipeline, JObject state) =>
        client.CreateIncidentAsync(new CreateIncidentRequest
        {
            ReportId = pipeline.ReportId(), Title = pipeline.Title, Level = "sev3", OccurredAtUtc = pipeline.CreatedAt,
            Content = $"Pipeline: {pipeline.Url}\n\nCommit: {pipeline.Commit}",
            Metadata = new JObject { ["source"] = pipeline.Source, ["ci"] = state }
        });
}

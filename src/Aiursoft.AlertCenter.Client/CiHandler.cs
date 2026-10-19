using System.CommandLine;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.SDK;
using Aiursoft.CommandFramework.Framework;

namespace Aiursoft.AlertCenter.Client;

public sealed class CiHandler : NavigationCommandHandlerBuilder
{
    protected override string Name => "ci";
    protected override string Description => "Report CI pipeline results.";
    protected override CommandHandlerBuilder[] GetSubCommandHandlers() => [new CiReportHandler()];
}

public sealed class CiReportHandler(Func<string, string?>? environment = null) : IncidentCommandHandler
{
    protected override string Name => "report";
    protected override string Description => "Report a GitLab pipeline result; count failures and mitigate recovery using incident metadata.";
    private readonly Option<string> _result = new("--result")
    {
        Required = true, Description = "Pipeline result: success or failed. Pipeline identity is read from GitLab CI environment variables."
    };
    protected override IEnumerable<Option> IncidentOptions() => [_result];
    protected override Task<AiurResponse> InvokeAsync(AlertCenterApiClient client, ParseResult context) =>
        new CiReporter(client).ReportAsync(GitLabPipeline.Read(environment ?? Environment.GetEnvironmentVariable, context.GetValue(_result)!));
}

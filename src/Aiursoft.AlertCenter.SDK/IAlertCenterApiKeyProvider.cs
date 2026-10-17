using Microsoft.Extensions.Options;

namespace Aiursoft.AlertCenter.SDK;

public interface IAlertCenterApiKeyProvider
{
    ValueTask<string> GetApiKeyAsync();
}

internal sealed class ConfiguredAlertCenterApiKeyProvider(IOptionsMonitor<AlertCenterApiOptions> options)
    : IAlertCenterApiKeyProvider
{
    public ValueTask<string> GetApiKeyAsync() => ValueTask.FromResult(options.CurrentValue.ApiKey);
}

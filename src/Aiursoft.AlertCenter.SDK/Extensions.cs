using Aiursoft.AiurProtocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aiursoft.AlertCenter.SDK;

public static class Extensions
{
    public static IServiceCollection AddAlertCenterSdk(
        this IServiceCollection services, string endpoint, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("The endpoint must be an HTTP or HTTPS base URL without credentials, a query, or a fragment.", nameof(endpoint));

        services.AddAiurProtocolClient();
        services.Configure<AlertCenterApiOptions>(options =>
        {
            options.Endpoint = endpoint.TrimEnd('/');
            options.ApiKey = apiKey;
        });
        services.TryAddScoped<IAlertCenterApiKeyProvider, ConfiguredAlertCenterApiKeyProvider>();
        services.AddScoped<AlertCenterApiClient>();
        return services;
    }
}

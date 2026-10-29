using Android.Content;
using Aiursoft.AiurProtocol;
using Aiursoft.AiurProtocol.Services;
using Aiursoft.AlertCenter.Android.Oidc;
using Aiursoft.AlertCenter.SDK;
using Aiursoft.AlertCenter.SDK.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Aiursoft.AlertCenter.Android;

public sealed class AppSession : IDisposable
{
    public const string DefaultServer = "https://alertcenter.aiursoft.com";
    private readonly ISharedPreferences _preferences;
    private readonly AndroidAccessTokenProvider _tokens;
    private readonly HttpClient _http = new();
    private ServiceProvider? _services;
    private OidcPkceClient? _oidc;

    public AppSession(Context context)
    {
        _preferences = context.GetSharedPreferences("alertcenter", FileCreationMode.Private)!;
        _tokens = new AndroidAccessTokenProvider(new AndroidKeystoreTokenStore(context));
        ConfigureApi(Endpoint);
        var client = new OidcPkceClient(_http, _preferences);
        if (_tokens.TryRestorePersistedSession(client)) _oidc = client;
    }

    public string? Feedback { get; set; }
    public string Endpoint => _preferences.GetString("server", DefaultServer) ?? DefaultServer;
    public bool IsAuthenticated => _tokens.IsAuthenticated;
    public MobileConfigurationResponse? Configuration { get; private set; }
    public string DisplayName { get; private set; } = "Signed in";
    public AlertCenterMobileClient? Api { get; private set; }
    public AlertCenterMobileClient RequireApi() => Api ?? throw new InvalidOperationException("Connect to a server first.");

    public async Task<MobileConfigurationResponse> ConnectAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.Host == "127.0.0.1")) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Enter an HTTPS server URL. HTTP is allowed only for the local USB tunnel.");
        endpoint = uri.ToString().TrimEnd('/');
        if (!string.Equals(endpoint, Endpoint, StringComparison.OrdinalIgnoreCase)) SignOut();
        _preferences.Edit()!.PutString("server", endpoint)!.Apply();
        ConfigureApi(endpoint);
        Configuration = await RequireApi().GetConfigurationAsync();
        if (Configuration.AuthenticationMode == "OIDC")
        {
            _oidc = new OidcPkceClient(_http, _preferences);
            await _oidc.ConfigureAsync(Configuration.Authority, Configuration.ClientId,
                Configuration.RedirectUri, Configuration.Scopes, cancellationToken);
            _tokens.TryRestoreSession(_oidc);
        }
        else if (Configuration.AuthenticationMode == "Local")
        {
            _oidc = null;
            _tokens.ClearOidcSession();
        }
        else throw new InvalidOperationException("Unsupported authentication mode.");
        return Configuration;
    }

    public Uri CreateAuthorizationUri() => (_oidc ?? throw new InvalidOperationException("Connect first.")).CreateAuthorizationUri();
    public async Task CompleteAuthorizationAsync(Uri callback, CancellationToken cancellationToken = default)
    {
        if (_oidc == null) await ConnectAsync(Endpoint, cancellationToken);
        var tokens = await _oidc!.CompleteAuthorizationAsync(callback, cancellationToken);
        _tokens.SetSession(_oidc, tokens);
    }
    public async Task LoginLocalAsync(string identity, string password)
    {
        var response = await RequireApi().LoginLocalAsync(new LocalLoginRequest { EmailOrUserName = identity, Password = password });
        _tokens.SetLocalSession(response.AccessToken, response.ExpiresAt);
        DisplayName = response.DisplayName;
    }
    public async Task RegisterLocalAsync(string email, string password)
    {
        var response = await RequireApi().RegisterLocalAsync(new LocalRegistrationRequest { Email = email, Password = password });
        _tokens.SetLocalSession(response.AccessToken, response.ExpiresAt);
        DisplayName = response.DisplayName;
    }
    public void SignOut()
    {
        _tokens.Clear();
        _preferences.Edit()!.Remove("oidc.state")!.Remove("oidc.verifier")!.Apply();
        DisplayName = string.Empty;
    }
    public void UpdateDisplayName(string name) => DisplayName = name;
    private void ConfigureApi(string endpoint)
    {
        _services?.Dispose();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiurProtocolClient();
        _services = services.BuildServiceProvider();
        Api = new AlertCenterMobileClient(_services.GetRequiredService<AiurProtocolClient>(), endpoint, _tokens);
    }
    public void Dispose()
    {
        _services?.Dispose();
        _http.Dispose();
    }
}

# Android app

AlertCenter has an optional native .NET Android client, built with the same
Activity/XML, Material and shared AiurProtocol SDK structure as Kanban.
It supports Android 6.0 (API 23) and later.

## Respond to incidents

Connect to your AlertCenter HTTPS server and sign in. Open the navigation drawer
for all incidents, your assigned incidents, accessible applications, new incidents,
and sign-out. Filters include progress, severity, acknowledgement and keywords;
pagination is at the bottom. The default queue shows active incidents using the
server's visibility policy. Explicit severity filters can include levels hidden
from the default queue.
Select sev4 or all severities to view informational incidents.

Incident details show the original and current on-call engineers, local times,
context, metadata, and the discussion/timeline. Engineers with incident update
access can acknowledge, take ownership, comment, change severity, mitigate,
resolve, and reopen. Acknowledgement assigns ownership independently of progress.
Mitigation requires acknowledgement; final resolution requires mitigation and an
explicit confirmation. Conflicting edits require a refresh and retain the draft.
The server applies the same application permissions and notification rules as the
web client. Assignment and on-call duty do not grant application access.

## Authentication

The app discovers its server configuration at `/api/v1/config`. Employee requests
use `/api/v1/mobile`; existing application API keys and integration APIs retain
their existing behavior. Do not place an application integration key in the app.

For OIDC, register a separate **public/native** client with your identity provider:

- Client ID: `alertcenter-android`.
- Exact redirect URI: `com.aiursoft.alertcenter:/oauth2redirect`.
- Authorization Code with PKCE `S256`; no client secret.
- Scopes: `openid profile email offline_access`.
- Signed access-token audience: `alertcenter-android`.
- Access-token claims: immutable `sub`, `preferred_username`, `name`, and `email`.
  An already linked account can authenticate with its existing immutable subject.

Keep the confidential web client and its HTTPS `/signin-oidc` callback. Configure
the native provider separately on the server:

```text
AppSettings__OIDC__MobileAuthority=https://identity.example.com/application/o/alertcenter-android/
AppSettings__OIDC__MobileClientId=alertcenter-android
AppSettings__OIDC__ApiAudience=alertcenter-android
AppSettings__OIDC__MobileRedirectUri=com.aiursoft.alertcenter:/oauth2redirect
AppSettings__OIDC__ApiScope=
AppSettings__OIDC__RequireHttpsMetadata=true
```

`MobileAuthority` falls back to the web authority when empty. Check that both
providers identify the same account consistently; their immutable subject mapping
must match the deployment's account-linking policy.

Login opens the system browser. Refresh tokens are encrypted with AES-GCM and an
Android Keystore key; sign-out and changing servers remove the saved session.
Local authentication follows the server's existing registration and password
policies, with a 12-hour token held for the current app process.

HTTPS uses the device's normal certificate trust. For private development over
USB, bind the server to loopback and use `adb reverse tcp:5080 tcp:5080`; enter
`http://127.0.0.1:5080` in the app. Cleartext HTTP is permitted only for that address.

## Build and install

The Android project is separate from the server solution so normal server builds
do not require an Android workload. Install the .NET 10 SDK and Android workload:

```bash
dotnet workload install android
mkdir -p .android-sdk .android-jdk
dotnet build src/Aiursoft.AlertCenter.Android/Aiursoft.AlertCenter.Android.csproj \
  -t:InstallAndroidDependencies -f net10.0-android \
  -p:AndroidSdkDirectory="$PWD/.android-sdk" \
  -p:JavaSdkDirectory="$PWD/.android-jdk" \
  -p:AcceptAndroidSDKLicenses=True
```

Build a sideloadable APK:

```bash
dotnet publish src/Aiursoft.AlertCenter.Android/Aiursoft.AlertCenter.Android.csproj \
  -c Release -f net10.0-android -p:AndroidPackageFormats=apk \
  -p:AndroidSdkDirectory="$PWD/.android-sdk" \
  -p:JavaSdkDirectory="$PWD/.android-jdk"
```

Install the signed APK from `bin/Release/net10.0-android/publish/` with
`adb install -r <apk-path>`. Use your deployment's protected signing key for
releases and retain it for updates. See the [.NET Android build properties](https://learn.microsoft.com/en-us/dotnet/android/building-apps/build-properties#androidpackageformats)
for package format and signing configuration.

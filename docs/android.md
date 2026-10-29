# Android app

AlertCenter has an optional native .NET Android client, built with the same
Activity/XML, Material and shared AiurProtocol SDK structure as Kanban.
It supports Android 6.0 (API 23) and later.

## Respond to incidents

Connect to your AlertCenter HTTPS server and sign in. The bottom navigation keeps
**Incidents**, **Applications**, and **Mine** available. Search and filters are in
the toolbar; account, server, appearance, language, and sign-out are in account
settings. Each queue retains its filters and current position when you return
from an incident. Pagination is at the bottom of the incident list.

The default queue shows unmitigated incidents using the server's visibility
policy. Explicit severity filters can include levels hidden from that default.
Filter by application, progress, severity or severity range, acknowledgement,
keywords, and inclusive received dates (UTC). Progress and acknowledgement are
separate: an acknowledged incident can still need mitigation. Cards distinguish
the last acknowledging owner from the incident's original on-call roster.

Details put the summary and people first, followed by the description and activity.
Technical context and metadata are available separately. Markdown uses the same
bundled UIStack renderer as the web application. HTML is escaped; remote images
require an explicit tap. Links open in the browser. The reader has no native
authentication bridge. Advanced web enhancements such as Mermaid and MathJax
remain available through **Open on the web**.

The bottom action follows the incident's current state. **More actions** exposes
other permitted actions. Engineers can acknowledge, take ownership, comment,
change severity, mitigate, resolve, and reopen, using the same server services
and notification rules as the web client. Mitigation requires acknowledgement;
resolution follows mitigation. For sev2–4, an explicit checkbox can mitigate and
resolve together. The app never treats automated recovery as implicit permission
to resolve an incident.

Editors show consequences before submission, support Markdown preview, preserve
drafts on errors and rotation, and ask before discarding unsent text. Version
conflicts require a refresh; the app never silently retries a write. A network
failure does not prove that a submitted operation was cancelled.

Deleting an incident requires its independent delete permission, its exact title,
and its current version. Deleting a comment retains the server's deletion audit.
Assignment or on-call duty does not grant access. Low-frequency application/access,
on-call, and account administration opens the existing web interface explicitly;
these are not duplicated native administration forms.

The palette follows the system or the selected light/dark theme. Text scales with
the device, English and Simplified Chinese are selectable, and icon controls have
accessible names. User-supplied incident content and names are not translation keys.

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

## Shared Markdown and translations

`Assets/markdown/uistack-markdown-ui.js` is the unmodified browser bundle from
`@aiursoft/uistack-markdown-ui` 1.2.6, checked against the web lockfile's integrity.
When updating it, verify the package integrity, retain its upstream notices, and
run `node --test tests/Frontend/mobile-markdown.test.cjs`. Dependency licenses are
available in the app's **Open-source notices**.

`UiText.cs` declares literal localization keys for the existing DotLang extractor;
`Resources/UiText.resx` provides English fallback. Generate language resources with
DotLang rather than adding a separate translation system. Existing shared web
terms should keep their established translations.

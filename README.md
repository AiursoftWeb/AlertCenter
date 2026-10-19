# AlertCenter

[![MIT licensed](https://img.shields.io/badge/license-MIT-blue.svg)](./LICENSE)
[![Pipeline stat](https://gitlab.aiursoft.com/aiursoft/alertcenter/badges/master/pipeline.svg)](https://gitlab.aiursoft.com/aiursoft/alertcenter/-/pipelines)
[![Test Coverage](https://gitlab.aiursoft.com/aiursoft/alertcenter/badges/master/coverage.svg)](https://gitlab.aiursoft.com/aiursoft/alertcenter/-/pipelines)
[![Man hours](https://manhours.aiursoft.com/r/github.com/aiursoftweb/alertcenter.svg)](https://manhours.aiursoft.com/r/github.com/aiursoftweb/alertcenter.html)
[![Docker](https://img.shields.io/docker/pulls/aiursoft/alertcenter.svg)](https://hub.docker.com/r/aiursoft/alertcenter)

AlertCenter is a self-hosted alert management and notification platform built on ASP.NET Core (.NET 10). Business systems create incidents through one HTTP API; teams manage incidents, on-call schedules, and delivery through Webhook, email, and in-app notifications.

![AlertCenter screenshot](./screenshot.png)

On an empty database, the default local account is `admin@default.com` with password `Admin@123456!`. Change the password before exposing the application.

## What is AlertCenter?

- Independent API keys, idempotent reporting, and five severity levels (`sev0` through `sev4`).
- Independent incidents, acknowledgement, ownership, mitigation, resolution, and Markdown discussions.
- On-call teams, reusable scheduling rules, and an interactive schedule editor with overlap and rest validation.
- Severity-based notification routing, personal inboxes, and delivery history.
- Application access grants for users and roles, with separate permissions for application, on-call, and push management.
- SQLite for standalone use, MySQL support, and local or OIDC authentication through [Aiursoft Template](https://github.com/AiursoftWeb/template).

## Install with Docker

Run the installer with the desired port (omit it to choose an available port):

```bash
curl -fsSL https://raw.githubusercontent.com/aiursoftweb/alertcenter/master/install.sh | sudo bash -s -- 8080
```

Installation starts after three seconds; press Ctrl+C to cancel. Then open `http://localhost:8080`.

## Run manually

1. Install the .NET 10 SDK and Node.js/npm.
2. Prepare the frontend assets:

   ```bash
   npm ci --prefix src/Aiursoft.AlertCenter/wwwroot
   npm run build --prefix src/Aiursoft.AlertCenter/wwwroot
   ```

3. Start the application:

   ```bash
   dotnet run --project src/Aiursoft.AlertCenter
   ```

4. Open the URL printed by the application.

The defaults use SQLite and local accounts. Configure authentication, database, and storage through [appsettings.json](src/Aiursoft.AlertCenter/appsettings.json) or environment variables.

## Run in Microsoft Visual Studio

1. Prepare the frontend assets as described above.
2. Open `Aiursoft.AlertCenter.sln` and run the `Aiursoft.AlertCenter` project with `F5`.

## Run in Docker

```bash
image=aiursoft/alertcenter
appName=alertcenter
sudo docker pull $image
sudo docker run -d --name $appName --restart unless-stopped -p 5000:5000 -v /var/www/$appName:/data $image
```

Open `http://localhost:5000`. Keep `/data` persistent for configuration, the SQLite database, uploaded files, and Data Protection keys.

| Properties | Value |
| --- | --- |
| Image | aiursoft/alertcenter |
| Ports | 5000 |
| Binary path | /app |
| Data path | /data |
| Config path | /data/appsettings.json |

The image copies its default configuration to `/data/appsettings.json` on first start and retains that file on subsequent starts. Environment variables use double underscores, such as `ConnectionStrings__DefaultConnection` or `AppSettings__OIDC__ClientSecret`.

## Creating incidents

Open **Applications**, create an application, then open **API Key Management** to create named keys for its integrations. Each key has independent query, create, mitigate, resolve and severity permissions. A secret is shown only once; edit, disable or delete individual keys without rotating the others. The built-in **Integration Guide** at `/Applications/GettingStarted` provides examples for the current server.

Create an incident using `POST /api/v1/incidents`:

```bash
curl -i 'http://localhost:5000/api/v1/incidents' \
  -H 'Authorization: Bearer YOUR_API_KEY' \
  -H 'Content-Type: application/json' \
  --data '{
    "reportId": "0c561d22-5b71-4a4f-9c13-c824e273a8aa",
    "title": "Order payment timed out",
    "content": "The downstream provider did not respond within 5 seconds.",
    "level": "sev1",
    "occurredAtUtc": "2026-10-16T12:00:00Z",
    "context": {"orderId": "ORDER-1001", "traceId": "demo-trace"}
  }'
```

Use your server's HTTPS URL for remote reporting and keep API keys outside source control. Reuse a `reportId` only when retrying the same report. A new report returns `201 Created`; a duplicate returns `202 Accepted` with `Code: 1` and `Duplicate: true`. Invalid credentials return `401`; invalid fields return `400`. Requests are limited to 64 KiB, titles to 200 characters, and content to 4,000 characters.

The API uses [AiurProtocol](https://github.com/AiursoftWeb/aiurprotocol). Receipts contain `Code`, `Message`, `ProtocolVersion`, `ReportId`, `ReceivedAtUtc`, `IncidentId`, and `Duplicate`. A new report uses `Code: 2` (`JobDone`); a duplicate uses `Code: 1` (`NoActionTaken`). Invalid credentials use `Code: -1` (`WrongKey`); invalid input uses `Code: -10` (`InvalidInput`) with an `Items` array describing errors. Oversized requests return HTTP `413` with the same invalid-input envelope.

### Command-line client

Install the .NET 10 tool:

```bash
dotnet tool install --global Aiursoft.AlertCenter.Client
```

Every business command requires an explicit `--server` and `--api-key`. The key
selects the application and its permitted operations; credentials are never saved
or automatically read from environment variables. The shell variable below is
passed explicitly. Use `--json` for the complete AiurProtocol response.

```bash
alertcenter list --server https://alertcenter.example.com --api-key "$APP_API_KEY" --active-only --page 1 --page-size 50 --json
alertcenter get --server https://alertcenter.example.com --api-key "$APP_API_KEY" --id "$INCIDENT_ID" --json
alertcenter create --server https://alertcenter.example.com --api-key "$APP_API_KEY" --report-id "$REPORT_ID" --title "Main branch build failed" --level sev2 --occurred-at "2026-10-18T12:00:00Z" --content "Compiler error" --context '{"pipeline":42}' --json
alertcenter severity --server https://alertcenter.example.com --api-key "$APP_API_KEY" --id "$INCIDENT_ID" --version "$INCIDENT_VERSION" --level sev1 --reason "Failure persisted" --json
alertcenter mitigate --server https://alertcenter.example.com --api-key "$APP_API_KEY" --id "$INCIDENT_ID" --version "$INCIDENT_VERSION" --reason "Build recovered" --json
alertcenter resolve --server https://alertcenter.example.com --api-key "$APP_API_KEY" --id "$INCIDENT_ID" --version "$INCIDENT_VERSION" --reason "Build recovered" --json
```

List and detail queries require the key's query scope and only return its own
application's incidents. A missing or other-application incident returns the same
`NotFound`. Detail responses include `incident.version`, content and Context;
`GET /api/v1/incidents/{incidentId}` and SDK `GetIncidentAsync` expose the same data.

Retain the report ID and occurrence time before reporting; reuse them after an
uncertain outcome. Duplicate reports and already resolved incidents exit
successfully. Mitigate, resolve and severity commands require the version from a prior
query and a reason. A conflict fails without automatically fetching a new version
or retrying. Results go to stdout; errors go to stderr with a nonzero exit code.
Use `alertcenter <command> --help` for options. CLI and SDK version: `10.5.0`.

### .NET SDK

Install the .NET 10 reporting client:

```bash
dotnet add package Aiursoft.AlertCenter.SDK
```

Register it with your server's base URL and an application key from configuration:

```csharp
using Aiursoft.AlertCenter.SDK;
using Aiursoft.AlertCenter.SDK.Models;

builder.Services.AddAlertCenterSdk(
    "https://alertcenter.example.com",
    builder.Configuration["AlertCenter:ApiKey"]!);
```

Inject `AlertCenterApiClient` and send a report:

```csharp
public class PaymentIncidents(AlertCenterApiClient incidents)
{
    public Task<CreateIncidentResponse> ReportTimeoutAsync(Guid reportId, DateTimeOffset occurredAtUtc)
        => incidents.CreateIncidentAsync(new CreateIncidentRequest
        {
            ReportId = reportId,
            Title = "Order payment timed out",
            Level = "sev1",
            OccurredAtUtc = occurredAtUtc
        });
}
```

Generate and retain the report ID and occurrence time before the first attempt. The SDK sends once; after an uncertain network outcome, retry the same request with the same ID. Successful duplicates return the original receipt. Input errors throw `AiurBadApiInputException`; authentication and other protocol failures throw `AiurUnexpectedServerResponseException` (inspect its `Response.Code`). These types are in `Aiursoft.AiurProtocol.Exceptions`. Transport failures propagate to the caller. The SDK reports persistence and dispatch scheduling, not notification delivery success.

For rotating credentials, register your own scoped `IAlertCenterApiKeyProvider`; the client retrieves a key for each call. Keep the client on the server, never in a browser.

**Reporting clients must rate-limit at the source.** Each new report ID creates an independent incident, even when its content matches another incident. Retrying the same application/report ID returns the original receipt without creating another incident or delivery. All handling takes place on the incident detail page.

### Query incidents with the SDK

SDK 10.2.0 adds `QueryIncidentsAsync` (`GET /api/v1/incidents`). The same application
Bearer key authorizes both reporting and querying; queries only return that application's
incidents, including their complete structured `Context`.

```csharp
var page = await client.QueryIncidentsAsync(new QueryIncidentsRequest
{
    ActiveOnly = true,
    Page = 1,
    PageSize = 50
});
foreach (var incident in page.Items)
{
    Console.WriteLine($"{incident.IncidentId}: {incident.Title} ({incident.Status})");
}
```

`ActiveOnly` defaults to false; true includes `new` and `mitigated`, excluding
`resolved`. Pages start at 1 (maximum 1,000,000); page sizes range from 1 to 100 and default
to 50. Responses include `Page`, `PageSize`, and `TotalCount`, with newest received incidents
first and incident ID as the tie-breaker. Read all relevant pages before deciding no match
exists. Pagination reflects live data, not a snapshot across requests.

Callers decide which titles, services, or Context fields constitute a duplicate. The SDK
and API do not compare metadata, suppress reports, or make query-and-create atomic.
Queries have no state or notification side effects. AiurProtocol response codes and error
handling match reporting; invalid query parameters return its validation reasons.


### Resolve an incident with the SDK

Use `ResolveIncidentAsync` (`POST /api/v1/incidents/{incidentId}/resolve`) after the
calling application has confirmed recovery. Supply the `Version` returned by a fresh query
and a non-blank `Reason` of at most 4,000 characters:

```csharp
var result = await client.ResolveIncidentAsync(incident.IncidentId, new ResolveIncidentRequest
{
    Version = incident.Version,
    Reason = "Audit run 42 confirmed that the endpoint is healthy."
});
```

The application key can resolve only its own incidents. Any active state can transition
directly to `resolved`; the existing human workflow is unchanged. Resolution preserves the
owner and records the previous state, application identity and reason in the incident timeline.
No engineer is impersonated, and no acknowledgement or mitigation steps are fabricated.

The first resolution returns `JobDone`; an already resolved incident returns `NoActionTaken`
without another timeline entry. A changed active incident returns `Conflict`: query again and
re-evaluate recovery rather than blindly retrying with a new version. This also prevents an old
request from closing a subsequently reopened incident. Missing and other-application incidents
both return `NotFound`. SDK protocol exceptions match reporting. No automatic resolution or
recovery detection runs inside AlertCenter; the caller decides when to invoke this method.

### Mitigating incidents

`POST /api/v1/incidents/{incidentId}/mitigate`, SDK `MitigateIncidentAsync`, and
CLI `alertcenter mitigate` move a `new` incident to `mitigated` without
resolving it or changing its assignee or acknowledgement. Supply the queried
`Version` and a nonempty `Reason` (up to 4,000 characters). This requires the
separate **Mitigate incidents** API key permission, disabled on existing keys.
An already mitigated incident returns `NoActionTaken` without another event or
email. Resolved, reopened-with-a-new-version, and concurrently changed incidents
return `Conflict`; callers must query and re-evaluate rather than blindly retry.
The application-scoped timeline records the reason, and the existing once-per-stage
yellow mitigation email is queued. No recovery detection or CI counter runs here.

## Permissions and on-call response

Platform capabilities use `Permission` claims. Roles collect permissions; the name `Administrators` does not grant privileges by itself.

- `CanManageApplications` permits creating applications. It does not bypass per-application ownership or user/role grants.
- `CanManageOnCall` permits managing teams, scheduling rules, and schedules. Saved shifts determine the current on-call engineers (OCEs); a rule preview alone does not provide coverage.
- `CanManagePush` permits managing channels and sensitive delivery configuration. A user's channel binding access and recipient membership are separate; binding a channel also requires application manage access.

Only the application owner or users explicitly granted incident update permission may handle an incident. OCE duty and incident assignment do not grant access. A view grant alone does not permit incident handling. Default identity initialization runs only when both users and roles are empty; restarting an existing installation does not restore revoked permissions or grant newly introduced permissions.

Applications may bind an on-call team. Each new incident and explicit incident reopen queues built-in email to that team's current OCEs, independently of custom severity routes. Delivery recipients are snapshotted when queued. Missing coverage or invalid recipient configuration produces a visible delivery failure without rejecting the alert. There are no automatic email retries; inspect delivery records before manually retrying an SMTP failure.

## Configuration

See the comments in [appsettings.json](src/Aiursoft.AlertCenter/appsettings.json) for database, storage, authentication, and logging settings. Keep secrets in protected environment variables.

- **OIDC:** set `AppSettings__AuthProvider=OIDC` and the `AppSettings__OIDC__*` values. Register the application's HTTPS `/signin-oidc` callback with your identity provider and assign the required application permissions.
- **Email:** configure `Email__Host`, `Email__Port`, `Email__From`, and, when required, `Email__UserName` and `Email__Password`. STARTTLS is enabled by default. Both custom channels and built-in on-call email use these settings. Emails include HTML and plain-text alternatives with a direct incident link. Incident email is sent only for creation (red), mitigation (yellow), and resolution (green), at most once per stage per configured email route. Mitigate-and-resolve sends only the green resolution email. Acknowledgement, reassignment, comments, severity changes, and reopening do not send email; API resolution follows the same rules. Set `Email__PublicBaseUrl` to your public site address (including any path prefix); it defaults to `https://alertcenter.aiursoft.com`.
- **Webhook:** custom encrypted request headers require `Webhook__HeaderProtectionKey`, a Base64-encoded 32-byte key generated with `openssl rand -base64 32`. Preserve this key: losing or changing it makes saved custom headers unreadable. Without it, default headers and custom JSON body templates remain available.

### Database migrations

The multiple-key migration removes the old application-level secret hash without migrating credentials. Create new scoped keys and update integration configuration after upgrading. EmployeeCenter audit synchronization needs query, create and resolve permissions.

The application applies pending migrations at startup. Generate future migrations for both providers using the [SQLite guide](src/Aiursoft.AlertCenter.Sqlite/HOW_TO_ADD_MIGRATIONS.md) and [MySQL guide](src/Aiursoft.AlertCenter.MySql/HOW_TO_ADD_MIGRATIONS.md).

## How to contribute

Report bugs and suggestions through the issue tracker. Create a fork and a feature branch, then submit a merge request for review.

After preparing frontend assets, run the existing checks:

```bash
dotnet test tests/Aiursoft.AlertCenter.Tests.csproj
node --test tests/Frontend/*.test.cjs
./lint.sh
```

### Application sharing

An application owner has every application permission. Sharing grants independent permissions to users or roles: view, create, update incidents (including comments and status), manage API keys, permanently delete incidents, and manage application settings/sharing. Direct and role grants are combined. Updating or deleting incidents also requires view access. Application creation uses the existing platform permission; platform permissions, incident assignment, and on-call duty do not grant access to another application's incidents.

Only the owner can transfer ownership or delete the application. Sharing managers cannot grant permissions they do not have. Permanent incident deletion requires its exact title and current version. Existing explicit view/manage grants are preserved by the migration; older applications have no inferred owner and need an explicit ownership assignment before owner-only operations. Owner accounts must transfer their applications before deletion.

Each application owns multiple API keys. Owners and users/roles granted API key management can list, create, edit and delete its keys; newly granted key permissions cannot exceed the manager's application permissions. Keys authenticate as `<applicationId>.<keyId>.<secret>` and cannot access another application. The key list shows persistent authenticated-call totals and last-use timestamps (UTC). Counts include permission denials and business failures after valid input/authentication, but exclude malformed requests and invalid credentials.

Applications can change an active incident severity through `POST /api/v1/incidents/{incidentId}/severity` or SDK `ChangeIncidentSeverityAsync`. Supply the queried `Version`, a `Level` from `sev0` to `sev4`, and a `Reason`. This requires the separate **Change incident severity** API key scope (disabled on existing keys). Changes are application-scoped, concurrency-checked and recorded in the timeline without sending email. SDK version: `10.3.0`.

Acknowledgement is independent of incident progress (`new`, `mitigated`, `resolved`).
Any authorized user may acknowledge repeatedly; each acknowledgement assigns the
incident to that user without changing its progress. `AcknowledgedAtUtc` is the
latest acknowledgement time, and `IsAcknowledged` reports whether it has ever
been acknowledged. Reopening preserves acknowledgement and ownership. SDK 10.6.0
query/detail responses also expose these fields and the current owner. Legacy
`status=acked` web links select acknowledged incidents across all progress states;
use `acknowledged=true` or `false` to combine confirmation with a progress filter.

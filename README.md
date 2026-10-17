# AlertCenter

[![MIT licensed](https://img.shields.io/badge/license-MIT-blue.svg)](./LICENSE)
[![Pipeline stat](https://gitlab.aiursoft.com/aiursoft/alertcenter/badges/master/pipeline.svg)](https://gitlab.aiursoft.com/aiursoft/alertcenter/-/pipelines)
[![Test Coverage](https://gitlab.aiursoft.com/aiursoft/alertcenter/badges/master/coverage.svg)](https://gitlab.aiursoft.com/aiursoft/alertcenter/-/pipelines)
[![Man hours](https://manhours.aiursoft.com/r/github.com/aiursoftweb/alertcenter.svg)](https://manhours.aiursoft.com/r/github.com/aiursoftweb/alertcenter.html)
[![Docker](https://img.shields.io/docker/pulls/aiursoft/alertcenter.svg)](https://hub.docker.com/r/aiursoft/alertcenter)

AlertCenter is a self-hosted alert management and notification platform built on ASP.NET Core (.NET 10). Business systems report alerts through one HTTP API; teams manage incidents, on-call schedules, and delivery through Webhook, email, and in-app notifications.

![AlertCenter screenshot](./screenshot.png)

On an empty database, the default local account is `admin@default.com` with password `Admin@123456!`. Change the password before exposing the application.

## What is AlertCenter?

- Independent API keys, idempotent reporting, and five severity levels (`sev0` through `sev4`).
- Incident correlation, acknowledgement, ownership, mitigation, resolution, and Markdown discussions.
- On-call teams, reusable scheduling rules, and an interactive schedule editor with overlap and rest validation.
- Severity-based notification routing, personal inboxes, and delivery history.
- Application access grants for users and roles, with separate permissions for application, on-call, and push management.
- SQLite for standalone use, MySQL support, and local or OIDC authentication through [Aiursoft Template](https://gitlab.aiursoft.com/aiursoft/template).

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

## Reporting alerts

Open **Applications** to create an application and save its API key. The key is shown only on creation or reset; resetting it immediately invalidates the old key. The built-in **Integration Guide** at `/Alerts/GettingStarted` provides examples for the current server.

Send an alert using `POST /api/v1/messages`:

```bash
curl -i 'http://localhost:5000/api/v1/messages' \
  -H 'Authorization: Bearer YOUR_API_KEY' \
  -H 'Content-Type: application/json' \
  --data '{
    "reportId": "0c561d22-5b71-4a4f-9c13-c824e273a8aa",
    "incidentKey": "payment-provider-timeout",
    "title": "Order payment timed out",
    "content": "The downstream provider did not respond within 5 seconds.",
    "level": "sev1",
    "occurredAtUtc": "2026-10-16T12:00:00Z",
    "context": {"orderId": "ORDER-1001", "traceId": "demo-trace"}
  }'
```

Use your server's HTTPS URL for remote reporting and keep API keys outside source control. Reuse a `reportId` only when retrying the same report. A new report returns `201 Created`; a duplicate returns `200 OK` with `duplicate: true`. Invalid credentials return `401`; invalid fields return `400`. Requests are limited to 64 KB, titles and incident keys to 200 characters, and content to 4,000 characters.

**Reporting clients must rate-limit at the source.** AlertCenter does not rate-limit ingestion. An `incidentKey` correlates reports; it does not replace caller-side rate limiting. Without a key, correlation uses the application, title, and severity. Acknowledged or mitigated incidents retain matching reports without creating new deliveries; resolving an incident allows the next matching report to create a new incident.

## Permissions and on-call response

Platform capabilities use `Permission` claims. Roles collect permissions; the name `Administrators` does not grant privileges by itself.

- `CanManageApplications` permits creating applications, managing their access grants, and managing all applications. Otherwise, per-application user or role grants provide view or manage access.
- `CanManageOnCall` permits managing teams, scheduling rules, and schedules. Saved shifts determine the current on-call engineers (OCEs); a rule preview alone does not provide coverage.
- `CanManagePush` permits managing channels and sensitive delivery configuration. A user's channel binding access and recipient membership are separate; binding a channel also requires application manage access.

The current OCEs, incident owner, and application managers may handle an incident. A view grant alone does not permit incident handling. Default identity initialization runs only when both users and roles are empty; restarting an existing installation does not restore revoked permissions or grant newly introduced permissions.

Applications may bind an on-call team. Each new, non-archived alert and explicit incident reopen queues built-in email to that team's current OCEs, independently of custom severity routes. Delivery recipients are snapshotted when queued. Missing coverage or invalid recipient configuration produces a visible delivery failure without rejecting the alert. There are no automatic email retries; inspect delivery records before manually retrying an SMTP failure.

## Configuration

See the comments in [appsettings.json](src/Aiursoft.AlertCenter/appsettings.json) for database, storage, authentication, and logging settings. Keep secrets in protected environment variables.

- **OIDC:** set `AppSettings__AuthProvider=OIDC` and the `AppSettings__OIDC__*` values. Register the application's HTTPS `/signin-oidc` callback with your identity provider and assign the required application permissions.
- **Email:** configure `Email__Host`, `Email__Port`, `Email__From`, and, when required, `Email__UserName` and `Email__Password`. STARTTLS is enabled by default. Both custom channels and built-in on-call email use these settings.
- **Webhook:** custom encrypted request headers require `Webhook__HeaderProtectionKey`, a Base64-encoded 32-byte key generated with `openssl rand -base64 32`. Preserve this key: losing or changing it makes saved custom headers unreadable. Without it, default headers and custom JSON body templates remain available.

### Database migrations

The SQLite and MySQL migration baselines are intended for **empty databases** and replace earlier development migration history. Back up an older development database and plan its recreation before using this baseline; clearing only `__EFMigrationsHistory` while retaining old tables is not an upgrade path.

The application applies pending migrations at startup. Generate future migrations for both providers using the [SQLite guide](src/Aiursoft.AlertCenter.Sqlite/HOW_TO_ADD_MIGRATIONS.md) and [MySQL guide](src/Aiursoft.AlertCenter.MySql/HOW_TO_ADD_MIGRATIONS.md).

## How to contribute

Report bugs and suggestions through the issue tracker. Create a fork and a feature branch, then submit a merge request for review.

After preparing frontend assets, run the existing checks:

```bash
dotnet test tests/Aiursoft.AlertCenter.Tests.csproj
node --test tests/Frontend/*.test.cjs
./lint.sh
```

# AlertCenter

[![MIT licensed](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/aiursoftweb/alertCenter/blob/master/LICENSE)
[![Pipeline stat](https://gitlab.aiursoft.com/aiursoft/alertCenter/badges/master/pipeline.svg)](https://gitlab.aiursoft.com/aiursoft/alertCenter/-/pipelines)
[![Test Coverage](https://gitlab.aiursoft.com/aiursoft/alertCenter/badges/master/coverage.svg)](https://gitlab.aiursoft.com/aiursoft/alertCenter/-/pipelines)
[![Man hours](https://manhours.aiursoft.com/r/github.com/aiursoftweb/alertCenter.svg)](https://manhours.aiursoft.com/r/github.com/aiursoftweb/alertCenter.html)
[![Website](https://img.shields.io/website?url=https%3A%2F%2FalertCenter.aiursoft.com)](https://alertCenter.aiursoft.com)
[![Docker](https://img.shields.io/docker/pulls/aiursoft/alertCenter.svg)](https://hub.docker.com/r/aiursoft/alertCenter)

AlertCenter is a centralized alert-management and notification platform. Business systems report structured alerts through one HTTP API; AlertCenter stores them, routes them by severity, and delivers them through Webhook, email, or in-app notification channels.

![screenshot](./screenshot.png)

Default user name is `admin@default.com` and default password is `Admin@123456!`.

## Try

Try a running AlertCenter instance [here](https://alertCenter.aiursoft.com).

## What is AlertCenter?

AlertCenter provides:

- Independent API keys for every reporting application.
- Structured, idempotent alert reporting through a standard HTTP API.
- Five severity levels (`sev0` 致命 through `sev4`) with independently configurable routing.
- Webhook, SMTP email, and targeted in-app notification channels.
- Automatically created incidents with acknowledgement, resolution, and a timestamped handling timeline.
- Message history, notification inboxes, and delivery records for tracking alert handling.
- Self-hosted deployment with SQLite by default and MySQL support for production deployments.

## Reporting alerts

Create an application in **接入应用** and save the displayed API Key. It is shown only when created or reset. Send alerts to `POST /api/v1/messages` using that key as a bearer token:

```bash
curl -i 'https://alertcenter.example.com/api/v1/messages' \
  -H 'Authorization: Bearer YOUR_API_KEY' \
  -H 'Content-Type: application/json' \
  --data '{
    "reportId":"0c561d22-5b71-4a4f-9c13-c824e273a8aa",
    "incidentKey":"payment-provider-timeout",
    "title":"Order payment timed out",
    "content":"The downstream provider did not respond within 5 seconds.",
    "level":"sev1",
    "occurredAtUtc":"2026-09-14T14:28:13Z",
    "context":{"orderId":"ORDER-1001","traceId":"demo-trace"}
  }'
```

`reportId`, `title`, `level`, and `occurredAtUtc` are required. The optional `incidentKey` is limited to 200 characters and can group matching reports into the same automatically created incident. Without it, AlertCenter groups reports by application, title, and severity. The title is limited to 200 characters, content to 4,000 characters, and the entire request body to 64 KB. Repeating the same `reportId` for an application is idempotent: the original message is retained and the response has `duplicate: true`. A successful new report returns `201 Created`; authentication failures return `401 Unauthorized`; invalid fields return `400 Bad Request`.

AlertCenter does not rate-limit reports or suppress notifications before acknowledgement. Reporting applications **must rate-limit at the source** and report only actionable alerts; use stable `reportId` values when retrying the same report. `incidentKey` is a correlation aid, not a substitute for caller-side rate limiting.

## On-call schedules

In **值班中心 → 团队管理**, administrators manage each team's time zone, UTC schedule anchor, ordered members, and optional rule binding. The time zone dropdown supports searching by name, city, or UTC offset. Teams can have zero to 200 members, and members can be added or removed at any time. Removing a member clears that person's assignments in shifts that have not yet started; started and historical assignments remain visible as **已退出**. Saved shift slots are retained, including empty slots that administrators must fill manually. Adding members does not automatically change saved schedules.

**排班规则** manages reusable templates: eight-hour shifts, minimum rest (0–168 hours, default 16), and rotation preferences. A team can bind one template, and multiple teams can share it. Changing a template or a team's binding does not regenerate saved schedules. **值班排班** manages actual shifts owned by the team, independently of templates. Automatic generation requires a bound template and enough members (at least three, and potentially more for longer rest). Manual scheduling does not require a template or a minimum team size, but every shift must be staffed and satisfy overlap/rest constraints; without a template, new manual schedules use sixteen hours of minimum rest. There are three consecutive eight-hour shifts per 24 hours, with at least one engineer per shift and optional redundant staff. Times display in the team time zone; durations and rest are measured in UTC, including daylight-saving transitions.

Employees may belong to multiple on-call teams. Each team's schedule shows their other memberships and renders their saved shifts from other teams in separate gray read-only lanes, labeled with the team and times in the displayed team's time zone. These shifts also appear in the expandable adjacent-day regions; they cannot be moved, swapped, added to, or removed from the current team's editor and never count toward its totals. Multi-team employees can still be assigned manually within their teams, subject to cross-team overlap and minimum-rest validation. Automatic previews and generation exclude every employee currently belonging to another team, even if that team has no rule or saved schedule. Joining another team never removes or regenerates existing saved assignments; it only changes eligibility for subsequent automatic generation. If too few single-team employees remain, generation is unavailable and administrators must add eligible members or schedule manually.

Templates also configure **每班最低人数** and **每班目标人数** (1–200; target cannot be below minimum). New templates default to one person for both. Leaving the target empty uses automatic distribution across available staff. Automatic generation must meet the minimum and tries to reach the target without compromising coverage or rest. Its minimum team size is `minimumStaff × max(3, 1 + ceil(restHours / 8))`. Manual schedules below the minimum show warnings but can be saved; empty shifts, overlaps, and rest violations still block saving. Saved weeks retain their minimum-staff snapshot independently of subsequent template changes.

**轮换偏好** offers **固定时段优先** or **尽量均衡早、中、晚班**. The latter uses recent saved assignments to distribute work and vary shift times. Weekly transition windows may reduce redundant staffing to allow sufficient rest. This is a best-effort preference, not a save constraint. Exactly three engineers with sixteen hours of rest cannot rotate time slots while maintaining continuous coverage.

Schedules are saved in seven-day windows aligned to the team's anchor. Administrators can drag a Gantt bar onto another bar to swap the two assignees, or onto an empty cell to move the assignment to that engineer and time. Drag **交换整周** onto another engineer to exchange their entire week's assignments. Clicking the source and then the target also works; Escape cancels selection. Changes appear immediately in the chart and totals, with undo and reset controls. Empty shifts and insufficient rest within the displayed week are highlighted without blocking further edits. Save the complete draft once all changes are ready; saving also validates adjacent weeks and other teams. The expandable member form supports keyboard editing and adding/removing redundant staff. Manual creation is also available when automatic generation cannot find a feasible schedule. Saving rejects empty shifts, unknown/duplicate engineers, overlaps, and insufficient rest. Automatic generation never overwrites saved shifts. Concurrent team, template, or schedule edits invalidate the team's schedule version; stale saves return a conflict instead of overwriting changes. Once shifts are saved, the team's anchor is locked. Saved shifts retain their original minimum-rest snapshot, even after template edits or rebinding. Exited members may remain only in their original saved slots and cannot receive new assignments. A bound template cannot be deleted until teams unbind it; deleting an unbound template never deletes schedules. Teams with saved schedules cannot be deleted, protecting their history.

Use **添加班次** to click an empty engineer/time cell and add an assignment, or **删除班次** to click a bar and remove that engineer's assignment. Both update the draft immediately and support undo; removing the last assignee leaves a visible gap that must be filled before saving. Switch back to **移动 / 交换** (or press Escape) to resume dragging.

**复制本周到下一周** copies the current draft's assignees into all 21 slots of the next week, without saving immediately. Replacing a saved or modified next-week draft requires confirmation; cancellation leaves it unchanged. Exited members cannot be copied. The timeline's two edge buttons independently expand the previous week's last 24 hours and next week's first 24 hours (elapsed UTC hours, even across daylight-saving changes). These gray regions start collapsed and administrators can edit, move or swap assignments across the week boundary; collapsing does not discard changes. **交换整周** and chart totals still apply only to the selected week. The member form can edit all 21 slots of either adjacent week. Undo and reset include all three drafts. **保存排班调整** atomically saves the selected week plus modified adjacent weeks, validating their individual staffing/rest snapshots together and against other saved schedules. An unsaved adjacent week must have all 21 slots staffed before it can be saved; copying is a shortcut to filling the next week. Any validation or version conflict retains the drafts and writes none of the weeks.

Administrator JSON endpoints use the existing login cookie. POST requests require the `RequestVerificationToken` header from the schedule details page's antiforgery field:

| Endpoint | Behavior |
| --- | --- |
| `GET /OnCall/GetSchedule?id={groupId}&startUtc={UTC}` | Returns `groupId`, `version`, `startUtc`, `saved`, `restHours`, `minimumStaff`, `targetStaff`, `warnings`, `canGenerate`, members (including a `departed` flag and `otherGroups` with `id`/`name`), `otherGroupShifts` (`groupId`, `groupName`, `startsAtUtc`, `endsAtUtc`, and displayed members' `userIds`, covering the previous/current/next weeks), and 21 current-team slots with `startsAtUtc`, `endsAtUtc`, and `userIds`. Omit `startUtc` for the current week. `generationError` explains generation failures or empty saved slots; all slots remain available for manual assignment. |
| `POST /OnCall/GenerateSchedule` | Accepts `{ "groupId": "...", "version": "...", "startUtc": "...Z" }`; generates and saves an unsaved team week. |
| `POST /OnCall/SaveSchedule` | Accepts the same identifiers plus `slots`: all 21 `{ "startsAtUtc": "...Z", "userIds": ["..."] }` entries. Optional `adjacentWeeks` contains at most two unique `{ "startUtc": "...Z", "slots": [...] }` objects, exactly seven days before/after the selected week, each with all 21 slots. Atomically creates or updates every submitted week using one group version; any failure writes none. Generation does not accept adjacent-week edits. |

Successful writes return 200 with a `warnings` array (including below-minimum staffing notices). Validation failures return 400 with an `errors` array; stale versions/write conflicts return 409. Refresh the schedule after saving to obtain the new version. Saved shifts determine OCE recipients for applications bound to the team. Leave is handled by manually removing/replacing assignees, without a separate leave-approval workflow.

### Optional application binding

Applications may optionally bind one on-call team, and a team can serve multiple applications. Select a team when creating an application, or use **接入应用 → 值班设置** to bind, change, or remove it; administrators and users with application manage access can update the binding. Applications that only need alerts can remain unbound. All engineers assigned to the team's current saved shift are OCEs. Binding automatically enables built-in on-call email for `sev0` through `sev4`, independently of custom channels; it does not change alert ingestion, incident creation, or application access permissions. A team bound to any application cannot be deleted until those applications are rebound or unbound. Applications without a team binding do not queue built-in on-call email.

## Application access control

Administrators always have full access to every application. In **接入应用 → 访问权限**, an administrator can grant either users or roles **view** or **manage** access for a single application. Role grants are inherited by every role member; if a user has multiple grants, manage access takes precedence over view access. View access permits only that application’s alerts, incidents, and related delivery records. Manage access also permits application settings, API-key reset, enable/disable, push bindings, and Incident actions. Creating applications and editing application access grants remain administrator-only.

## Incident response and alert suppression

Every new alert automatically creates or joins an active incident in `new`. The supported workflow is **new → acked → mitigated → resolved**:

- **Acknowledge**: only from `new`; the acting engineer becomes the owner.
- **Take ownership**: only from `acked`; transfers ownership to the acting engineer without changing state.
- **Mitigate**: only from `acked`, with a required reason.
- **Resolve**: only from `mitigated`, with a required reason; releases the active correlation key.
- **Reopen**: only from `mitigated` or `resolved`; clears the owner and handling timestamps, returns to `new`, and queues a new built-in email to the current OCEs with a fresh immutable recipient snapshot. Reopening does not fabricate a reported alert or resend custom channels. If another active incident has claimed the same correlation key, reopening returns 409 rather than merging incidents.
- **Change level**: `sev0`–`sev4` on any unresolved incident, with a required reason. Changes the incident severity only; original reports and the original fallback matching severity remain unchanged.

Only the current OCEs, current owner, application managers or system administrators may operate an incident. OCEs and owners can view the relevant incident and its associated alerts without gaining application settings or unrelated alert access. A role/direct view grant alone does not permit handling. Existing plain-text handling notes remain restricted to handlers while unresolved. Independent Markdown discussion comments are available to all incident viewers in every state; see below.

All incident workflow mutations validate a mandatory incident version. Stale or concurrent edits return 409; invalid states or missing/oversized reasons return 400. In the browser, these expected failures appear in a UiStack dialog on the original page, retaining reasons and selections; conflicts offer an explicit refresh rather than discarding drafts automatically. Application/channel enable-disable controls, channel deletion, team management, user/role creation and deletion, and inbox read actions use the same in-page feedback. Team deletion uses a UiStack confirmation dialog; refreshing after a conflict requires a second explicit confirmation, and cancellation keeps the draft. Copy controls in the integration guide, notification context and delivery details show accessible success/failure feedback only after the clipboard result, without replacing button icons. Successful actions still navigate to their existing destinations. Regular form submissions without JavaScript and API/JSON endpoints retain their original HTTP behavior. Each successful operation atomically saves the new state, owner and severity together with its actor, UTC time, reason and before/after audit values. Reopen also saves its OCE notification in that same transaction. Missing coverage or email creates a visible delivery failure without undoing the reopen; a database write failure rolls back the action and audit together.

Both `acked` and `mitigated` incidents retain matching reports but automatically archive subsequent reports without creating new notification deliveries. Delivery tasks created before acknowledgement remain unchanged. Reopening resumes notifications on future matching reports.

Reports match an active incident by the same application and `incidentKey`. If no key is supplied, matching falls back to application, alert title, and severity. Matching distinguishes case and accents on both SQLite and MySQL; leading/trailing whitespace in an explicit `incidentKey` is trimmed at ingestion, while title whitespace is retained. MySQL matching columns explicitly use `utf8mb4_0900_bin` rather than the server's default collation. A resolved incident no longer matches new reports; the next matching report opens a new incident and notifications resume. The incident page shows current OCEs, the owner, state-specific actions and the audited timeline. Each workflow action retains the actor's user ID and display-name snapshot independently of the nullable user reference, so renaming or deleting an account does not rewrite historical attribution.

### Incident discussion comments

**事故详情 → 事故讨论** provides an independent, newest-first comment timeline (20 entries per page), separate from handling/audit notes. Anyone who can currently view the incident—including application viewers, current OCEs and the current owner—can comment in `new`, `acked`, `mitigated` or `resolved`. Posting or deleting a comment never changes incident state, ownership, version, alert correlation or notifications.

Comments store the original Markdown (up to 4,000 UTF-16 code units), author identity/name snapshot and UTC creation time. Each entry, including deleted-comment tombstones, shows the author's current profile avatar; deleted accounts use the default avatar while retaining the historical author name. The shared UiStack Markdown editor supports editing, split view, preview, Markdown shortcuts and code highlighting using locally hosted Monaco. If Monaco is unavailable, the textarea continues to work with the same preview; with JavaScript disabled, the escaped source remains readable and the textarea still posts normally. Native HTML is escaped and unsafe link schemes are rejected by the shared renderer; stored content is never treated as trusted HTML. Images can be referenced by Markdown URL, but attachment uploads are not part of incident comments.

Comments cannot be edited. The author or an application manager/system administrator can **soft-delete** a visible comment after confirmation. Being the current OCE or incident owner alone does not authorize deleting someone else's comment. Deletion still requires current incident viewing access; losing access also prevents deleting old comments. Deleted entries show a tombstone with author, creation time, deleting actor/name snapshot and deletion time; the original Markdown is retained only in the database audit record and is not returned in the page. Concurrent deletion returns a conflict without replacing the first deletion audit. Deleting an application/incident removes its comment history; deleting user accounts retains the historical display-name snapshots. Rolling back the initial database migration removes all application tables and their data; back up before any rollback.

Frontend assets are installed with `npm ci --prefix src/Aiursoft.AlertCenter/wwwroot` and retained by the existing `npm run build --prefix src/Aiursoft.AlertCenter/wwwroot` packaging step; no external CDN is required. Run `node --test tests/Frontend/markdown-comments.test.cjs` after preparing assets to verify the exact browser renderer's HTML and link safety.

## Notification channels

### Built-in on-call email

An application bound to a team always queues an on-call email for each new, non-archived alert and for each successful incident reopen. This is a system route, not an editable push-channel record: it cannot be disabled or filtered by severity in **推送设置**. Unbinding the team stops future on-call deliveries but does not cancel queued deliveries. The acknowledged/mitigated-incident suppression applies to both built-in and custom routes for matching reports; explicit reopen notifications are not suppressed.

**To** contains all current OCEs' registered user email addresses, deduplicated case-insensitively. Administrators configure optional **CC** in **值班中心 → 团队管理 → 编辑团队** using a group mailbox or explicit addresses (up to 50 unique addresses; newline, comma and semicolon separators). CC never automatically expands to all team members, and addresses already in To are removed from CC. No personal channel binding is needed. Both routes use the global SMTP configuration below.

OCE lookup uses receipt time in UTC and saved half-open shift intervals (`start <= time < end`), not the caller's alert occurrence time or a template preview. Queueing atomically persists the alert, incident and delivery together with the team, shift, OCE user IDs, emails, notification time and CC snapshot. Changing schedules, user emails, team names or bindings cannot rewrite pending or historical recipients.

No coverage, an empty/overlapping shift, invalid CC or any OCE without a valid registered email creates a failed delivery with a clear reason without rejecting the alert or blocking configured custom notifications. Recipient-validation failures do not send a partial email and cannot be retried by selecting a new OCE; correct future configuration and submit a new actionable report. SMTP failures and interrupted deliveries can be manually retried after checking whether the server already received them; retry uses the original To/CC snapshot. There are no automatic retries.

**推送中心 → 推送记录** includes a built-in-email filter and per-route results. Application viewers may see their application's built-in delivery metadata; email addresses are visible only to administrators or `CanManagePush` users. Manual retries still require `CanManagePush` and application manage access. Custom email, in-app and Webhook channels are additive: only explicitly configured channels receive notifications, and failure of one route does not prevent processing the others. No test button sends to a production OCE; custom-channel tests continue to target their explicitly configured recipients.

### In-app notifications and inbox search

In **推送中心 → 推送渠道**, an in-app channel can target multiple users and RBAC roles. Recipients are deduplicated when a notification is delivered, and every user manages their own read state. At least one user or role must be selected; a channel with no resolved recipients fails rather than notifying administrators implicitly. Updating recipients does not retract historical notifications. Existing in-app channels need a recipient scope after upgrading.

All signed-in users can access their own inbox and notification details. `CanManagePush` controls creating, editing, testing, enabling, deleting, and viewing sensitive configuration for channels; the Administrators role receives it at startup.

A channel has two independent access lists. Its **recipient list** controls who receives in-app notifications (or the fixed SMTP addresses for an email channel). Its **binding access list** controls which users and roles can see and bind that channel while managing an application. Recipients do not automatically receive binding access, and a binding-access grant does not expose recipient addresses, Webhook endpoints or headers, channel tests, or channel editing. A binding user must also have application manage access. Administrators and users with `CanManagePush` can bind any channel. Existing channels are intentionally private after upgrading until a channel manager grants binding access.

**站内通知** opens with unread notifications by default. Use **仅看未读 / 全部通知 / 仅看已读** to change scope, retaining other filters and returning to page one. Combine title/body keywords, read status, source application, severity, notification channel, and inclusive UTC receipt-date filters. Filtering happens before the 20-item pagination; application and channel options come from the signed-in user's notification history. Test notifications have no source application or severity.

Pagination, refresh, and read actions preserve the selected filters. **我的全部通知标为已读** marks every unread notification for the current user, including notifications outside the current filters and page.

### Resetting an application API key

Administrators can select **接入应用 → 重置密钥** and confirm the reset. The old key becomes
invalid immediately, with no grace period; update every reporting client to use the new key.
The new key is shown only once, with a copy button. Save it before refreshing or leaving the page;
if it is lost, reset it again. Only its password hash is stored in the database.

Resetting preserves the application ID, name, enabled/disabled state, historical messages,
channel bindings, delivery records, and inbox notifications. A disabled application remains unable
to report until it is explicitly enabled. Resetting does not resend historical messages.

### Severity-based notification channels

In **接入应用 → 推送设置**, select channels independently for each severity:
`sev0` (致命), `sev1` (严重), `sev2` (错误), `sev3` (警告), and `sev4` (信息). The page shows only channels for which the current application manager has binding access.
Each level can target multiple custom channels. Leaving a level unselected still saves its alerts and creates no custom deliveries; a bound team's built-in email remains enabled.
Matching is exact; selecting `sev2` does not implicitly include `sev1`.

Disabled channels retain their selections but do not receive new deliveries.
Changes apply only to new alerts, not historical messages or existing delivery tasks. In-app recipients are snapshotted when a delivery is queued, so later channel or role-membership changes cannot alter that delivery.
New application/channel bindings default to enabling all five levels.
Existing clients remain compatible for `sev1` through `sev4`; update to a Sev0-capable SDK version before reporting `sev0`.

### Email notification channels

Custom email channels use a global SMTP configuration, one fixed To address and up to 50 CC addresses separated by newlines, commas, or semicolons. They remain separate from built-in on-call email. Messages are sent as UTF-8 plain text; the subject includes the alert severity and title. A channel test sends a test email to its configured recipients.

Configure SMTP through protected environment variables rather than `appsettings.json`. STARTTLS is required by default; set `Email__RequireTls=false` only for a trusted SMTP server that does not support it:

```bash
docker run \
  -e Email__Host='smtp.example.com' \
  -e Email__Port='587' \
  -e Email__UserName='alertcenter@example.com' \
  -e Email__Password='YOUR_SMTP_PASSWORD' \
  -e Email__From='alertcenter@example.com' \
  -e Email__RequireTls='true' \
  aiursoft/alertCenter
```

`Email__UserName` and `Email__Password` must either both be set or both be empty, for SMTP servers that permit unauthenticated relay. SMTP timeouts are 10 seconds. Delivery errors do not expose SMTP hostnames, account names, recipients, or server responses.

### Webhook request format

Webhook channels send an HTTP `POST` with `Content-Type: application/json` and an `Idempotency-Key`
header containing the delivery ID. A `2xx` response succeeds; requests time out after 10 seconds and
redirects are not followed. The channel configuration screen shows the default JSON format and supports
optional custom JSON headers (except transport and system-managed headers) and a JSON body template.
Custom header values are encrypted at rest. To enable custom request headers, configure
`Webhook__HeaderProtectionKey` as a **Base64-encoded value that decodes to exactly 32 bytes** (an AES-256 key).
Generate a key with `openssl rand -base64 32`, then provide it through a protected environment variable; for example:

```bash
docker run -e Webhook__HeaderProtectionKey='YOUR_GENERATED_BASE64_KEY' aiursoft/alertCenter
```

Do not place this key in `appsettings.json`, source control, or the persistent data volume. Keep the same key for
the lifetime of existing channel configurations: losing or changing it makes saved custom headers unreadable, so
those headers must be entered and saved again. Without the key, Webhook channels can still use the default request
and custom request-body templates, but cannot save custom request headers. Templates use complete-string placeholders
such as `{{title}}` and `{{context}}`; the screen lists all available placeholders. Leaving the template empty retains
the documented default payload.

## Projects using Aiursoft AlertCenter

* [Stathub](https://github.com/aiursoftweb/stathub)
* [MarkToHtml](https://github.com/aiursoftweb/marktohtml)
* [MusicTools](https://github.com/aiursoftweb/musictools)
* [AnduinOS Home](https://github.com/aiursoftweb/AnduinOS-Home)
* [Manhours](https://github.com/aiursoftweb/manhours)
* [Tracer](https://github.com/aiursoftweb/tracer)
* [Warp](https://github.com/aiursoftweb/warp)
* [AiurDrive](https://github.com/aiursoftweb/aiurdrive)
* [EmployeeCenter](https://github.com/aiursoftweb/employeecenter)
* [Git Mirror Server](https://github.com/aiursoftweb/gitmirrorserver)
* [CppRunner](https://github.com/aiursoftweb/cpprunner)
* [Ollama Gateway](https://github.com/aiursoftweb/ollamagateway)
* [Polls](https://github.com/aiursoftweb/polls)
* [WeChatExam](https://github.com/aiursoftweb/wechatexam)
* [MusicExam](https://github.com/aiursoftweb/musicexam)
* [CorpHome](https://github.com/aiursoftweb/corphome)
* [Events Recorder](https://github.com/aiursoftweb/eventsrecorder)
* [Translate](https://github.com/aiursoftweb/translate)
* [Apkg](https://github.com/aiursoftweb/apkg)
* [HowToCook Viewer](https://github.com/aiursoftweb/howtocookviewer)
* [Kanban](https://github.com/aiursoftweb/kanban)
* [MoongladeV2](https://github.com/aiursoftweb/moongladev2)
* [DocsViewer](https://github.com/aiursoftweb/docsviewer)

## Run in Ubuntu

The following script will install or update this app on your Ubuntu server. Supports Ubuntu 25.04.

On your Ubuntu server, run the following command:

```bash
curl -sL https://github.com/aiursoftweb/alertCenter/raw/master/install.sh | sudo bash
```

You can append a custom port number to the command:

```bash
curl -sL https://github.com/aiursoftweb/alertCenter/raw/master/install.sh | sudo bash -s 8080
```

It will install the app as a systemd service, and start it automatically. Binary files will be located at `/opt/apps`. Service files will be located at `/etc/systemd/system`.

### apt package filesystem layout

When installed via `apt install aiursoft-alertCenter`, the following paths are created:

| Role | Path | `apt remove` | `apt purge` |
|------|------|:---:|:---:|
| Working directory & binaries | `/usr/share/aiursoft-alertCenter/` | ✓ | ✓ |
| Config file | `/etc/aiursoft-alertCenter/appsettings.json` | | ✓ |
| Runtime data (DB, storage, keys) | `/var/lib/aiursoft-alertCenter/` | | |
| systemd unit | `/lib/systemd/system/aiursoft-alertCenter.service` | ✓ | ✓ |

The config file is a dpkg conffile — kept on `remove`, deleted only on `purge`.
Runtime data under `/var/lib/aiursoft-alertCenter/` is user data and intentionally never removed by dpkg.

## Run manually

Requirements about how to run

1. Install [.NET 10 SDK](http://dot.net/) and [Node.js](https://nodejs.org/).
2. Execute `npm install` at `src/Aiursoft.AlertCenter/wwwroot` to install the dependencies.
3. Execute `dotnet run --project src/Aiursoft.AlertCenter/Aiursoft.AlertCenter.csproj` to run the app.
4. Use your browser to view [http://localhost:5000](http://localhost:5000).

## Run in Microsoft Visual Studio

1. Open the `.sln` file in the project path.
2. Press `F5` to run the app.

## Run in Docker

First, install Docker [here](https://docs.docker.com/get-docker/).

Then run the following commands in a Linux shell:

```bash
image=aiursoft/alertCenter
appName=alertCenter
sudo docker pull $image
sudo docker run -d --name $appName --restart unless-stopped -p 5000:5000 -v /var/www/$appName:/data $image
```

That will start a web server at `http://localhost:5000` and you can test the app.

The docker image has the following context:

| Properties  | Value                         |
|-------------|-------------------------------|
| Image       | aiursoft/alertCenter          |
| Ports       | 5000                          |
| Binary path | /app                          |
| Data path   | /data                         |
| Config path | /data/appsettings.json        |

## How to contribute

There are many ways to contribute to the project: logging bugs, submitting pull requests, reporting issues, and creating suggestions.

Even if you have push rights on the repository, you should create a personal fork and create feature branches there when you need them. This keeps the main repository clean and your workflow cruft out of sight.

We're also interested in your feedback on the future of this project. You can submit a suggestion or feature request through the issue tracker. To make this process more effective, we're asking that these include more information to help define them more clearly.

## Database migrations

SQLite and MySQL each have a single EF Core-generated `InitialCreate` migration that creates the current schema directly, including incident comments, on-call email snapshots and workflow actor snapshots. The baseline was regenerated on 2026-10-15 and replaces all earlier migration history; it is intended for **empty databases only**. Existing development databases cannot be upgraded using this baseline: back up any data you need, stop the application, and explicitly recreate the database before restarting. Do not just clear `__EFMigrationsHistory` while retaining the old tables.

Generate future migrations for both providers using the EF Core CLI; do not hand-edit migration files or model snapshots. See the [SQLite migration guide](src/Aiursoft.AlertCenter.Sqlite/HOW_TO_ADD_MIGRATIONS.md) and [MySQL migration guide](src/Aiursoft.AlertCenter.MySql/HOW_TO_ADD_MIGRATIONS.md). The application applies pending migrations at startup.

## Configuration Guide

The defaults run as a standalone application with SQLite, local accounts, local file storage, and no ClickHouse server. Change a setting only after preparing the dependency named in its comment.

For SMTP and Webhook secret configuration, see [Email notification channels](#email-notification-channels) and [Webhook request format](#webhook-request-format). Configure secrets with protected environment variables rather than this file.

The Docker image copies this file to `/data/appsettings.json` on its first start and keeps that copy on later starts. Mount `/data` as a persistent volume. To override a value without editing the file, use an environment variable with double underscores, such as `Storage__Path` or `AppSettings__OIDC__ClientSecret`.

```jsonc
{
  "ConnectionStrings": {
    // True: cache EF Core query results in this process. False: read from the database on every query.
    "AllowCache": "True",

    // Sqlite: use a local database file and no database server. MySql: use an external MySQL server.
    // When changing this value, also replace DefaultConnection with a connection string for that provider.
    "DbType": "Sqlite",
    // With Sqlite, this creates app.db in the process working directory and applies migrations at startup.
    "DefaultConnection": "DataSource=app.db;Cache=Shared"

    // MySql requires a reachable server, database, user, and password. The application applies migrations at startup.
    // sudo docker run -d --name db -e MYSQL_RANDOM_ROOT_PASSWORD=true -e MYSQL_DATABASE=alertCenter -e MYSQL_USER=alertCenter -e MYSQL_PASSWORD=alertCenter_password -p 3306:3306 hub.aiursoft.com/mysql
    //"DbType": "MySql",
    //"DefaultConnection": "Server=localhost;Database=alertCenter;Uid=alertCenter;Pwd=alertCenter_password;"
  },
  "Storage": {
    // Writable local directory for public Workspace files and token-protected private Vault files.
    // This is not an S3 or MinIO bucket.
    // The Docker image changes /tmp/data to /data; mount /data as a persistent volume in production.
    "Path": "/tmp/data",

    // Optional second hostname for this application's own /download endpoints; this is not an S3 or MinIO URL.
    // Example: set https://files.example.com, point that DNS name to this server, and add this Caddy route:
    // files.example.com {
    //     reverse_proxy 127.0.0.1:5000
    // }
    // Replace 127.0.0.1:5000 with the same upstream used by the web UI. Leave empty to use the UI hostname.
    "PublicOrigin": "",

    // False: verified images and configured media can open on the normal web hostname.
    // True: inline viewing works only through PublicOrigin; the same request on the UI hostname downloads the file.
    // True requires PublicOrigin plus DNS, TLS, and a reverse-proxy route to this same application instance.
    "RequireDedicatedInlineOrigin": false,

    // False: only verified images and SafeInlineMediaExtensions open in the browser; every other file downloads.
    // True: every file type may open through PublicOrigin, and HTML or SVG scripts may run there.
    // True requires RequireDedicatedInlineOrigin=true and a PublicOrigin that receives no application cookies.
    "AllowArbitraryInlineOnDedicatedOrigin": false,

    // File extensions allowed to open after content-signature validation. Empty means no extra audio, video, or SVG.
    // Supported values are mp3, mp4, ogg, svg, wav, and webm. This does not grant upload permission.
    "SafeInlineMediaExtensions": [],

    // SVG opens only when svg is enabled above and its Workspace path starts with an entry in this list.
    // Example: [ "markdown-images" ]. Empty means every SVG downloads.
    "SafeInlineSvgSubfolders": [],

    // These values are copied into newly issued signed upload tokens; existing tokens keep their old policy.
    "UploadPolicy": {
      // False: a valid signed upload token is enough. True: the uploader must also be signed in to this application.
      "RequireAuthenticatedUser": false,

      // False: preserve spaces in uploaded file names. True: save each space as a hyphen.
      "ReplaceSpacesWithHyphens": false
    }
  },
  "AppSettings": {
    // Local: use built-in accounts and passwords. OIDC: redirect sign-in to an external identity provider.
    // OIDC also requires the OIDC section below and a provider callback URL of https://<web-host>/signin-oidc.
    "AuthProvider": "Local",

    // Empty: assign no automatic role. Otherwise, assign this role to every new local or OIDC user.
    // With Local, create the role first. With OIDC, the application creates it during account synchronization.
    "DefaultRole": "",

    // False: sign-in ends when the browser session closes. True: issue a persistent sign-in cookie.
    "PersistsSignIn": false,

    // This section is used only when AuthProvider is OIDC.
    "OIDC": {
      // Provider discovery base URL. It must be reachable by the application and should use HTTPS in production.
      "Authority": "https://auth.aiursoft.com/application/o/alertCenter",
      // Client identifier created for this application in the OIDC provider.
      "ClientId": "",
      // Client secret created by the provider. Set AppSettings__OIDC__ClientSecret instead of committing a real secret.
      "ClientSecret": "",

      // Claim containing role names. On every login, local roles are replaced by these roles plus DefaultRole.
      "RolePropertyName": "groups",
      // Required claim used as the local username when an OIDC account is first created.
      "UsernamePropertyName": "preferred_username",
      // Required claim copied to the local display name on every login.
      "UserDisplayNamePropertyName": "name",
      // Required unique email claim. The provider must keep it verified, unique, and stable.
      "EmailPropertyName": "email",
      // Required immutable account identifier. Keep this mapped to the standard OIDC sub claim.
      "UserIdentityPropertyName": "sub"
    },

    // This section is used only when AuthProvider is Local.
    "Local": {
      // True: show self-registration and allow new local accounts. False: existing users can sign in, but cannot register.
      "AllowRegister": true,
      // True: require only six characters. False: require eight characters with upper, lower, digit, and symbol.
      "AllowWeakPassword": true
    }
  },
  "GlobalSettings": {
    // Leave an entry commented to use the database value or built-in default.
    // Uncommenting an entry forces that value and overrides changes made in the administration UI.

    // True: users may change their display nickname. False: only administrators control it.
    // "Allow_User_Adjust_Nickname": "True",
    // Text shown as the brand name in the footer.
    // "BrandName": "Aiursoft",
    // URL opened when a user clicks the footer brand name.
    // "BrandHomeUrl": "https://www.aiursoft.com/",
    // Mainland China ICP license number. Empty hides it.
    // "Icp": "",
    // Application name shown in the web interface.
    // "ProjectName": "Aiursoft AlertCenter"
  },
  "Logging": {
    "LogLevel": {
      // Minimum level for application logs. Typical values: Debug, Information, Warning, Error, or None.
      "Default": "Information",
      // Separate minimum level for ASP.NET Core framework logs; Warning reduces routine request noise.
      "Microsoft.AspNetCore": "Warning"
    },
    "Clickhouse": {
      // False: do not send logs to ClickHouse. True: create TableName at startup and send logs in batches.
      // Before enabling, provide a reachable ClickHouse server and valid credentials in ConnectionString.
      "Enabled": false,
      // ClickHouse host, protocol, port, credentials, and database. Used only when Enabled is true.
      "ConnectionString": "Host=localhost;Protocol=http;Port=8123;User=default;Password=;Database=AspNetCoreLogs",
      // ClickHouse table created and used for application logs when Enabled is true.
      "TableName": "AlertCenterLogs"
    }
  },
  // ASP.NET Core Host-header allowlist; this is not a CORS setting.
  // * accepts every hostname. In production, use semicolon-separated names such as example.com;www.example.com.
  "AllowedHosts": "*"
}
```

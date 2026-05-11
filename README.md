# dotnet-interview / TodoApi

[![Open in Coder](https://dev.crunchloop.io/open-in-coder.svg)](https://dev.crunchloop.io/templates/fly-containers/workspace?param.Git%20Repository=git@github.com:crunchloop/dotnet-interview.git)

A Todo List API built in .NET 8 with a bidirectional sync engine that mirrors local
state with an external Todo API. Local mutations are captured transactionally via
an Outbox table; a background service runs four push/pull phases per tick with
last-write-wins reconciliation, `source_id`-based orphan adoption, and
mirror-policy cascade-delete. The implementation is the senior-engineer challenge
from Crunchloop, layered on top of the original full-stack interview project.

## Architecture

The solution has three projects:

- **`TodoApi`** — ASP.NET Core 8 controllers, services, EF Core models, and
  FluentValidation validators. CRUD over `TodoList` and nested `TodoListItem`.
- **`TodoApi.Sync`** — class library that hosts the bidirectional sync engine.
  Referenced from `TodoApi`; runs in the same process via a `BackgroundService`.
- **`TodoApi.Tests`** — xUnit test suite. Unit tests use EF InMemory and direct
  service instantiation; integration tests use `WebApplicationFactory<Program>`
  + `WireMock.Net`.

The application schema and the sync-engine bookkeeping are documented under
[Domain Model](#domain-model).

## Domain Model

Two layers: the application entities a user creates, and the sync-engine
bookkeeping the engine maintains around them.

**Application entities**

| Entity | Persisted in | Purpose |
| --- | --- | --- |
| `TodoList` | `TodoLists` | Top-level list. Holds `Name`, `UpdatedAt`. 1 → N items, cascade delete. |
| `TodoListItem` | `TodoListItems` | List entry. Holds `Description`, `IsCompleted`, `UpdatedAt`, FK `TodoListId`. |

**Sync engine entities**

| Entity | Persisted in | Purpose |
| --- | --- | --- |
| `SyncMapping` | `SyncMappings` | Bridge between local id and external id, per entity type. Stores snapshots of `LocalUpdatedAtAtSync` / `ExternalUpdatedAtAtSync` to drive last-write-wins reconciliation, plus a unique `IdempotencyKey`. |
| `OutboxEvent` | `OutboxEvents` | Reliable change-capture record written transactionally on local Create/Update/Delete. The push phases drain it in `OccurredAt` order. |
| `SyncRun` | `SyncRuns` | Audit row written per phase per tick: `EntityType`, `Direction`, counters, `Status` (`Running` / `Succeeded` / `Failed` / `Partial`). |

For the rationale behind each table see
[Key Design Decisions](./NOTES.md#key-design-decisions) in `NOTES.md`.

## Database

The repo ships with a devcontainer that provisions a SQL Server 2022 instance.
Inside the devcontainer, the connection string is injected as an environment
variable (`ConnectionStrings__TodoContext`) in
[`.devcontainer/docker-compose.yml`](.devcontainer/docker-compose.yml); no
`appsettings.Development.json` is required.

Outside the devcontainer, provision SQL Server yourself and put the connection
string under `ConnectionStrings:TodoContext` in
`TodoApi/appsettings.Development.json` (git-ignored).

## Inside the devcontainer

After "Reopen in Container", `postCreateCommand` runs
`dotnet tool restore && dotnet restore && dotnet build` so `csharpier` and
`dotnet-ef` are ready. `dotnet test` runs without SQL (unit tests use EF
InMemory; integration tests stub the external API with WireMock).

To run the full demo end-to-end, use two terminals:

```bash
# Terminal A — fake external API + dashboard
dotnet run --project TodoApi.FakeExternalApi

# Terminal B — TodoApi
dotnet run --project TodoApi
```

From the host, the following ports are forwarded:

- `http://localhost:5000/swagger` — TodoApi Swagger UI.
- `http://localhost:8080` — FakeApi inspection dashboard.
- `localhost:1433` — SQL Server (e.g. Azure Data Studio with `sa` / `Password123`).

If the app can't reach SQL, verify the `ConnectionStrings__TodoContext` env var
is set on the `app` container and that `sqlserver` is healthy
(`docker compose ps`).

## Build

```bash
dotnet build
```

## Run the API

```bash
dotnet run --project TodoApi
```

Swagger UI is available at `/swagger`. EF Core migrations are applied
automatically on startup in `Development`. In other environments, apply
migrations out-of-band before starting the app:

```bash
dotnet ef database update --project TodoApi
```

## Sync Engine

The TodoApi syncs bidirectionally with an external Todo API. The engine lives
as a separate class library (`TodoApi.Sync`) referenced from `TodoApi`, with
three layers:

1. **Trigger** — `SyncBackgroundService` runs each `Sync:Interval` (default 60s)
   after `Sync:StartupDelay`. Per tick, four phases run in fixed order under
   independent try/catch blocks: **list push → item push → list pull → item
   pull**. A failure in one phase is logged and the next phase still runs.
2. **Logic** — `TodoListSyncService` and `TodoListItemSyncService` implement
   push and pull:
   - **Push** drains `OutboxEvents` (Create / Update / Delete) in `OccurredAt`
     order, calls the external client, and records a `SyncMapping` on success.
     A legacy fallback path still handles unmapped locals and orphaned
     mappings as a safety net.
   - **Pull** fetches the external state and reconciles each external entity
     against its local mapping: **last-write-wins** when both sides changed
     (tie goes to external), apply-remote when only the external moved,
     push-local when only the local moved. Externals without a mapping but
     whose `source_id` matches an unmapped local are **adopted** — a new
     mapping is created instead of a duplicate. A second pass detects
     externals that disappeared and applies **mirror-policy cascade-delete**
     locally; list deletes cascade to items and their mappings.
3. **Client** — `IExternalTodoListClient` is a typed `HttpClient` registered via
   `IHttpClientFactory` with a Polly v8 resilience pipeline (retry on
   5xx/408/429/`HttpRequestException` + circuit breaker + per-attempt timeout).
   Requests use snake_case JSON and pass an `Idempotency-Key` header sourced
   from `SyncMapping.IdempotencyKey`.

Persistence: three sync tables (`SyncMappings`, `OutboxEvents`, `SyncRuns`) live
in `TodoContext`, exposed to `TodoApi.Sync` through `ISyncDbContext` to avoid a
circular reference with `TodoApi.Models`.

For design decisions, edge cases, and assumptions see
[`NOTES.md`](./NOTES.md) — the formal documentation deliverable for the
challenge. The single-page synthesis (capability matrix, configuration
cheat-sheet, failure modes, runbook, frozen limitations) lives at
[Sync v1 Closeout](./NOTES.md#sync-v1-closeout); use it as the operational
entry point. The analysis of intentional cuts (multi-instance horizontal
scaling, push-side PATCH, mirror-policy alternatives, etc.) lives at
[Out of Scope & What It Would Take](./NOTES.md#out-of-scope--what-it-would-take)
— each item follows a six-header template (status quo · dimensions touched ·
options · operational implications · test strategy · decision criteria). Live
state of the engine is exposed at
[`GET /api/sync/status`](#observability) — last run per `(entity, direction)`
pair, pending outbox count, and the active configuration.

### Outbox Pattern

Every `TodoListService` and `TodoListItemService` mutation writes an
`OutboxEvent` row in the **same EF transaction** as the entity change. The push
phases drain pending events in `OccurredAt` order, mark them processed, and
only then move on. This is what makes a local change durable across
background-service crashes and gives push idempotency a transactional
foundation.

The drain is bounded by `Sync:OutboxBatchSize` (default `1000`); larger
backlogs are processed across subsequent ticks. A **5th tick phase** purges
processed events older than `Sync:OutboxRetention` (default `7.00:00:00`)
through a provider-aware bulk delete (`ExecuteDeleteAsync` on relational
providers, `RemoveRange` + `SaveChanges` for InMemory). Set
`Sync:OutboxRetention=00:00:00` to disable the cleanup; the table is then
expected to be managed externally.

See [`diagrams/outbox-syncmapping-flow.html`](./diagrams/outbox-syncmapping-flow.html)
for a visual walk-through of how `OutboxEvent` and `SyncMapping` interact
across a tick.

### Configuration

| Key | Default | Purpose |
| --- | --- | --- |
| `ExternalApi:BaseAddress` | `http://localhost:8080` | URL of the external Todo API. The typed client appends relative paths like `todolists`. |
| `ExternalApi:RetryMaxAttempts` | `3` | Polly retry attempts on 5xx, 408, 429, and `HttpRequestException`. |
| `ExternalApi:PerAttemptTimeoutSeconds` | `10` | Per-attempt timeout before Polly retries or gives up. |
| `Sync:Interval` | `00:01:00` | Time between sync ticks. Reloaded via `IOptionsMonitor` if config changes. |
| `Sync:StartupDelay` | `00:00:05` | Delay before the first tick after the host starts. |
| `Sync:Enabled` | `true` | Set to `false` to disable the background ticker. The manual trigger endpoint still works. |
| `Sync:OutboxBatchSize` | `1000` | Cap on `OutboxEvents` drained per tick (Phase A). Backlogs spill into subsequent ticks. |
| `Sync:OutboxRetention` | `7.00:00:00` | TTL for processed `OutboxEvents`. The 5th tick phase deletes rows with `ProcessedAt != null && OccurredAt < UtcNow - retention`. `00:00:00` disables the cleanup. |

Settings live in `TodoApi/appsettings.json` and can be overridden via
environment variables (`ExternalApi__BaseAddress=...`) or per-environment
`appsettings.<Env>.json` files.

### Manual Trigger

`POST /api/sync/run` runs the four sync phases on demand and returns a
`SyncRunResponse` that aggregates the four `SyncRunResult` payloads. The
endpoint is synchronous — by the time it returns, all outbound HTTP traffic
has completed. Useful for development, debugging, and end-to-end tests.

```bash
curl -X POST http://localhost:5000/api/sync/run | jq
```

```json
{
  "listPush": { "total": 1, "pushed": 1, "failed": 0, "status": 2 },
  "itemPush": { "total": 0, "pushed": 0, "failed": 0, "status": 2 },
  "listPull": { "total": 1, "pushed": 1, "failed": 0, "status": 2 },
  "itemPull": { "total": 0, "pushed": 0, "failed": 0, "status": 2 }
}
```

`status` is the `SyncRunStatus` enum: `1=Running, 2=Succeeded, 3=Failed, 4=Partial`.

### Observability

`GET /api/sync/status` is a read-only snapshot for operators and downstream
consumers (e.g., a real-time UI). It returns the most recent `SyncRun` per
`(EntityType, Direction)` pair (up to four entries — `TodoList`/`TodoListItem` ×
`Push`/`Pull`), the current count of pending `OutboxEvents`, the oldest pending
`OccurredAt`, and a snapshot of the active `SyncOptions`.

```bash
curl http://localhost:5000/api/sync/status | jq
```

```json
{
  "lastRuns": [
    {
      "entityType": 1, "direction": 1,
      "startedAt": "2026-05-10T12:00:00Z",
      "finishedAt": "2026-05-10T12:00:02Z",
      "status": 2, "itemsProcessed": 5, "itemsFailed": 0, "error": null
    }
  ],
  "pendingOutboxCount": 3,
  "oldestPendingOutboxOccurredAt": "2026-05-10T11:30:00Z",
  "config": {
    "interval": "00:01:00", "startupDelay": "00:00:05",
    "enabled": true, "outboxBatchSize": 1000, "outboxRetention": "7.00:00:00"
  }
}
```

Cheap (4 indexed lookups + 2 outbox queries, all using existing indices). Safe
to poll. The full operational runbook lives at
[Sync v1 Closeout](./NOTES.md#sync-v1-closeout) in `NOTES.md`.

### Real-Time Notifications (SignalR)

A second `BackgroundService` (`OutboxBroadcastService`) tail-reads `OutboxEvents`
and pushes lightweight notifications over a **SignalR hub** so connected
frontends can refresh in real time without polling. The hub lives at
`/hubs/todosync` and broadcasts two strongly-typed methods —
`TodoListChanged` and `TodoListItemChanged` — with the payload
`{ eventId, entityType, entityId, operation, occurredAt }`. Clients refetch
the affected REST endpoint on receipt; no contract duplication.

Configuration (`appsettings.json`):

```json
"Realtime": {
  "BroadcastInterval": "00:00:02",   // tail poll cadence (250 ms .. 60 s)
  "BatchSize": 200                    // events per loop iteration (1 .. 1000)
},
"Cors": {
  "AllowedOrigins": [ "http://localhost:5173" ]
}
```

CORS allows the configured origins with credentials (SignalR requires explicit
origins, not `*`). Single-host assumption inherits from the sync engine — the
broadcaster's cursor is in-memory and resets to `MAX(OutboxEvents.Id)` on
restart. No replay of events generated during downtime; clients are expected to
bootstrap on `onreconnected`.

See [`diagrams/signalr-broadcast-flow.html`](./diagrams/signalr-broadcast-flow.html)
for a visual walk-through of how an `OutboxEvent` becomes a live UI update via
the broadcaster, the hub, and the client re-fetch.

The full implementation guide for the React client lives at
[`docs/realtime-frontend-integration.md`](./docs/realtime-frontend-integration.md).

### Running with the Fake External API (no Docker)

For fast local iteration and showcase demos, the solution ships
[`TodoApi.FakeExternalApi`](./TodoApi.FakeExternalApi/) — a stateful in-memory
stand-in for the upstream reference impl that implements the same
[`assets/external-api.yaml`](./assets/external-api.yaml) contract with full HTTP
fidelity (snake_case JSON, `Idempotency-Key` replay, real status codes). The
sync engine exercises Polly retries and the genuine push/pull paths against it
exactly as it would against the upstream Docker.

Run it in a separate terminal — the default port `8080` is a drop-in for
`ExternalApi:BaseAddress` and requires no `appsettings.json` change:

```bash
dotnet run --project TodoApi.FakeExternalApi
```

Then start `TodoApi` as usual:

```bash
dotnet run --project TodoApi
```

Open [http://localhost:8080](http://localhost:8080) for the inspection
dashboard:

- **§01 STATE** — `TodoList`s currently held by the fake (in-memory; reset on
  process restart or `POST /__admin/reset`).
- **§02 LAST REQUESTS** — last 50 inbound HTTP calls with method, path, status,
  and `Idempotency-Key` for visual confirmation of sync activity.
- **§03 CHAOS** — slider for fail-rate (`0-100%`), status code
  (`500 / 503 / 502 / 504 / 429 / 408`), and per-request delay (`0-30000` ms).
  Settings apply globally to the contract endpoints (admin and Swagger bypass
  them). Useful to demo Polly retries and `Partial` runs live.

Admin endpoints (also reachable via curl):

| Endpoint | Purpose |
| --- | --- |
| `GET  /__admin/state` | Full snapshot consumed by the UI (lists + chaos + recent requests). |
| `POST /__admin/reset` | Clear all lists, idempotency cache, request log, and chaos config. |
| `POST /__admin/chaos` | Body: `{ "fail_rate": 0-100, "status_code": 4xx-5xx, "delay_ms": 0-30000 }`. |
| `POST /__admin/seed`  | Body: `{ "lists": [...] }`. Bulk-load `ExternalTodoList`s, useful for adoption (CASE B) demos. |

Swagger is exposed at `/swagger` for ad-hoc requests against the contract.

For high-fidelity verification against the real upstream reference impl, see
[Running with the External API](#running-with-the-external-api) below.

### Running with the External API

The external API contract is documented in
[`assets/external-api.yaml`](./assets/external-api.yaml). A reference
implementation is published at
[`crunchloop/challenge-senior-engineer`](https://github.com/crunchloop/challenge-senior-engineer).
Clone it next to this repo and start it with Docker Compose:

```bash
git clone https://github.com/crunchloop/challenge-senior-engineer.git
cd challenge-senior-engineer
docker compose up
```

The default `ExternalApi:BaseAddress` (`http://localhost:8080`) points at the
upstream's default port. To verify the sync end-to-end:

1. Start the external API (above).
2. Start `TodoApi` (`dotnet run --project TodoApi`).
3. Create a list locally: `curl -X POST http://localhost:5000/api/todolists -H 'Content-Type: application/json' -d '{"name":"Groceries"}'`.
4. Trigger sync: `curl -X POST http://localhost:5000/api/sync/run | jq`.
5. Confirm the list appears in the external API: `curl http://localhost:8080/todolists`.

### Troubleshooting

- **`POST /api/sync/run` returns 200 with `Status=Failed` on every phase** —
  the external API isn't reachable. Check `ExternalApi:BaseAddress` and that
  the external service is running. The endpoint never returns 5xx; phase
  exceptions are caught and reported as `Failed` results.
- **Sync ticks don't run automatically** — verify `Sync:Enabled=true` in the
  active config. The manual trigger endpoint is unaffected by this flag.
- **Local edits disappear after a sync tick** — the external API likely
  deleted the corresponding entry; mirror policy cascade-deletes the local on
  the next pull. Check the structured Warning logs (`{LocalId}`,
  `{ExternalId}`) for `LocalUpdatedAt > LocalUpdatedAtAtSync` entries — those
  flag local edits that were lost. See
  [`NOTES.md`](./NOTES.md) for the full conflict policy.

## Test

Run the full suite (unit + integration):

```bash
dotnet test
```

Unit tests only:

```bash
dotnet test --filter "FullyQualifiedName!~Integration"
```

Integration tests only:

```bash
dotnet test --filter "FullyQualifiedName~Integration"
```

The suite is organized in two layers:

- **Unit tests** — xUnit + EF InMemory + services and controllers instantiated
  directly. Each test owns its own `TodoContext` (`UseInMemoryDatabase(Guid.NewGuid().ToString())`)
  for isolation. Cover controllers, services, validators, the typed HTTP
  client, and the sync services in detail.
- **Integration tests** (`TodoApi.Tests/Integration/`) — boot `TodoApi`
  in-process via `WebApplicationFactory<Program>`, with the external API
  stubbed by `WireMock.Net`. Cover end-to-end sync flows (push, pull, delete,
  adoption, and edge cases) through the live ASP.NET Core pipeline.

External integration tests against a real upstream live at
[crunchloop/interview-tests](https://github.com/crunchloop/interview-tests).

### Formatting

CI checks formatting with [csharpier](https://csharpier.com/):

```bash
dotnet csharpier --check .   # CI-equivalent check
dotnet csharpier .           # apply formatting
```

Restore tools first if needed: `dotnet tool restore`.

## Areas of Improvement

The active backlog (telemetry, multi-host concurrency, `TimeProvider`
abstraction, bounded concurrency on outbox drain, push-side PATCH for
TodoList Update events, etc.) lives in
[`NOTES.md` Areas for Improvement](./NOTES.md#areas-for-improvement).
The Outbox pattern shipped in Slice 6; configurable batch size, retention
cleanup, and the provider-aware bulk-delete helper shipped in Slice 7.

## Documentation & Diagrams

**Video Walkthrough**

- [Application Walkthrough](https://youtu.be/OfGnYIk3Zt8) — Video overview of
  the architecture, sync engine, observability, and key features.

**Reference documents**

- [`CHALLENGE.md`](./CHALLENGE.md) — frozen upstream specification of the
  senior challenge. Source of truth for required behaviour.
- [`NOTES.md`](./NOTES.md) — design decisions, edge cases, assumptions, and the
  decision log. Sections worth bookmarking:
  [High-Level Overview](./NOTES.md#high-level-overview) ·
  [Key Design Decisions](./NOTES.md#key-design-decisions) ·
  [Resilience and Error Handling](./NOTES.md#resilience-and-error-handling) ·
  [Edge Cases](./NOTES.md#edge-cases) ·
  [Assumptions](./NOTES.md#assumptions) ·
  [Decision Log](./NOTES.md#decision-log).
- [`assets/external-api.yaml`](./assets/external-api.yaml) — OpenAPI contract
  of the external Todo API the sync engine talks to.

**Diagrams** (the [`diagrams/`](./diagrams) folder)

- [`diagrams/outbox-syncmapping-flow.html`](./diagrams/outbox-syncmapping-flow.html)
  — *OutboxEvent vs SyncMapping — sync engine internals.* Self-contained HTML
  explainer covering the operational and conceptual relationship between the
  two tables across a tick.
- [`diagrams/signalr-broadcast-flow.html`](./diagrams/signalr-broadcast-flow.html)
  — *SignalR over Outbox — how OutboxEvents become live UI updates.* Vertical
  flow with tick boundary and Y-split: hub, broadcaster, hosted service, and
  why the broadcaster never touches `ProcessedAt`.
- [`diagrams/sync-tick-lifecycle.html`](./diagrams/sync-tick-lifecycle.html)
  — *Sync tick lifecycle — the 5 phases of the BackgroundService.* Vertical
  flow of push lists → push items → pull lists → pull items → retention purge
  with isolated try/catch boundaries and a one-snippet pseudocode of
  `ExecuteAsync`.
- [`diagrams/lww-decision-tree.html`](./diagrams/lww-decision-tree.html)
  — *LWW reconciliation decision tree — how each external entity is matched,
  adopted, or created locally.* Y-split branching on mapping presence, on
  whether both sides changed since the last snapshot, and on `source_id`
  parseability — with the tie-break rule and the three pre-conditions that
  break it.
- [`diagrams/STYLE.md`](./diagrams/STYLE.md) — visual system ("Terminal
  Schematic") and conventions for any new HTML or diagram added under
  `diagrams/`. New visual artifacts should follow this guide.

The `diagrams/` folder is the home for HTML explainers and visual flows. New
diagrams land there and get linked from this section.

## Contact

- Martín Fernández (mfernandez@crunchloop.io)

## About Crunchloop

![crunchloop](https://crunchloop.io/logo-blue.png)

We strongly believe in giving back. Let's work together [`Get in touch`](https://crunchloop.io/contact).

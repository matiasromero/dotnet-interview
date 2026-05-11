# NOTES — Crunchloop Senior Challenge

Decision log, tradeoffs, and assumptions for the work on [`CHALLENGE.md`](./CHALLENGE.md). This file is the documentation deliverable required by the spec.

**Convention:** the formal sections (Overview → Assumptions) are the document delivered at the end; the **Decision Log** at the bottom is append-only and captures the "why" of each slice while work is in progress. A new decision is first noted in the Decision Log; once it is confirmed as the project's final stance, it gets promoted/synthesized into the corresponding formal section.

---

## High-Level Overview

The sync engine lives as a separate class library (`TodoApi.Sync`) referenced from `TodoApi`, with three explicit layers:

1. **Trigger** — `SyncBackgroundService : BackgroundService` runs in the API host. Each `Sync:Interval` (default 60s, `IOptionsMonitor` for reload) opens a fresh DI scope, resolves the sync service, runs an iteration, and logs totals. A top-level catch prevents a failed tick from killing the host.
2. **Logic** — `TodoListSyncService` is scoped, depends on `ISyncDbContext` (minimal interface from the sync project) + `IExternalTodoListClient` + `ILogger<T>`. A run persists a `SyncRun` Running, looks for candidates via server-side anti-join, pushes them one by one (save-per-list), and closes the `SyncRun` with an aggregate status.
3. **Client** — `IExternalTodoListClient` typed HttpClient registered via `IHttpClientFactory` (avoids socket exhaustion without singleton). Decorated with a `Microsoft.Extensions.Http.Resilience` (Polly v8) pipeline: exponential retry + jitter, circuit breaker, per-attempt timeout. JSON snake_case via `JsonNamingPolicy.SnakeCaseLower`.

Persistence: two new tables (`SyncMappings`, `SyncRuns`) in the existing `TodoContext`, exposed to the sync project via `ISyncDbContext` with a specific method `GetUnmappedTodoListsAsync(CancellationToken)` that projects to `LocalTodoListRecord`. That design prevents `TodoApi.Sync` from referencing `TodoApi.Models` (which would create a circular dependency: `TodoApi → TodoApi.Sync` already exists).

Typed configuration with `IOptions<ExternalApiOptions>` (DataAnnotations + `ValidateOnStart` for fail-fast on a malformed BaseAddress, retry/timeout out of range) and `IOptions<SyncOptions>` (no annotations — sane defaults).

**Slice 1 only delivers PUSH of TodoLists local→external.** PULL, items, deletes, and updates are left for subsequent slices (see Areas for Improvement).

**Slice 2 adds PULL external→local + bidirectional reconciliation for TodoLists.** Each tick of the background service now runs `PushTodoListsAsync` and then `PullTodoListsAsync` in the same scope (independent try/catch, one failure doesn't prevent the other). The push adds a `Guid IdempotencyKey` per intent: it is sent as the `Idempotency-Key` header in the POST and persisted in `SyncMapping.IdempotencyKey` (unique). The pull does `GET /todolists` (full scan, no server-side filters available) and for each entry decides among three cases: (A) already mapped → reconcile last-write-wins comparing `local.UpdatedAt` vs `external.updated_at` (tie-break to the external); (B) parseable `source_id` pointing to a local without a mapping → ADOPTION (closes the gap from the slice 1 crash mid-write); (C) none of the above → create a local `TodoList` with `UpdatedAt = external.updated_at`. Items are out of scope for the slice (slice 3); external deletes are out of scope (slice 4).

**Slice 3 adds bidirectional sync of `TodoListItem` resolving the asymmetry of the external contract.** The contract (verified in `assets/external-api.yaml`) exposes `PATCH /todolists/{listId}/todoitems/{itemId}` and `DELETE /todolists/{listId}/todoitems/{itemId}`, but **does not expose isolated POST of items**: items are only created externally embedded in the initial `POST /todolists`. The slice covers: (1) `TodoListItem.UpdatedAt` (mirror of slice 2 on `TodoList`), set by `TodoListItemService` on Create/Update; (2) modification of the slice 1 push to embed `local.Items` in the initial POST and persist item mappings via `PersistEmbeddedItemMappingsAsync`; (3) `TodoListItemSyncService.PushTodoListItemsAsync` → PATCH local→external for mapped items that changed + DELETE local→external of orphan mappings (LocalId without a corresponding TodoListItem, anti-join) + Warning log for new local items in an already-pushed list (no-sync, documented contract limitation); (4) `PullTodoListItemsAsync` that receives the `ExternalListWithMapping` from the list pull and applies LWW (CASE A), adoption by `source_id` (CASE B), or local create (CASE C) per item. `SyncMapping` gains a `ParentExternalId nvarchar(64) NULL` column so that the parent path survives the hard-delete of the local item. The `SyncBackgroundService` orchestrates 4 sync phases per tick at this point (slice 7 later adds the outbox-retention phase): list push → item push → list pull (returns a tuple with mapped externals) → item pull. Re-parenting is out of scope; external deletes detected by GET disappearance are left for slice 4 (unified bidirectional delete).

**Slice 4 closes the bidirectional DELETE cycle for TodoLists and TodoListItems.** Three new flows: (1) Local DELETE of `TodoList` → `DELETE /todolists/{externalId}`, detected via orphan mapping anti-join (mirror of the item-orphan pattern from slice 3); the push reuses the `404` grace and the external cascade handles the child items (whose mappings are cleaned up in the next item-push with their own `404` grace). (2) PULL detects an external delete of a `TodoList` by absence in `GET /todolists`: local cascade (TodoList + child TodoListItems + their SyncMappings) executed atomically via `ISyncDbContext.ApplyExternalDeleteListAsync` (a single `SaveChanges`; explicitly deletes child items + mappings because InMemory does not enforce FK cascade). (3) PULL detects an external delete of a `TodoListItem` (parent alive) by absence from the embedded `items`: filters by `ParentExternalId IN seenExternalListIds` to avoid double-delete with the cascade from (2), and deletes atomically via `ApplyExternalDeleteItemAsync`. **Mirror + Warning conflict policy:** when the external disappeared but `local.UpdatedAt > LocalUpdatedAtAtSync`, structured Warning log before deleting (consistency + observability). The `Idempotency-Key` is kept only on POST (DELETE is naturally idempotent). The `SyncBackgroundService` did not change: the existing 4 phases absorb the 2nd pass on each side.

**Slice 5 hardens the sync with E2E tests + manual endpoint + docs.** `POST /api/sync/run` runs the 4 phases synchronously (orchestrator useful in dev/debug and as a handle for the E2E tests). 10 integration tests with `WebApplicationFactory<Program>` + `WireMock.Net` cover push, pull, bidirectional delete, adoption, and the `source_id→deleted local` edge case. 5 new tests in `SyncBackgroundServiceTests` with `CapturingLoggerProvider` assert log entries for each phase. README expanded in English with sync engine, configuration, troubleshooting. Total tests: 143 → 160. `TimeProvider/IClock` cross-cutting was evaluated and discarded due to scope creep — the weak `UpdateAsync` test was closed with `Thread.Sleep(1)` and the abstraction remains as explicit debt.

**Slice 6 introduces the outbox pattern on the push side.** New `OutboxEvents(EntityType, EntityId, Operation, Payload?, OccurredAt, ProcessedAt?, IdempotencyKey)` table with unique index `IdempotencyKey`, `(EntityType, EntityId)` index for diagnostics, and a filtered index `OccurredAt WHERE ProcessedAt IS NULL` (efficient FIFO drain, SqlServer-only). `TodoListService` and `TodoListItemService` write an OutboxEvent for every CRUD in their `SaveChanges` (Update/Delete are atomic; Create does two saves with the gap covered by the legacy anti-join fallback). Refactor of `PushTodoListsAsync` and `PushTodoListItemsAsync` into two phases per tick: **Phase A** drains up to `OutboxBatchSize=1000` events FIFO via `ISyncDbContext.GetPendingOutboxEventsAsync`; **Phase B** runs the full legacy flow (anti-join unmapped + orphan-mappings) as a transient safety net. Each processed event is marked with `ProcessedAt = UtcNow`. The asymmetry inherited from previous slices is preserved: for `TodoList`, push does POST (Create) and DELETE (Delete) — PATCH (Update) remains the pull side's responsibility via LWW (Update events for TodoList are no-op in slice 6, slice 7 may activate it); for `TodoListItem`, push does do PATCH (preserves slice 3). Item Create always logs Warning + marks processed (limitation inherited from slice 3 — the external contract does not expose isolated POST of items). New indices on `TodoList.UpdatedAt` and `TodoListItem.UpdatedAt` to enable delta-cursor-based queries in slice 7+. Transient coexistence of the legacy Phase B enables zero-cost migration: tests from slice 1-5 stay green because the legacy flow processes pre-slice-6 entries without an event. 23 new tests (160 → 183).

**Slice 7 hardens the outbox lifecycle: configurable batch size, retention cleanup with a provider-aware bulk delete helper.** Three pieces. (1) `SyncOptions` gains `OutboxBatchSize` (int, default 1000) and `OutboxRetention` (TimeSpan, default 7 days). `TodoListSyncService` and `TodoListItemSyncService` now consume `IOptions<SyncOptions>` (read once per tick via the new DI scope) and pass `OutboxBatchSize` to `GetPendingOutboxEventsAsync` instead of a hardcoded constant. (2) `BulkDeleteExtensions.ExecuteBulkDeleteAsync<T>(IQueryable<T>, DbContext, CancellationToken)` — provider-aware delete that uses `ExecuteDeleteAsync` on relational providers and falls back to `RemoveRange` + `SaveChanges` for InMemory (detected via `ProviderName` string check, avoids a hard dependency on `Microsoft.EntityFrameworkCore.InMemory` from production code). New package reference `Microsoft.EntityFrameworkCore.Relational` on `TodoApi.Sync` (transitive dep already present in TodoApi via SqlServer). (3) New `ISyncDbContext.PurgeProcessedOutboxEventsAsync(cutoff)` predicate-deletes events with `ProcessedAt != null && OccurredAt < cutoff`, returns the deleted count. The `SyncBackgroundService` adds a 5th phase per tick: independent try/catch, log Info on success (`purged={Count} olderThan={Cutoff:o}`), Debug-and-skip when `OutboxRetention <= TimeSpan.Zero`, Error log on exception (does not abort the loop). 13 new tests (183 → 196): 4 BulkDeleteExtensions, 4 PurgeProcessedOutboxEventsAsync, 2 batch-size respects-limit, 3 retention-phase (logs / fail-isolated / disabled).

## Key Design Decisions

| Decision | Alternatives discarded | Why | Trade-off accepted | Slice |
|---|---|---|---|---|
| Separate class library `TodoApi.Sync`, hosted in TodoApi | Worker SDK (separate process); inline merge in TodoApi | Decoupling the sync concern without duplicating config/host. The user explicitly requested it. | A single deployment unit (acceptable for the challenge); sync deploys are coupled to API deploys. | 1 |
| `SyncMapping` + `SyncRun` tables in the existing `TodoContext` | Separate DbContext; inline ExternalId in TodoList | Single migration, simple transactions. Keeps the domain (`TodoList`) free of the sync concern. | An extra join in sync read-paths (not a hot path). | 1 |
| `ISyncDbContext` with specific method `GetUnmappedTodoListsAsync` | `((DbContext)_db).Set<TodoList>()` with cast in the service | Circular dependency (`TodoApi.Sync` cannot reference `TodoApi.Models`). Server-side anti-join avoids SQL Server's 2100 parameters limit. | Each new syncable entity adds a method to the interface (reasonable scale for 2-3 entities). | 1 |
| `Microsoft.Extensions.Http.Resilience` (official Polly v8) | `Polly` + `Microsoft.Extensions.Http.Polly` (legacy v7) | Microsoft's official line post-Polly v8, native integration with `IHttpClientFactory`, built-in telemetry. | One more abstraction layer; less code in exchange. | 1 |
| Save-per-list (not batch) in the push loop | Batch at the end of the run | Simple idempotency: a crash mid-batch leaves already-written mappings intact, the next run won't re-push them. | More DB round-trips (irrelevant for expected volumes of new lists per tick). | 1 |
| `source_id` as bidirectional correlation key | Magic mapping table with internal UUIDs | The external exposes `source_id` explicitly in the contract. Push sends `local.Id.ToString()`; future pulls can detect locally-originated entries without an additional lookup. | Couples the external contract to the local ID schema (stringified long). | 1 |
| `Idempotency-Key` header + column in `SyncMapping` + adoption in pull | Adoption-only without header; pre-flight GET before each POST | The external server does NOT document the header (does not deduplicate today), but the combination is forward-compatible and the real closing of the crash mid-write gap comes from the pull adopting orphans by `source_id`. The unique column allows tracing/debugging. | A `Guid` column that for weeks does not provide server-side deduplication; an extra Guid generated per intent. | 2 |
| Last-write-wins by `updated_at` with tie-break to the external (`>=`) | Symmetric last-writer-wins (rejection on tie); local-always-wins; conflict-table that triggers human review | The external server is authoritative for its timestamps (it generates them, ISO 8601, monotonic). On exact tie, preferring the external is stable and predictable: the next local push will overwrite it if the user edits later. | Loses the local change on exact ties (rare but possible if two clients hit at the same second). No notification to the user of the applied external change. | 2 |
| Items out of scope for slice 2 | Item sync in the same slice as the list pull | The external contract has no isolated POST of items (they are only created when creating the list, or via individual PATCH/DELETE). That asymmetry deserves its own brainstorm: re-POST the entire list with new items? items outbox? Architectural decision I don't want to tie to the list pull. | New local items don't sync to the external until slice 3. List pull brings nested items but we discard them. | 2 |
| Serial pull after push in the same tick | Alternating push/pull tick; two hosted services with separate intervals | Keeps mental order: push first accepts the "local winner"; the pull afterward reconciles and adopts orphans created by push crashes from the same or previous tick. A single `SyncBackgroundService`. | A slow push delays the pull. Acceptable for expected volumes. | 2 |
| `ApplyExternalCreateAsync` with two `SaveChanges` (TodoList → SyncMapping) | A single explicit transaction with `BeginTransaction` | The EF InMemory provider ignores transactions; documenting simple is simpler than provider branches. The gap between the two saves is microscopic and, if it crashes, the next pull creates again (documented edge case). | Theoretical possibility of leaving a local without a mapping → on the next tick the local is duplicated (no way to correlate via source_id because the external entry didn't have one). Documented as Edge Case. | 2 |
| New local items in already-pushed list → no-sync + Warning log | Destructive re-create of the entire list (DELETE + POST with items); embed-on-first-push only (reject late items) | The external contract does not expose `POST /todolists/{id}/todoitems`. Destructive re-create changes the `ExternalId` of the list and of ALL its items, invalidates mappings, destabilizes last-write-wins. No-sync + Warning preserves consistency and leaves a clear fallback: the user re-edits or the server adds the endpoint. The spec says "feel free to suggest changes" → reported as a suggestion to the server. | Items added to an already-pushed list are NOT synced to the external. Documented as a contract limitation. | 3 |
| `SyncMapping.ParentExternalId nvarchar(64) NULL` (denormalized) | Join with the list's SyncMapping (col `ParentLocalId long?` to look up the parent's ExternalId); single transaction parent+child | The DELETE of orphan items needs the path `/todolists/{listId}/todoitems/{itemId}` and the only persistence that survives the local item's hard-delete is the item's SyncMapping. Denormalizing the parent's ExternalId avoids an extra JOIN on every DELETE; the parent's ExternalId is server-generated and immutable per the contract, so the denormalization doesn't go stale. NULL for TodoList mappings. | If in the future the external server allowed moving an item between lists (re-parenting) and that change normalized the ParentExternalId, it would go stale. Slice 3 marks re-parenting as out-of-scope. | 3 |
| Own `TodoListItemSyncService` + `PullTodoListsAsync` returns tuple `(SyncRunResult, IReadOnlyList<ExternalListWithMapping>)` | Integrate item push and pull within `TodoListSyncService`; orchestrate via callback within the list pull | Keeps the slice 1 "one entity → one service" pattern. The tuple in pull lets the `SyncBackgroundService` invoke each service explicitly without coupling `TodoListSyncService` to the item service. The external fetch (GET /todolists) still happens only once per tick — the item pull receives the embedded items from the response. | The item pull has a less uniform signature with the item push (receives `mappedExternals` instead of receiving nothing). Acceptable due to the real asymmetry of the flow. | 3 |
| Local DELETE detection of items by orphan mapping (anti-join) | Tombstone soft-delete (`IsDeleted` flag in `TodoListItem`) + sync deletes external + local hard-delete | The tombstone invades the domain model to serve a sync concern, contradicting the slice 1 principle ("mapping table keeps the domain free of the concern"). The anti-join `SyncMappings type TodoListItem WHERE NOT EXISTS TodoListItem(Id = LocalId)` runs once per tick and doesn't affect the user's delete path. | In the case of a local DELETE of a mapped item, there is one tick of delay between local removal and external propagation (until the next `PushTodoListItemsAsync`). Acceptable. If the external item was already deleted (404), we treat as resolved and clean up the local mapping. | 3 |
| Items embedded in initial POST: persist mappings with their returned `ExternalId` | POST with items + second step "discover items via GET" to build mappings; items outbox | The POST response already brings items with `id` + `source_id` + `updated_at`. Parsing `source_id` (which we send as `local.Id.ToString()`) allows mapping bidirectionally without an extra GET. Edge case: if the server normalizes the `source_id`, log Warning and skip — the item stays without a mapping and gets duplicated on the next pull (same risk as the list, already documented). | An extra unique column (`Idempotency-Key` not applicable to individual items — the POST covers the entire list). | 3 |
| Mirror + Warning for conflict of external DELETE + unsynced local edits | Pure Mirror (without distinction); preserve+re-push (resurrect with new ExternalId); skip+Warning (conscious zombie) | The external is authoritative for what exists. Pure Mirror loses the edit silently; preserve+re-push conceptually duplicates the entity with a new ExternalId that breaks the "one entity, one mapping" mental model; skip+zombie leaves inconsistent state requiring intervention. Mirror + Warning achieves consistency + observability: the structured log records exactly which edit was lost. | Loses the local edit when the external disappeared after it. The Warning makes it visible but not recoverable. Under single-instance + aligned clocks, the case is rare. | 4 |
| Orphan mapping anti-join to detect local DELETE of `TodoList` | Tombstone soft-delete (`IsDeleted` flag in `TodoList`); delete event captured in the service | Mirrors the slice 3 decision for items: anti-join `SyncMapping(EntityType=TodoList) WHERE NOT EXISTS TodoList(Id=LocalId)` keeps the domain free of the sync concern (no new column in `TodoList`). The change only lives in `TodoApi.Sync.Data` and `TodoListSyncService`. | One-tick latency (~60s) between local delete and external propagation. If the external was already deleted (404), 404 grace resolves it. | 4 |
| `ApplyExternalDeleteListAsync` explicitly deletes items + item-mappings before the list mapping + list | Trust EF cascade `TodoList → TodoListItem` configured in `OnModelCreating` | EF cascade works on real SqlServer, **but the InMemory provider does NOT enforce it**. Without the explicit delete, tests would give false parity with production (items would remain in InMemory after deleting the list). Explicit deletion in a single `SaveChanges` maintains atomicity in SqlServer and consistency with InMemory. | A couple of extra queries per delete (`Where(...).ToListAsync` + `RemoveRange`). Acceptable; not a hot path. | 4 |
| Detect external delete by adding a 2nd pass after the existing loop, in the same pull phase | Separate "delete detection" phase in `SyncBackgroundService`; "external tombstones" table | The 2nd pass reuses the GET query already done (a set of seen external IDs). Keeping everything within `PullTodoListsAsync` and `PullTodoListItemsAsync` avoids a new phase and preserves the invariant "the 4 phases of `SyncBackgroundService` are independent with their own try/catch". | Each pull method grows slightly. Acceptable; the cognitive cost is low thanks to the uniform pattern: anti-set check + Warning conditional + Apply{X}Async. | 4 |
| `Total = base + deleteCount` and `Processed += deleted` in `SyncRunResult` | `SyncRunResult` separated by direction/operation; new record with distinct buckets | The Mirror+Warning's warning **is not** a "warning without action" case (the delete does execute), therefore it adds to Processed just like any delete. Keeping a single record avoids breaking compat for tests from slice 1-3. The "Pushed misnamed for pulls" debt remains documented in Areas for Improvement. | The `SyncRunResult.Pushed` record now also reports deletes from pulls. The "items processed successfully in any direction" semantics still hold. | 4 |
| `BulkDeleteAsync` extension with provider guard via `ProviderName` substring | Hard dep on `Microsoft.EntityFrameworkCore.InMemory` (`IsInMemory()`); separate code paths in each call site; reflection probe of `ExecuteDeleteAsync` | Keeps production code free from a test-only package. Uses the relational `ExecuteDeleteAsync` when available, falls back to `RemoveRange` + `SaveChanges` for InMemory so tests still exercise the predicate. Added `Microsoft.EntityFrameworkCore.Relational` to `TodoApi.Sync` (transitive in TodoApi via SqlServer). | A string substring check is fragile if a future provider has "InMemory" in its name (no current example). The InMemory branch loads matching rows into memory before deleting; acceptable for the tests we run. | 7 |
| Outbox retention runs as the 5th phase of the existing tick (no separate `BackgroundService`) | Dedicated `OutboxRetentionBackgroundService` with its own interval; cleanup at start of `PushTodoListsAsync`; modulo-based "every N ticks" | Mirrors the slice 4/6 pattern of independent try/catch phases. One service to register, test, disable in E2E. Single SQL DELETE per tick is negligible overhead even at the 60s cadence (`ExecuteDeleteAsync` is O(rows-deleted), not O(table-size)). | The cleanup runs every tick (60s) even though retention is 7 days. Wasted query cost is negligible (~0 rows to delete most ticks) and falls within the existing per-tick budget. | 7 |

## Resilience and Error Handling

**Pipeline (registered in `SyncServiceCollectionExtensions.AddTodoSync`, outer→inner order):**

1. **Retry** (outer) — `MaxRetryAttempts = ExternalApiOptions.RetryMaxAttempts` (default 3), `BackoffType.Exponential`, `UseJitter = true`, `Delay = 1s`. Retries `HttpRequestException`, `TimeoutRejectedException`, and responses `>= 500 || 408 || 429`. Does NOT retry generic 4xx (the local API knows how to ask correctly or not, retry makes no sense).
2. **CircuitBreaker** (middle) — `FailureRatio = 0.5`, `SamplingDuration = 30s`, `MinimumThroughput = 5`, `BreakDuration = 30s`. If the external API is sustainedly down, the sync stops spending retries; auto-recovery in 30s.
3. **Timeout** (inner, per-attempt) — `ExternalApiOptions.PerAttemptTimeoutSeconds` (default 10s). Applies per individual attempt, not per pipeline — a retry after timeout gets its own fresh window.

The order matters: if Timeout were outer, retries would share a single global window and the first retry could already be out of time.

**Run partial-failure semantics:**

- For each candidate `TodoList`, push does an independent try/catch. An individual failure increments the `failed` counter, logs Warning, and continues with the next.
- Final `SyncRun` status:
  - `failed == 0` → `Succeeded`.
  - `pushed == 0 && failed > 0` → `Failed`.
  - Otherwise → `Partial`.
- The `SyncRunResult` returned by the service reflects the same counters.

**Idempotency:**

- The candidates query (`GetUnmappedTodoListsAsync`) uses anti-join against `SyncMappings`: only considers TodoLists without mapping. Already-synced lists are skipped on subsequent runs — verified by the `PushTodoListsAsync_WithExistingMapping_OnlyPushesUnmapped` test.
- Save-per-list: each mapping is persisted immediately after its `client.Create`. If the process dies mid-batch, already-synced lists have their mapping and won't be re-pushed.
- **Slice 2:** each push intent generates a `Guid IdempotencyKey` that travels as the `Idempotency-Key` header in the POST and is persisted in `SyncMapping.IdempotencyKey` (unique). The external server does not process the header today (it's not in the OpenAPI spec) — it's forward-compatible for when it does.
- **Slice 4 (bidirectional DELETE):** DELETEs are naturally idempotent (same end-state on retry). The 404 grace in the push's orphan-DELETE treats "external already deleted" as success. The pull's 2nd pass is idempotent: if a mapped list does not appear in the GET on two consecutive ticks, the first deletes it and the second doesn't find it (mapping already removed), no-op. PUSH and PULL of DELETEs do not write new `IdempotencyKey`s (the column exists but is only used for POSTs).

**Adoption (pull → closing the slice 1 gap):**

The crash mid-write gap between `CreateTodoListAsync` (success) and `SaveChangesAsync(mapping)` is **closed by the pull**. When the pull finds an external entry with a `source_id` parseable to a `long` matching a local `TodoList.Id` that **has no mapping**, it creates the mapping instead of treating the entry as new. Verified by the `PullTodoListsAsync_ExternalWithLocalSourceIdNoMapping_AdoptsAsMapping` test.

Limitation: adoption only works if the server preserved the `source_id` literally as the push sent it. If it normalizes/transforms it (e.g., with a prefix), correlation breaks and the orphan would stay. The current spec doesn't document normalization; we assume literal preservation.

**Pull: partial-failure semantics:**

- `GET /todolists` fails → status `Failed` with `ItemsProcessed = 0` and `ItemsFailed = 0` (we didn't get to iterate). Verified by `PullTodoListsAsync_GetThrows_StatusFailedAndZeroProcessed`.
- For each external item, independent try/catch. An individual failure (external PATCH, ApplyExternalCreate, etc.) increments `failed` and continues with the next.
- Final run status: same semantics as push (`failed == 0` → `Succeeded`; `processed == 0 && failed > 0` → `Failed`; mixed → `Partial`).

**Logging:**

- Structured logging with placeholders (`{LocalId}`, `{ExternalId}`, `{Total}`, `{Pushed}`, `{Failed}`, `{Status}`) — consistent with the repo pattern (see `TodoListService`).
- Levels: Info for the success of each push and for the tick summary; Warning for individual failures; Error for exceptions that escape the loop's try/catch (last resort, should not happen); Info for `Sync:Enabled = false`.

## Edge Cases

- [x] **TodoList without items** — slice 1 we always send `Items = Array.Empty<...>()` (items are a future slice). Acceptable for the initial push; the item sync recovers existing items when implemented.
- [x] **TodoList already mapped** — anti-join excludes it. The external is not called.
- [x] **Partial failure (1 of N)** — status `Partial`, mappings of the successful ones persist. Test `PushTodoListsAsync_OneOfThreeFails_StatusPartialAndOthersMapped`.
- [x] **Total failure (N of N)** — status `Failed`, no new mappings. Test `PushTodoListsAsync_AllFail_StatusFailed`.
- [x] **No candidates (empty DB or all mapped)** — `SyncRun` is persisted with `Succeeded` + 0 counts. Test `PushTodoListsAsync_NoLocalLists_ReturnsZeroAndSucceeded`.
- [x] **Sync disabled by config** — `SyncBackgroundService.ExecuteAsync` returns immediately if `SyncOptions.Enabled = false`. Smoke test verifies clean start/stop.
- [x] **External API returns empty body in 2xx** — the client throws `ExternalApiException` with the message "POST/GET/PATCH todolists returned empty body".
- [x] **5xx / 408 / 429** — Polly retry. If the circuit breaker opens, subsequent ticks fail fast until it closes.
- [x] **Generic 4xx** — the client throws `ExternalApiException`; the service catches it as an individual failure. NOT retried (generic 4xx are not transient).
- [x] **Push crash mid-write (slice 1 gap)** — the pull adopts the external orphan via `source_id == local.Id.ToString()` and creates the local mapping. Test `PullTodoListsAsync_ExternalWithLocalSourceIdNoMapping_AdoptsAsMapping`.
- [x] **Pull: external newer than the mapped local** — remote wins, `local.Name` and `local.UpdatedAt` are overwritten with `external.updated_at`. Test `PullTodoListsAsync_MappedExternalNewer_UpdatesLocalName`.
- [x] **Pull: local newer than the mapped external** — local wins, PATCH to the external with the new Name. Test `PullTodoListsAsync_MappedLocalNewer_PatchesExternal`.
- [x] **Pull: both sides changed since the last sync** — last-write-wins by timestamp; tie (`==`) goes to the external. Tests `..._BothChanged_ExternalWinsOnTimestamp`, `..._LocalWinsOnTimestamp`, `..._TieGoesToExternal`.
- [x] **Pull: nothing changed** — only bumps `LastSyncedAt`, doesn't touch local or external. Test `PullTodoListsAsync_MappedNoChanges_BumpsLastSyncedOnly`.
- [x] **Pull: external without local counterpart** — `source_id` null or non-parseable → CASE C, creates a local `TodoList` with `UpdatedAt = external.updated_at`. Test `PullTodoListsAsync_ExternalWithUnknownSourceId_CreatesLocalAndMapping`.
- [x] **Pull: GET /todolists fails in bulk** — status `Failed` without iterating items. Test `PullTodoListsAsync_GetThrows_StatusFailedAndZeroProcessed`.
- [x] **Pull: partial failure mid-loop** — try/catch per entry; status `Partial`, the successful ones persist. Test `PullTodoListsAsync_OneOfThreeFails_StatusPartial`.
- [x] **`source_id` parseable but pointing to a local that no longer exists** — the `FindUnmappedLocalByIdAsync` returns null and we fall back to CASE C: we create a new local with a different auto-incremented Id. Slice 5 confirms this behavior by test (`PullTodoListsAsync_ExternalSourceIdPointsToDeletedLocal_FallsToCaseC`) under the assumption that the orphan-DELETE push from slice 4 already cleaned up the orphan mapping in a previous tick. Under single-instance the window is narrow (multi-host race not supported).
- [ ] **Crash between the two `SaveChanges` of `ApplyExternalCreateAsync`** — would leave a local `TodoList` without a mapping. Since the external entry doesn't have a `source_id` pointing to the new local (it came from outside), the next pull would treat it as CASE C again and create ANOTHER local + mapping → local duplicate. Low risk, microscopic window. Correct mitigation: explicit transaction or outbox. Documented as debt.
- [ ] **External returns an `id` > 64 chars** — the `ExternalId` column is `nvarchar(64)`. The insert would fail in real SqlServer. We don't document length in the external contract; we assume UUIDs (~36 chars). Future mitigation: raise the limit or fail explicitly when validating the response. Low risk for the challenge.
- [ ] **Concurrency between two API hosts running simultaneously** — the sync would run 2x over the same lists. Mappings have a unique index on `(EntityType, LocalId)` + `IdempotencyKey`, so the second would lose with `DbUpdateException` — we are not handling it, concurrent runs step on each other. Single-instance assumption for now.
- [x] **External "deleted" a list we have mapped** — slice 4 detects the delete by absence in `GET /todolists` and applies Mirror policy: local cascade (TodoList + items + their mappings) via `ApplyExternalDeleteListAsync`. If `local.UpdatedAt > LocalUpdatedAtAtSync`, log Warning before the delete. Test `PullTodoListsAsync_MappedExternalDisappeared_DeletesLocalListAndAllMappings`.
- [ ] **External server does NOT preserve `source_id` literally** — if it normalizes/transforms it, the pull adoption fails and the crash mid-write orphans stay. The current spec doesn't document normalization. If it happened, pull would create local duplicates (another `TodoList` with the same name). Future mitigation: outbox pattern.
- [x] **New local item in already-pushed list → cannot be synced** — external contract limitation (does not expose isolated `POST /todolists/{id}/todoitems`). The push detects those items via `GetUnmappedTodoListItemsWithMappedParentAsync` (anti-join: items without mapping whose parent list DOES have a mapping) and emits a structured Warning for each one. They do NOT increment the run's `Total`/`Processed`/`Failed` counters. Workaround for the user: re-edit the list to force a change (does not solve the problem), or wait for the server to add the endpoint (formal suggestion in Areas for Improvement). Test `PushTodoListItemsAsync_UnmappedItemsWithMappedParent_LogsWarningAndDoesNotCallClient`.
- [x] **Local DELETE of a mapped item → propagates to the external on the next tick** — the hard-delete of the local item leaves the `SyncMapping` orphan (LocalId points to a row that no longer exists). `GetOrphanedItemMappingsAsync` detects it via anti-join and `PushTodoListItemsAsync` invokes `DeleteTodoItemAsync(ParentExternalId, ExternalItemId)` to sync it. Latency: ~1 tick (default 60s). Test `PushTodoListItemsAsync_OrphanMapping_DeletesExternalAndRemovesMapping`.
- [x] **External item was already deleted when we DELETE (404)** — `catch (ExternalApiException ex) when (ex.StatusCode == 404)` treats as resolved: cleans up the local mapping + increments `Processed` + Info log. Test `PushTodoListItemsAsync_OrphanMappingExternal404_TreatsAsResolved`.
- [x] **External item `source_id` not parseable as `long`** (initial POST response or pull) — `long.TryParse` fails → Warning log + skip mapping. The item stays without a mapping and will be re-created as a local duplicate on the next pull (CASE C). Same risk as for lists, already documented. Tests `PushTodoListsAsync_ResponseItemHasNonParseableSourceId_*` and `PullTodoListItemsAsync_ExternalWithUnknownSourceId_CreatesLocalItem`.
- [x] **External item with parseable `source_id` that does NOT match any just-pushed local item** (initial POST response) — Warning log + skip. Test `PushTodoListsAsync_ResponseItemSourceIdDoesNotMatchAnyLocalItem_*`.
- [x] **Item LWW: both sides changed, tie on `updated_at`** — external wins (`>=` rule), symmetric with the slice 2 policy for lists. Tests `PullTodoListItemsAsync_MappedBothChanged_*`.
- [x] **Items inside an orphan list adopted by pull (CASE B of list pull)** — the list pull adopts the orphan and adds `mappedExternals.Add(...)`. The subsequent item pull processes the embedded items as CASE A/B/C as appropriate. No special case. Covered by the combination of list pull tests (adoption) and item pull tests (with non-empty `mappedExternals`).
- [x] **Embedded items in pull of CASE C (new list from the external)** — the call site `CreateLocalFromExternalAsync` projects `external.Items` to `IReadOnlyList<EmbeddedExternalItem>` and passes it to `ApplyExternalCreateAsync`, which creates the local `TodoListItem` + `SyncMapping` for each item of the plan within the same method. The item pull does NOT process them again (CASE C of list pull does NOT add to `mappedExternals`). 3 saves in the flow: list, items, mappings — the weak atomicity inherits the slice 2 risk (a crash between saves duplicates on the next pull, same reasoning).
- [ ] **Re-parenting (item moved between lists, locally or externally)** — out of scope. The local `UpdateTodoListItem` does NOT allow changing `TodoListId` (DTO only exposes `Description` + `IsCompleted`); the external `UpdateTodoItemBody` doesn't either. If an external item with a parent changed post-mapping arose, the SyncMapping's `ParentExternalId` would go stale and the next PATCH/DELETE would go to the old URL → 404 → Warning log + (in the case of DELETE) mapping cleanup. Acceptable. A future slice will decide the policy if the contract exposes it.
- [x] **External DELETE of a mapped item (parent alive)** — slice 4 detects by absence in the GET's embedded `items`, filtering by `ParentExternalId IN seenExternalListIds` to avoid double-delete with the list-pull cascade. Mirror + Warning if there were local edits. Test `PullTodoListItemsAsync_MappedItemDisappearedFromAliveList_DeletesLocalAndMapping`.
- [x] **Local DELETE of a mapped TodoList** — anti-join `SyncMapping(EntityType=TodoList) WHERE NOT EXISTS TodoList(Id=LocalId)` detects orphans; `PushTodoListsAsync` invokes `DeleteTodoListAsync(externalId)` and cleans up the mapping. 404 grace if the external was already deleted. Latency ~1 tick. Test `PushTodoListsAsync_OrphanListMapping_DeletesExternalAndRemovesMapping`.
- [x] **Item PATCH transient 404 when parent was externally deleted** — the item push runs before the list pull that would detect the delete. If the item has local changes, the PATCH to the external returns 404 → `failed++`, status `Partial` that tick. It self-heals on the next tick when the list pull cascade-deletes the item locally via `ApplyExternalDeleteListAsync`. Acceptable as a transient failure (don't add 404 grace to the item push PATCH branch because that 404 may have other causes).
- [x] **External list reappears after detected delete** — the local was cascade-deleted, mapping clean. The next pull treats it as CASE C (create new local). The external's `source_id` points to a LocalId that no longer exists → falls into CASE C. Local data before the delete: lost (Mirror policy).
- [x] **InMemory does NOT enforce FK cascade `TodoList → TodoListItem`** — `ApplyExternalDeleteListAsync` explicitly deletes items + item mappings + list mapping + the list, all in a single `SaveChanges`. Without this, InMemory tests would pass but items would remain orphaned in production. Test `PullTodoListsAsync_MappedExternalDisappeared_DeletesLocalListAndAllMappings` verifies that `ctx.TodoList`, `ctx.TodoListItem`, and `ctx.SyncMappings` are all empty.
- [x] **Crash mid-write of `ApplyExternalDeleteListAsync`** — a single `SaveChanges` makes it atomic on real SqlServer. On InMemory, partial state could be observed but the provider doesn't support transactions. Same caveat documented in slice 2 for `ApplyExternalCreateAsync`. Formal outbox remains debt.
- [x] **Concurrent local write during the pull's 2nd pass** — the `GetMappedTodoListsAsync` query snapshots state at T1; `ApplyExternalDelete*Async` runs at T2. If the user edits the list between T1 and T2, that edit is silently lost (Mirror policy). Acceptable under the single-instance assumption.
- [x] **Push orphan-DELETE fails (5xx)** — the `TodoList` no longer exists locally (deleted by the user), but the mapping persists to retry on the next tick. Same pattern as slice 3 for items. Test `PushTodoListsAsync_OrphanListMappingExternal500_FailsAndKeepsMapping`.
- [x] **`LocalUpdatedAtAtSync == null` in legacy mapping + external disappeared** — to avoid noisy Warning on mappings that never received a snapshot (legacy/manual seeds), the check is `LocalUpdatedAtAtSync.HasValue && CurrentLocalUpdatedAt > LocalUpdatedAtAtSync.Value`. Null → no Warning. Test `PullTodoListsAsync_MappedExternalDisappearedWithLocalUpdatedAtAtSyncNull_DoesNotLogWarning`.
- [x] **`UpdateAsync` test of TodoListItemService is weak** — slice 5 closes it with `Thread.Sleep(1)` before the `UpdateAsync` and a strict assert `> before` (instead of `>=`). Confirmed that the test now fails if `item.UpdatedAt = DateTime.UtcNow` is removed from the service. The cross-cutting `TimeProvider`/`IClock` abstraction remains as explicit debt in Areas for Improvement (out of scope for the hardening slice).
- [x] **Outbox Create event with local deleted mid-flight** (race between Create CRUD and event drain on next tick) — the dispatch verifies `GetLocalTodoListByIdAsync(EntityId)`; if null, marks processed without action. The subsequent Delete event (which will have been emitted upon deletion) will be processed afterward and as there is no mapping, will also be a no-op. End-state: zero side effects, both events processed. Test `PushTodoListsAsync_OutboxCreateEvent_LocalDeletedMidFlight_MarksProcessedNoOp`.
- [x] **Outbox Create event with mapping already existing** (typical case: pull adopted the local before push processed the event, or idempotent re-drain) — the dispatch checks `SyncMappings.Where(EntityType==TodoList && LocalId==EntityId).FirstOrDefault`; if it exists, marks processed without POST. Idempotent. Test `PushTodoListsAsync_OutboxCreateEvent_AlreadyMapped_SkipsPostAndMarksProcessed`.
- [x] **Outbox Update event without mapping** (item never synced but received a local Update) — structured Warning log + mark processed. PATCH not attempted. For items: test `PushTodoListItemsAsync_OutboxUpdateEvent_NoMapping_SkipsAndMarksProcessed`.
- [x] **Outbox Delete event without mapping** (entity never synced, the user created and deleted it before the first successful sync) — marks processed without external DELETE. Test `PushTodoListsAsync_OutboxDeleteEvent_NoMapping_MarksProcessedNoOp`.
- [x] **Outbox Delete event with external already deleted (404)** — 404 grace treats as resolved, cleans up the local mapping + marks event processed. Tests `PushTodoListsAsync_OutboxDeleteEvent_404Grace_StillCleansMapping` and `PushTodoListItemsAsync_OutboxDeleteEvent_404Grace_StillCleansMapping`.
- [ ] **Crash between the two `SaveChanges` of the Create in `TodoListService.CreateAsync`** (TodoList persists without OutboxEvent, EntityId known but event not flushed) — the TodoList is left without an event. Next tick, Phase A drain of the outbox doesn't find the event. Phase B legacy (anti-join `GetUnmappedTodoListsAsync`) detects it and pushes as before. Net effect: zero regression vs. pre-slice-6. Documented as why Phase B remains active in slice 6.
- [x] **Outbox Create event for TodoListItem in already-pushed list** (limitation inherited from slice 3: external contract does not expose isolated POST of items) — the dispatch logs a structured Warning + marks processed. Identical end-state to the pre-slice-6 legacy flow (`unmappedWithMappedParent` warn-and-skip). Test `PushTodoListItemsAsync_OutboxCreateEvent_UnmappedItem_LogsWarningAndMarksProcessed`.
- [ ] **Race between push-side Update event of item and pull-side PATCH of the same item** — both can fire PATCH to the same item in the same tick if there is a pending outbox Update event and `local newer than external` evaluates true on the pull. The second PATCH is no-op (same state) but does an extra round-trip. Acceptable; slice 7+ may mitigate it (skip pull-PATCH if there is a pending outbox event for the entity).
- [x] **OutboxEvents table grows without retention** — slice 7 introduces periodic cleanup via `PurgeProcessedOutboxEventsAsync`. Retention configurable via `SyncOptions.OutboxRetention` (default 7 days). The 5th phase of the tick deletes events with `ProcessedAt != null && OccurredAt < UtcNow - retention`. Test `PurgeProcessedOutboxEventsAsync_OnlyOldProcessedDeleted` covers the predicate; `ExecuteAsync_OutboxRetention_LogsPurgedCount` covers the tick wiring.
- [x] **`OutboxRetention <= TimeSpan.Zero` disables the cleanup phase** — log Debug + skip. Test `ExecuteAsync_OutboxRetentionDisabled_SkipsPhase` verifies the strict-mock dbContext is never invoked.
- [x] **Cleanup on empty outbox table** — returns 0, no error. InMemory path: `ToListAsync()` returns empty list, early return. Relational path: `ExecuteDeleteAsync` issues DELETE with WHERE matching nothing. Test `PurgeProcessedOutboxEventsAsync_EmptyTable_ReturnsZero` covers it.
- [x] **`OutboxBatchSize=N` with `M > N` pending events** — only the first `N` (FIFO) are drained this tick; the rest in subsequent ticks. Test `PushTodoListsAsync_OutboxBatchSize_RespectsLimit` and `PushTodoListItemsAsync_OutboxBatchSize_RespectsLimit` verify with already-mapped lists / unmapped item events so Phase B's anti-join finds nothing and only Phase A drains.
- [x] **`OutboxBatchSize <= 0`** — `Take(0)` returns empty (no events drained); `Take(-1)` undefined per LINQ spec. Slice 8 closed the `=0` boundary with `PushTodoListsAsync_OutboxBatchSizeZero_DrainsNothingAndDoesNotCrash` (Phase A no-op + Phase B legacy fallback covers the rest). Negative values remain undefined per spec; formal `IValidateOptions<SyncOptions>` would close it (still listed in Areas for Improvement).
- [x] **Cleanup throws mid-tick** — log Error + the 4 sync phases of the tick already finished. Subsequent ticks continue. Test `ExecuteAsync_OutboxRetentionThrows_OtherPhasesUnaffected` verifies all 4 phases fired before the cleanup throw, and the cleanup error is logged independently.

## Areas for Improvement

Final-form roadmap at v1 closeout. Items already covered in
[Out of Scope & What It Would Take](#out-of-scope--what-it-would-take) link
there rather than restating the analysis. Slice-by-slice context for delivered
items lives in the [Decision Log](#decision-log).

### Delivered (for traceability)

Slices 2–10 are recorded in the Decision Log. Live capabilities are summarized
in the [Capability matrix](#capability-matrix); resolved edge cases in
[Edge Cases](#edge-cases).

### Backlog with full analysis

These items have an entry in *Out of Scope* with options, tradeoffs, and
decision criteria. Listed here only as pointers:

- Multi-instance horizontal scaling → [§01](#01-multi-instance-horizontal-scaling)
- Push-side PATCH for `TodoList.Update` → [§02](#02-push-side-patch-for-todolistupdate-events)
- Mirror-policy alternatives (incl. soft-delete tombstones / external restore) → [§03](#03-mirror-policy-alternatives)
- Bounded concurrency on outbox drain → [§04](#04-bounded-concurrency-on-outbox-drain)
- `TimeProvider` / `IClock` cross-cutting → [§05](#05-timeprovider--iclock-cross-cutting)
- `IValidateOptions<SyncOptions>` → [§07](#07-ivalidateoptionssyncoptions)
- `OutboxEvent.Payload` usage → [§08](#08-outboxeventpayload-usage)
- Telemetry / metrics → [§09](#09-telemetry--metrics)
- `SyncRunResult.Pushed` → `Processed` rename → [§10](#10-syncrunresultpushed-rename--processed)

### Contract-blocked (external API)

Cuts blocked on upstream contract changes, analyzed in [§06](#06-contract-bound-limitations):

- `POST /todolists/{listId}/todoitems` — would unblock new-item-in-pushed-list sync (today: structured Warning + skip).
- Re-parenting `TodoListItem` between lists — requires mutable `parent_list_id` in `PATCH`.
- `source_id` literal-preservation as a contract guarantee (today: inferred).

### Local debt (no §-entry yet)

- **Deprecate Phase B legacy of push.** Once production has been on Outbox for N days without legacy hits, the anti-join unmapped + orphan-mappings becomes dead code. Independent of outbox retention.
- **`OutboxRetention` cutoff bound to `DateTime.UtcNow` of the local host.** Drift between hosts could purge events earlier or later than configured. Composes with §01 (C4) and §05.
- **`SyncRun.Error` column exists but is unwritten.** Populate with an aggregated last-error summary when offline forensics are needed.
- **`ExternalApiException.Body` without cap.** A large 5xx HTML body is allocated whole. Reasonable cap (~2 KB) in the client.
- **`SyncOptions` without validation** (`Interval = 0`, negative `StartupDelay`). Subsumed by §07 once that lands.
- **`db.Database.Migrate()` only in Development.** Inherited from the repo. In Production, migrations must be applied out-of-band before bringing up the app.
- **`GET /todolists` without pagination / filters on the external side.** Full scan every tick. OK for challenge volumes; if volume grows, requires either an external contract change (`?modified_since=…`) or a local cache of last seen `updated_at`.
- **`ApplyExternalCreateAsync` with two `SaveChanges`.** InMemory provider does not support transactions; the formal outbox closes it at the root. Microscopic risk, documented as Edge Case.

## Assumptions

Explicit assumptions from slice 1-4 — each one answers "what breaks if it isn't true?".

- **The external's `source_id` is preserved verbatim.** If the external modifies/normalizes it, we lose bidirectional correlation and future pulls (slice 2) create local duplicates instead of detecting locally-originated entries.
- **External IDs are strings up to 64 chars.** If the external returns something longer (URL-based, composite keys, etc.), inserts fail in SqlServer with a truncation error (visible in sync logs). UUID defaults (36 chars) — fits with margin.
- **The external has no aggressive rate limiting, no auth.** Spec says "All endpoints do not require authorization". If this changes, the typed client needs interceptors for tokens; the pipeline already handles 429 with retry.
- **Local TodoLists are created ONLY by the local API (not by another source).** Push assumes any list without a mapping is a candidate to sync. If in the future there is a second creation source, we need a flag to distinguish.
- **Single-instance of the host.** The background service has no leader election or distributed locking. Multiple instances running in parallel would try to push the same lists; the unique index on `SyncMappings` prevents corruption but the second instance would lose with unhandled `DbUpdateException`.
- **`Sync:Interval = 60s` is reasonable** for expected volumes (lists created via user interfaces, not by batch). If throughput rises significantly, consider lower interval or event trigger (future slice).
- **The external base address ends without a trailing slash** (default `http://localhost:8080`). The typed client composes with relative path `"todolists"`. If someone configures `BaseAddress = "http://localhost:8080/api"` (with path segment), the resolve concatenates incorrectly and the requests go to `http://localhost:8080/todolists` losing `/api`. Mitigation: document in README or validate in `ExternalApiOptions`. Low risk in the challenge because the spec uses root.
- **The external server preserves `source_id` literally.** Slice 2 depends on this for the pull adoption to work: we send `local.Id.ToString()` as `source_id` in the push and, post-crash mid-write, we expect the GET to return it intact for reconciliation. If the server normalizes/transforms it, the orphans are not adopted and locals are duplicated on the next pull (CASE C). The OpenAPI spec does not document normalization; we assume literal preservation.
- **Tie-break on exact `updated_at` tie goes to the external (`>=` rule).** The server is authoritative for its timestamps (it generates them). In the (rare) case where two clients hit the same fraction of a second, we lose the local change. If the user re-edits afterward, the next push will overwrite it. No notification to the user of the applied external change.
- **UTC clock of the external server and the local "reasonably" aligned.** Without formal time sync (NTP, etc.), drift of seconds is tolerable; drift of minutes could trigger erroneous last-write-wins (e.g., local would win even though the external already applied a more recent real-time change). We assume a homogeneous cluster or NTP on all hosts.
- **The `Idempotency-Key` header we send is not processed by the server today** (not documented in the OpenAPI spec). We send it forward-compatible. If it never supports it, no harm — the gap closure comes from the adoption in pull.
- **`GET /todolists` is the single source of truth for "what exists" externally.** Slice 4 detects external deletes by absence in this GET. If the GET is inconsistent (undocumented partial pagination, stale cache, race with a concurrent POST/DELETE), a mapping could be improperly deleted locally. The current spec does not document pagination or filters; we assume atomicity of the response.
- **External DELETE is idempotent and symmetrically recoverable.** A retry with the same ExternalId after 5xx should reproduce the same end-state (deleted list). The 404 grace covers the "already deleted" case.
- **Mirror policy is the accepted policy for "external disappeared + local with edits".** The local loses its edits without syncing; the structured Warning records them for forensics. If in the future the model prohibited it (e.g., undo requirements), preserve+re-push or tombstones would be the alternative.
- **`OutboxRetention` cutoff is computed against `DateTime.UtcNow` of the local host.** Slice 7 assumes the local clock is monotonic and aligned with the clocks that wrote the OutboxEvents (single-instance, NTP). Drift of minutes between hosts could purge events earlier or later than the configured window. Same caveat as LWW.
- **The cleanup predicate uses `OccurredAt < cutoff` (not `ProcessedAt < cutoff`).** Assumes processing happens within a tick of the Occurred (typical case: event written at T, drained at T+60s). On a recovery scenario where a 1000-event backlog gets drained over many ticks, events processed-but-recently-occurred remain alive a tick longer; acceptable.

---

## Sync v1 Closeout

One-page synthesis of the sync engine after slices 0–8 — the answer to "what does it do, what doesn't it do, how do I operate it, what would I change next". For depth, follow links into the rest of this file.

### Capability matrix

| Entity | Operation | Push (local → external) | Pull (external → local) |
|---|---|---|---|
| `TodoList` | Create | ✓ Outbox drain (Phase A) + legacy anti-join (Phase B) | ✓ Adopt via `source_id` or create local (CASE C) |
| `TodoList` | Update | ⚠ Push no-op — Pull side handles PATCH via LWW reconciliation | ✓ PATCH external if local newer; remote-wins if external newer; tie → external |
| `TodoList` | Delete | ✓ Orphan-mapping anti-join → external DELETE + 404 grace | ✓ Mirror + Warning (cascade local items + mappings) |
| `TodoListItem` | Create | ⚠ Embedded in parent POST only — locally-new items in already-mapped lists log Warning and are skipped (external contract limitation) |  ✓ Adopt via `source_id` or create local |
| `TodoListItem` | Update | ✓ Outbox drain → external PATCH; LWW reconciliation on pull | ✓ PATCH external if local newer; remote-wins if external newer; tie → external |
| `TodoListItem` | Delete | ✓ Orphan-mapping anti-join → external DELETE + 404 grace | ✓ Mirror + Warning (per-item) |

Status endpoint (`GET /api/sync/status`) surfaces last run per (entity × direction) pair plus pending outbox count.

### Configuration cheat-sheet

| Option | Default | When to change |
|---|---|---|
| `Sync:Interval` | `00:01:00` (60s) | Lower for fresher data; higher to reduce CPU/network if changes are rare |
| `Sync:StartupDelay` | `00:00:05` (5s) | Increase if other startup work is slow and you don't want a tick mid-init |
| `Sync:Enabled` | `true` | `false` disables the entire engine without removing code (maintenance mode) |
| `Sync:OutboxBatchSize` | `1000` | Lower if memory-constrained; higher to drain backlogs faster (validated `> 0`; `<= 0` is documented graceful no-op) |
| `Sync:OutboxRetention` | `7.00:00:00` (7d) | Lower under disk pressure; higher for longer forensic windows; `<= 0` disables Phase 5 cleanup |
| `ExternalApi:BaseAddress` | `http://localhost:8080` | Set to the real external endpoint in non-dev environments |
| `ExternalApi:RetryMaxAttempts` | `3` | Tune to observed transient failure rate (Polly exp + jitter applies between attempts) |
| `ExternalApi:PerAttemptTimeoutSeconds` | `10` | Lower for tighter SLO; higher if the external is known to be slow |

### Failure modes covered

| Scenario | Mitigation | Log signature |
|---|---|---|
| External 5xx / 408 / 429 | Polly exponential retry + jitter, then circuit breaker | `HttpClient` resilience pipeline logs |
| External non-transient 4xx | Per-entry `try/catch` → `failed++`, status `Partial`, run continues | `LogError` per-entry in `TodoListSyncService` / `TodoListItemSyncService` |
| External `DELETE` returns 404 | 404 grace: treat as already resolved, clean local mapping | `LogInformation` "treating 404 as resolved" |
| Circuit breaker open | Subsequent attempts fast-fail until half-open probe | Polly state-change logs |
| Per-attempt timeout | Polly cancels and (if budget remains) retries | Polly logs |
| Push crash mid-write | Pull adopts external orphan via `source_id`; Phase B legacy anti-join also recovers unmapped locals | `LogWarning` "adopting external by source_id" |
| Outbox event for already-mapped entity | Skip POST, mark processed (idempotent re-drain) | `LogDebug` "outbox event already mapped, marking processed" |
| Outbox event for deleted local (Create or Update) | No-op skip, mark processed | `LogDebug` "outbox event source missing, marking processed" |
| Outbox event for unmapped item with mapped parent | Warning + mark processed (slice 3 contract limitation) | `LogWarning` "cannot sync new item — external lacks POST endpoint" |
| External list disappeared (had local edits) | Mirror + Warning, cascade delete (list, items, mappings) | `LogWarning` "external disappeared, cascading local delete" |
| Outbox grows unbounded | Phase 5 retention purge with configurable cutoff | `LogInformation` "purged N processed outbox events" |
| Misconfigured `OutboxBatchSize = 0` | `Take(0)` empty → Phase A no-op; Phase B legacy fallback still drains | (silent — covered by `PushTodoListsAsync_OutboxBatchSizeZero_DrainsNothingAndDoesNotCrash`) |
| `OutboxRetention <= 0` | Phase 5 skipped with `LogDebug` | `LogDebug` "outbox retention disabled" |

### Operational runbook

1. **Check status.** `curl http://localhost:5054/api/sync/status` (or whatever port Kestrel binds). Look for: stale `StartedAt`, non-`Succeeded`/`Partial` status, rising `PendingOutboxCount`, mismatched `Config` vs. expected.
2. **Force a manual sync.** `POST /api/sync/run` runs all 4 sync phases inline and returns the per-phase result. Useful after an incident to verify external connectivity without waiting for the next tick.
3. **Inspect logs.** Filter on `SyncBackgroundService`, `TodoListSyncService`, `TodoListItemSyncService`, `ExternalTodoListClient`. Common patterns: `circuit breaker opened` → external is down; retry exhaustion → external is slow or returning persistent errors; `adopting external by source_id` → recovery from a prior crash.
4. **Purge the outbox manually.** Set `Sync:OutboxRetention` smaller in `appsettings.json` (e.g., `00:01:00` for 1 minute), restart. Phase 5 of the next tick removes processed events older than the cutoff. Restore the value when done.
5. **Disable the engine without code changes.** Set `Sync:Enabled = false` in config (or env var `Sync__Enabled=false`), restart. `SyncBackgroundService.ExecuteAsync` returns immediately, no phases run, the API stays serving CRUD (which keeps writing outbox events that will drain when re-enabled).

### Frozen limitations (v1)

These are intentional cuts. Each has a known "what would unfreeze it":

| Limitation | What would unfreeze it | Detail |
|---|---|---|
| Locally-new `TodoListItem` in already-pushed list does **not** propagate to external | External adds `POST /todolists/{listId}/todoitems` (formal suggestion in Areas for Improvement) | [→ §06](#06-contract-bound-limitations) |
| Re-parenting items between lists is unsupported | Both local DTO and external contract would need to expose mutable `TodoListId` | [→ §06](#06-contract-bound-limitations) |
| Single-instance assumption (one host running the BackgroundService) | Distributed leader election (Redis lock, Postgres advisory lock, etc.) or outbox partitioning by host | [→ §01](#01-multi-instance-horizontal-scaling) |
| Mirror policy on external delete (local edits lost silently) | Product decision to preserve local with conflict UI; or external exposing tombstone/restore endpoints | [→ §03](#03-mirror-policy-alternatives) |
| `OutboxBatchSize <= 0` (negative) is undefined behavior | `IValidateOptions<SyncOptions>` fail-fast (slice 8+ backlog) | [→ §07](#07-ivalidateoptionssyncoptions) |
| Push-side `PATCH` of `TodoList.Update` is a no-op (only pull-side handles the asymmetry) | Slice 9 backlog: design race semantics vs. pull-side PATCH first, then implement | [→ §02](#02-push-side-patch-for-todolistupdate-events) |

### What's documented but not implemented (acceptance, no test)

These edge cases in `## Edge Cases` are marked `[ ]` — accepted because the cost of mitigation outweighs the residual risk for v1. Each is documented in-line above with rationale:

- Crash between the two `SaveChanges` of `ApplyExternalCreateAsync` (microscopic window; pull would re-create on next tick → local duplicate).
- Crash between `TodoListService.CreateAsync` and the OutboxEvent flush (Phase B legacy anti-join recovers).
- External returns `id` > 64 chars (assumption: UUIDs ≤ 36 chars).
- Concurrent execution from two API hosts (single-instance assumption).
- External does not preserve `source_id` literally (assumption documented).
- Race between push-side outbox `Update` event and pull-side PATCH of the same item (extra round-trip, idempotent end state).
- `Take(-1)` for `OutboxBatchSize` (LINQ-undefined; covered by validator backlog above).

---

## Out of Scope & What It Would Take

This section expands [Frozen limitations (v1)](#frozen-limitations-v1) by explaining, for each cut, **what dimensions an implementation would touch, what technical options exist, what tradeoffs each carries, and how it would be tested**. It is not an execution plan — it is the prior analysis that would prevent deciding badly under pressure if v2 happened.

| # | Topic | Depth | Driver |
|---|---|---|---|
| §01 | [Multi-instance horizontal scaling](#01-multi-instance-horizontal-scaling) | High | Hard scaling cap of single-host assumption |
| §02 | [Push-side PATCH for `TodoList.Update` events](#02-push-side-patch-for-todolistupdate-events) | Medium | Asymmetry between push and pull update paths |
| §03 | [Mirror-policy alternatives](#03-mirror-policy-alternatives) | Medium | Product decision: silent local edit loss |
| §04 | [Bounded concurrency on outbox drain](#04-bounded-concurrency-on-outbox-drain) | Medium | Throughput when backlog spikes |
| §05 | [`TimeProvider` / `IClock` cross-cutting](#05-timeprovider--iclock-cross-cutting) | Medium | Deterministic LWW tests + clock drift hygiene |
| §06 | [Contract-bound limitations](#06-contract-bound-limitations) | Low | External API contract gaps |
| §07 | [`IValidateOptions<SyncOptions>`](#07-ivalidateoptionssyncoptions) | Low | Hardening |
| §08 | [`OutboxEvent.Payload` usage](#08-outboxeventpayload-usage) | Low | Snapshot replay for create-deleted-mid-flight |
| §09 | [Telemetry / metrics](#09-telemetry--metrics) | Low | Operability beyond structured logs |
| §10 | [`SyncRunResult.Pushed` rename → `Processed`](#10-syncrunresultpushed-rename--processed) | Low | Naming hygiene |

Each sub-section follows the same six-header template: **Status quo · Dimensions touched · Options · Operational implications · Test strategy · Decision criteria**.

---

### 01. Multi-instance horizontal scaling

**Status quo.** Exactly one process runs `SyncBackgroundService` and `OutboxBroadcastService`. Neither uses a distributed lock or leader election. The assumption is documented in `## Assumptions` (single-instance) and as an Edge Case (concurrent execution from two API hosts). With two hosts the sync ticks would step on each other: the unique index on `SyncMappings(EntityType, LocalId)` prevents corruption but the second host loses with an unhandled `DbUpdateException`. The broadcaster's in-memory cursor would also drift between hosts.

**Dimensions touched.** Five sub-problems, each independently decidable:

**(C1) Coordination of `SyncBackgroundService`.** Pick one of:

| Strategy | How it works | Pros | Cons |
|---|---|---|---|
| **SqlServer `sp_getapplock`** *(recommended for this stack)* | `EXEC sp_getapplock @Resource='sync-tick', @LockMode='Exclusive', @LockTimeout=0` at tick start; release at tick end | Reuses the existing transactional backend; clear "session-scoped" semantics; zero infra additions | Requires raw SQL via `DbContext.Database.ExecuteSqlRawAsync`; SqlServer-specific |
| **Redis Redlock** | `SET key NX PX <ttl>` with TTL > tick duration | Mature .NET library (`RedLock.net`); fast-fail on contention | New infra (Redis); Kleppmann's well-known critique applies — must commit to TTL and (ideally) fencing tokens |
| **Kubernetes Lease** | `coordination.k8s.io/Lease` resource via a sidecar or `LeaderElector` library | Native if already on k8s; no extra infra | Couples runtime to k8s; needs RBAC; not portable to VM/bare-metal |
| **Outbox partitioning** | Each host claims `OutboxEvents.Id MOD N == hostIndex` | Linearly parallelizable; no leader | Needs stable host-index assignment (another coordination primitive); legacy Phase B anti-join becomes ambiguous (who owns the unmapped backfill?) |

**(C2) `OutboxBroadcastService` fan-out.** Three sub-problems that the doc must keep separate, because each can be solved independently:

- **(a) SignalR backplane.** Without one, a client connected to host A never sees events published by host B. Solution: `Microsoft.AspNetCore.SignalR.StackExchangeRedis` — one DI line: `services.AddSignalR().AddStackExchangeRedis(redisConn)`. Cost: a Redis dependency to monitor (drop rate, latency).
- **(b) Cursor strategy.** Today the cursor is `MAX(OutboxEvents.Id)` in process memory. With N broadcasters: **(i)** shared cursor in Redis (race on advance); **(ii)** per-host cursor + client-side dedupe (simpler server, deduper cost on the client — note the ring-buffer is **already** documented in `docs/realtime-frontend-integration.md` as the React handoff, so this is the path of least resistance); **(iii)** partition the outbox by host (compatible with C1's "outbox partitioning" option).
- **(c) Cross-instance dedupe.** With the Redis backplane SignalR replicates broadcasts but does not dedupe by `eventId`. If two broadcasters publish the same event, clients receive two notifications with identical payloads. Mitigation: the existing client ring-buffer of recent `eventId`s already in `docs/realtime-frontend-integration.md` covers this without server-side change.

**(C3) Outbox writes with N hosts — race conditions.** `OutboxEvents.IdempotencyKey` is unique → it protects against double-publishing the same intent. It does **not** protect two hosts from pushing the same `TodoList` in parallel: both call POST, both receive different `ExternalId`s, and the second insert into `SyncMappings` fails with `DbUpdateException` on the unique index. Two mitigations, additive:

- Pessimistic: solve via C1 (only one host runs the tick at a time).
- Optimistic: keep C1 best-effort and `try/catch DbUpdateException` in the dispatch with a "treat as already-mapped, mark event processed" recovery. Costs an extra POST round-trip but no data loss. For a system where C1 is "good enough" (sp_getapplock with timeout 0), the optimistic catch is the safety net for the rare race.

**(C4) Clock drift / `OutboxRetention` cutoff.** Today `DateTime.UtcNow` is read on the host running phase 5. With N hosts whose clocks differ by minutes the cutoff slides. Three options:

- Inject `TimeProvider.System` (aligns naturally with §05) — zero-functional change, opens deterministic tests.
- Compute the cutoff on the host that holds the C1 lock — single-writer of cleanup → single clock.
- Use `MAX(OutboxEvents.OccurredAt) - retention` as the cutoff — clock-independent, but opens an attack surface if a future host with a wildly skewed clock writes an `OccurredAt` far in the future.

**(C5) Test strategy in CI without prod infra.** The actual race between hosts can't be reproduced with mocks. Pragmatic approach: a separate integration suite tagged `Multihost`, gated off the default `dotnet test` run, that uses Testcontainers (Redis + SqlServer) and spins up two `WebApplicationFactory<Program>` instances with DI overrides. Run on a nightly or merge-gate CI lane, not on every PR.

**Options (top-level recommendation).** No single answer fits all stacks. For SqlServer-only deployments: **(C1) sp_getapplock + (C2) SignalR Redis backplane + (b) per-host cursor with client dedupe + (C3) optimistic catch as safety net + (C4) inject `TimeProvider` + (C5) Testcontainers-based Multihost suite.** For k8s-native deployments substitute (C1) with the k8s Lease library and the rest stays.

**Operational implications.** New monitoring surface: lock contention rate (C1), backplane drop rate and latency (C2a), SignalR reconnect storms after a leader transition (C2b), `DbUpdateException` recovery counter (C3). Failover behavior: when the leader host dies, expect a gap of up to one tick (≤60 s by default) before the next host acquires the lock. Acceptable because the sync is already eventually consistent.

**Test strategy.** Unit test the C1 wrapper (`IDistributedLock.AcquireAsync` interface) with both a real backend (Testcontainers SqlServer with `sp_getapplock`) and a `NoopLock` for the single-instance default. Unit test the C3 optimistic catch with a deliberate duplicate insert. Multi-host integration covered in C5.

**Decision criteria.** Move out of "out of scope" when **any** of the following holds: production runs more than one host, the average tick duration exceeds 30 s (so a stuck tick blocks the next), or the operator needs zero-downtime deploys (rolling restart with two pods overlap by definition).

---

### 02. Push-side PATCH for `TodoList.Update` events

**Status quo.** When `TodoListService.UpdateAsync` runs, an `OutboxEvent` of type `Update` is written. The push phase **marks it processed without doing anything** — the comment in `TodoListSyncService` is explicit: "PATCH for `TodoList.Update` is the pull side's responsibility". The pull side handles list-name divergence via LWW reconciliation. Documented in Frozen Limitations.

**Dimensions touched.** `TodoListSyncService.DispatchOutboxUpdateAsync` (a no-op branch for lists today, real PATCH for items). Adding a real PATCH would mean: external client call (`UpdateTodoListAsync` already exists), update `LocalUpdatedAtAtSync` and `ExternalUpdatedAtAtSync` snapshots on the mapping, decide what happens if the pull-side then sees `local newer than external` and tries to PATCH again the same tick.

**Options.** Three:

- **Add the PATCH, keep the pull-side PATCH unchanged.** Both can fire in the same tick. Second PATCH is a no-op (same state) but burns a round-trip. Already documented as Edge Case (race between push-side Update and pull-side PATCH).
- **Add the PATCH, suppress the pull-side PATCH** when an outbox `Update` event for the same entity is pending. Adds a query-per-pull-entity (`PendingOutboxFor(entityId)`) — correctness gain at minor cost.
- **Status quo + tighten the pull-side PATCH.** Today is acceptable; the slice 5 hardening proved it self-heals on the next tick.

**Operational implications.** With option 1, expect ~2× PATCH count per tick when an entity has a pending Update event AND the LWW evaluates `local newer`. With option 2, expect a small CPU cost on every pull entity (an `EXISTS` query against the outbox).

**Test strategy.** Unit tests already cover the pull-side PATCH and the no-op branch. New tests: outbox `Update` event with mapping and external state stale → PATCH happens once, mapping snapshots updated; race scenario where an Update event AND pull-side detects local-newer → assert exactly one PATCH per option chosen.

**Decision criteria.** Move out of scope when metrics show enough push-side Update events per tick to justify the work. Today the count is observed-zero in dev — there's no signal.

---

### 03. Mirror-policy alternatives

**Status quo.** When the pull detects an external `TodoList` or `TodoListItem` has disappeared (absent from `GET /todolists`), the local is cascade-deleted. If the local had been edited after the last sync (`LocalUpdatedAt > LocalUpdatedAtAtSync`), a structured Warning logs the lost change before the delete. No persistence of the pre-delete state, no user notification. Documented in Frozen Limitations (mirror policy on external delete).

**Dimensions touched.** `TodoListSyncService.ApplyExternalDeleteListAsync` (and the item analogue), the structured Warning emission, and any new persistence (tombstone columns or a `DeletedTodoLists` table). Cross-cuts the API contract because if a tombstone is preserved, `GET /todolists` either filters it out (silent) or surfaces it (new fields).

**Options.** Three:

- **Tombstone preservation.** Add `IsDeleted` + `DeletedAt` columns; soft-delete instead of hard-delete; the API filters them out by default with an opt-in `?includeDeleted=true`. The user can recover deletions via a "trash" UI. Requires app-side support and migration.
- **Conflict UI delegation.** Don't delete — write a `SyncConflict` row and surface it via a new endpoint (`GET /api/sync/conflicts`). The user resolves manually (keep local, accept remote delete). Highest fidelity, highest UX cost.
- **Restore-on-reappearance.** If the same external `id` (or the same `source_id`) reappears in a later GET, undelete the local. Cheap to add but requires the `IsDeleted` column.

**Operational implications.** Tombstones grow the table monotonically — needs a retention policy (analogous to outbox retention). Conflict UI implies an unbounded backlog if the user doesn't resolve. Restore-on-reappearance assumes external doesn't re-use IDs after delete (a contract claim worth adding to `## Assumptions`).

**Test strategy.** Snapshot test the structured Warning emitted today (it's the formal record of the lost edit). Integration test the new flow under each option: external disappears → local edits visible → resolution applied. The existing `WireMock` setup already supports this.

**Decision criteria.** Move out of scope when product confirms that silent loss is unacceptable (today it's accepted with the Warning as audit trail).

---

### 04. Bounded concurrency on outbox drain

**Status quo.** Phase A drains outbox events one-by-one in a serial `foreach` loop within `TodoListSyncService.PushTodoListsAsync` (and the item analogue). Each event makes one external call. Throughput is `tick_duration / (avg_external_latency × event_count)`. With a 60s tick, 200ms p50 latency, and a 1000-event backlog, the engine processes ~300 events/tick → 3.3 ticks to drain. Acceptable for current volumes, painful for spikes.

**Dimensions touched.** The drain loop, `IExternalTodoListClient` (which already uses Polly with circuit breaker), and the order semantics: today FIFO per `OccurredAt` is a *property of the loop*, not a contract — concurrent dispatch breaks it.

**Options.** Three:

- **`System.Threading.Channels.BoundedChannel<OutboxEventRecord>`** with paginated producer (keyset by `Id`) + N consumer tasks (configurable, default 8) + Polly bulkhead per consumer. Already listed in `## Areas for Improvement` as the slice 8 candidate. Loses strict FIFO **across entities** but preserves it per-entity if the partition key is `(EntityType, EntityId)`.
- **`Parallel.ForEachAsync` over batches.** Simpler, no channel. Bounds via `MaxDegreeOfParallelism`. No backpressure if consumers stall — the producer keeps reading.
- **Status quo + larger `OutboxBatchSize`.** Workaround that scales linearly with memory but doesn't reduce wall-clock per tick.

**Operational implications.** New config: `Sync:OutboxConsumerConcurrency` (default 1 for backwards compat). Per-entity FIFO must be advertised explicitly: cross-entity ordering is no longer guaranteed and consumers downstream must not assume it. The circuit breaker still protects the external — concurrency above the breaker's threshold just hits "half-open" faster.

**Test strategy.** Stress test with a synthetic backlog of N>1000 events → assert tick wall time decreases linearly with concurrency until the external becomes the bottleneck. Order test: emit a Create then an Update for the same entity, assert dispatch order (Create before Update) regardless of consumer assignment.

**Decision criteria.** Move out of scope when an observed backlog exceeds 2× the single-tick capacity at p95 latency. Until then, the simple loop is correct and trivial to reason about.

---

### 05. TimeProvider / IClock cross-cutting

**Status quo.** `DateTime.UtcNow` is called directly in `TodoListService`, `TodoListItemService`, `TodoListSyncService`, `TodoListItemSyncService`, and `SyncBackgroundService` (for the phase 5 retention cutoff). This forced one ugly `Thread.Sleep(1)` in the slice 5 hardening to write a strict-monotonic test on `UpdateAsync`. Listed in `## Areas for Improvement` as the slice-X+ candidate.

**Dimensions touched.** Every site that reads "now". The .NET 8 `TimeProvider` abstraction is the obvious pick — it ships in the BCL and has a `FakeTimeProvider` companion in `Microsoft.Extensions.TimeProvider.Testing`.

**Options.** Two real ones:

- **`TimeProvider.System` injected via DI**, all `DateTime.UtcNow` replaced by `_timeProvider.GetUtcNow().UtcDateTime`. Touches ~12 call sites; each is a one-line change. Tests get `FakeTimeProvider` and stop sleeping.
- **Roll a custom `IClock`** with `UtcNow` only. Cheaper to wire than `TimeProvider`'s richer surface, but reinvents what the BCL already gives us. Not recommended.

**Operational implications.** Zero behavioral change in production (`TimeProvider.System` reads the same clock). Test runtimes drop because no more `Thread.Sleep`. The `OutboxRetention` cutoff in C4 of §01 starts to compose cleanly with multi-host clock alignment.

**Test strategy.** Replace the `Thread.Sleep(1)` in `TodoListItemServiceTests.UpdateAsync_RowExists_BumpsUpdatedAt` with a `FakeTimeProvider.Advance(TimeSpan.FromMilliseconds(1))`. Add a deterministic LWW tie test that's currently not writable (exact `==` on `UpdatedAt` between local and external).

**Decision criteria.** Move out of scope when a developer needs to write a deterministic LWW test or when §01.C4 is being implemented (the alignment becomes structural).

---

### 06. Contract-bound limitations

Three coupled cuts that are blocked on the **external API contract**, not on local code: (a) locally-new `TodoListItem` in already-pushed list, (b) re-parenting an item between lists, (c) `source_id` literal preservation.

**Status quo.** (a) Logs a structured Warning and skips — the external lacks `POST /todolists/{listId}/todoitems`. (b) The `UpdateTodoListItem` DTO doesn't expose `TodoListId`; the external `UpdateTodoItemBody` doesn't either. (c) Slice 2 adoption depends on the external echoing `source_id` byte-for-byte; if it normalizes, adoption fails and locals duplicate on next pull.

**Dimensions touched.** External API contract. Locally there's nothing to implement until the upstream changes. The formal upstream suggestion for (a) is already in `## Areas for Improvement`.

**Options.** None local. Possible upstream changes:

- (a) Add `POST /todolists/{listId}/todoitems` with a body matching the existing item shape.
- (b) Allow `PATCH /todolists/{listId}/todoitems/{itemId}` to accept `parent_list_id` (or equivalent).
- (c) Document `source_id` preservation as a contract guarantee — currently inferred, not promised.

**Operational implications.** When unblocked, item creation in an already-mapped list propagates instead of warn-skip; re-parenting gets a defined policy; `source_id` preservation becomes an SLA, not a hopeful assumption.

**Test strategy.** Each unblocking would land its own slice with TDD per the project convention (xUnit + WireMock + InMemory).

**Decision criteria.** Out of scope until the upstream changes the contract (the suggestion has been formally raised).

---

### 07. IValidateOptions<SyncOptions>

**Status quo.** `SyncOptions` has sane defaults but no validation. `Sync:Interval=0`, `OutboxBatchSize=-1`, `OutboxRetention=-2.00:00:00` would all start the host without an error. Today most are caught by guarded code paths (`OutboxBatchSize<=0` → no-op + Phase B fallback, `OutboxRetention<=0` → skip phase 5) but the failure mode is silent. Listed in `## Areas for Improvement`.

**Dimensions touched.** A new class `SyncOptionsValidator : IValidateOptions<SyncOptions>` registered in `SyncServiceCollectionExtensions`. Same for `ExternalApiOptions` if that gets the same treatment.

**Options.** Single sensible path: implement `IValidateOptions<SyncOptions>` with `ValidateDataAnnotations` for trivial bounds (`[Range]` on the integers) plus custom logic for the timespans.

**Operational implications.** Bad config now fails the host at startup with a descriptive message instead of starting and silently degrading. Slightly slower startup on misconfigured environments (which is the right tradeoff).

**Test strategy.** Unit test the validator directly (`ValidateOptionsResult.Failed("...")` on each invalid case).

**Decision criteria.** Move out of scope after the first prod misconfig incident — the cost of writing it is minutes; the cost of not having it is variable.

---

### 08. OutboxEvent.Payload usage

**Status quo.** The `Payload` column exists on `OutboxEvents` but is never written or read. It was scaffolded with the intent of carrying a serialized snapshot of the entity at emit time.

**Dimensions touched.** `TodoListService.CreateAsync`/`UpdateAsync`/`DeleteAsync` would `JsonSerializer.Serialize` the entity into `Payload` at write time. The push dispatch would `JsonSerializer.Deserialize<T>(Payload)` on re-drain when the local entity has been deleted between emit and drain — today the dispatch marks the event processed without action; with `Payload` it could still POST with the historical snapshot.

**Options.** Three:

- **Snapshot every Create event** (the case where it matters). Update events can re-read the entity if it still exists.
- **Snapshot every event regardless** (uniform path). Simpler, costs storage.
- **Status quo** (don't populate). Cheap; the "create + delete in the same tick" is microscopic per Edge Cases.

**Operational implications.** Storage growth on every emit (~500 bytes per event). Combined with `OutboxRetention=7d` and 1000 events/day → ~3 MB/year. Negligible.

**Test strategy.** Round-trip: emit Create → delete local → drain → assert POST happens with the historical name.

**Decision criteria.** Move out of scope when the "create + delete in the same tick" race becomes observable (today: zero occurrences in dev).

---

### 09. Telemetry / metrics

**Status quo.** The engine exposes structured logs only. No counters, no traces, no histograms. `## Areas for Improvement` lists the gap.

**Dimensions touched.** All the hosted services and the `TodoListSyncService`/`TodoListItemSyncService`. Plus a metric export endpoint or sidecar.

**Options.** Three exporter targets, mostly orthogonal:

- **OpenTelemetry exporter** (`OpenTelemetry.Extensions.Hosting` + an OTLP exporter) — standards-aligned, vendor-neutral, integrates with Grafana/Honeycomb/etc.
- **Prometheus client** (`prometheus-net.AspNetCore`) — adds a `/metrics` endpoint, pull-based, simple.
- **Application Insights** — vendor lock-in but turn-key on Azure.

**Operational implications.** Adds a dependency surface and a monitoring contract (alert thresholds, dashboards). The structured logs already in place become a complement, not the primary signal.

**Test strategy.** Smoke test that the metrics endpoint returns non-empty after a tick. Beyond that, telemetry is operational, not unit-testable.

**Decision criteria.** Move out of scope when the operator needs an objective dashboard (today the runbook defaults to `GET /api/sync/status` + log filters).

---

### 10. SyncRunResult.Pushed rename → Processed

**Status quo.** `SyncRunResult.Pushed` is the field name used to count successfully-processed entries on **both** push and pull operations. The semantics are "items processed successfully (in either direction)". The name is a slice-2 holdover that became misleading once pull was implemented and dramatically more so once delete-from-pull was added in slice 4. Listed in `## Areas for Improvement`.

**Dimensions touched.** The struct field, every call site (~36 sync tests + the `SyncStatusController` projection that reads it), and the response shape of `POST /api/sync/run` and `GET /api/sync/status` (the JSON property name changes).

**Options.** Two:

- **Hard rename.** `Pushed` → `Processed`, update tests, update the API JSON property. Breaks any external consumer of the API JSON.
- **Soft rename.** Add `Processed` alongside `Pushed`, deprecate `Pushed`, remove in a later slice. Verbose but contract-safe.

**Operational implications.** With a hard rename, any frontend or operator script consuming the sync run JSON breaks. With a soft rename, the JSON has both fields temporarily.

**Test strategy.** Mechanical: replace and re-run `dotnet test`. Field rename is local to the codebase.

**Decision criteria.** Move out of scope when the next contract-breaking change is on the table for an unrelated reason — bundle the rename with that to amortize the breakage.

---

## Decision Log

_Chronological, append-only. One entry per closed slice or per loaded decision warranting a record. When an entry becomes obsolete, it is not deleted: a new entry is added with `**Supersedes:** YYYY-MM-DD <title>`._

### Template

```
### YYYY-MM-DD — Slice N: <title>
- **Decision:**
- **Alternatives discarded:**
- **Why:**
- **New assumptions:**
- **Debt / follow-ups:**
```

---

### 2026-05-08 — Slice 0: Workspace setup

- **Decision:** spec frozen in `CHALLENGE.md` (not editable), NOTES.md and CLAUDE.md at the workspace root; implementation will extend `dotnet-interview/` (the TodoApi from the previous interview).
- **Alternatives discarded:**
  - New sibling folder (`senior-challenge/`) — discarded to reuse the already-built TodoApi/EF/xUnit; the spec explicitly says "enhancing an existing Todo API".
  - Cloning `crunchloop/challenge-senior-engineer` — discarded because the upstream only has README + `docs/` (no starter code), it doesn't add anything we can't pull on demand.
  - Creating a new skill for the "spec → implement → document" flow — discarded for now; the generic skills (`brainstorming`, `writing-plans`, `test-driven-development`, `verification-before-completion`) cover the cadence, and this flow is project-specific. It gets promoted to a skill if it reappears in other challenges.
- **Why:** the key decision is to separate **contract** (CHALLENGE.md, immutable) from **live state** (NOTES.md, append-mostly) from **process** (CLAUDE.md, instructions for Claude). Allows any future session to pick up the context without re-explaining.
- **New assumptions:**
  - The upstream spec will not change during development. If it changes, it gets pulled again and discussed.
  - The workspace root **is not a git repo** (`Is a git repository: false`). Pending: decide whether to initialize one of its own or whether commits live within `dotnet-interview/`.
- **Debt / follow-ups:**
  - Pull `docs/` from the upstream (OpenAPI contract) when slice 1 starts.
  - Decide workspace versioning strategy (root as new repo vs. just `dotnet-interview/`).

### 2026-05-09 — Slice 1: Sync engine scaffolding + PUSH of TodoLists

- **Decision:** sync engine modeled as class library `TodoApi.Sync` referenced from `TodoApi`, with three explicit stages — `SyncBackgroundService` (trigger), `TodoListSyncService` (logic), `IExternalTodoListClient` typed HttpClient (client). Persistence in `TodoContext` with two new tables (`SyncMapping`, `SyncRun`) exposed via `ISyncDbContext` interface. Resilience with `Microsoft.Extensions.Http.Resilience` (official Polly v8): exponential retry + jitter + circuit breaker + per-attempt timeout. Typed configuration with `IOptions<ExternalApiOptions>` and `IOptions<SyncOptions>`. Slice 1 covers only PUSH of TodoLists local→external (no items, no pull, no updates/deletes).
- **Alternatives discarded:**
  - Separate Worker SDK — more prod-ready but duplicates config and orchestration; the challenge runs in one process.
  - Inline `ExternalId` in `TodoList` — would couple the domain to the sync concern; the `SyncMapping` table maintains decoupling and supports multi-target in the future.
  - Direct Polly (`Polly` package + `Microsoft.Extensions.Http.Polly`) — `Microsoft.Extensions.Http.Resilience` is Microsoft's official line post-Polly v8, with native integration to `IHttpClientFactory`.
  - `((DbContext)_db).Set<TodoApi.Models.TodoList>()` from the sync service — discarded upon detecting it required `TodoApi.Sync` to reference `TodoApi`, creating a circular dependency (`TodoApi` already references the sync project). Replaced with specific method `ISyncDbContext.GetUnmappedTodoListsAsync(CancellationToken)` that projects to a `LocalTodoListRecord` in `TodoApi.Sync.Models` — the query uses server-side anti-join (`!SyncMappings.Any(...)`) instead of `!mappedIds.Contains(...)`, avoiding SQL Server's 2100 parameters limit.
  - Save-per-batch (commit at end of run) — worse blast radius on crash mid-run (external dups without local mapping). Save-per-list gives simple idempotency.
- **Why:** the cardinal decision is **decouple**. The user explicitly requested it: the sync should not contaminate `TodoListService` or `TodoListItemService`. We achieve that decoupling with (a) separate project, (b) minimalist `ISyncDbContext` interface with a specific method for the push query, (c) mapping table instead of inline. Polly + IHttpClientFactory typical to avoid socket exhaustion without singleton.
- **New assumptions:**
  - The external's `source_id` is used as bidirectional correlation key — we send our local Id on each push, and on future pulls we can detect locally-originated entries without a magic table.
  - `db.Database.Migrate()` only in Development (behavior inherited from the repo). In Production, migrations must be applied out-of-band before bringing up the app, or sync blows up on first save.
  - The typed HttpClient uses relative URLs (`PostAsJsonAsync("todolists", ...)`). The `BaseAddress` comes from config; the defaults (`http://localhost:8080`) cover the case of docker compose with the local external API.
  - InMemory provider does not enforce unique indices — tests do not detect mapping collisions. The migration on real SqlServer does enforce them.
  - `ExternalId nvarchar(64)` covers UUIDs (36 chars) and most reasonable string IDs. We assume the external does not return long IDs (URL-based, composite keys). If it happens, the insert fails in SqlServer with truncation error — would be visible in sync logs.
- **Debt / follow-ups:**
  - PULL external→local with reconciliation by `updated_at` (slice 2).
  - Sync of `TodoListItem` — the external spec does not expose isolated POST of items (only individual PATCH/DELETE); deserves its own slice to resolve the workaround.
  - Bidirectional DELETE / UPDATE + conflict resolution policy (slice 3+).
  - Outbox pattern for exactly-once guarantee on push (current risk: crash between `client.Create` and mapping save → external duplicate).
  - Telemetry / metrics of sync runs (Prometheus exporter or similar). Today only structured logs.
  - `POST /api/sync/run` endpoint for manual trigger (useful in development).
  - Document in README how to bring up the external API for real end-to-end verification (docker compose with the upstream repo `crunchloop/challenge-senior-engineer`).
  - Seal types with `sealed` where applicable (already done in `ExternalApiException`, `SyncBackgroundService`).
  - The original plan assumed `((DbContext)_db).Set<TodoList>()`; the live plan (this Decision Log) records that the final implementation uses a dedicated method on the interface — for any future slice extending the sync, the pattern to follow is to add specific methods to `ISyncDbContext` instead of filtering the query on the service side with imported types.

### 2026-05-09 — Slice 2: PULL external→local + Idempotency-Key + last-write-wins

- **Decision:** the slice adds three pieces that complement each other: (1) **Idempotency-Key**: `Guid` per push intent, sent as HTTP header and persisted in `SyncMapping.IdempotencyKey` (unique). (2) **PULL** of TodoLists: `GET /todolists` per tick, decision per entry among three cases — A. mapped → reconcile last-write-wins; B. parseable `source_id` points to a local without mapping → adoption (closes the slice 1 crash mid-write gap); C. otherwise → create local `TodoList`. (3) **Last-write-wins** comparing `local.UpdatedAt` vs `external.updated_at`, with tie-break to the external (`>=` rule). To support (3), `TodoList.UpdatedAt` is a new column (default `GETUTCDATE()` in real SqlServer); the `TodoListService` sets it on Create/Update. Cadence: push and pull serial in the same `SyncBackgroundService` tick, independent try/catch.
- **Alternatives discarded:**
  - **Adoption-only without `Idempotency-Key` header** — simpler, but leaves the system without anything prepared for when the server adds support. The user explicitly asked for "idempotency key", so the literal implementation is worth it even knowing the server doesn't process it today.
  - **Pre-flight GET before each POST** to check if `source_id` already matches — closes the gap of the crash within the same tick, but if the pull runs in the same tick (which it does), the GET is redundant. More round-trips.
  - **Items synced in this same slice** — the external contract does not expose isolated POST of items, they are only created when creating the list or via individual PATCH/DELETE. That asymmetry deserves its own brainstorm (re-POST entire vs items outbox vs other). Its own slice 3.
  - **External delete detection in this slice** — the user said "modification conflicts, last-write-wins". Did not mention deletes. Slice 4.
  - **Alternating push/pull tick or two hosted services with separate intervals** — over-engineering for expected volumes. Serial same tick maintains mental order.
  - **`SyncRunResult` separated by direction (`PushRunResult` vs `PullRunResult`)** — unnecessary; the existing record works if we interpret `Pushed` as "items processed successfully". Documented as minor debt in Areas for Improvement.
  - **A single `BeginTransaction` for `ApplyExternalCreateAsync`** — InMemory provider ignores them; provider branches complicate tests. Two consecutive `SaveChanges` with documented edge case is acceptable.
- **Why:** the cardinal decision is **closing the slice 1 gap (external duplicate on crash mid-write) without waiting for the formal outbox**. The pull adoption does it elegantly: the pull has to match by `source_id` anyway to avoid local duplicates — adding the "matches local without mapping → create mapping" branch costs little and solves the problem. The `Idempotency-Key` header literally fulfills what the user requested and is forward-compatible at no cost. Last-write-wins with tie to external is the simplest policy that makes sense (server is authoritative for its timestamps). To implement all this without touching the slice 1 pattern: new methods in `ISyncDbContext` (`GetMappedTodoListsAsync`, `FindUnmappedLocalByIdAsync`, `ApplyExternalCreateAsync`, `ApplyRemoteWinsAsync`) — the sync project still doesn't reference `TodoApi.Models`. Operations that only touch `SyncMappings` stay in the service (same as the slice 1 push).
- **New assumptions:**
  - The external server preserves `source_id` literally. If it normalizes, adoption fails and crash mid-write orphans are duplicated.
  - Tie-break on exact `updated_at` tie goes to the external. The user re-editing afterward fires the push, so the loss is transient.
  - UTC clock of the external "reasonably" aligned with the local. Drift of seconds OK; minutes not.
  - The `Idempotency-Key` header travels to the server but is not processed today. The local column is for tracing/debugging.
  - The `JsonSerializer` deserializes `created_at`/`updated_at` (ISO 8601 with `Z`) as `DateTime` with `Kind = Utc`. Verified by the external client test (`UpdateTodoListAsync_HappyPath_PatchesAndDeserializesResponse` compares against `new DateTime(..., DateTimeKind.Utc)`).
- **Debt / follow-ups:**
  - Items (slice 3) — now promoted to next.
  - External delete detection (slice 4).
  - Formal outbox — the pull adoption covers the case of the push crash mid-write, but does NOT cover the case of the crash between the two `SaveChanges` of `ApplyExternalCreateAsync`. Urgency dropped but debt persists.
  - `SyncRunResult.Pushed` misnamed for pulls — rename to `Processed` when it's worth breaking compat.
  - `GET /todolists` without pagination/filters on the external side — full scan every tick. Acceptable for the challenge; if it grows, requires external contract change or local `updated_at` cache.
  - Rename `LocalTodoListRecord` to something more neutral (we use it both for "unmapped local" in push and in CASE B of pull). Low priority.

### 2026-05-09 — Slice 3: Sync of TodoListItem (POST asymmetry + bidirectional)

- **Decision:** bidirectional sync of `TodoListItem` resolving the confirmed asymmetry of the external contract (verified in `assets/external-api.yaml`): isolated `PATCH` and `DELETE` of items exist but **`POST /todolists/{id}/todoitems` does not exist**; items are only created externally embedded in the initial `POST /todolists`. The slice covers four flows: (1) `TodoListItem` gains `UpdatedAt` column (mirror of slice 2 on `TodoList`); the local service sets it on Create/Update. (2) Modification of slice 1 push: `PushTodoListsAsync` now embeds `local.Items` in the POST body and, after the response, persists mappings of the returned embedded items (with `ParentExternalId = external.Id`). (3) `TodoListItemSyncService.PushTodoListItemsAsync` runs every tick: PATCH local→external for mapped items that changed (`CurrentLocalUpdatedAt > LocalUpdatedAtAtSync`); DELETE local→external for orphan mapping (anti-join `SyncMapping(EntityType=TodoListItem) WHERE NOT EXISTS TodoListItem(Id=LocalId)`) with grace for 404; Warning log for new local items in already-pushed list (no-sync, the run counters do NOT count them). (4) `PullTodoListItemsAsync(IReadOnlyList<ExternalListWithMapping>, ct)` receives the items already brought by the list pull (which now returns a tuple) and applies LWW per item: CASE A `mapped` → reconcile (4 branches: external wins / local wins / tie-to-external / no-changes-bump); CASE B `source_id` points to a local without mapping → adoption; CASE C → create local from external. `SyncMapping` gains `ParentExternalId nvarchar(64) NULL` column so that the item's parent path survives the local row's hard-delete. `SyncBackgroundService` orchestrates 4 phases per tick (list push → item push → list pull → item pull), each in independent try/catch.
- **Alternatives discarded:**
  - **Destructive re-create of the entire list** to sync new local items in already-pushed list — DELETE external list + new POST with ALL items. Changes ExternalIds of the list and of ALL its items, invalidates mappings, destabilizes timestamps last-write-wins. Huge blast radius to solve a documentable edge case.
  - **Embed-on-first-push only** (reject any item created after the list's first push, not just log it) — stricter but identical end-state to the chosen approach. The difference is semantic: with "no-sync + Warning" the local item persists and the next change will push it (when the server adds the endpoint).
  - **Tombstone soft-delete** (`IsDeleted` flag in `TodoListItem`) to detect local deletes — invades the domain model to serve a sync concern. Contradicts the slice 1 principle ("mapping table keeps the domain free of the concern"). Anti-join over orphan mappings resolves equally without contaminating.
  - **Integrating item push and pull within `TodoListSyncService`** — breaks the slice 1 "one entity → one service" pattern and makes the service much larger. The list pull return tuple allows maintaining the split without double GET.
  - **Re-parenting of items between lists** — the local DTO does not expose mutable `TodoListId` and the external contract doesn't either. Out of scope. Documented as Edge Case.
  - **External DELETE detection of mapped items in this slice** — slice 4 (unified bidirectional delete for lists and items) will cover it. Meanwhile, a PATCH local→external to an item already deleted externally would give 404 and is logged Warning.
  - **`Idempotency-Key` header in PATCH/DELETE** — PATCH and DELETE are naturally idempotent (the operation with the same ExternalId/content produces the same result). The header is kept only on the initial POST. PATCH/DELETE do not send it.
- **Why:** the cardinal decision is **respecting the contract asymmetry without destroying information**. The destructive re-create was the only alternative that covered late items, but at a cost (ExternalId change, invalidated mappings, destabilized timestamps) disproportionate to the problem. The "no-sync + Warning + report to server" policy preserves local consistency, leaves a clear fallback (the user re-edits or the server adds the endpoint), and is honest about the limitation. The rest of the slice mirrors the pattern established in slice 1+2: anti-join to detect candidates, specific methods in `ISyncDbContext` (without filtering with imported types), own service per entity, last-write-wins with tie to external. The new piece conceptually is the denormalization of `ParentExternalId` in `SyncMapping` — a conscious trade-off between "extra JOIN on each DELETE" and "a column that doesn't go stale because the parent ExternalId is server-generated and immutable".
- **New assumptions:**
  - The external server preserves `source_id` literally for items embedded in the initial POST (same as for lists). If it normalizes, items remain without mapping and are duplicated on the next pull (same risk as the list, already documented).
  - The list's `ExternalId` (`ParentExternalId` in item mappings) does not change during the list's lifetime. The contract does not document normalization or reassignment; we assume immutability.
  - New local items created in an already-pushed list are NOT synced to the external until the server adds `POST /todolists/{listId}/todoitems`. Documented as limitation + formal suggestion in Areas for Improvement.
  - Re-parenting (change of an item's `TodoListId`) does NOT occur — neither the local `UpdateTodoListItem` allows it, nor the external `UpdateTodoItemBody`. If the server exposed it in the future, the SyncMapping's `ParentExternalId` would go stale and break.
  - The `JsonSerializer` deserializes items' `description`/`completed`/`created_at`/`updated_at` the same as those of lists (snake_case + `DateTime` with `Kind=Utc` for timestamps).
  - `Total` of `SyncRunResult` for items = `mapped.Count + orphans.Count` (warnings of late items do NOT add). Mapped items without changes count as `Processed` (examined, decision: skip), not as Failed.
  - Single-instance of host (inherited from slice 1+2) — if two instances ran the sync in parallel, orphan-detection could double-DELETE or fire unnecessary 404-grace. Acceptable under single-instance.
- **Debt / follow-ups:**
  - **Slice 4 — Bidirectional DELETE** complete (external DELETE detection of lists and items + propagated local list deletes). The local→external item delete is already covered by slice 3; slice 4 unifies the policy with lists and adds the inverse direction.
  - **Formal suggestion to the external server: add `POST /todolists/{listId}/todoitems`**. Blocks the case "new local item in already-pushed list".
  - **Outbox pattern** — slice 3 inherits the slice 2 gap in `ApplyExternalCreateAsync` (now with 3 saves in the path with items: list, items, mappings). The crash between any pair can leave inconsistent state that the pull cures as CASE C of items, same reasoning. Formal outbox remains debt.
  - **Weak Update test of `TodoListItemService`** — `Assert.True(updated.UpdatedAt >= before)` passes even without the service setter. The Create test is strict. Future mitigation: `IClock` abstraction for deterministic tests.
  - **`SyncRunResult.Pushed` misnamed for pulls** — continues as debt, aggravated now that we have 4 phases (push list, push item, pull list, pull item) using the same record.
  - **Test seeding helper** — `TodoListItemSyncServiceTests.cs` ended up at ~1650 lines; the helper `SeedMappedPair(ctx, parentLocalId, parentExternalId, itemLocalId, itemExternalId, snapshot)` would reduce duplication. Low priority.
  - **CRLF→LF normalization** — a slice 3 commit inadvertently reformatted `TodoContext.cs` (line endings). Doesn't affect build but creates noise in the diff. Configuring `.gitattributes` with `* text=auto` would resolve for future slices.
  - **`TodoContext.cs` grew to ~370 lines** — still cohesive (single-responsibility = "ISyncDbContext implementation + EF model config") but candidate for partial class `TodoContext.Sync.cs` when slice 4 adds more methods.

### 2026-05-09 — Slice 4: Bidirectional DELETE (TodoLists + TodoListItems)

- **Decision:** closes the bidirectional DELETE cycle with three new flows without adding a new phase to `SyncBackgroundService`. (1) **PUSH local→external of TodoList:** `IExternalTodoListClient.DeleteTodoListAsync(externalId)` (exact mirror of `DeleteTodoItemAsync` from slice 3); detection via anti-join `SyncMapping(EntityType=TodoList) WHERE NOT EXISTS TodoList(Id=LocalId)` via `ISyncDbContext.GetOrphanedListMappingsAsync`; 2nd pass at the end of `PushTodoListsAsync` with 404 grace and Info log per delete. (2) **PULL external→local of TodoList:** after the existing per-external loop, compare `mappedLists` against the set of external IDs seen in the GET; mappings whose `ExternalId` does not appear are processed via `ISyncDbContext.ApplyExternalDeleteListAsync(plan)` which in a single `SaveChanges` explicitly deletes child items + item mappings + list mapping + TodoList (explicit because InMemory does not enforce FK cascade). (3) **PULL external→local of TodoListItem:** after the existing loop, filter `mappedItems` by `ParentExternalId IN seenExternalListIds && ExternalItemId NOT IN seenExternalItemIds`; matches are processed via `ApplyExternalDeleteItemAsync(plan)` (atomic: deletes mapping + local item in a `SaveChanges`). The filter prevents double-delete with the (2) cascade. **Mirror + Warning conflict policy:** both pulls before the delete check `mapped.LocalUpdatedAtAtSync.HasValue && mapped.CurrentLocalUpdatedAt > mapped.LocalUpdatedAtAtSync.Value` and, if true, emit structured Warning log with `{LocalId}`, `{ExternalId}`, `{LocalUpdatedAt}`, `{LocalUpdatedAtAtSync}`. The delete proceeds anyway (consistency consistent with tie-to-external from slice 2/3). The `HasValue` check avoids noisy Warning for legacy mappings with `LocalUpdatedAtAtSync == null`. **Counting:** `Total = base + deleteCandidates`, `Processed += deleted_succeeded`, `Failed += deleted_failed`. Status semantics same as before. 22 new tests: 2 client (`DeleteTodoListAsync_*`), 7 push list orphan, 8 pull list disappeared, 5 pull item disappeared.
- **Alternatives discarded:**
  - **Pure Mirror (without Warning) or Skip+Warning** — the first loses edits silently (bad observability), the second leaves inconsistent state (zombie mapping + zombie local). Mirror+Warning is the in-between: consistency + observability.
  - **Preserve+re-push** (delete mapping, leave local; next push treats it as new) — preserves edits but creates a new `ExternalId`, breaks the "one logical entity = one mapping" model and destabilizes last-write-wins (reset timestamps). More complex and less predictable.
  - **Tombstone soft-delete** (`IsDeleted` flag in `TodoList`) — would invade the domain to serve a sync concern. Anti-join over `SyncMappings` resolves without contaminating (same reasoning as slice 3 for items).
  - **Pre-cleanup of child item mappings during list-orphan-DELETE** — N+1 wasted round-trips on cascade avoided, but couples the list flow with the item flow. The item push's existing 404 grace cleans them up automatically; YAGNI.
  - **New phase in `SyncBackgroundService` for "delete detection"** — over-engineering. The existing 4 phases absorb the 2nd pass on each side without structural changes.
  - **Trust EF cascade `TodoList → TodoListItem`** for the local delete — works in SqlServer but NOT in InMemory. Explicitly deleting gives parity between providers and keeps tests reliable.
  - **404 grace in the item push PATCH branch** (when the parent was externally deleted) — that 404 may have other causes (stale mapping due to race with a concurrent external delete, item already externally deleted without having detected the parent's delete). Treating it as transient failure + self-heal on next tick is more predictable than swallow.
  - **`Idempotency-Key` header in DELETE** — DELETE is naturally idempotent (the operation with the same ExternalId produces the same end-state). The header is kept only on POST.
- **Why:** the cardinal decision is **respecting the symmetry with slice 3 without sacrificing real atomicity**. Slice 3 established the orphan-anti-join as the local delete detection pattern; slice 4 extends it to `TodoList`. The new piece is the atomic local cascade via `ApplyExternalDeleteListAsync`: a single `SaveChanges` that deletes EVERYTHING (items + their mappings + list mapping + list) on real SqlServer, with explicit deletes for parity with InMemory. The Mirror+Warning policy resolves the conflict problem that slice 2 left open without introducing new complexity: the same `LocalUpdatedAtAtSync` that slice 2 uses for LWW of updates now distinguishes "silent delete" from "delete with lost edit". The `SyncBackgroundService` does not change: each pull simply expands its loop with a 2nd pass over the same dataset it already has in memory (mappedLists / mappedItems), preserving the independence of phases.
- **New assumptions:**
  - **`GET /todolists` is the single source of truth for "what exists" externally.** If a mapped list does not appear, it is assumed deleted. If the GET is inconsistent (partial pagination, stale cache, race), a mapping could be improperly deleted locally. The OpenAPI spec does not document pagination or filters; we assume atomicity of the GET.
  - **External DELETE is idempotent and symmetrically recoverable.** A retry with the same ExternalId after 5xx should reproduce the same end-state (deleted list). The 404 grace covers the "already deleted" case. DELETE does not generate new `IdempotencyKey`s (push DELETEs do not write mappings, only delete them).
  - **Mirror policy: the local loses edits if the external deleted after the last push.** Acceptable under "external authoritative for what exists". If it were a deal-breaker, the structured Warning enables offline forensics.
  - **Single-instance of the host** (inherited) — if two instances ran simultaneously, the 2nd pass could double-delete: instance A calls `ApplyExternalDeleteListAsync(mapping=X)`, instance B also invokes it; B finds the mapping already removed and `SyncMappings.FindAsync` returns null → silent no-op (no error). The local item is also attempted to be deleted twice; `TodoListItem.FindAsync` null → no-op. Tolerant to races, but not designed for real concurrency.
- **Debt / follow-ups:**
  - **Slice 5 candidate:** external restore (if the server exposed it), re-association of "new external whose source_id points to a just-deleted local" (today CASE C duplicates), batch DELETE in the external server if volumes justify it.
  - **Formal outbox** — the gap is now symmetric for deletes: if it crashes between `client.Delete` and `RemoveMappingAsync`, the mapping persists but the external is already deleted → next tick retries `client.Delete`, receives 404, 404 grace cures it. Acceptable. For `ApplyExternalDeleteListAsync`, the single `SaveChanges` is atomic in SqlServer but partial in InMemory; outbox closes it.
  - **`SyncRunResult.Pushed` misnamed** — aggravated by slice 4: now a pull can report "Pushed=N" where N includes creates + reconciles + deletes. Renaming to `Processed` remains debt.
  - **SyncBackgroundService tests** — the existing smoke covers startup/shutdown but does not assert on the new surface. Per-service unit tests remain sufficient.
  - **`TodoContext.cs` grew to ~470 lines** — still cohesive, but the split to `TodoContext.Sync.cs` (partial class) would be reasonable if slice 5 adds more methods. Not yet blocking.
  - **Test "PullTodoListsAsync_MissingExternalApplyDeleteThrows_StatusPartial"** — uses `Mock<ISyncDbContext>` with partial setup (delegating most calls to the real ctx). The pattern is ugly but necessary to inject the throw in `ApplyExternalDeleteListAsync` without patching the ctx. If more tests of this style appear, consider extracting a `RecordingSyncDbContext` decorator.

### 2026-05-09 — Slice 5: Hardening — E2E + edge cases + reinforced tests + docs

- **Decision:** close the implementation cycle with four pieces: (1) manual `POST /api/sync/run` endpoint (`SyncController` + `SyncRunResponse` DTO) that orchestrates the 4 phases with the same try/catch-per-phase semantics as `SyncBackgroundService` and returns 200 with `SyncRunResult` aggregated per phase. Synchronous — when it responds, all HTTP outbound finished. (2) integration test infrastructure: `TodoApiWebApplicationFactory<Program>` with InMemory DB (sharing `InMemoryDatabaseRoot`) + `Sync:Enabled=false` to disable the ticker in tests + `ExternalApi:BaseAddress` pointing to `WireMockFixture`. **Fresh factory per test** to isolate the Polly circuit-breaker state. (3) 10 E2E tests in `SyncEndToEndTests.cs` covering push (without items, with embedded items), pull (CASE A LWW external newer, CASE A local newer, CASE B adoption, CASE C create), bidirectional delete (local DELETE propagates, external disappears cascade), items (PATCH local newer), endpoint smoke, edge case `source_id` pointing to deleted local. (4) reinforcements: 1 edge case unit test in `TodoListSyncServiceTests` (`PullTodoListsAsync_ExternalSourceIdPointsToDeletedLocal_FallsToCaseC`); 5 tests in `SyncBackgroundServiceTests` (tick happy-path, push throws continues with rest, pull throws skip pull-item, pull empty mapped skip pull-item, cancellation during startup) using a new `CapturingLoggerProvider` helper to assert specific log entries; reinforcement of `UpdateAsync_WhenIdExists_UpdatesItem` with `Thread.Sleep(1)` + strict assert `>` (instead of `>=`). README expanded in English with Sync Engine section, configuration, manual trigger, instructions to bring up the external API, troubleshooting. Total tests: 143 → 160.
- **Alternatives discarded:**
  - **`TimeProvider`/`IClock` cross-cutting** to resolve the weak UpdateAsync test — discarded due to scope creep: impacts `TodoListService`, `TodoListItemService`, the two sync services + 36+23 sync tests. `Thread.Sleep(1)` resolves the hole today with 1 line, keeping the slice bounded. `TimeProvider` remains as explicit debt in Areas for Improvement.
  - **Testcontainers + docker compose with the real external API** — discarded due to overhead (Docker in CI, ~3-5s startup per test) without clear gain: WireMock covers the full HTTP contract and allows deterministic stubs for edge cases (404 grace, 409 conflict, 5xx).
  - **Endpoint with query params** (`?direction=push|pull|both`, `?type=lists|items|all`) — discarded due to unnecessary test surface. Full sync is always what the two consumers (operators + E2E tests) need.
  - **`IClassFixture<TodoApiWebApplicationFactory>`** to reuse the factory between tests — discarded because it shares Polly circuit-breaker state between tests; a 5xx failure in one test could open the circuit and fail subsequent tests with shortcircuit. Fresh factory per test costs ~1s extra per test but guarantees isolation.
  - **`Sync:Enabled=true` in E2E** with low interval (`10ms`) and waiting for the ticker — discarded due to flaky races with the manual trigger. Background OFF + synchronous manual trigger is deterministic.
- **Why:** the cardinal decision is **harden without abstracting**. The slice does not introduce new concepts (no `IClock`, no outbox, no multi-host) — it only covers the real contract with E2E tests that exercise the full ASP.NET Core pipeline (middleware → controller → service → Polly → HTTP), closes two pending edge cases, and documents the sync engine in the README. The new piece conceptually is the discovery (during E2E test implementation) that the GET stub must echo the just-pushed entry, otherwise the slice 4 mirror-policy detects "mapped local missing from external" and cascade-deletes the local in the same tick — natural limitation of testing the 4 phases together with WireMock that is documented as inline comment in the affected tests.
- **New assumptions:**
  - The external GET in E2E tests must be **consistent** with post-push state to not trigger the slice 4 cascade-delete. In production this is trivial (POST persists, GET reads); in tests with WireMock it requires stubbing both consistently.
  - The manual endpoint and the `BackgroundService` can run concurrently under `Sync:Enabled=true` — slice 1+ single-instance assumption is maintained; no lock added. The `Sync:Enabled=false` override in the test factory avoids races by construction.
  - WireMock.Net 2.5.0 is the current stable version for .NET 8 (published in 2026). Upgrade to 3.x or incompatible changes would be future debt.
- **Debt / follow-ups:**
  - **`TimeProvider` cross-cutting** — formalize the time abstraction in production services for deterministic tests (especially for exact LWW tie-break where `Thread.Sleep` doesn't scale). Future slice.
  - **Formal outbox** — the weak atomicity of `ApplyExternalCreateAsync`/`ApplyExternalDeleteListAsync` in InMemory remains; outbox closes it at the root. Microscopic risk in SqlServer; documented.
  - **Telemetry / metrics** — the `CapturingLoggerProvider` in tests is ad-hoc for asserts; production still has no OTel/Prometheus. Future slice.
  - **Multi-host concurrency** — concurrent runs between two API instances + multi-trigger (background + manual endpoint) could race on `SyncMapping.IdempotencyKey` unique index. Under single-instance assumed and documented.
  - **NU1608 build warnings** — WireMock.Net 2.5.0 brings `Microsoft.CodeAnalysis 4.0.0` transitively, conflict with the solution's already-resolved version 4.8.0. Runtime irrelevant (we don't use those assemblies); cosmetic noise in CI logs. Future mitigation: package override in `Directory.Packages.props`.

### 2026-05-09 — Slice 6: Outbox pattern + indices on UpdatedAt (push side)

- **Decision:** introduce a table-driven outbox push pipeline maintaining the legacy flow (anti-join) as a transient safety net. Four pieces: (1) New `OutboxEvent` entity (`Id`, `EntityType`, `EntityId`, `Operation` ∈ {Create, Update, Delete}, nullable `Payload`, `OccurredAt`, `ProcessedAt`, unique `IdempotencyKey`) with `AddOutboxEvents` migration that creates the table + 3 indices (unique `IdempotencyKey`, `(EntityType, EntityId)` for diagnostics, filtered `OccurredAt WHERE ProcessedAt IS NULL` for efficient FIFO drain — SqlServer-only filter, InMemory ignores it silently). The same migration adds indices on `TodoList.UpdatedAt` and `TodoListItem.UpdatedAt` to enable delta-cursor-based queries in future slices. (2) Hooks in `TodoListService` and `TodoListItemService`: each Create/Update/Delete writes an OutboxEvent in its `SaveChanges`. Update and Delete are atomic (single Save because the `EntityId` is already known); Create does two consecutive Saves (TodoList first to assign Id, OutboxEvent after with that Id as `EntityId`). The microscopic gap between the two Saves of Create is covered by the legacy anti-join Phase B fallback (same reasoning as slice 2 with `ApplyExternalCreateAsync`). Private `BuildOutboxEvent(entityId, op)` helper in each service to centralize the shape (`OccurredAt = UtcNow`, `IdempotencyKey = Guid.NewGuid()`). PULL side flows (`ApplyExternalCreateAsync`, `ApplyExternalDelete*Async`) do NOT emit outbox events — they are writes coming from the external, they don't go back. (3) Refactor of `PushTodoListsAsync` and `PushTodoListItemsAsync` into two phases per tick: **Phase A** drains up to `OutboxBatchSize=1000` events FIFO (oldest first) via new `ISyncDbContext.GetPendingOutboxEventsAsync(EntityType, take, ct)` and dispatches them by operation; **Phase B** runs the full legacy flow (anti-join unmapped + orphan-mappings) as transient safety net. Each processed event is marked with `ProcessedAt = UtcNow` via `ISyncDbContext.MarkOutboxEventProcessedAsync(eventId, ct)`. (4) TodoList vs TodoListItem asymmetry in dispatch — inherited from legacy behavior: for `TodoList`, push only does POST (Create) and DELETE (Delete); the PATCH (Update) is exclusively the pull side's responsibility via LWW, so the outbox Update event for TodoList is marked processed without action (no-op slice 6, slice 7 could implement it if the pattern is validated). For `TodoListItem`, push does do PATCH (Update) — preserves slice 3 behavior which already emitted PATCHes from push. Item Create always logs Warning + mark processed (documented slice 3 limitation: the external contract does not expose isolated POST of items). 23 new tests: 5 service hooks (TodoList) + 5 service hooks (TodoListItem) + 7 sync drain (TodoList) + 6 sync drain (TodoListItem) — total 160 → 183.
- **Alternatives discarded:**
  - **Global `SaveChanges` override in `TodoContext`** to auto-emit outbox events — discarded because the PULL side (`ApplyExternalCreateAsync`, etc.) also goes through the same SaveChanges, and would need ambient flags (`[ThreadStatic]`?) to distinguish "CRUD writes" from "apply external writes". Coupling via globals fragile. Explicit hooks in CRUD services are more explicit and less invasive.
  - **Option (a) Backfill on startup** (generate Create event in data migration for each pre-slice-6 local without mapping) — discarded due to startup cost proportional to the backlog. Option (b) transient coexistence via legacy Phase B has no startup cost and converges to the same end-state after a few ticks (because the outbox fills with all new operations; pre-slice-6 locals without event get pushed by Phase B). The TODO of removing Phase B is noted in Areas for Improvement for slice 7+.
  - **Single SaveChanges for TodoList Create** via explicit `BeginTransactionAsync` — real atomic in SqlServer but InMemory ignores transactions (tests would not detect regressions). Two consecutive `SaveChanges` + safety net of legacy Phase B is the same reasoning as slice 2 for `ApplyExternalCreateAsync`.
  - **Configure `OutboxBatchSize` via `SyncOptions`** — slice 6 leaves it hardcoded at 1000 within each service. Slice 7 exposes it when introducing bounded concurrency.
  - **External PATCH from push for TodoList Update events** — discarded in slice 6 for two reasons: (i) the slice 2 legacy flow already does PATCH from the pull side when `local newer than external`, so the net effect of behavior is the same in both cases; (ii) push-side PATCH introduces a new race semantics ("local always wins during the tick") whose trade-off vs. the current "external wins on race with concurrent external change" deserves its own discussion. Slice 7 can take the decision with data.
  - **Bulk delete pattern (`ExecuteDeleteAsync` with provider guard) + retention cleanup of processed OutboxEvents** — discarded in slice 6 due to scope: today there is no critical site to apply it (existing cleanups are per-element `RemoveMappingAsync`, and outbox events retention is a new mechanism that makes more sense alongside slice 7's bounded concurrency). Adding the pattern without real use would be dead code. Noted in Areas for Improvement.
  - **Refactor of the `OutboxOperation` enum** to reuse `SyncDirection` or `HttpMethod` — discarded: the semantics is "CRUD operation that generated the event", different from "sync direction" (push/pull) and from "HTTP method". Dedicated enum is clearer.
- **Why:** the cardinal decision is **introducing outbox as the preferred propagation network without breaking any existing flow**. The "Phase A drains outbox + Phase B legacy fallback" pattern guarantees zero-cost migration: tests from slice 1-5 stay green because (a) CRUD services now emit events that the outbox drains first, and the legacy flows (anti-join unmapped, orphan-DELETE) run afterward and find no candidates (because they were already processed via outbox); (b) E2E tests that create TodoLists via `service.CreateAsync` see the event processed in Phase A, mappings identical to the pre-slice-6 flow. The TodoList-vs-TodoListItem asymmetry in Update is inherited from legacy behavior and respects it, doesn't force it symmetric. The new piece conceptually is `OutboxEventRecord` (projection, parallel to `LocalTodoListRecord`, etc.) that keeps `TodoApi.Sync.Models` free of references to concrete entities — the sync project still doesn't reference `TodoApi.Models`. The new methods (`GetLocalTodoListByIdAsync`, `GetLocalTodoListItemByIdAsync`) are mapping-filter-less variants of existing `FindUnmappedLocalByIdAsync` (slice 2/3); the outbox drain needs them because the local CAN be mapped at the time of processing (pull adoption case between local create and drain).
- **New assumptions:**
  - **The OutboxEvent table is the preferred propagation source local→external for Create and Delete.** The legacy Phase B is transient safety net covering: (i) pre-slice-6 entries without event, (ii) orphan mappings whose local was deleted outside the service flow (rare but possible). Slice 7+ may deprecate Phase B once backfilled or N days post-deploy.
  - **The microscopic gap between the two `SaveChanges` of local Create + outbox event** is functionally equivalent to the gap of `ApplyExternalCreateAsync` documented in slice 2: if it crashes between the two saves, the TodoList persists without event → next tick Phase B (anti-join unmapped) pushes it as before. The outbox does not introduce regression in this edge case.
  - **For TodoLists, Update events are no-op of the push side in slice 6.** The net behavior vs. pre-slice-6 is identical: the pull side does PATCH when `local newer than external`. Slice 7 may activate push-side PATCH if the pattern is validated. For TodoListItem, Update events do PATCH from push (preserves slice 3).
  - **New local items in already-pushed list still don't propagate to the external.** The outbox Create event for items is emitted, but the dispatch logs Warning and marks processed without POST (limitation inherited from slice 3 — the external contract does not expose `POST /todolists/{id}/todoitems`). The net behavior is the same as the legacy `unmappedWithMappedParent` flow. The formal suggestion to the external server remains Areas for Improvement.
  - **`OutboxBatchSize=1000` per tick** suffices for expected volumes. If in a tick there are > 1000 events pending (post-deploy with large backlog, outage recovery), they are processed in subsequent ticks. Slice 7 makes this configurable + introduces bounded concurrency to process more events per tick.
  - **The `IdempotencyKey` of each OutboxEvent is the `IdempotencyKey` sent in the `Idempotency-Key` header of the POST when the event is a Create.** Forward-compatible with the server when it supports it. For Update/Delete the header is not sent (always was that way since slice 2).
  - **The filter `WHERE ProcessedAt IS NULL` of the index `IX_OutboxEvents_OccurredAt` is SqlServer-only.** In InMemory the filter does not apply (the provider ignores it) — queries still work with full scan. In real production over SqlServer the filtered index is what enables efficient drain with growing table (without retention, processed events accumulate; that index ensures the drain query is O(pending) not O(total)).
  - **Cleanup/retention of processed OutboxEvents is postponed.** The table grows monotonically today. Slice 7 introduces a cleanup job (`processed AND OccurredAt < UtcNow - retention`) using `ExecuteDeleteAsync` with provider guard. Acceptable short-term: in real production with retention 7 days + 1000 ops/day, ~7000 rows steady state — manageable.
- **Debt / follow-ups:**
  - **Slice 7 — bounded concurrency + cursor pagination + adaptive interval** — `System.Threading.Channels.BoundedChannel<OutboxEventRecord>` with paginated producer drain (keyset by `Id`) + N consumers (configurable, default 8) + Polly bulkhead. Today is serial within each Phase A.
  - **Slice 7 candidate — bulk delete pattern + retention cleanup** — extension method `BulkDeleteAsync` with provider guard (InMemory falls to `RemoveRange`+`SaveChanges`, SqlServer uses `ExecuteDeleteAsync`). Apply to: (a) cleanup of processed OutboxEvents with configurable retention, (b) bulk cleanup of orphan mappings in `ApplyExternalDeleteListAsync` when a list has many items.
  - **Slice 7+ — push-side PATCH for TodoList Update events** — today mark processed without action. Validate race behavior vs. pull-side PATCH before activating. Have metrics (slice 10) to distinguish cases.
  - **Deprecate legacy Phase B** — once N days post-deploy of slice 6 pass (or an explicit backfill is run that generates Create events for all pre-existing locals), the legacy Phase B flow becomes dead code. Note removal in NOTES as future entry.
  - **`OutboxBatchSize` configurable via `SyncOptions.OutboxBatchSize`** — today hardcoded `1000`. Configurable when slice 7 introduces concurrency.
  - **`Payload` field unused in slice 6** — the column exists but is not populated. Slice 7 may use it for serialized snapshot in JSON (useful if the local disappears between emit and drain — case "Create event with deleted local", today mark processed without action; with payload we could POST with the historical snapshot). Low priority, rare edge case.
  - **Race between push-side Update event of item and pull-side PATCH** — both can execute PATCH to the same item in the same tick if there is a pending outbox Update event AND `local newer than external` evaluates true on the pull. The second PATCH is no-op (same state), but does an extra round-trip to the external. Mitigation: the pull could check if there is a pending outbox event for the item and skip the PATCH. Slice 7+.
  - **`TodoContext.cs` grew to ~485 lines** — still cohesive (single-responsibility = "EF model + ISyncDbContext implementation") but a firm candidate for partial class `TodoContext.Sync.cs` when slice 7 adds cleanup methods. Marked as future action.

### 2026-05-10 — Slice 7: Outbox retention + bulk delete pattern + configurable OutboxBatchSize

- **Decision:** consolidate three small outbox-lifecycle debts in one cohesive slice. (1) `SyncOptions.OutboxBatchSize` (default 1000) and `OutboxRetention` (default `TimeSpan.FromDays(7)`); both consumed by `TodoListSyncService` / `TodoListItemSyncService` via `IOptions<SyncOptions>` (read-once-per-tick via the new DI scope) for Phase A drain caps. (2) New extension `BulkDeleteExtensions.ExecuteBulkDeleteAsync<T>(IQueryable<T>, DbContext, CancellationToken)` in `TodoApi.Sync/Data/`: uses `ExecuteDeleteAsync` (relational) when available, falls back to `ToListAsync` + `RemoveRange` + `SaveChanges` for InMemory. Provider detection via `Database.ProviderName?.Contains("InMemory", Ordinal)` to avoid a hard dep on `Microsoft.EntityFrameworkCore.InMemory` from production code. New package reference `Microsoft.EntityFrameworkCore.Relational` on `TodoApi.Sync` (transitive in TodoApi via SqlServer; needed for the `ExecuteDeleteAsync` symbol on `IQueryable<T>`). (3) `ISyncDbContext.PurgeProcessedOutboxEventsAsync(cutoff, ct)` returns count of deleted events with `ProcessedAt != null && OccurredAt < cutoff` via the bulk-delete extension. `SyncBackgroundService` adds a 5th phase per tick: independent try/catch + `LogInformation("Sync outbox retention tick: purged={Count} olderThan={Cutoff:o}", ...)`. When `OutboxRetention <= TimeSpan.Zero` the phase short-circuits with a Debug log and never resolves `ISyncDbContext`. 13 new tests (183 → 196): 4 BulkDeleteExtensions (empty/non-empty/null-source/null-context), 4 PurgeProcessedOutboxEventsAsync (empty/predicate/all/idempotent), 2 batch-size respects-limit (one per service), 3 retention-phase (logs purged count / failure does not abort other phases / disabled when `OutboxRetention=0`).
- **Alternatives discarded:**
  - **Hard dep on `Microsoft.EntityFrameworkCore.InMemory` (`IsInMemory()`)** — pulls a test-only package into production deps. The `ProviderName` substring check is fragile but pragmatic; documented in Edge Cases.
  - **Reflection probe of `ExecuteDeleteAsync`** — too clever for the actual problem. The compile-time reference + runtime branch is enough.
  - **Separate `OutboxRetentionBackgroundService` with its own interval** — doubles the service surface to register / disable in E2E. The 5th-phase pattern reuses the existing tick loop's try/catch and ticks at the same cadence as the rest. `ExecuteDeleteAsync` is fast enough that running it every 60s is negligible (most ticks delete 0 rows).
  - **Cleanup predicate `ProcessedAt < cutoff` (semantically "events processed N days ago")** — slightly more correct for forensics windows, but the filtered index `OccurredAt WHERE ProcessedAt IS NULL` from slice 6 is on `OccurredAt`, and a parallel index on `ProcessedAt` would multiply write cost. `OccurredAt < cutoff` is what NOTES.md documented as the slice 7 plan and what the index hint favors; difference is meaningful only for backlogs with multi-tick processing delay (acceptable).
  - **Separate validator (`IValidateOptions<SyncOptions>`) for `OutboxBatchSize > 0` and `OutboxRetention >= 0`** — kept the slice 1 pattern of "sane defaults without DataAnnotations". Validator is in Areas for Improvement.
  - **Refactoring `ApplyExternalDeleteListAsync` to use the new bulk-delete extension** — slice 4 explicitly chose single-`SaveChanges` atomicity for the cascade. `ExecuteDeleteAsync` would split the 4 deletes (items / item mappings / list mapping / list) into 4 separate transactions on real SqlServer unless wrapped in `BeginTransaction`. Atomicity wins; documented as deferred.
  - **Configuring `OutboxBatchSize` via `IOptionsMonitor<SyncOptions>` for hot reload** — `IOptions<SyncOptions>` is enough because `SyncBackgroundService` already creates a fresh DI scope per tick, so the value is re-read each tick.
  - **Test for batch-size that asserts on `result.Pushed`** — Phase B legacy fallback would still push the unmapped lists past the cap, so `result.Pushed = 5` even when `OutboxBatchSize = 2`. The test isolates Phase A by seeding already-mapped lists (Phase A marks processed without POST) so Phase B has nothing to do; assertion is `OutboxEvents.Count(processed) == 2 && Count(unprocessed) == 3`.
- **Why:** the cardinal decision is **completing the outbox lifecycle without abstracting away decisions** the next slice will need. Outbox without retention grows indefinitely (slice 6 explicit deuda); without configurable batch size it can't be tuned for the bounded-concurrency slice 8 candidate; without a bulk-delete helper the cleanup itself loads N rows into memory before deleting (defeats the purpose at scale). The three pieces compose: `OutboxBatchSize` makes Phase A tunable, `BulkDeleteExtensions` makes the cleanup primitive cheap, `PurgeProcessedOutboxEventsAsync` exposes it through `ISyncDbContext`. `SyncBackgroundService` absorbs the cleanup as the 5th independent phase, keeping the slice-4/6 pattern of "one fault per phase isolated by try/catch". The new package `Microsoft.EntityFrameworkCore.Relational` is conceptually correct: `TodoApi.Sync` was already implicitly relational via the SqlServer transitive dep on TodoApi, but the symbol resolution required the explicit reference now that we use `ExecuteDeleteAsync` directly.
- **New assumptions:**
  - **`OutboxRetention` cutoff against local `DateTime.UtcNow`** — single-instance + aligned clocks. Drift of minutes between hosts could purge earlier/later than configured.
  - **`OccurredAt < cutoff` (not `ProcessedAt`) is acceptable for cleanup semantics** — events processed within a tick of Occurred are the typical case; backlog scenarios with multi-tick processing delay leave events alive a tick longer (no correctness impact).
  - **Provider name "InMemory" is the canonical substring** — `Microsoft.EntityFrameworkCore.InMemory` provider returns `Microsoft.EntityFrameworkCore.InMemory` as `ProviderName`. Other providers don't have "InMemory" in their name.
  - **The new `Microsoft.EntityFrameworkCore.Relational` package on `TodoApi.Sync` is compatible with the existing `Microsoft.EntityFrameworkCore` 7.0.0-* wildcard** — both packages share the same major version line.
  - **`OutboxBatchSize=0` and `OutboxBatchSize<0` are invalid configurations** — the runtime behavior (Take(0) = empty, Take(<0) = LINQ-undefined) is documented as the user's problem, not the library's. Validator deferred.
- **Debt / follow-ups:**
  - **Slice 8 — bounded concurrency.** `BoundedChannel<OutboxEventRecord>` + N consumers. Builds on `OutboxBatchSize` (which now governs producer drain). Polly bulkhead per consumer.
  - **`SyncOptionsValidator`** — formalize `OutboxBatchSize > 0`, `OutboxRetention >= 0`, `Interval > 0`, `StartupDelay >= 0` invariants.
  - **`ApplyExternalDeleteListAsync` bulk-delete refactor** — deferred; atomicity wins over throughput at current volumes.
  - **Migrate `Payload` to populated** — slice 6 left it unused; could enable POST with historical snapshot for "local deleted between emit and drain" edge case. Rare; low priority.
  - **`TodoContext.cs` partial-class split** (~510 lines now after slice 7's `PurgeProcessedOutboxEventsAsync`) — `TodoContext.Sync.cs` would group the 19+ ISyncDbContext implementations. Cosmetic; low priority.
  - **Test pattern for `BulkDeleteExtensions` relational path** — slice 7 only tests the InMemory branch (production path is exercised indirectly by `dotnet ef` migrations on real SqlServer in dev). A future slice with a SqlServer fixture (Testcontainers) would close that gap.
  - **`SyncDbContextTests.cs` is the first test file in `TodoApi.Tests/Sync/Data/`** — pattern-establish for future ISyncDbContext-level tests. Currently scoped to the new method.

### 2026-05-10 — Slice 8: Sync v1 Closeout — observability endpoint + synthesis docs
- **Decision:** declare the sync engine **closed for v1**. Add a lightweight observability endpoint `GET /api/sync/status` (last run per `(EntityType, Direction)` pair, pending outbox count, oldest pending `OccurredAt`, current `SyncOptions` snapshot). Add a one-page **"Sync v1 Closeout"** section in `NOTES.md` synthesising capability matrix, configuration cheat-sheet, failure modes table, operational runbook, and frozen limitations. Convert the `OutboxBatchSize=0` boundary edge case to a regression test (the `[ ]` in Edge Cases is now `[x]`). No new sync features, no backlog items pulled in.
- **Alternatives discarded:**
  - **Pull `Slice 9 backlog` (push-side `PATCH TodoList`) into the closeout.** Would cross from "synthesis" into "new feature" and force a race-semantics decision against the pull-side PATCH that deserves its own brainstorm. Deferred.
  - **Pull `Slice 8 backlog` (bounded concurrency + cursor pagination).** At current volumes (`OutboxBatchSize=1000` × `Interval=60s` ≈ 60 k/h serial), the existing serial drain comfortably fits the challenge envelope. Concurrency is a future capacity decision, not a closeout requirement.
  - **Skip the status endpoint, document-only closeout (~1.5 h).** Cheaper but leaves the next slice (real-time bridge) blind to "is the engine even running, what's the backlog". The endpoint is the smallest piece of observability that pays for itself the moment a frontend connects.
  - **Expose status via `ISyncDbContext` extension (add `OutboxEvents` DbSet to the abstraction).** Would bleed an observability concern into the sync abstraction. Instead, `SyncStatusService` lives in `TodoApi/Services/` and depends directly on `TodoContext` (allowed because `TodoApi → TodoApi.Sync` is one-way; no cycle).
  - **Mock `SyncStatusService` in the controller test instead of using the real thing with InMemory.** Would test less. The real service against InMemory is the same shape used by every other controller test in this repo (`TodoListsControllerTests`, etc.) — consistent and exercises the actual queries.
  - **Add a `SyncStatusController` separate from `SyncController`.** One more file for one more `GET`. The existing `SyncController` already aggregates "operate the sync" actions (`POST /run`); status fits naturally.
- **Why:** the engine has been "feature-closed" since slice 7 — the remaining backlog is performance/multi-host/observability, none of which the spec requires. The cardinal need before connecting a real-time consumer is a **stamped boundary**: a single page that answers "what does the engine do, what doesn't it do, how do I operate it, when do I tune what". The closeout section is that stamp; the status endpoint is its runtime counterpart. Slice 9 (real-time bridge backend) becomes a clean next chapter rather than continued sync work.
- **New assumptions:**
  - **Last-run-per-pair is observed via 4 indexed lookups, not a `GROUP BY`.** Portable across providers (LINQ `GROUP BY` translation differs between InMemory and SqlServer) and the result set is bounded to 4 rows by definition. Each lookup hits the existing `(EntityType, StartedAt)` index on `SyncRuns`.
  - **`SyncStatusService` lives in `TodoApi/Services/` (not `TodoApi.Sync`).** Status reading is a presentation concern (consumed by an HTTP endpoint) and pulling from `TodoContext` directly is allowed by the one-way reference (`TodoApi → TodoApi.Sync`). Keeps the Sync project focused on the pipeline.
  - **Status response shape (`SyncStatusResponse`) is a v1 contract for whoever consumes it next** (the real-time frontend or a future ops dashboard). Adding fields is forward-compatible; renaming or removing requires coordination.
- **Debt / follow-ups:**
  - **Slice 9 — Real-time backend bridge** (`TodoSyncHub` + `OutboxBroadcastService` + frontend handoff doc). Already planned at `/Users/matiasromero/.claude/plans/revisa-lo-implementado-el-delegated-gosling.md`.
  - **Status response could include `nextRunEta`** (computed from last `StartedAt` + `Interval`). Trivial to add; wait until a UI actually wants it.
  - **Status endpoint has no auth.** Consistent with the rest of the API (challenge-internal). Production would need at least `[Authorize]` + a role check.
  - **Push-side `PATCH TodoList` (Slice 9 backlog)** — still deferred. The Frozen-limitations table in the closeout names it explicitly.

### 2026-05-10 — Slice 9: Real-time backend bridge — SignalR Hub + Outbox tail consumer + frontend handoff
- **Decision:** add a SignalR hub `TodoSyncHub` at `/hubs/todosync` and a second `BackgroundService` (`OutboxBroadcastService`) that tail-reads `OutboxEvents` and broadcasts a lightweight `ChangeNotification` to all connected clients. **Backend-only this session**: ship the hub + broadcaster + tests + a self-contained handoff doc (`docs/realtime-frontend-integration.md`) that the frontend team uses to wire the React client. Strongly-typed hub (`Hub<ITodoSyncClient>`); pure broadcast logic extracted to `IOutboxBroadcaster` for unit testing; the `BackgroundService` is a thin timing shell. CORS configured for `http://localhost:5173` with `AllowCredentials` (SignalR negotiation requires explicit origins).
- **Alternatives discarded:**
  - **Hook the hub directly into `TodoListService` / `TodoListItemService` / sync services.** Couples business code to SignalR and forces every CRUD call site to remember to publish. The Outbox is already the canonical "sync-relevant change happened" channel — reusing it is one consumer, zero new responsibilities for the writers.
  - **Hook the broadcast into `SyncBackgroundService` phases.** Would only fire after a sync tick — local CRUD changes wait up to `Sync:Interval` (default 60 s) before the user sees their own action. The independent broadcaster polls every `Realtime:BroadcastInterval` (default 2 s).
  - **Mediator / event bus inside the process.** Overkill for a single in-process consumer; adds an indirection without solving anything the broadcaster doesn't already cover.
  - **Send full entity payloads in the notification.** Couples the hub shape to the entity contract (versioning headache: changing the entity changes the wire format), grows the SignalR frame size, and forces clients to duplicate the merge logic the REST endpoints already encode. Refetch-on-notify wins for v1 — one extra round-trip per event in exchange for zero contract duplication. If telemetry ever shows it's a problem, flipping to push-with-payload is a backend-only change behind the same hub method names.
  - **Mark `OutboxEvents.ProcessedAt` from the broadcaster.** That column is owned by the sync engine; mutating it from a parallel consumer would silently break the sync's drain. The broadcaster maintains its own in-memory cursor (last `Id` published) and never touches `ProcessedAt`.
  - **Persist the broadcaster cursor.** Would let us replay events generated while the process was down. Not worth it for v1 because (a) clients bootstrap on `onreconnected` (full refresh covers the gap), (b) the sync engine is the durability layer for inter-process consistency, and (c) the persistence would have to be per-host in a future multi-instance world anyway. Documented as future debt.
  - **Redis SignalR backplane.** Required only if multiple backend instances broadcast to overlapping client pools. Inherits the single-host assumption already documented for the sync engine; multi-host is a different architecture decision, not this slice.
  - **Skip integration test, rely only on the broadcaster unit tests.** Unit tests cover the broadcaster's behaviour but not the actual hub negotiation, the `IHubContext<,>` DI wiring, or the CORS path. The 2 integration tests with `WebApplicationFactory<Program>` + a real `HubConnection` (long-polling transport over the in-process `TestServer.CreateHandler()`) close that gap.
  - **Use WebSockets transport in the integration test.** TestServer in-process does not support WebSocket upgrade in the standard configuration. Long-polling exercises the same hub method dispatch and DI graph; production uses WebSockets via Kestrel. Acceptable trade.
- **Why:** the cardinal goal is "frontend reacts to backend changes without polling, with the smallest possible blast radius on existing code". Reading the Outbox satisfies that perfectly: the outbox **already** records every relevant change, FIFO and durable, so the broadcaster is a pure consumer. Splitting into `OutboxBroadcaster` (logic) + `OutboxBroadcastService` (timing shell) lets us TDD the behaviour with real `TodoContext` + a `SpyClient` while keeping the BackgroundService trivially thin (no test needed beyond the integration path). The handoff doc is mandatory because the cleanest hub in the world is useless if the consumer team doesn't know how to call it — the doc covers transport choice, dedupe, optimistic-vs-broadcast races, CORS, smoke-testing without the React app, and explicit out-of-scope items so the contract boundary is unambiguous.
- **New assumptions:**
  - **Single-host backend.** Same as the sync engine. Cursor in memory, no Redis. Documented as a "what would unfreeze it" line in the handoff doc and in the Frozen Limitations table of the Closeout.
  - **`ChangeNotification` shape is the public wire contract.** Adding fields is forward-compatible (deserializers ignore unknowns); removing or renaming requires coordination with frontend. Versioning policy spelled out in the handoff doc.
  - **The broadcast cursor resets to `MAX(OutboxEvents.Id)` on every process start.** Events generated during downtime are NOT replayed by the hub — clients bootstrap on `onreconnected` (full refresh) to cover the gap. This is intentional: replay would require persisting per-cursor state and gives marginal benefit since the REST API is the authoritative store for any catch-up.
  - **CORS allows `http://localhost:5173` with credentials by default.** Other origins require an `appsettings.json` change (`Cors:AllowedOrigins`). SignalR with credentials cannot use `*` — browser spec.
  - **Long-polling transport is sufficient for in-process integration tests.** Production uses WebSockets via Kestrel. Both exercise the same hub method dispatch and DI graph; differences are at the byte-framing layer.
- **Debt / follow-ups:**
  - **Frontend implementation.** Owned by the React team. Doc at `docs/realtime-frontend-integration.md` is self-contained: install `@microsoft/signalr`, add Vite proxy with `ws: true`, wire `useTodoSyncHub` hook, dispatch refetch per notification.
  - **Replay during disconnect.** Currently covered by `onreconnected` → full bootstrap. If a richer "give me everything since cursor=N" semantics is wanted, persist cursor per-client (probably keyed by some client id sent during negotiation). Not v1.
  - **Multi-host backplane.** Add `Microsoft.AspNetCore.SignalR.StackExchangeRedis` + a Redis instance in `docker-compose.yml`. The broadcaster's per-host cursor would need to be coordinated (a global cursor in Redis or a per-host cursor with deduplication on the client). Requires a multi-host product decision first.
  - **Hub auth.** None today. If the API gains auth, the hub inherits it via `[Authorize]` + `MapHub` ordering; per-user filtering can use SignalR groups keyed by user id.
  - **Hub metrics.** Connection count, broadcast latency, dropped frames. `GET /api/sync/status` already covers "is the engine producing events"; a future Prometheus exporter could surface "is the broadcaster keeping up" too.
  - **`ChangeNotification.parentEntityId`.** The frontend currently has to refetch all lists to know which list an item belongs to. Adding `parentEntityId` (nullable, populated for `TodoListItem` notifications) would let the client refetch only the affected list. Trivial backend change once the frontend confirms the value.

### 2026-05-10 — Documentation closeout (post-slice cleanup)

- **Decision:** consolidate v1 documentation without adding features. Three changes: (1) new `## Out of Scope & What It Would Take` section in `NOTES.md` with 10 sub-sections under a six-header template (status quo · dimensions touched · options · operational implications · test strategy · decision criteria), with §01 *Multi-instance horizontal scaling* at full depth as the worked example of "what would it take" reasoning; (2) two new diagrams under `diagrams/` following `STYLE.md` — `sync-tick-lifecycle.html` (the 5 phases of the BackgroundService timeline) and `lww-decision-tree.html` (per-entity reconciliation branching); (3) cross-links from the existing `Frozen limitations (v1)` table (added a "Detail" column pointing to each §NN sub-section) and from `README.md` (Sync Engine section + Documentation & Diagrams bullets).
- **Alternatives discarded:**
  - **Extend `CHALLENGE.md`.** Frozen upstream contract — not editable per `CLAUDE.md` hard rule.
  - **Move out-of-scope analysis to a new `docs/out-of-scope.md`.** Breaks the "one formal deliverable" pattern (NOTES.md is the document the reviewer opens). Acceptable only if the section grows beyond ~150 lines, which it doesn't.
  - **Skip the section because `Frozen limitations` already enumerates the cuts.** That table is one line per cut — sufficient as an executive index, insufficient to communicate the **technical dimensions, options, and tradeoffs** that signal architectural visibility. The new section is the depth that the table lacks; the table now cross-links into it.
  - **Add a third diagram (multi-instance topology).** High signaling value but requires introducing an "out-of-scope badge" pattern to `STYLE.md` — extra work for marginal extra clarity once §01 already covers the ground in prose. Deferred.
  - **Mark this as Slice 10.** This is consolidation, not new capability — labelling it as a slice would distort the cadence (every prior slice closed a code-bearing feature). Recorded as a post-slice cleanup entry.
- **Why:** a senior reviewer typically evaluates not only what was built but also what was **intentionally not built and why**. Frozen limitations alone leaves the reviewer to infer tradeoffs; the new section makes the reasoning explicit and the criteria for revisiting each cut concrete. The diagrams condense temporally-dispersed prose (the 5 phases of the tick, the LWW branching) into images that are absorbed in 60 seconds — high pedagogical density without adding code.
- **New assumptions:** none. The new section reaffirms existing assumptions (single-host, NTP-aligned clocks, `source_id` literal preservation) and makes their resolution criteria explicit.
- **Debt / follow-ups:**
  - When §01 (multi-instance) is implemented, this entry is superseded by the slice that closes it — at that point, mark the §01 sub-section as `~~Closed in slice N~~` and add a `**Supersedes:** 2026-05-10 Documentation closeout` reference in the new slice's entry.
  - Same pattern applies to §02–§10 individually as they unfreeze.

### 2026-05-10 — Slice 10: Fake External API for local dev showcase

- **Decision:** introduce `TodoApi.FakeExternalApi`, a sibling minimal-API project on port 8080 that implements the upstream `assets/external-api.yaml` contract end-to-end (`GET`/`POST` `/todolists`, `PATCH`/`DELETE` `/todolists/{id}`, `PATCH`/`DELETE` `/todolists/{listId}/todoitems/{itemId}`) with a stateful in-memory store, idempotency replay (cache by `Idempotency-Key` Guid header), a chaos middleware (global random `fail_rate` 0-100% + `delay_ms` 0-30000 + configurable 4xx/5xx `status_code`), and a polling inspection UI at `/` matching `diagrams/STYLE.md` (terminal-schematic, IBM Plex Mono/Sans, amber+cyan, three sections `§01 STATE` / `§02 LAST REQUESTS` / `§03 CHAOS`). Admin surface at `/__admin/{state,reset,chaos,seed}` powers the UI and exposes manual chaos injection plus seeding for adoption (CASE B) demos. Three smoke tests in `TodoApi.Tests/FakeExternalApi/SmokeTests.cs` cover happy POST + idempotency replay + 404 on missing list. Default `ExternalApi:BaseAddress=http://localhost:8080` makes the fake a drop-in replacement for the upstream Docker reference (`crunchloop/challenge-senior-engineer`) without touching `appsettings.json`. Total tests: 208 → 211.
- **Alternatives discarded:**
  - **In-process `FakeExternalTodoListClient`** (DI swap of `IExternalTodoListClient`) — ~50 LOC but bypasses Polly + JSON snake_case + `Idempotency-Key` header semantics, hiding exactly what makes sync visible in a demo. No inspection surface without adding endpoints.
  - **WireMock.Net standalone** (Docker `wiremock/wiremock` or hosted as a tools project) — already a test dep, but stateful CRUD via JSON mappings + scenarios is verbose, and the WireMock admin UI doesn't fit the project's terminal-schematic aesthetic.
  - **Auto-launch the fake from `TodoApi`** via a Development-only `BackgroundService` — couples dev experience to a host conceptually outside the API. Kept as a separate process per the slice 1 "decouple" principle.
  - **Reusing `TodoApi.Sync.External.Models.ExternalTodoList`** (record types) inside the fake — the records are immutable deserialization targets in the sync project, while the fake's store needs mutable state. Re-defined as classes inside `TodoApi.FakeExternalApi.Models`; the JSON contract round-trips because both sides share `JsonNamingPolicy.SnakeCaseLower`.
  - **Top-level statements + global `Program` partial** (the standard for new minimal-API projects) — conflicts with `TodoApi.Tests` already using `WebApplicationFactory<Program>` against the global `TodoApi.Program`. Replaced with explicit `Main` inside `namespace TodoApi.FakeExternalApi;` so smoke tests reference `WebApplicationFactory<TodoApi.FakeExternalApi.Program>` without ambiguity; existing tests against `TodoApi`'s global `Program` are unaffected.
  - **Per-endpoint `LogRequest` calls** (as drafted in the plan) — replaced with a `RequestLoggingMiddleware` so the contract endpoints stay clean and the request log captures every contract path (including chaos-induced 5xx) without per-handler boilerplate. Skip prefixes `/__admin`, `/swagger`, `/_framework` so the UI poll and admin actions don't clutter the timeline.
  - **WireMock-vs-fake parallel test suite** to detect drift between the fake and `external-api.yaml` — high value but distorts the slice scope; left as explicit follow-up.
- **Why:** the cardinal decision is **showcase fidelity over implementation cost**. The user's case-of-use is `(c) Demo/showcase` — interview-time demos and dev-time iteration where Polly retries, idempotency replay, and the actual external response shapes need to be visible. An in-process fake hides exactly the layer that makes sync interesting. A standalone fake server in the same .NET stack as the rest of the solution costs ~600 LOC but mirrors the upstream contract bit-for-bit and gives the project its own visual surface. Stateful CRUD in C# is trivial (`ConcurrentDictionary` + `lock`); the same via WireMock JSON mappings and scenarios would be much more verbose. The terminal-schematic UI was a deliberate investment (option `2.iii`) — it costs one HTML file but pays back in interview narrative ("watch the request appear in real time, flip the chaos slider, see Polly retry"). The chaos middleware (option `3.ii`) closes the resilience-demo gap that the existing WireMock E2E tests cover for automated tests but could not be exercised manually before.
- **New assumptions:**
  - The fake is dev-only — no auth, no persistence, no rate limiting. `localhost:8080` is the only supported binding; the in-memory store resets on process restart.
  - Contract drift between the fake and `assets/external-api.yaml` is detected manually (smoke tests round-trip with `TodoApi.Sync` response shapes; if a new upstream field appears, both sides update in lockstep). A future WireMock-vs-fake parallel suite is the eventual automated detector.
  - Chaos rolls are stateless (per-request `Random.Shared.Next`) — there is no sticky-failure mode. Acceptable: Polly retries on the TodoApi side react to transient 5xx, which is the demo intent. For a deterministic failure run, set `fail_rate=100` temporarily.
  - The fake serves Swagger from `/swagger` and the inspection UI from `/`. The chaos middleware skips `/__admin`, `/swagger`, `/_framework`, and `/`, so neither admin actions nor the UI itself can be disabled by chaos config.
- **Debt / follow-ups:**
  - **WireMock-vs-fake parallel test suite** — repurpose the existing 10 E2E tests in `SyncEndToEndTests.cs` to also run against the fake server hosted in `WebApplicationFactory<TodoApi.FakeExternalApi.Program>`. Would catch contract drift automatically. Future slice.
  - **Chaos with sticky failures / per-endpoint scoping.** Today only global random failure rate. A future iteration could add `{ paths: [...], status_code, sticky: true }` configurations to model "the external is down for `/todolists` only" or "every third PATCH fails". Not in this slice.
  - **Persistence to disk.** The fake is in-memory only; restarting the process resets the world. A `--state-file` flag could persist to JSON, useful for repeatable demo state across restarts. Not in this slice.
  - **Recording mode** that proxies to the real upstream and saves responses, enabling offline playback of upstream's actual replies. Mentioned out-of-scope in the plan.
  - **NU1608 warnings** persist (pre-existing, unrelated to this slice — see slice 5 entry). The new project does not introduce additional warnings.

### 2026-05-10 — Devcontainer hardening for reviewer experience (post-slice cleanup)

- **Decision:** polish the existing `.devcontainer/` so the "Reopen in Container" flow works end-to-end for a fresh reviewer after the multi-project split, **without** re-orchestrating the demo in compose. Five changes: (1) `postCreateCommand` becomes `dotnet tool restore && dotnet restore && dotnet build` so `csharpier` (CI runs `--check`) and `dotnet-ef` are available cold; (2) `forwardPorts: [5000, 8080, 1433]` exposes TodoApi Swagger, FakeApi dashboard, and SQL Server to the reviewer's host; (3) `customizations.vscode.extensions` adds `ms-dotnettools.csdevkit`, `ms-mssql.mssql`, `humao.rest-client` alongside the original Postman extension; (4) `.devcontainer/docker-compose.yml` injects `ConnectionStrings__TodoContext` as an env var on the `app` service so the reviewer never needs to create `appsettings.Development.json`; (5) `applicationUrl` of both web projects' `launchSettings.json` switched to `http://+:<port>` (TodoApi → `:5000` matching the README, FakeApi → `:8080`) so Kestrel binds to all interfaces and `forwardPorts` can tunnel them — and HTTPS is dropped from TodoApi's dev profile to avoid `dotnet dev-certs` friction in the Linux container. README gains a new "Inside the devcontainer" section right after "Database" documenting the two-terminal demo workflow plus host URLs and SQL credentials.
- **Alternatives discarded:**
  - **Full multi-service compose orchestration** — each .NET project as its own container with a Dockerfile, networking via service names, demo via `docker compose up`. Drastically changes the compose shape the reviewer knows from the upstream repo, and creates conceptual collision with the companion repo `crunchloop/challenge-senior-engineer` (itself a Docker-served external API). Two ways to "obtain the external API" would confuse evaluation.
  - **Add `fakeapi` (only) as a compose service** while keeping `app + sqlserver` as the dev shell pair. Milder than full orchestration but suffers the same conceptual collision with the companion repo, and forces the FakeApi to bind to a service-network hostname instead of the `localhost:8080` ergonomic the README already documents.
  - **Compose profiles** (`dev` vs `demo`). Extra cognitive overhead with no clear win at this stage; the demo runs cleanly via `dotnet run` in two terminals.
  - **Version an `appsettings.Development.example.json`** with the sqlserver connection string and instruct the reviewer to copy it. Explicit but adds a manual step; the env-var approach is invisible and works out of the box.
  - **`scripts/run-demo.sh`** wrapping the two `dotnet run` commands in background processes. Attractive friction-reducer but orthogonal to "make the devcontainer work cleanly". Kept as an explicit follow-up.
- **Why:** the existing devcontainer is upstream-provided and is the contract the challenge reviewer opens when they pick "Reopen in Container". When the project grew to three csproj files (`TodoApi`, `TodoApi.Sync` library, `TodoApi.FakeExternalApi`), the devcontainer's shape stayed frozen and gaps accumulated: `dotnet csharpier --check .` failed cold because tools weren't restored; Swagger and the FakeApi dashboard were not reachable from the reviewer's host (no `forwardPorts`); the connection string had no template tied to the `sqlserver` service hostname; the launchSettings still pointed at randomly-chosen Visual Studio dev ports (`5083`/`7027`) instead of the `5000` the README documents in every `curl` example. The fix space is small and high-leverage: stay inside the upstream compose shape, fix env-var/ports/tooling gaps, and add the documentation a fresh reviewer needs. The launchSettings adjustment is the only change outside `.devcontainer/`, justified because the inconsistency between README (`5000`) and launchSettings (`5083`) would be a paper cut for anyone following the README literally — and `forwardPorts: [5000]` only helps when Kestrel actually listens on `5000` bound to all interfaces (`+`, not `localhost`).
- **New assumptions:**
  - Dev TLS is not supported inside this devcontainer. `applicationUrl` is HTTP-only because `dotnet dev-certs https --trust` requires extra setup in the Linux container. If a future slice needs HTTPS in dev, it must add a documented `dotnet dev-certs` step in `postCreateCommand` plus a forwarded HTTPS port.
  - `Password123` is the canonical SA password for the devcontainer SQL Server, matching the original upstream `MSSQL_SA_PASSWORD`. The env-var connection string reuses it; the README documents it for ad-hoc SQL client access from the host.
  - Running outside the devcontainer still works via the user's local `appsettings.Development.json` (gitignored). Inside the devcontainer the env var wins (ASP.NET Core config precedence: env vars > `appsettings.{Env}.json`). Both modes coexist without conflict.
- **Debt / follow-ups:**
  - **`scripts/run-demo.sh`** to wrap FakeApi + TodoApi in background processes with separated logs. Further reduces friction; kept out of this slice because the two-terminal flow is already documented and the script is orthogonal polish.
  - **First-class companion-repo support** from inside the devcontainer (`ExternalApi:BaseAddress=http://host.docker.internal:8080`). Requires `extra_hosts: ["host.docker.internal:host-gateway"]` in compose plus a documentation paragraph for evaluators who want to verify against the real upstream Docker reference. Out of this slice.
  - **HTTPS in dev**, if any future slice needs it — see assumption above.

### 2026-05-10 — Decision: DELETE-with-children stays "1 outbox parent + N orphan-detected children + 404 grace"

- **Decision:** when a local `TodoList` with N child `TodoListItem`s is deleted, `TodoListService.DeleteAsync` writes exactly **one** `OutboxEvent` (`TodoList` / `Delete`). EF cascade hard-deletes the local items without going through the service layer, leaving N orphan `SyncMapping` rows (`EntityType=TodoListItem`, `LocalId` no longer resolves locally). Phase B of `TodoListItemSyncService.PushTodoListItemsAsync` detects them via the anti-join (`ISyncDbContext.GetOrphanedItemMappingsAsync`) and emits one `DELETE /todolists/{parentExternalId}/todoitems/{externalItemId}` per orphan. The external response is typically `404` (the FakeExternalApi cascades child items in memory when the parent list is deleted, see `FakeExternalApi/Services/FakeStore.cs`) and is treated as success by the **404 grace** established in Slice 4. The visible effect on the external API for `DELETE /api/todolists/{id}` of a list with N items is `N × DELETE item (404)` followed by `1 × DELETE list (204)` in the same tick.
- **Alternatives discarded:**
  - **Emit per-item `OutboxEvent`s from `TodoListService.DeleteAsync` before the EF cascade.** Would make the per-item DELETEs explicit instead of relying on the orphan anti-join. Discarded because (a) Phase B already covers this correctly and idempotently for items deleted in any way (cascade, direct service call, or external delete pulled in), (b) it would duplicate work with the orphan detector — both flows would race to delete the same items — and (c) it breaks the Slice 3 invariant "items deleted via service layer only"; either the cascade is replaced with manual delete-children-first in `DeleteAsync`, or EF cascade has to be intercepted, neither of which is justified by the marginal gain.
  - **Optimize Phase B to skip orphan items whose parent `SyncMapping` was deleted in the same tick.** Would reduce the N `404` requests when the external cascades. Discarded because (a) the win is only visible against an external that cascades — if it does not, Phase B is the only thing that cleans up the orphans, so the optimization has to be conditional on knowledge the sync layer does not have, (b) it requires the item push to know what the list push did in the same tick (extra coupling between phases that today are independent under their own try/catch), and (c) the "404 grace" already makes the cost negligible — a `404` is the cheapest possible response, and Phase B already issues the request defensively.
- **Why:** the cardinal reason is **the FakeExternalApi cascading on parent delete is an implementation detail of THIS specific external, not a contract guarantee.** The upstream OpenAPI spec (`assets/external-api.yaml`) does not document whether `DELETE /todolists/{id}` cascades children externally. Treating the per-item DELETEs as redundant relies on undocumented behavior; treating them as required guarantees correctness for any conformant external (cascade or no-cascade). The cost is bounded — N cheap 404s in the cascade case, N necessary 204s in the no-cascade case — and the observability is good: the request log in the FakeExternalApi dashboard makes the flow obvious at a glance, which is exactly the kind of demo surface Slice 10 invested in.
- **New assumptions:** none. This entry confirms (does not extend) the assumptions already recorded in Slice 4: "GET /todolists is the single source of truth for what exists externally" and "404 on DELETE is treated as already-resolved".
- **Debt / follow-ups:**
  - **E2E test for `delete TodoList with N items` is missing.** Existing tests cover the single-list delete (`Delete_LocalListDeleted_PropagatesDeleteToExternal` in `SyncEndToEndTests`) and the orphan-item path indirectly, but no test exercises the combined flow: create list with N items + map externally → delete list locally → run sync → assert 1 outbox parent event + N orphan mappings cleaned via Phase B → external received 1 + N DELETEs (with N of them 404-graced). Gap documented for a future hardening slice.

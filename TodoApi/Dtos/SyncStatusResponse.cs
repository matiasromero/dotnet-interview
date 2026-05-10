using TodoApi.Sync.Models;

namespace TodoApi.Dtos;

/// <summary>
/// Snapshot of the sync engine state for observability — surfaces last completed run
/// per (entity, direction) pair, pending outbox queue size, and the active configuration.
/// Consumed by GET /api/sync/status. Cheap: 4 indexed lookups (one per pair) plus two
/// outbox queries (count + oldest), all using existing indices on SyncRuns and OutboxEvents.
/// </summary>
public sealed record SyncStatusResponse(
    IReadOnlyList<SyncRunSummary> LastRuns,
    int PendingOutboxCount,
    DateTime? OldestPendingOutboxOccurredAt,
    SyncConfigSnapshot Config
);

/// <summary>
/// One sync run reduced to the fields useful to operators: timing window, terminal
/// status, processed/failed counters, and the optional error string captured by the
/// sync services on failure.
/// </summary>
public sealed record SyncRunSummary(
    SyncEntityType EntityType,
    SyncDirection Direction,
    DateTime StartedAt,
    DateTime? FinishedAt,
    SyncRunStatus Status,
    int ItemsProcessed,
    int ItemsFailed,
    string? Error
);

/// <summary>
/// Configuration snapshot exposing the SyncOptions values currently in effect. Surfaced
/// alongside runtime state so a single GET reveals both "what's configured" and
/// "what happened most recently".
/// </summary>
public sealed record SyncConfigSnapshot(
    TimeSpan Interval,
    TimeSpan StartupDelay,
    bool Enabled,
    int OutboxBatchSize,
    TimeSpan OutboxRetention
);

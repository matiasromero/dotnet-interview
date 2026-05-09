using TodoApi.Sync.Services;

namespace TodoApi.Dtos;

public sealed record SyncRunResponse(
    SyncRunResult ListPush,
    SyncRunResult ItemPush,
    SyncRunResult ListPull,
    SyncRunResult ItemPull
);

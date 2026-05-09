using TodoApi.Sync.Models;

namespace TodoApi.Sync.Services;

public record SyncRunResult(int Total, int Pushed, int Failed, SyncRunStatus Status);

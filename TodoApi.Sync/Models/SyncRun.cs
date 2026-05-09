namespace TodoApi.Sync.Models;

public class SyncRun
{
    public long Id { get; set; }
    public SyncEntityType EntityType { get; set; }
    public SyncDirection Direction { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public SyncRunStatus Status { get; set; }
    public int ItemsProcessed { get; set; }
    public int ItemsFailed { get; set; }
    public string? Error { get; set; }
}

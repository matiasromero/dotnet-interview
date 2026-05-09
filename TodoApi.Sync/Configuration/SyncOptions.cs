namespace TodoApi.Sync.Configuration;

public class SyncOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(5);
    public bool Enabled { get; set; } = true;
}

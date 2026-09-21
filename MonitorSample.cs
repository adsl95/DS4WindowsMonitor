namespace DS4WindowsMonitor;

internal sealed record MonitorSample(
    DateTimeOffset Timestamp,
    int Pid,
    string UiState,
    long UiLatencyMs,
    double CpuPercent,
    long WorkingSetMb,
    long PrivateMb,
    int HandleCount,
    int ThreadCount);

namespace DS4WindowsMonitor;

internal sealed record MonitorSample(
    DateTimeOffset Timestamp,
    int Pid,
    string UiState,
    double UiLatencyMs,
    double CpuPercent,
    long WorkingSetMb,
    long PrivateMb,
    int HandleCount,
    int ThreadCount);

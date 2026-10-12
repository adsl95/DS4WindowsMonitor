using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DS4WindowsMonitor;

internal sealed class MonitorContext : ApplicationContext, IDisposable
{
    private readonly EventWaitHandle stopEvent;
    private readonly NotifyIcon trayIcon;
    private readonly CancellationTokenSource cts = new();
    private readonly MonitorConfig config;
    private readonly MonitorService service;
    private bool disposed;

    public MonitorContext(EventWaitHandle stopEvent)
    {
        this.stopEvent = stopEvent;
        AppPaths.Initialize();
        config = MonitorConfig.LoadOrCreate(AppPaths.Root);
        service = new MonitorService(config, AppPaths.Root, cts.Token);

        trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "DS4Windows Monitor"
        };

        ContextMenuStrip menu = new();
        menu.Items.Add("Open monitor folder", null, (_, _) => OpenFolder(AppPaths.Root));
        menu.Items.Add("Open logs", null, (_, _) => OpenFolder(AppPaths.DirectoryFor(config.LogFolderName)));
        menu.Items.Add("Open dumps", null, (_, _) => OpenFolder(AppPaths.DirectoryFor(config.DumpFolderName)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit monitor", null, (_, _) => ExitMonitor());
        trayIcon.ContextMenuStrip = menu;

        Thread monitorThread = new(() => MonitorThreadMain())
        {
            IsBackground = true,
            Name = "DS4Windows Monitor Worker"
        };
        monitorThread.Start();
    }

    private void MonitorThreadMain()
    {
        try
        {
            service.RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            try { service.LogFatal(ex); }
            catch { /* Never let monitor exceptions take down the desktop app. */ }
        }
    }

    private void ExitMonitor()
    {
        try { stopEvent.Set(); }
        catch { /* ignored */ }
        cts.Cancel();
        trayIcon.Visible = false;
        ExitThread();
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { /* ignored */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposed) return;
        disposed = true;
        if (disposing)
        {
            cts.Cancel();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            cts.Dispose();
            stopEvent.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class MonitorService
{
    private readonly MonitorConfig config;
    private readonly string root;
    private readonly CancellationToken token;
    private readonly Queue<MonitorSample> ring = new();
    private readonly object logLock = new();
    private Process? process;
    private int currentPid;
    private DateTimeOffset processStart = DateTimeOffset.MinValue;
    private DateTimeOffset lastMetricsAt = DateTimeOffset.MinValue;
    private DateTimeOffset? lastMetricsSuccessAt;
    private DateTimeOffset lastHeartbeatAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastUiAccessDeniedLogAt = DateTimeOffset.MinValue;
    private DateTimeOffset? slowStartedAt;
    private bool hungEpisode;
    private int consecutiveHungFailures;
    private int dumpsWritten;
    private double? previousCpuMs;
    private DateTimeOffset? previousCpuAt;

    // Keep the last successfully collected metrics between collection intervals.
    // Sentinel values are used only until the first successful collection.
    private double cachedCpuPercent = double.NaN;
    private long cachedWorkingSetMb = -1;
    private long cachedPrivateMb = -1;
    private int cachedHandleCount = -1;
    private int cachedThreadCount = -1;

    private string LogDir => Path.Combine(root, config.LogFolderName);
    private string DumpDir => Path.Combine(root, config.DumpFolderName);
    private string EventDir => Path.Combine(root, config.EventFolderName);
    private string SessionLogPath => Path.Combine(LogDir, $"monitor-{DateTime.Now:yyyyMMdd}.log");

    public MonitorService(MonitorConfig config, string root, CancellationToken token)
    {
        this.config = config;
        this.root = root;
        this.token = token;
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(DumpDir);
        Directory.CreateDirectory(EventDir);
    }

    public async Task RunAsync()
    {
        Log($"START monitorVersion=1.0.1 root=\"{root}\" fallbackRoot={AppPaths.UsingFallbackRoot}");
        Log($"CONFIG poll={config.PollIntervalMs}ms metrics={config.MetricsIntervalMs}ms uiTimeout={config.UiPingTimeoutMs}ms slow={config.SlowUiThresholdMs}ms hang={config.HungAfterMs}ms dumpOnHang={config.CaptureDumpOnHang} dumpFlags=0x{config.DumpTypeFlags:X8}");
        Log("SAFETY: monitor only targets DS4Windows.exe. It does not inject DLLs, hook game processes, enumerate game memory, or open game-process handles.");

        while (!token.IsCancellationRequested)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            try { await SampleOnceAsync(now); }
            catch (Exception ex) { Log($"MONITOR_ERROR type={ex.GetType().Name} message=\"{Sanitize(ex.Message)}\""); }

            try { await Task.Delay(config.PollIntervalMs, token); }
            catch (OperationCanceledException) { break; }
        }

        Log("STOP monitor");
        CleanupProcess();
    }

    public void LogFatal(Exception ex)
        => Log($"FATAL type={ex.GetType().FullName} message=\"{Sanitize(ex.Message)}\"\n{ex}");

    private async Task SampleOnceAsync(DateTimeOffset now)
    {
        Process? p = FindDs4Windows();
        if (p == null)
        {
            if (process != null)
            {
                Log($"PROCESS_EXIT pid={currentPid}");
                EndSession();
            }
            await Task.CompletedTask;
            return;
        }

        AttachProcessIfNeeded(p, now);
        if (process == null) return;

        bool exited;
        try { exited = process.HasExited; }
        catch { exited = true; }
        if (exited)
        {
            Log($"PROCESS_EXIT pid={currentPid}");
            EndSession();
            return;
        }

        string uiState = "NO_WINDOW";
        double uiLatency = -1.0;
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            process.Refresh();
            hwnd = process.MainWindowHandle;
        }
        catch { /* Keep NO_WINDOW state. */ }

        if (hwnd != IntPtr.Zero)
        {
            Stopwatch sw = Stopwatch.StartNew();
            bool ok = false;
            int lastError = 0;
            try
            {
                // UIPI access denial is not evidence that DS4Windows is hung.
                NativeMethods.SetLastError(0);
                ok = NativeMethods.SendMessageTimeout(
                    hwnd,
                    NativeMethods.WM_NULL,
                    UIntPtr.Zero,
                    IntPtr.Zero,
                    NativeMethods.SMTO_BLOCK | NativeMethods.SMTO_ABORTIFHUNG,
                    checked((uint)config.UiPingTimeoutMs),
                    out _suppressResult) != IntPtr.Zero;
                if (!ok) lastError = Marshal.GetLastWin32Error();
            }
            catch { ok = false; }

            sw.Stop();
            uiLatency = sw.Elapsed.TotalMilliseconds;
            if (ok)
            {
                consecutiveHungFailures = 0;
                uiState = uiLatency >= config.SlowUiThresholdMs ? "UI_SLOW" : "UI_OK";
            }
            else if (lastError == 5)
            {
                consecutiveHungFailures = 0;
                uiState = "UI_ACCESS_DENIED";
                if ((now - lastUiAccessDeniedLogAt) >= TimeSpan.FromMinutes(1))
                {
                    lastUiAccessDeniedLogAt = now;
                    Log($"UI_PING_ACCESS_DENIED pid={currentPid} note=run-monitor-at-same-integrity-level-for-ui-detection");
                }
            }
            else
            {
                consecutiveHungFailures++;
                uiState = "UI_NO_RESPONSE";
            }
        }

        // Refresh metrics only at the configured interval. On failure, keep the previous
        // successful values rather than writing misleading zero/default values.
        if (now - lastMetricsAt >= TimeSpan.FromMilliseconds(config.MetricsIntervalMs))
        {
            lastMetricsAt = now;
            try
            {
                process.Refresh();
                long newWorkingSetMb = process.WorkingSet64 / 1024 / 1024;
                long newPrivateMb = process.PrivateMemorySize64 / 1024 / 1024;
                int newHandleCount = process.HandleCount;
                int newThreadCount = process.Threads.Count;
                double cpuMs = process.TotalProcessorTime.TotalMilliseconds;
                double? newCpuPercent = null;

                if (previousCpuMs.HasValue && previousCpuAt.HasValue)
                {
                    double wallMs = Math.Max(1.0, (now - previousCpuAt.Value).TotalMilliseconds);
                    double deltaCpu = Math.Max(0.0, cpuMs - previousCpuMs.Value);
                    newCpuPercent = deltaCpu / wallMs / Environment.ProcessorCount * 100.0;
                }

                cachedWorkingSetMb = newWorkingSetMb;
                cachedPrivateMb = newPrivateMb;
                cachedHandleCount = newHandleCount;
                cachedThreadCount = newThreadCount;
                if (newCpuPercent.HasValue) cachedCpuPercent = newCpuPercent.Value;
                previousCpuMs = cpuMs;
                previousCpuAt = now;
                lastMetricsSuccessAt = now;
            }
            catch (Exception ex)
            {
                Log($"METRICS_ERROR pid={currentPid} type={ex.GetType().Name} message=\"{Sanitize(ex.Message)}\"");
            }
        }

        double cpu = cachedCpuPercent;
        long workingSetMb = cachedWorkingSetMb;
        long privateMb = cachedPrivateMb;
        int handleCount = cachedHandleCount;
        int threadCount = cachedThreadCount;
        long metricsAgeMs = lastMetricsSuccessAt.HasValue
            ? Math.Max(0L, (long)(now - lastMetricsSuccessAt.Value).TotalMilliseconds)
            : -1L;

        ring.Enqueue(new MonitorSample(
            now,
            currentPid,
            uiState,
            uiLatency,
            cpu,
            workingSetMb,
            privateMb,
            handleCount,
            threadCount));

        TrimRing(now);
        bool slow = uiState == "UI_SLOW" || uiState == "UI_NO_RESPONSE";
        if (slow && slowStartedAt == null)
        {
            slowStartedAt = now;
            Log($"UI_SLOW_START pid={currentPid} latencyMs={FormatLatency(uiLatency)} state={uiState}");
        }
        else if (!slow && slowStartedAt.HasValue)
        {
            long duration = (long)(now - slowStartedAt.Value).TotalMilliseconds;
            Log($"UI_SLOW_END pid={currentPid} durationMs={duration}");
            slowStartedAt = null;
        }

        long hungDuration = slowStartedAt.HasValue
            ? (long)(now - slowStartedAt.Value).TotalMilliseconds
            : 0;

        if (consecutiveHungFailures > 0 && hungDuration >= config.HungAfterMs)
        {
            if (!hungEpisode)
            {
                hungEpisode = true;
                await CaptureHangAsync(now, uiLatency, hungDuration);
            }
        }
        if (hungEpisode && uiState == "UI_OK")
        {
            long duration = slowStartedAt.HasValue ? (long)(now - slowStartedAt.Value).TotalMilliseconds : -1;
            Log($"UI_HANG_RECOVERED pid={currentPid} durationMs={duration}");
            hungEpisode = false;
            consecutiveHungFailures = 0;
        }

        if (now - lastHeartbeatAt >= TimeSpan.FromSeconds(10))
        {
            lastHeartbeatAt = now;
            Log($"HEARTBEAT pid={currentPid} ui={uiState} uiLatencyMs={FormatLatency(uiLatency)} cpuPct={Format(cpu)} wsMb={workingSetMb} privateMb={privateMb} handles={handleCount} threads={threadCount} metricsAgeMs={metricsAgeMs}");
        }
    }

    private async Task CaptureHangAsync(DateTimeOffset now, double uiLatency, long hungDurationMs)
    {
        Log($"UI_HANG_DETECTED pid={currentPid} failureCount={consecutiveHungFailures} durationMs={hungDurationMs} lastLatencyMs={FormatLatency(uiLatency)}");
        WriteEventSnapshot(now, uiLatency, hungDurationMs);

        if (!config.CaptureDumpOnHang)
        {
            Log("DUMP_SKIPPED reason=config-disabled");
            await Task.CompletedTask;
            return;
        }
        if (dumpsWritten >= config.MaxDumpsPerProcess)
        {
            Log($"DUMP_SKIPPED reason=max-per-process count={dumpsWritten}");
            return;
        }

        Process? p = process;
        if (p == null)
        {
            Log("DUMP_SKIPPED reason=process-reference-null");
            return;
        }

        string name = $"DS4Windows-{now:yyyyMMdd-HHmmss-fff}-pid{currentPid}-hang.dmp";
        string path = Path.Combine(DumpDir, name);
        uint flags = checked((uint)config.DumpTypeFlags);
        await Task.Run(() =>
        {
            (bool success, string message) = MiniDumpWriter.Write(p, path, flags);
            if (success) dumpsWritten++;
            Log($"DUMP_RESULT success={success} {message}");
        });
    }

    private void WriteEventSnapshot(DateTimeOffset now, double uiLatency, long hungDurationMs)
    {
        string path = Path.Combine(EventDir, $"hang-{now:yyyyMMdd-HHmmss-fff}-pid{currentPid}.txt");
        StringBuilder sb = new();
        sb.AppendLine("DS4Windows Monitor Hang Event");
        sb.AppendLine($"Time: {now:O}");
        sb.AppendLine($"PID: {currentPid}");
        sb.AppendLine($"Process start: {processStart:O}");
        sb.AppendLine($"Last UI latency: {FormatLatency(uiLatency)} ms");
        sb.AppendLine($"Hung failures: {consecutiveHungFailures}");
        sb.AppendLine($"Measured slow/no-response duration: {hungDurationMs} ms");
        sb.AppendLine($"Metrics last success: {(lastMetricsSuccessAt.HasValue ? lastMetricsSuccessAt.Value.ToString("O") : "n/a")}");
        sb.AppendLine();
        sb.AppendLine("Recent samples:");
        foreach (MonitorSample sample in ring)
        {
            sb.AppendLine(string.Join(" | ",
                sample.Timestamp.ToString("O"),
                $"ui={sample.UiState}",
                $"uiMs={FormatLatency(sample.UiLatencyMs)}",
                $"cpu={Format(sample.CpuPercent)}%",
                $"ws={sample.WorkingSetMb}MB",
                $"private={sample.PrivateMb}MB",
                $"handles={sample.HandleCount}",
                $"threads={sample.ThreadCount}"));
        }
        sb.AppendLine();
        sb.AppendLine("Thread snapshot:");
        if (process != null)
        {
            try
            {
                foreach (ProcessThread thread in process.Threads)
                {
                    string waitReason = "-";
                    try
                    {
                        if (thread.ThreadState == System.Diagnostics.ThreadState.Wait)
                            waitReason = thread.WaitReason.ToString();
                    }
                    catch (Exception ex) { waitReason = $"<error:{ex.GetType().Name}>"; }
                    sb.AppendLine(
                        $"tid={thread.Id} state={thread.ThreadState} wait={waitReason} " +
                        $"userMs={thread.UserProcessorTime.TotalMilliseconds:F0} " +
                        $"kernelMs={thread.PrivilegedProcessorTime.TotalMilliseconds:F0} " +
                        $"priority={thread.BasePriority}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"<thread enumeration failed: {ex.GetType().Name}: {ex.Message}>");
            }
        }
        SafeWriteText(path, sb.ToString());
        Log($"HANG_EVENT_WRITTEN path=\"{path}\"");
    }

    private void AttachProcessIfNeeded(Process p, DateTimeOffset now)
    {
        if (process != null && currentPid == p.Id)
        {
            // FindDs4Windows returns a fresh Process wrapper on each poll.
            // Dispose the unused wrapper instead of leaking one per poll.
            p.Dispose();
            return;
        }

        EndSession();
        process = p;
        currentPid = p.Id;
        processStart = SafeStartTime(p);
        dumpsWritten = 0;
        lastMetricsAt = DateTimeOffset.MinValue;
        lastMetricsSuccessAt = null;
        lastHeartbeatAt = DateTimeOffset.MinValue;
        lastUiAccessDeniedLogAt = DateTimeOffset.MinValue;
        slowStartedAt = null;
        hungEpisode = false;
        consecutiveHungFailures = 0;
        previousCpuAt = null;
        previousCpuMs = null;
        ResetMetricCache();
        ring.Clear();
        Log($"PROCESS_START pid={currentPid} start={processStart:O}");
    }

    private void EndSession()
    {
        if (process == null) return;
        try { process.Dispose(); }
        catch { /* ignored */ }
        CleanupProcess();
    }

    private void CleanupProcess()
    {
        process = null;
        currentPid = 0;
        processStart = DateTimeOffset.MinValue;
        dumpsWritten = 0;
        ring.Clear();
        slowStartedAt = null;
        hungEpisode = false;
        consecutiveHungFailures = 0;
        previousCpuAt = null;
        previousCpuMs = null;
        lastMetricsAt = DateTimeOffset.MinValue;
        lastMetricsSuccessAt = null;
        ResetMetricCache();
    }

    private void ResetMetricCache()
    {
        cachedCpuPercent = double.NaN;
        cachedWorkingSetMb = -1;
        cachedPrivateMb = -1;
        cachedHandleCount = -1;
        cachedThreadCount = -1;
    }

    private Process? FindDs4Windows()
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName(config.ProcessName); }
        catch { return null; }
        if (processes.Length == 0) return null;
        if (processes.Length > 1)
            Log($"MULTIPLE_MATCHES name={config.ProcessName} count={processes.Length}");
        Process selected = processes[0];
        for (int i = 1; i < processes.Length; i++) processes[i].Dispose();
        return selected;
    }

    private void TrimRing(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - TimeSpan.FromSeconds(config.RingBufferSeconds);
        while (ring.Count > 0 && ring.Peek().Timestamp < cutoff) ring.Dequeue();
    }

    private static DateTimeOffset SafeStartTime(Process p)
    {
        try { return p.StartTime.ToUniversalTime(); }
        catch { return DateTimeOffset.MinValue; }
    }

    private void Log(string text)
    {
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}|{text}";
        lock (logLock)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(SessionLogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* Logging must never kill monitoring. */ }
        }
    }

    private static void SafeWriteText(string path, string content)
    {
        try { File.WriteAllText(path, content, Encoding.UTF8); }
        catch { /* Best effort only. */ }
    }

    private static string Sanitize(string text)
        => text.Replace("\r", " ").Replace("\n", " ");

    private static string Format(double value)
        => double.IsNaN(value) ? "n/a" : value.ToString("F1", CultureInfo.InvariantCulture);

    private static string FormatLatency(double value)
        => value < 0 || double.IsNaN(value)
            ? "n/a"
            : value.ToString("F3", CultureInfo.InvariantCulture);

    private static UIntPtr _suppressResult;
}

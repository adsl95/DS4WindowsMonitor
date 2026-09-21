using System.Text.Json;
using System.Text.Json.Serialization;

namespace DS4WindowsMonitor;

public sealed class MonitorConfig
{
    public int PollIntervalMs { get; set; } = 250;
    public int MetricsIntervalMs { get; set; } = 1000;
    public int UiPingTimeoutMs { get; set; } = 100;
    public int SlowUiThresholdMs { get; set; } = 200;
    public int HungAfterMs { get; set; } = 1000;
    public bool CaptureDumpOnHang { get; set; } = true;
    public int MaxDumpsPerProcess { get; set; } = 3;
    public int RingBufferSeconds { get; set; } = 60;
    public int DumpTypeFlags { get; set; } = 0x00001921;
    public string ProcessName { get; set; } = "DS4Windows";
    public string LogFolderName { get; set; } = "Logs";
    public string DumpFolderName { get; set; } = "Dumps";
    public string EventFolderName { get; set; } = "Events";
    public string ConfigFileName { get; set; } = "monitor.json";

    [JsonIgnore]
    public string ConfigPath { get; internal set; } = string.Empty;

    public static MonitorConfig LoadOrCreate(string preferredRoot)
    {
        Directory.CreateDirectory(preferredRoot);
        string path = Path.Combine(preferredRoot, "monitor.json");

        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                MonitorConfig? config = JsonSerializer.Deserialize<MonitorConfig>(json, JsonOptions);
                if (config != null)
                {
                    config.ConfigPath = path;
                    config.Validate();
                    return config;
                }
            }
        }
        catch
        {
            // A bad config should never stop monitoring. The default file is replaced.
        }

        MonitorConfig result = new();
        result.ConfigPath = path;
        result.Validate();
        result.Save();
        return result;
    }

    public void Save()
    {
        ConfigPath = string.IsNullOrWhiteSpace(ConfigPath)
            ? Path.Combine(AppPaths.Root, ConfigFileName)
            : ConfigPath;

        string json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    private void Validate()
    {
        PollIntervalMs = Math.Clamp(PollIntervalMs, 100, 5000);
        MetricsIntervalMs = Math.Clamp(MetricsIntervalMs, 250, 10000);
        UiPingTimeoutMs = Math.Clamp(UiPingTimeoutMs, 25, 500);
        SlowUiThresholdMs = Math.Max(SlowUiThresholdMs, UiPingTimeoutMs);
        HungAfterMs = Math.Clamp(HungAfterMs, 500, 10000);
        MaxDumpsPerProcess = Math.Clamp(MaxDumpsPerProcess, 0, 20);
        RingBufferSeconds = Math.Clamp(RingBufferSeconds, 10, 300);
        if (DumpTypeFlags == 0)
            DumpTypeFlags = 0x00001921;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

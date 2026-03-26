// LogicFlow.Dashboard — Settings Manager
// Proprietary implementation by DelgadoLogic.Tech
// Persistent user preferences with JSON storage

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogicFlow.Dashboard;

/// <summary>
/// Manages persistent application settings stored in %LOCALAPPDATA%\LogicFlow\settings.json.
/// Thread-safe with automatic save-on-change.
/// </summary>
public sealed class SettingsManager
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LogicFlow", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly object _lock = new();
    private static UserSettings? _cached;

    /// <summary>
    /// Loads settings from disk (cached after first load).
    /// </summary>
    public static UserSettings Load()
    {
        if (_cached != null) return _cached;

        lock (_lock)
        {
            if (_cached != null) return _cached;

            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    _cached = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions);
                }
            }
            catch { /* Corrupt file, use defaults */ }

            _cached ??= new UserSettings();
            return _cached;
        }
    }

    /// <summary>
    /// Saves current settings to disk.
    /// </summary>
    public static void Save()
    {
        lock (_lock)
        {
            _cached ??= new UserSettings();
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(_cached, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
    }

    /// <summary>
    /// Resets all settings to defaults.
    /// </summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _cached = new UserSettings();
            Save();
        }
    }
}

/// <summary>
/// User-configurable settings for LogicFlow.
/// </summary>
public sealed class UserSettings
{
    // ─── General ────────────────────────────────────────────────
    public bool StartWithWindows { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool CheckForUpdatesAutomatically { get; set; } = true;
    public string Language { get; set; } = "en-US";

    // ─── Appearance ─────────────────────────────────────────────
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public AccentColor Accent { get; set; } = AccentColor.Cyan;
    public bool EnableAnimations { get; set; } = true;
    public double UiScale { get; set; } = 1.0;

    // ─── Scanning ───────────────────────────────────────────────
    public int ScanIntervalMinutes { get; set; } = 30;
    public bool AutoScanOnStartup { get; set; } = false;
    public bool ScanDriversOnStartup { get; set; } = false;
    public bool ScanRegistryOnStartup { get; set; } = false;
    public bool ScanSmartOnStartup { get; set; } = true;

    // ─── Security ───────────────────────────────────────────────
    public bool EnableTelemetryBlocking { get; set; } = true;
    public bool EnableFirewallMonitoring { get; set; } = false;
    public bool AutoScrubPrivacy { get; set; } = false;
    public int PrivacyScrubIntervalHours { get; set; } = 24;

    // ─── Performance ────────────────────────────────────────────
    public bool EnableStartupOptimization { get; set; } = false;
    public bool EnableDebloat { get; set; } = false;
    public bool EnablePowerPlanManagement { get; set; } = false;
    public int DriverScanIntervalDays { get; set; } = 7;

    // ─── Recovery ───────────────────────────────────────────────
    public string DefaultRecoveryOutputPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "LogicFlow_Recovered");
    public bool DeepScanEnabled { get; set; } = false;
    public int MaxRecoveryFileSizeMB { get; set; } = 500;

    // ─── Agent ──────────────────────────────────────────────────
    public bool AgentEnabled { get; set; } = true;
    public int AgentReportRetentionDays { get; set; } = 7;

    // ─── Advanced ───────────────────────────────────────────────
    public bool EnableDebugLogging { get; set; } = false;
    public bool AllowUnsafeRegistryFixes { get; set; } = false;
    public int MaxConcurrentScans { get; set; } = 2;
    public string CustomDataPath { get; set; } = "";

    // ─── Metadata ───────────────────────────────────────────────
    public DateTimeOffset FirstRunDate { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastScanDate { get; set; } = DateTimeOffset.MinValue;
    public int TotalScansRun { get; set; } = 0;
    public int TotalIssuesFixed { get; set; } = 0;
}

public enum ThemeMode { Dark, Light, System }
public enum AccentColor { Cyan, Purple, Blue, Green, Orange, Red, Pink }

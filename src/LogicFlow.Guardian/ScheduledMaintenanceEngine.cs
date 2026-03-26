// ─────────────────────────────────────────────────────────────────────────────
// LogicFlow.Guardian — Scheduled Maintenance Engine
// Registers LogicFlow 1-Click Maintenance as a Windows Scheduled Task.
// ─────────────────────────────────────────────────────────────────────────────

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LogicFlow.Guardian;

/// <summary>
/// Creates and manages Windows scheduled tasks for automatic maintenance.
/// Uses schtasks.exe (works without extra NuGet packages).
/// </summary>
public sealed class ScheduledMaintenanceEngine
{
    private readonly ILogger<ScheduledMaintenanceEngine>? _logger;
    private const string TaskName = "LogicFlow_AutoMaintenance";
    private const string TaskFolder = "\\LogicFlow";

    public ScheduledMaintenanceEngine(ILogger<ScheduledMaintenanceEngine>? logger = null)
        => _logger = logger;

    /// <summary>
    /// Creates or updates the maintenance scheduled task.
    /// </summary>
    public ScheduleResult CreateSchedule(MaintenanceSchedule schedule)
    {
        try
        {
            // Delete existing task if it exists
            DeleteSchedule();

            var exePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppContext.BaseDirectory, "LogicFlow.exe");

            var frequencyFlag = schedule.Frequency switch
            {
                ScheduleFrequency.Daily => "/SC DAILY",
                ScheduleFrequency.Weekly => $"/SC WEEKLY /D {schedule.DayOfWeek?.ToString()?.Substring(0, 3) ?? "SUN"}",
                ScheduleFrequency.Monthly => "/SC MONTHLY /D 1",
                _ => "/SC WEEKLY /D SUN"
            };

            var args = $"/Create /TN \"{TaskFolder}\\{TaskName}\" " +
                       $"/TR \"\\\"{exePath}\\\" --auto-maintenance\" " +
                       $"{frequencyFlag} " +
                       $"/ST {schedule.Time:HH:mm} " +
                       $"/RL HIGHEST " +
                       $"/F " +
                       $"/RU SYSTEM";

            var result = RunSchtasks(args);

            if (result.Success)
            {
                _logger?.LogInformation("Scheduled maintenance: {Freq} at {Time}",
                    schedule.Frequency, schedule.Time);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to create maintenance schedule");
            return new(false, ex.Message);
        }
    }

    /// <summary>
    /// Deletes the maintenance scheduled task.
    /// </summary>
    public ScheduleResult DeleteSchedule()
    {
        try
        {
            return RunSchtasks($"/Delete /TN \"{TaskFolder}\\{TaskName}\" /F");
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }

    /// <summary>
    /// Checks if a maintenance schedule is currently configured.
    /// </summary>
    public ScheduleStatus GetStatus()
    {
        try
        {
            var result = RunSchtasks($"/Query /TN \"{TaskFolder}\\{TaskName}\" /FO CSV /NH");

            if (!result.Success || string.IsNullOrWhiteSpace(result.Message))
                return new(false, ScheduleFrequency.Weekly, TimeOnly.Parse("03:00"), null, "Not configured");

            // Parse CSV output: "TaskName","Next Run Time","Status"
            var parts = result.Message.Split(',');
            var nextRun = parts.Length > 1 ? parts[1].Trim('"') : "";
            var status = parts.Length > 2 ? parts[2].Trim('"') : "";

            DateTimeOffset? nextRunDt = null;
            if (DateTimeOffset.TryParse(nextRun, out var nrd))
                nextRunDt = nrd;

            return new(true, ScheduleFrequency.Weekly, TimeOnly.Parse("03:00"), nextRunDt, status);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to query schedule status");
            return new(false, ScheduleFrequency.Weekly, TimeOnly.Parse("03:00"), null, "Error querying status");
        }
    }

    /// <summary>
    /// Runs the scheduled maintenance tasks immediately (for testing or manual trigger).
    /// </summary>
    public ScheduleResult RunNow()
    {
        try
        {
            return RunSchtasks($"/Run /TN \"{TaskFolder}\\{TaskName}\"");
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }

    // ─── Internal ────────────────────────────────────────────────────────

    private ScheduleResult RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return new(false, "Failed to start schtasks");

        var output = proc.StandardOutput.ReadToEnd();
        var error = proc.StandardError.ReadToEnd();
        proc.WaitForExit(10000);

        return proc.ExitCode == 0
            ? new(true, output.Trim())
            : new(false, string.IsNullOrEmpty(error) ? output.Trim() : error.Trim());
    }
}

// ─── Data Models ────────────────────────────────────────────────────────

public enum ScheduleFrequency { Daily, Weekly, Monthly }

public sealed class MaintenanceSchedule
{
    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Weekly;
    public TimeOnly Time { get; set; } = new(3, 0); // 3:00 AM default
    public DayOfWeek? DayOfWeek { get; set; } = System.DayOfWeek.Sunday;
    public bool RunJunkCleaner { get; set; } = true;
    public bool RunMemoryOptimizer { get; set; } = true;
    public bool RunStartupCleanup { get; set; } = true;
    public bool RunTweakScan { get; set; } = false;
}

public sealed record ScheduleResult(bool Success, string Message);

public sealed record ScheduleStatus(
    bool IsConfigured,
    ScheduleFrequency Frequency,
    TimeOnly Time,
    DateTimeOffset? NextRun,
    string Status);

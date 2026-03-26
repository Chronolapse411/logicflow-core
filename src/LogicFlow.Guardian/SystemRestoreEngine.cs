// ─────────────────────────────────────────────────────────────────────────────
// LogicFlow.Guardian — System Restore Engine
// Creates, lists, and restores Windows System Restore points.
// Auto-creates snapshots before risky operations (driver updates, tweaks).
// ─────────────────────────────────────────────────────────────────────────────

using System.Diagnostics;
using System.Management;
using Microsoft.Extensions.Logging;

namespace LogicFlow.Guardian;

/// <summary>
/// Manages Windows System Restore points.
/// Creates snapshots, lists existing restore points, and triggers restore.
/// </summary>
public sealed class SystemRestoreEngine
{
    private readonly ILogger<SystemRestoreEngine>? _logger;

    public SystemRestoreEngine(ILogger<SystemRestoreEngine>? logger = null) => _logger = logger;

    /// <summary>
    /// Creates a new system restore point with the given description.
    /// Returns true if successful.
    /// </summary>
    public async Task<RestorePointResult> CreateRestorePointAsync(string description = "LogicFlow Checkpoint",
        CancellationToken ct = default)
    {
        _logger?.LogInformation("Creating restore point: {Description}", description);

        try
        {
            // Use PowerShell Checkpoint-Computer which is more reliable than WMI
            var script = $"Checkpoint-Computer -Description '{description.Replace("'", "''")}' -RestorePointType 'APPLICATION_INSTALL' -ErrorAction Stop";

            var output = await RunPowerShell(script, ct);

            _logger?.LogInformation("Restore point created: {Description}", description);
            return new RestorePointResult
            {
                Success = true,
                Description = description,
                CreatedAt = DateTimeOffset.UtcNow,
                Message = "Restore point created successfully"
            };
        }
        catch (Exception ex)
        {
            // Common error: too many restore points in 24 hours
            var message = ex.Message.Contains("frequency", StringComparison.OrdinalIgnoreCase)
                ? "Windows limits restore point creation to once per 24 hours"
                : ex.Message;

            _logger?.LogWarning(ex, "Failed to create restore point");
            return new RestorePointResult
            {
                Success = false,
                Description = description,
                Message = message
            };
        }
    }

    /// <summary>
    /// Lists all available system restore points.
    /// </summary>
    public List<RestorePointInfo> ListRestorePoints()
    {
        _logger?.LogInformation("Listing system restore points...");
        var points = new List<RestorePointInfo>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\default",
                "SELECT * FROM SystemRestore ORDER BY CreationTime DESC");

            foreach (var obj in searcher.Get())
            {
                points.Add(new RestorePointInfo
                {
                    SequenceNumber = Convert.ToInt32(obj["SequenceNumber"]),
                    Description = obj["Description"]?.ToString() ?? "",
                    CreationTime = ManagementDateTimeConverter.ToDateTime(
                        obj["CreationTime"]?.ToString() ?? ""),
                    RestorePointType = ParseRestoreType(
                        Convert.ToInt32(obj["RestorePointType"])),
                    EventType = ParseEventType(
                        Convert.ToInt32(obj["EventType"]))
                });
            }

            _logger?.LogInformation("Found {Count} restore points", points.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to list restore points");
        }

        return points;
    }

    /// <summary>
    /// Initiates a system restore to the specified restore point.
    /// WARNING: This will restart the computer.
    /// </summary>
    public async Task<RestorePointResult> RestoreToPointAsync(int sequenceNumber,
        CancellationToken ct = default)
    {
        _logger?.LogWarning("Initiating system restore to point #{Seq}", sequenceNumber);

        try
        {
            var script = $"Restore-Computer -RestorePoint {sequenceNumber} -Confirm:$false";
            await RunPowerShell(script, ct);

            return new RestorePointResult
            {
                Success = true,
                Message = $"System restore to point #{sequenceNumber} initiated. The computer will restart."
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "System restore failed");
            return new RestorePointResult
            {
                Success = false,
                Message = $"Restore failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Checks if System Restore is enabled on the system drive.
    /// </summary>
    public bool IsSystemRestoreEnabled()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\default",
                "SELECT * FROM SystemRestoreConfig");

            foreach (var obj in searcher.Get())
            {
                var rpSessionInterval = Convert.ToInt32(obj["RPSessionInterval"]);
                return rpSessionInterval > 0;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not check System Restore status");
        }

        return false;
    }

    /// <summary>
    /// Enables System Restore on the system drive.
    /// </summary>
    public async Task<bool> EnableSystemRestore(CancellationToken ct = default)
    {
        try
        {
            var script = "Enable-ComputerRestore -Drive $env:SystemDrive -ErrorAction Stop";
            await RunPowerShell(script, ct);
            _logger?.LogInformation("System Restore enabled on system drive");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to enable System Restore");
            return false;
        }
    }

    // ─── Internal ────────────────────────────────────────────────────────

    private async Task<string> RunPowerShell(string script, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"{script}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start PowerShell");

        var output = await proc.StandardOutput.ReadToEndAsync(ct);
        var error = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException(error.Trim());

        return output;
    }

    private static string ParseRestoreType(int type) => type switch
    {
        0 => "Application Install",
        1 => "Application Uninstall",
        6 => "Restore",
        7 => "Checkpoint",
        10 => "Device Driver Install",
        11 => "First Run",
        12 => "Modify Settings",
        13 => "Cancelled Operation",
        _ => $"Other ({type})"
    };

    private static string ParseEventType(int type) => type switch
    {
        100 => "Begin System Change",
        101 => "End System Change",
        102 => "Begin Nested System Change",
        103 => "End Nested System Change",
        _ => $"Other ({type})"
    };
}

// ─── Data Models ────────────────────────────────────────────────────────

public sealed class RestorePointResult
{
    public bool Success { get; set; }
    public string Description { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RestorePointInfo
{
    public int SequenceNumber { get; set; }
    public string Description { get; set; } = "";
    public DateTime CreationTime { get; set; }
    public string RestorePointType { get; set; } = "";
    public string EventType { get; set; } = "";
}

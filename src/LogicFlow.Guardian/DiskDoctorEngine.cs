// ─────────────────────────────────────────────────────────────────────────────
// LogicFlow.Guardian — Disk Doctor (chkdsk wrapper)
// Checks file system integrity and bad sectors via chkdsk.
// ─────────────────────────────────────────────────────────────────────────────

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LogicFlow.Guardian;

/// <summary>
/// Wraps Windows chkdsk to scan drives for file system errors and bad sectors.
/// Provides both read-only analysis and repair modes.
/// </summary>
public sealed class DiskDoctorEngine
{
    private readonly ILogger<DiskDoctorEngine>? _logger;

    public DiskDoctorEngine(ILogger<DiskDoctorEngine>? logger = null) => _logger = logger;

    /// <summary>
    /// Analyzes a drive for errors without making changes (read-only scan).
    /// </summary>
    public async Task<DiskCheckResult> AnalyzeAsync(string driveLetter = "C",
        CancellationToken ct = default)
    {
        _logger?.LogInformation("Analyzing drive {Drive}: for errors...", driveLetter);

        var result = new DiskCheckResult { DriveLetter = driveLetter.TrimEnd(':') };

        try
        {
            var output = await RunChkdsk($"{driveLetter.TrimEnd(':')}:", readOnly: true, ct);
            result = ParseChkdskOutput(output, result);
            result.RawOutput = output;
            result.ScannedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "chkdsk analysis failed for drive {Drive}", driveLetter);
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Repairs file system errors on a drive (requires admin + may need reboot for system drive).
    /// </summary>
    public async Task<DiskCheckResult> RepairAsync(string driveLetter = "C",
        bool fixBadSectors = false, CancellationToken ct = default)
    {
        _logger?.LogInformation("Repairing drive {Drive}: (badSectors={Fix})...",
            driveLetter, fixBadSectors);

        var result = new DiskCheckResult { DriveLetter = driveLetter.TrimEnd(':'), IsRepairMode = true };

        try
        {
            var args = fixBadSectors ? "/F /R" : "/F";
            var output = await RunChkdsk($"{driveLetter.TrimEnd(':')}:", readOnly: false, ct, args);
            result = ParseChkdskOutput(output, result);
            result.RawOutput = output;
            result.ScannedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "chkdsk repair failed for drive {Drive}", driveLetter);
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Gets a quick health summary for all drives.
    /// </summary>
    public List<DriveHealthSummary> GetAllDrivesHealth()
    {
        var results = new List<DriveHealthSummary>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;

            results.Add(new DriveHealthSummary
            {
                DriveLetter = drive.Name[..1],
                Label = drive.VolumeLabel,
                FileSystem = drive.DriveFormat,
                TotalGB = drive.TotalSize / (1024.0 * 1024 * 1024),
                FreeGB = drive.TotalFreeSpace / (1024.0 * 1024 * 1024),
                UsedPercent = 100.0 * (1.0 - (double)drive.TotalFreeSpace / drive.TotalSize)
            });
        }

        return results;
    }

    // ─── Internal ────────────────────────────────────────────────────────

    private async Task<string> RunChkdsk(string drive, bool readOnly,
        CancellationToken ct, string? extraArgs = null)
    {
        var args = readOnly ? $"{drive}" : $"{drive} {extraArgs ?? "/F"}";

        var psi = new ProcessStartInfo("chkdsk", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Verb = readOnly ? "" : "runas"
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start chkdsk");

        var output = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        _logger?.LogDebug("chkdsk exited with code {Code}", proc.ExitCode);
        return output;
    }

    private DiskCheckResult ParseChkdskOutput(string output, DiskCheckResult result)
    {
        // Parse total disk space
        var totalMatch = Regex.Match(output, @"([\d,]+)\s+bytes total disk space");
        if (totalMatch.Success)
            result.TotalBytes = long.Parse(totalMatch.Groups[1].Value.Replace(",", ""));

        // Parse used space
        var usedMatch = Regex.Match(output, @"([\d,]+)\s+bytes in \d+ files");
        if (usedMatch.Success)
            result.UsedBytes = long.Parse(usedMatch.Groups[1].Value.Replace(",", ""));

        // Parse free space
        var freeMatch = Regex.Match(output, @"([\d,]+)\s+bytes available on disk");
        if (freeMatch.Success)
            result.FreeBytes = long.Parse(freeMatch.Groups[1].Value.Replace(",", ""));

        // Parse bad sectors
        var badMatch = Regex.Match(output, @"([\d,]+)\s+bytes in bad sectors");
        if (badMatch.Success)
        {
            result.BadSectorBytes = long.Parse(badMatch.Groups[1].Value.Replace(",", ""));
            result.HasBadSectors = result.BadSectorBytes > 0;
        }

        // Check for "no problems found"
        result.HasErrors = !output.Contains("Windows has checked the file system and found no problems",
            StringComparison.OrdinalIgnoreCase);

        // Check for corruption
        result.HasCorruption = output.Contains("Correcting error", StringComparison.OrdinalIgnoreCase) ||
                               output.Contains("corrupt", StringComparison.OrdinalIgnoreCase);

        // Parse file count
        var fileMatch = Regex.Match(output, @"([\d,]+)\s+files processed");
        if (fileMatch.Success)
            result.FilesProcessed = int.Parse(fileMatch.Groups[1].Value.Replace(",", ""));

        // Check for reboot required
        result.NeedsReboot = output.Contains("in use", StringComparison.OrdinalIgnoreCase) &&
                             output.Contains("next time", StringComparison.OrdinalIgnoreCase);

        return result;
    }
}

// ─── Data Models ────────────────────────────────────────────────────────

public sealed class DiskCheckResult
{
    public string DriveLetter { get; set; } = "";
    public bool HasErrors { get; set; }
    public bool HasBadSectors { get; set; }
    public bool HasCorruption { get; set; }
    public bool NeedsReboot { get; set; }
    public bool IsRepairMode { get; set; }
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public long FreeBytes { get; set; }
    public long BadSectorBytes { get; set; }
    public int FilesProcessed { get; set; }
    public string? ErrorMessage { get; set; }
    public string RawOutput { get; set; } = "";
    public DateTimeOffset ScannedAt { get; set; }
}

public sealed class DriveHealthSummary
{
    public string DriveLetter { get; set; } = "";
    public string Label { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public double TotalGB { get; set; }
    public double FreeGB { get; set; }
    public double UsedPercent { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// LogicFlow.Guardian — Disk Defragmenter Engine
// Wraps Windows defrag.exe. SSD-aware: runs TRIM/optimize for SSDs,
// traditional defrag for HDDs.
// ─────────────────────────────────────────────────────────────────────────────

using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LogicFlow.Guardian;

/// <summary>
/// Disk defragmentation and optimization engine.
/// Detects SSD vs HDD and runs the appropriate optimization.
/// </summary>
public sealed class DiskDefragEngine
{
    private readonly ILogger<DiskDefragEngine>? _logger;

    public DiskDefragEngine(ILogger<DiskDefragEngine>? logger = null) => _logger = logger;

    /// <summary>
    /// Analyzes a drive's fragmentation level without making changes.
    /// </summary>
    public async Task<DefragAnalysis> AnalyzeAsync(string driveLetter = "C",
        CancellationToken ct = default)
    {
        _logger?.LogInformation("Analyzing fragmentation on drive {Drive}:...", driveLetter);

        var analysis = new DefragAnalysis { DriveLetter = driveLetter.TrimEnd(':') };

        try
        {
            analysis.IsSsd = IsSsd(driveLetter);
            analysis.MediaType = analysis.IsSsd ? "SSD" : "HDD";

            var output = await RunDefrag($"{driveLetter.TrimEnd(':')}:", "/A", ct);
            analysis = ParseAnalysisOutput(output, analysis);
            analysis.RawOutput = output;
            analysis.AnalyzedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Defrag analysis failed for {Drive}", driveLetter);
            analysis.ErrorMessage = ex.Message;
        }

        return analysis;
    }

    /// <summary>
    /// Optimizes a drive — TRIM for SSDs, defrag for HDDs.
    /// Uses the /O flag (optimize) which auto-detects the best strategy.
    /// </summary>
    public async Task<DefragResult> OptimizeAsync(string driveLetter = "C",
        CancellationToken ct = default)
    {
        _logger?.LogInformation("Optimizing drive {Drive}:...", driveLetter);

        var result = new DefragResult { DriveLetter = driveLetter.TrimEnd(':') };

        try
        {
            result.IsSsd = IsSsd(driveLetter);
            result.OperationType = result.IsSsd ? "TRIM/Retrim" : "Defragmentation";

            // /O = Optimize (auto-selects TRIM for SSD, defrag for HDD)
            // /U = Print progress
            var output = await RunDefrag($"{driveLetter.TrimEnd(':')}:", "/O /U", ct);
            result.RawOutput = output;
            result.Success = !output.Contains("error", StringComparison.OrdinalIgnoreCase);
            result.OptimizedAt = DateTimeOffset.UtcNow;

            // Parse pre/post fragmentation from output
            var fragBefore = Regex.Match(output, @"Total fragmented space\s+=\s+(\d+)%");
            if (fragBefore.Success)
                result.FragmentationBefore = int.Parse(fragBefore.Groups[1].Value);

            _logger?.LogInformation("Drive {Drive}: optimization complete ({Type})",
                driveLetter, result.OperationType);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Defrag optimization failed for {Drive}", driveLetter);
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Gets optimization status for all fixed drives.
    /// </summary>
    public List<DriveOptimizationStatus> GetAllDrivesStatus()
    {
        var results = new List<DriveOptimizationStatus>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;

            var letter = drive.Name[..1];
            results.Add(new DriveOptimizationStatus
            {
                DriveLetter = letter,
                Label = drive.VolumeLabel,
                IsSsd = IsSsd(letter),
                MediaType = IsSsd(letter) ? "SSD" : "HDD",
                TotalGB = drive.TotalSize / (1024.0 * 1024 * 1024),
                FreeGB = drive.TotalFreeSpace / (1024.0 * 1024 * 1024)
            });
        }

        return results;
    }

    // ─── Internal ────────────────────────────────────────────────────────

    private bool IsSsd(string driveLetter)
    {
        try
        {
            // Query MSFT_PhysicalDisk for media type
            var scope = new ManagementScope(@"\\.\root\microsoft\windows\storage");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT MediaType FROM MSFT_PhysicalDisk"));

            foreach (var disk in searcher.Get())
            {
                // MediaType: 3=HDD, 4=SSD
                var mediaType = disk["MediaType"]?.ToString();
                if (mediaType == "4") return true;
                if (mediaType == "3") return false;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "SSD detection via WMI failed, falling back to seek penalty check");
        }

        // Fallback: assume SSD if no seek penalty (heuristic)
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Model FROM Win32_DiskDrive WHERE Index=0");
            foreach (var obj in searcher.Get())
            {
                var model = obj["Model"]?.ToString() ?? "";
                if (model.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
                    model.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }

        return false; // Default to HDD
    }

    private async Task<string> RunDefrag(string drive, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("defrag", $"{drive} {args}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start defrag");

        var output = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        _logger?.LogDebug("defrag exited with code {Code}", proc.ExitCode);
        return output;
    }

    private DefragAnalysis ParseAnalysisOutput(string output, DefragAnalysis analysis)
    {
        // Parse fragmentation percentage
        var fragMatch = Regex.Match(output, @"Total fragmented space\s+=\s+(\d+)%");
        if (fragMatch.Success)
            analysis.FragmentationPercent = int.Parse(fragMatch.Groups[1].Value);

        // Parse free space percentage
        var freeMatch = Regex.Match(output, @"Free space\s+=\s+(\d+)%");
        if (freeMatch.Success)
            analysis.FreeSpacePercent = int.Parse(freeMatch.Groups[1].Value);

        // Check if optimization needed
        analysis.NeedsOptimization = analysis.FragmentationPercent > 10 || analysis.IsSsd;
        analysis.Recommendation = analysis.IsSsd
            ? "SSD detected — TRIM optimization recommended"
            : analysis.FragmentationPercent > 10
                ? $"Drive is {analysis.FragmentationPercent}% fragmented — defragmentation recommended"
                : "Drive fragmentation is within acceptable levels";

        return analysis;
    }
}

// ─── Data Models ────────────────────────────────────────────────────────

public sealed class DefragAnalysis
{
    public string DriveLetter { get; set; } = "";
    public bool IsSsd { get; set; }
    public string MediaType { get; set; } = "";
    public int FragmentationPercent { get; set; }
    public int FreeSpacePercent { get; set; }
    public bool NeedsOptimization { get; set; }
    public string Recommendation { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public string RawOutput { get; set; } = "";
    public DateTimeOffset AnalyzedAt { get; set; }
}

public sealed class DefragResult
{
    public string DriveLetter { get; set; } = "";
    public bool Success { get; set; }
    public bool IsSsd { get; set; }
    public string OperationType { get; set; } = "";
    public int FragmentationBefore { get; set; }
    public string? ErrorMessage { get; set; }
    public string RawOutput { get; set; } = "";
    public DateTimeOffset OptimizedAt { get; set; }
}

public sealed class DriveOptimizationStatus
{
    public string DriveLetter { get; set; } = "";
    public string Label { get; set; } = "";
    public bool IsSsd { get; set; }
    public string MediaType { get; set; } = "";
    public double TotalGB { get; set; }
    public double FreeGB { get; set; }
}

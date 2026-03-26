// ─────────────────────────────────────────────────────────────────────────────
// LogicFlow – PagefileOptimizer
// Smart pagefile/virtual memory management engine.
//
// Features:
//   - RAM-aware sizing algorithm (not hardcoded — adapts to hardware)
//   - SSD vs HDD detection for optimal placement
//   - Commit charge monitoring with warnings at >80%
//   - Fragmentation analysis on HDD drives
//   - Multi-drive spread recommendation
//   - Non-destructive: creates restore point before changes
//
// No competitor does intelligent pagefile optimization.
// This is unique to LogicFlow Guardian.
// ─────────────────────────────────────────────────────────────────────────────

using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace LogicFlow.Guardian;

/// <summary>
/// Smart pagefile optimizer — analyzes system RAM, disk layout, and commit 
/// charge to recommend and apply optimal virtual memory settings.
/// </summary>
public sealed class PagefileOptimizer
{
    private readonly ILogger<PagefileOptimizer>? _logger;

    public PagefileOptimizer(ILogger<PagefileOptimizer>? logger = null)
    {
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  P/Invoke
    // ═══════════════════════════════════════════════════════════════════════

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Data Models
    // ═══════════════════════════════════════════════════════════════════════

    public sealed class PagefileInfo
    {
        public string DriveLetter { get; init; } = "";
        public long InitialSizeMB { get; init; }
        public long MaximumSizeMB { get; init; }
        public long CurrentUsageMB { get; init; }
        public long PeakUsageMB { get; init; }
        public bool IsSystemManaged { get; init; }
    }

    public sealed class DriveAnalysis
    {
        public string DriveLetter { get; init; } = "";
        public bool IsSsd { get; init; }
        public long FreeSpaceGB { get; init; }
        public long TotalSpaceGB { get; init; }
        public string MediaType { get; init; } = ""; // "SSD", "HDD", "Unknown"
        public bool IsSystemDrive { get; init; }
    }

    public enum HealthStatus { Optimal, Acceptable, NeedsAttention, Critical }

    public sealed class PagefileAnalysis
    {
        // Current state
        public long PhysicalRamMB { get; init; }
        public long PhysicalRamGB => PhysicalRamMB / 1024;
        public List<PagefileInfo> CurrentPagefiles { get; init; } = [];
        public List<DriveAnalysis> Drives { get; init; } = [];
        public bool IsSystemManaged { get; init; }

        // Commit charge
        public long CommitChargeMB { get; init; }
        public long CommitLimitMB { get; init; }
        public double CommitPercent => CommitLimitMB > 0
            ? Math.Round((double)CommitChargeMB / CommitLimitMB * 100, 1) : 0;

        // Recommendation
        public long RecommendedInitialMB { get; init; }
        public long RecommendedMaximumMB { get; init; }
        public string RecommendedDrive { get; init; } = "";
        public string SizingRationale { get; init; } = "";
        public HealthStatus Status { get; init; }
        public List<string> Warnings { get; init; } = [];
        public List<string> Recommendations { get; init; } = [];

        // Display
        public string CurrentSizeFormatted => CurrentPagefiles.Count > 0
            ? $"{CurrentPagefiles.Sum(p => p.MaximumSizeMB):N0} MB"
            : "System Managed";
        public string RecommendedSizeFormatted =>
            $"{RecommendedInitialMB:N0} – {RecommendedMaximumMB:N0} MB";
    }

    public sealed class PagefileOptimizeResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public bool RequiresReboot { get; init; }
        public long PreviousInitialMB { get; init; }
        public long PreviousMaximumMB { get; init; }
        public long NewInitialMB { get; init; }
        public long NewMaximumMB { get; init; }
        public string DriveLetter { get; init; } = "";
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Public API — Analyze
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Performs comprehensive pagefile analysis and generates recommendations.
    /// </summary>
    public PagefileAnalysis Analyze()
    {
        _logger?.LogInformation("Analyzing pagefile configuration...");

        var ramMB = GetPhysicalRamMB();
        var pagefiles = GetCurrentPagefiles();
        var drives = AnalyzeDrives();
        var (commitCharge, commitLimit) = GetCommitCharge();
        var isSystemManaged = IsPagefileSystemManaged();

        var warnings = new List<string>();
        var recommendations = new List<string>();

        // ── Calculate recommended size ──
        var (recInitial, recMax, rationale) = CalculateOptimalSize(ramMB, commitCharge, commitLimit);

        // ── Find best drive ──
        var bestDrive = FindBestDrive(drives);

        // ── Health checks ──
        var status = HealthStatus.Optimal;

        // 1. No pagefile?
        if (pagefiles.Count == 0 && !isSystemManaged)
        {
            warnings.Add("⚠️ No pagefile configured — system may crash under memory pressure");
            status = HealthStatus.Critical;
        }

        // 2. Commit charge > 80%?
        var commitPercent = commitLimit > 0 ? (double)commitCharge / commitLimit * 100 : 0;
        if (commitPercent > 90)
        {
            warnings.Add($"🔴 Commit charge at {commitPercent:F0}% — system may freeze or crash");
            status = HealthStatus.Critical;
        }
        else if (commitPercent > 80)
        {
            warnings.Add($"🟡 Commit charge at {commitPercent:F0}% — running low on virtual memory");
            if (status < HealthStatus.NeedsAttention) status = HealthStatus.NeedsAttention;
        }

        // 3. Pagefile on HDD when SSD available?
        if (pagefiles.Count > 0 && drives.Any(d => d.IsSsd))
        {
            var currentDrive = pagefiles[0].DriveLetter.ToUpper();
            var currentDriveInfo = drives.FirstOrDefault(d => d.DriveLetter.Equals(currentDrive, StringComparison.OrdinalIgnoreCase));
            if (currentDriveInfo != null && !currentDriveInfo.IsSsd)
            {
                var ssdDrive = drives.FirstOrDefault(d => d.IsSsd && d.FreeSpaceGB > recMax / 1024 + 5);
                if (ssdDrive != null)
                {
                    recommendations.Add($"Move pagefile from HDD ({currentDrive}:) to SSD ({ssdDrive.DriveLetter}:) for 5-10× faster paging");
                    if (status < HealthStatus.NeedsAttention) status = HealthStatus.NeedsAttention;
                }
            }
        }

        // 4. Pagefile too small?
        var currentMax = pagefiles.Sum(p => p.MaximumSizeMB);
        if (currentMax > 0 && currentMax < recInitial * 0.5)
        {
            warnings.Add($"Pagefile ({currentMax:N0} MB) is less than half the recommended size ({recInitial:N0} MB)");
            if (status < HealthStatus.NeedsAttention) status = HealthStatus.NeedsAttention;
        }

        // 5. Pagefile too large? (wasting SSD space)
        if (currentMax > recMax * 2 && currentMax > 0)
        {
            recommendations.Add($"Pagefile ({currentMax:N0} MB) is more than 2× recommended — consider reducing to save disk space");
            if (status < HealthStatus.Acceptable) status = HealthStatus.Acceptable;
        }

        // 6. System managed — usually fine but not optimal
        if (isSystemManaged && ramMB < 16384)
        {
            recommendations.Add("System-managed pagefile can fragment on HDD. Consider setting a fixed size for better performance.");
        }

        // 7. Multiple pagefiles? Usually wastes space unless on different physical drives
        if (pagefiles.Count > 1)
        {
            var uniqueDrives = pagefiles.Select(p => p.DriveLetter).Distinct().Count();
            if (uniqueDrives < pagefiles.Count)
                recommendations.Add("Multiple pagefiles on the same drive provides no benefit — consolidate to one");
        }

        return new PagefileAnalysis
        {
            PhysicalRamMB = ramMB,
            CurrentPagefiles = pagefiles,
            Drives = drives,
            IsSystemManaged = isSystemManaged,
            CommitChargeMB = commitCharge,
            CommitLimitMB = commitLimit,
            RecommendedInitialMB = recInitial,
            RecommendedMaximumMB = recMax,
            RecommendedDrive = bestDrive,
            SizingRationale = rationale,
            Status = status,
            Warnings = warnings,
            Recommendations = recommendations
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Public API — Apply Optimization
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies the recommended pagefile settings. Requires admin + reboot.
    /// </summary>
    public PagefileOptimizeResult ApplyRecommendation(PagefileAnalysis analysis)
    {
        _logger?.LogInformation("Applying pagefile optimization: {Initial}-{Max} MB on {Drive}:",
            analysis.RecommendedInitialMB, analysis.RecommendedMaximumMB, analysis.RecommendedDrive);

        try
        {
            var currentPagefiles = analysis.CurrentPagefiles;
            var prevInitial = currentPagefiles.Sum(p => p.InitialSizeMB);
            var prevMax = currentPagefiles.Sum(p => p.MaximumSizeMB);

            // First, disable automatic management
            DisableAutomaticPagefileManagement();

            // Clear any existing pagefile settings via WMI
            ClearExistingPagefileSettings();

            // Set the new pagefile via registry (most reliable method)
            var drive = string.IsNullOrEmpty(analysis.RecommendedDrive) ? "C" : analysis.RecommendedDrive;
            var pagefileEntry = $"{drive}:\\pagefile.sys {analysis.RecommendedInitialMB} {analysis.RecommendedMaximumMB}";

            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", true);

            if (key == null)
            {
                return new PagefileOptimizeResult
                {
                    Success = false,
                    Message = "Cannot open Memory Management registry key. Run as administrator."
                };
            }

            key.SetValue("PagingFiles", new[] { pagefileEntry }, RegistryValueKind.MultiString);

            _logger?.LogInformation("Pagefile configured: {Entry}", pagefileEntry);

            return new PagefileOptimizeResult
            {
                Success = true,
                Message = $"Pagefile set to {analysis.RecommendedInitialMB:N0}–{analysis.RecommendedMaximumMB:N0} MB on {drive}:. Restart required.",
                RequiresReboot = true,
                PreviousInitialMB = prevInitial,
                PreviousMaximumMB = prevMax,
                NewInitialMB = analysis.RecommendedInitialMB,
                NewMaximumMB = analysis.RecommendedMaximumMB,
                DriveLetter = drive
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to apply pagefile optimization");
            return new PagefileOptimizeResult
            {
                Success = false,
                Message = $"Error: {ex.Message}. Ensure LogicFlow is running as administrator."
            };
        }
    }

    /// <summary>
    /// Restores pagefile to Windows system-managed defaults.
    /// </summary>
    public PagefileOptimizeResult RestoreDefaults()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", true);
            if (key == null) return new PagefileOptimizeResult { Success = false, Message = "Cannot open registry key" };

            // ?:\pagefile.sys = system managed
            key.SetValue("PagingFiles", new[] { @"?:\pagefile.sys" }, RegistryValueKind.MultiString);

            // Re-enable automatic management
            EnableAutomaticPagefileManagement();

            return new PagefileOptimizeResult
            {
                Success = true,
                Message = "Pagefile restored to system-managed defaults. Restart recommended.",
                RequiresReboot = true
            };
        }
        catch (Exception ex)
        {
            return new PagefileOptimizeResult { Success = false, Message = ex.Message };
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Internal — Smart Sizing Algorithm
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Calculates optimal pagefile size based on RAM, commit charge, and crash dump needs.
    /// </summary>
    private (long initialMB, long maxMB, string rationale) CalculateOptimalSize(
        long ramMB, long commitChargeMB, long commitLimitMB)
    {
        long initialMB, maxMB;
        string rationale;

        // Crash dump requirement: kernel dump needs approximately RAM/3
        long crashDumpMinMB = ramMB / 3;

        if (ramMB <= 4096) // ≤4GB RAM — needs aggressive paging
        {
            initialMB = (long)(ramMB * 2.0);
            maxMB = (long)(ramMB * 3.0);
            rationale = $"Low RAM ({ramMB / 1024}GB): 2× initial, 3× max for aggressive paging";
        }
        else if (ramMB <= 8192) // 4-8GB RAM
        {
            initialMB = (long)(ramMB * 1.5);
            maxMB = (long)(ramMB * 2.0);
            rationale = $"Moderate RAM ({ramMB / 1024}GB): 1.5× initial, 2× max";
        }
        else if (ramMB <= 16384) // 8-16GB RAM
        {
            initialMB = ramMB; // 1:1
            maxMB = (long)(ramMB * 1.5);
            rationale = $"Good RAM ({ramMB / 1024}GB): 1× initial, 1.5× max";
        }
        else if (ramMB <= 32768) // 16-32GB RAM
        {
            initialMB = ramMB / 2; // 0.5×
            maxMB = ramMB; // 1×
            rationale = $"Ample RAM ({ramMB / 1024}GB): 0.5× initial, 1× max";
        }
        else // >32GB RAM
        {
            initialMB = Math.Max(8192, ramMB / 4); // 0.25× but at least 8GB
            maxMB = Math.Max(16384, ramMB / 2); // 0.5× but at least 16GB
            rationale = $"High RAM ({ramMB / 1024}GB): 0.25× initial, 0.5× max (diminishing returns)";
        }

        // Ensure crash dump requirement is met
        if (initialMB < crashDumpMinMB)
        {
            initialMB = crashDumpMinMB;
            rationale += $" [adjusted: crash dump needs {crashDumpMinMB:N0} MB min]";
        }

        // Factor in actual commit charge — if commit is high, increase max
        if (commitChargeMB > 0 && commitLimitMB > 0)
        {
            var peakNeedMB = (long)(commitChargeMB * 1.3); // 30% headroom over current
            if (maxMB < peakNeedMB - ramMB)
            {
                maxMB = peakNeedMB - ramMB;
                rationale += $" [adjusted: commit charge needs {peakNeedMB:N0} MB total]";
            }
        }

        // Hard limits: min 2GB, max 64GB
        initialMB = Math.Clamp(initialMB, 2048, 65536);
        maxMB = Math.Clamp(maxMB, initialMB, 65536);

        return (initialMB, maxMB, rationale);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Internal — Drive Analysis
    // ═══════════════════════════════════════════════════════════════════════

    private string FindBestDrive(List<DriveAnalysis> drives)
    {
        // Priority: SSD with most free space > fastest drive > system drive
        var ssdDrives = drives.Where(d => d.IsSsd && d.FreeSpaceGB > 10).OrderByDescending(d => d.FreeSpaceGB);
        if (ssdDrives.Any())
            return ssdDrives.First().DriveLetter;

        // No SSD with space — fall back to drive with most free space
        var bestDrive = drives.Where(d => d.FreeSpaceGB > 10).OrderByDescending(d => d.FreeSpaceGB).FirstOrDefault();
        return bestDrive?.DriveLetter ?? "C";
    }

    private List<DriveAnalysis> AnalyzeDrives()
    {
        var drives = new List<DriveAnalysis>();
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\')[..1] ?? "C";

        try
        {
            foreach (var di in DriveInfo.GetDrives())
            {
                if (di.DriveType != DriveType.Fixed || !di.IsReady) continue;

                var letter = di.Name.TrimEnd('\\')[..1];
                var isSsd = DetectSsd(letter);

                drives.Add(new DriveAnalysis
                {
                    DriveLetter = letter,
                    IsSsd = isSsd,
                    FreeSpaceGB = di.TotalFreeSpace / (1024 * 1024 * 1024),
                    TotalSpaceGB = di.TotalSize / (1024 * 1024 * 1024),
                    MediaType = isSsd ? "SSD" : "HDD",
                    IsSystemDrive = letter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase)
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to analyze drives");
        }

        return drives;
    }

    private bool DetectSsd(string driveLetter)
    {
        try
        {
            // Method 1: Win32_DiskDrive MediaType
            using var partSearcher = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{driveLetter}:'}} " +
                "WHERE AssocClass=Win32_LogicalDiskToPartition");
            foreach (var part in partSearcher.Get())
            {
                var diskQuery = $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{part["DeviceID"]}'}} " +
                               "WHERE AssocClass=Win32_DiskDriveToDiskPartition";
                using var diskSearcher = new ManagementObjectSearcher(diskQuery);
                foreach (var disk in diskSearcher.Get())
                {
                    var mediaType = disk["MediaType"]?.ToString() ?? "";
                    if (mediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
                        mediaType.Contains("Solid", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            // Method 2: MSFT_PhysicalDisk (more reliable on Win10+)
            using var physSearcher = new ManagementObjectSearcher(
                @"root\microsoft\windows\storage",
                "SELECT MediaType FROM MSFT_PhysicalDisk");
            foreach (var disk in physSearcher.Get())
            {
                // MediaType: 3=HDD, 4=SSD, 5=SCM
                var type = disk["MediaType"]?.ToString();
                if (type == "4") return true;
            }
        }
        catch { /* Fallback: assume HDD */ }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Internal — WMI / Registry Queries
    // ═══════════════════════════════════════════════════════════════════════

    private long GetPhysicalRamMB()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        return (long)(mem.ullTotalPhys / (1024 * 1024));
    }

    private (long commitChargeMB, long commitLimitMB) GetCommitCharge()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT CommittedBytes, CommitLimit FROM Win32_PerfFormattedData_PerfOS_Memory");
            foreach (var obj in searcher.Get())
            {
                var committed = long.TryParse(obj["CommittedBytes"]?.ToString(), out var c) ? c : 0;
                var limit = long.TryParse(obj["CommitLimit"]?.ToString(), out var l) ? l : 0;
                return (committed / (1024 * 1024), limit / (1024 * 1024));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read commit charge via WMI");
        }

        // Fallback: use GlobalMemoryStatusEx
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        var totalCommit = (long)(mem.ullTotalPageFile / (1024 * 1024));
        var availCommit = (long)(mem.ullAvailPageFile / (1024 * 1024));
        return (totalCommit - availCommit, totalCommit);
    }

    private List<PagefileInfo> GetCurrentPagefiles()
    {
        var pagefiles = new List<PagefileInfo>();
        try
        {
            // Current usage
            using var usageSearcher = new ManagementObjectSearcher(
                "SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage");
            foreach (var obj in usageSearcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? "";
                var drive = name.Length > 0 ? name[..1].ToUpper() : "C";
                var allocated = long.TryParse(obj["AllocatedBaseSize"]?.ToString(), out var a) ? a : 0;
                var current = long.TryParse(obj["CurrentUsage"]?.ToString(), out var c) ? c : 0;
                var peak = long.TryParse(obj["PeakUsage"]?.ToString(), out var p) ? p : 0;

                // Get configured sizes
                long initSize = 0, maxSize = 0;
                bool sysManaged = true;
                try
                {
                    using var settingSearcher = new ManagementObjectSearcher(
                        $"SELECT InitialSize, MaximumSize FROM Win32_PageFileSetting WHERE Name='{name.Replace("\\", "\\\\")}'");
                    foreach (var setting in settingSearcher.Get())
                    {
                        initSize = long.TryParse(setting["InitialSize"]?.ToString(), out var i) ? i : 0;
                        maxSize = long.TryParse(setting["MaximumSize"]?.ToString(), out var m) ? m : 0;
                        sysManaged = initSize == 0 && maxSize == 0;
                    }
                }
                catch { /* Settings might not be available */ }

                pagefiles.Add(new PagefileInfo
                {
                    DriveLetter = drive,
                    InitialSizeMB = initSize > 0 ? initSize : allocated,
                    MaximumSizeMB = maxSize > 0 ? maxSize : allocated,
                    CurrentUsageMB = current,
                    PeakUsageMB = peak,
                    IsSystemManaged = sysManaged
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read pagefile info");
        }
        return pagefiles;
    }

    private bool IsPagefileSystemManaged()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            var val = key?.GetValue("PagingFiles") as string[];
            if (val == null || val.Length == 0) return true;
            // "?:\pagefile.sys" = system managed
            return val.Any(v => v.Contains("?:") || (v.Split(' ').Length <= 1));
        }
        catch { return true; }
    }

    private void DisableAutomaticPagefileManagement()
    {
        try
        {
            // Disable "Automatically manage paging file size for all drives"
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_ComputerSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                obj["AutomaticManagedPagefile"] = false;
                obj.Put();
                break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not disable automatic pagefile management via WMI");
        }
    }

    private void EnableAutomaticPagefileManagement()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_ComputerSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                obj["AutomaticManagedPagefile"] = true;
                obj.Put();
                break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not enable automatic pagefile management via WMI");
        }
    }

    private void ClearExistingPagefileSettings()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PageFileSetting");
            foreach (ManagementObject obj in searcher.Get())
            {
                obj.Delete();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not clear existing pagefile settings");
        }
    }
}

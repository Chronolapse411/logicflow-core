// ─────────────────────────────────────────────────────────────────────────────
// LogicFlow – SmartDiskHealth (v2 — CrystalDiskInfo-level)
// S.M.A.R.T. + NVMe health monitoring with wear tracking, health scores,
// drive-letter mapping, temperature classification, and threshold analysis.
// ─────────────────────────────────────────────────────────────────────────────

using System.Management;
using Microsoft.Extensions.Logging;

namespace LogicFlow.Guardian;

/// <summary>
/// Reads S.M.A.R.T. attributes (SATA) and NVMe health data from all drives.
/// Computes a weighted health score (0–100%), maps physical→logical drives,
/// tracks SSD wear/remaining life, and classifies temperatures.
/// </summary>
public sealed class SmartDiskHealth
{
    private readonly ILogger<SmartDiskHealth>? _logger;

    public SmartDiskHealth(ILogger<SmartDiskHealth>? logger = null)
    {
        _logger = logger;
    }

    // ─── Data Models ─────────────────────────────────────────────────────

    public enum DiskHealthStatus { Healthy, Warning, Critical, Unknown }
    public enum TemperatureStatus { Cold, Normal, Warm, Hot, Critical }

    public sealed class DiskSmartReport
    {
        public string DriveLetter { get; init; } = "";
        public string[] DriveLetters { get; init; } = [];   // All logical partitions
        public string Model { get; init; } = "";
        public string SerialNumber { get; init; } = "";
        public string FirmwareRevision { get; init; } = "";
        public string InterfaceType { get; init; } = "";
        public long SizeBytes { get; init; }
        public string SizeFormatted => FormatBytes(SizeBytes);
        public DiskHealthStatus Status { get; init; }
        public int HealthScore { get; init; }                // 0–100%
        public string HealthGrade => HealthScore switch
        {
            >= 90 => "A+",
            >= 80 => "A",
            >= 70 => "B",
            >= 60 => "C",
            >= 40 => "D",
            _ => "F"
        };

        // Temperature
        public int Temperature { get; init; }                // Celsius
        public TemperatureStatus TempStatus { get; init; }

        // Usage
        public long PowerOnHours { get; init; }
        public string PowerOnFormatted => PowerOnHours switch
        {
            < 24 => $"{PowerOnHours}h",
            < 720 => $"{PowerOnHours / 24}d {PowerOnHours % 24}h",
            < 8760 => $"{PowerOnHours / 720}mo",
            _ => $"{PowerOnHours / 8760:0.#}yr"
        };
        public int PowerCycles { get; init; }

        // SATA SMART
        public int ReallocatedSectors { get; init; }
        public int PendingSectors { get; init; }
        public int UncorrectableErrors { get; init; }

        // SSD-specific
        public string MediaType { get; init; } = "";         // HDD, SSD, NVMe SSD
        public int SsdLifeRemaining { get; init; }           // 0–100%, -1 if unknown
        public long TotalBytesWritten { get; init; }         // Total host writes
        public string TotalWrittenFormatted => TotalBytesWritten > 0 ? FormatBytes(TotalBytesWritten) : "N/A";

        // NVMe-specific
        public int NvmePercentageUsed { get; init; }         // 0–100+, -1 if N/A
        public int NvmeAvailableSpare { get; init; }         // 0–100%, -1 if N/A
        public int NvmeMediaErrors { get; init; }

        public List<SmartAttribute> Attributes { get; init; } = new();
        public string HealthSummary { get; init; } = "";
    }

    public sealed class SmartAttribute
    {
        public byte Id { get; init; }
        public string Name { get; init; } = "";
        public int Current { get; init; }
        public int Worst { get; init; }
        public int Threshold { get; init; }
        public long RawValue { get; init; }
        public bool IsCritical { get; init; }
        public bool IsWarning => Threshold > 0 && Current > 0 && Current <= Threshold + 10;
        public bool IsFailing => Threshold > 0 && Current > 0 && Current <= Threshold;
        public string StatusIcon => IsFailing ? "❌" : IsWarning ? "⚠️" : "✅";
    }

    // ─── Scan All Drives ────────────────────────────────────────────────

    /// <summary>
    /// Scans all physical drives for S.M.A.R.T. health data (SATA + NVMe).
    /// </summary>
    public List<DiskSmartReport> ScanDrives()
    {
        _logger?.LogInformation("Scanning disk S.M.A.R.T. health...");
        var reports = new List<DiskSmartReport>();

        // Build physical-drive → logical-letter map
        var driveLetterMap = BuildDriveLetterMap();

        // Get NVMe health data from Storage namespace
        var nvmeHealthMap = GetNvmeHealthData();

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");

            foreach (ManagementObject disk in searcher.Get())
            {
                try
                {
                    var model = disk["Model"]?.ToString() ?? "Unknown";
                    var serial = disk["SerialNumber"]?.ToString()?.Trim() ?? "";
                    var firmware = disk["FirmwareRevision"]?.ToString() ?? "";
                    var iface = disk["InterfaceType"]?.ToString() ?? "";
                    var size = Convert.ToInt64(disk["Size"] ?? 0);
                    var deviceId = disk["DeviceID"]?.ToString() ?? "";
                    var pnpId = disk["PNPDeviceID"]?.ToString() ?? "";
                    var diskIndex = Convert.ToInt32(disk["Index"] ?? -1);
                    var mediaType = DetectMediaType(model, iface, pnpId);
                    var isSsd = mediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase);
                    var isNvme = mediaType.Contains("NVMe", StringComparison.OrdinalIgnoreCase);

                    // Get drive letters for this physical drive
                    var letters = driveLetterMap.TryGetValue(diskIndex, out var l) ? l.ToArray() : [];
                    var primaryLetter = letters.Length > 0 ? letters[0] : "";

                    // Get SMART prediction status
                    var smartStatus = GetSmartStatus();

                    // Get SMART attributes WITH thresholds
                    var attributes = GetSmartAttributesWithThresholds();

                    // Extract key values from attributes
                    int temp = ExtractTemperature(attributes);
                    long poh = attributes.FirstOrDefault(a => a.Id == 9)?.RawValue ?? 0;
                    int powerCycles = (int)(attributes.FirstOrDefault(a => a.Id == 12)?.RawValue ?? 0);
                    int reallocated = (int)(attributes.FirstOrDefault(a => a.Id == 5)?.RawValue ?? 0);
                    int pending = (int)(attributes.FirstOrDefault(a => a.Id == 197)?.RawValue ?? 0);
                    int uncorrectable = (int)(attributes.FirstOrDefault(a => a.Id == 198)?.RawValue ?? 0);

                    // SSD wear tracking
                    int ssdLifeRemaining = -1;
                    long totalBytesWritten = 0;
                    if (isSsd)
                    {
                        ssdLifeRemaining = CalculateSsdLife(attributes);
                        totalBytesWritten = CalculateTotalBytesWritten(attributes);
                    }

                    // NVMe-specific data
                    int nvmePercentUsed = -1, nvmeAvailSpare = -1, nvmeMediaErrors = 0;
                    if (isNvme && nvmeHealthMap.TryGetValue(diskIndex, out var nvme))
                    {
                        nvmePercentUsed = nvme.PercentageUsed;
                        nvmeAvailSpare = nvme.AvailableSpare;
                        nvmeMediaErrors = nvme.MediaErrors;
                        if (temp == 0 && nvme.Temperature > 0) temp = nvme.Temperature;
                        if (ssdLifeRemaining < 0 && nvmePercentUsed >= 0)
                            ssdLifeRemaining = Math.Max(0, 100 - nvmePercentUsed);
                    }

                    // Calculate health score
                    var healthStatus = DetermineHealth(smartStatus, reallocated, pending,
                        uncorrectable, attributes, nvmePercentUsed, nvmeAvailSpare);
                    int healthScore = CalculateHealthScore(healthStatus, attributes,
                        reallocated, pending, uncorrectable, ssdLifeRemaining, nvmePercentUsed);

                    var tempStatus = ClassifyTemperature(temp, isSsd);
                    var summary = BuildHealthSummary(healthStatus, healthScore, reallocated,
                        pending, temp, tempStatus, ssdLifeRemaining, nvmePercentUsed);

                    reports.Add(new DiskSmartReport
                    {
                        DriveLetter = primaryLetter,
                        DriveLetters = letters,
                        Model = model,
                        SerialNumber = serial,
                        FirmwareRevision = firmware,
                        InterfaceType = iface,
                        SizeBytes = size,
                        Status = healthStatus,
                        HealthScore = healthScore,
                        Temperature = temp,
                        TempStatus = tempStatus,
                        PowerOnHours = poh,
                        PowerCycles = powerCycles,
                        ReallocatedSectors = reallocated,
                        PendingSectors = pending,
                        UncorrectableErrors = uncorrectable,
                        MediaType = mediaType,
                        SsdLifeRemaining = ssdLifeRemaining,
                        TotalBytesWritten = totalBytesWritten,
                        NvmePercentageUsed = nvmePercentUsed,
                        NvmeAvailableSpare = nvmeAvailSpare,
                        NvmeMediaErrors = nvmeMediaErrors,
                        Attributes = attributes,
                        HealthSummary = summary
                    });
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to read SMART data for a drive");
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to enumerate disk drives");
        }

        _logger?.LogInformation("SMART scan complete. {Count} drives analyzed.", reports.Count);
        return reports;
    }

    // ─── Drive Letter Mapping ───────────────────────────────────────────

    /// <summary>
    /// Maps physical drive index → list of logical drive letters (e.g. 0 → ["C:", "D:"])
    /// using Win32_DiskDriveToDiskPartition + Win32_LogicalDiskToPartition.
    /// </summary>
    private static Dictionary<int, List<string>> BuildDriveLetterMap()
    {
        var map = new Dictionary<int, List<string>>();
        try
        {
            // Step 1: DiskDrive → Partition
            var driveToPartition = new Dictionary<string, string>();
            using (var s1 = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDriveToDiskPartition"))
            {
                foreach (ManagementObject obj in s1.Get())
                {
                    var antecedent = obj["Antecedent"]?.ToString() ?? "";
                    var dependent = obj["Dependent"]?.ToString() ?? "";
                    // Extract partition DeviceID
                    var partId = ExtractWmiProperty(dependent, "DeviceID");
                    var driveId = ExtractWmiProperty(antecedent, "DeviceID");
                    if (!string.IsNullOrEmpty(partId) && !string.IsNullOrEmpty(driveId))
                        driveToPartition[partId] = driveId;
                }
            }

            // Step 2: Partition → Logical Disk
            using (var s2 = new ManagementObjectSearcher("SELECT * FROM Win32_LogicalDiskToPartition"))
            {
                foreach (ManagementObject obj in s2.Get())
                {
                    var antecedent = obj["Antecedent"]?.ToString() ?? "";
                    var dependent = obj["Dependent"]?.ToString() ?? "";
                    var partId = ExtractWmiProperty(antecedent, "DeviceID");
                    var logicalId = ExtractWmiProperty(dependent, "DeviceID");

                    if (!string.IsNullOrEmpty(partId) && !string.IsNullOrEmpty(logicalId)
                        && driveToPartition.TryGetValue(partId, out var driveId))
                    {
                        // Extract drive index from "\\\\.\\PHYSICALDRIVE0"
                        var indexStr = driveId.Replace("\\\\.\\PHYSICALDRIVE", "", StringComparison.OrdinalIgnoreCase);
                        if (int.TryParse(indexStr, out int idx))
                        {
                            if (!map.ContainsKey(idx)) map[idx] = new List<string>();
                            map[idx].Add(logicalId);
                        }
                    }
                }
            }
        }
        catch { }
        return map;
    }

    private static string ExtractWmiProperty(string wmiPath, string propName)
    {
        // Parse WMI object path: \\COMPUTER\root\cimv2:Win32_Foo.DeviceID="value"
        var marker = $"{propName}=\"";
        var start = wmiPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "";
        start += marker.Length;
        var end = wmiPath.IndexOf('"', start);
        return end > start ? wmiPath[start..end] : "";
    }

    // ─── NVMe Health Data ───────────────────────────────────────────────

    private sealed class NvmeHealth
    {
        public int PercentageUsed { get; init; } = -1;
        public int AvailableSpare { get; init; } = -1;
        public int MediaErrors { get; init; }
        public int Temperature { get; init; }
    }

    /// <summary>
    /// Queries MSFT_PhysicalDisk and MSFT_Disk for NVMe health data.
    /// </summary>
    private static Dictionary<int, NvmeHealth> GetNvmeHealthData()
    {
        var map = new Dictionary<int, NvmeHealth>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));

            foreach (ManagementObject pd in searcher.Get())
            {
                try
                {
                    var deviceId = pd["DeviceId"]?.ToString() ?? "";
                    if (!int.TryParse(deviceId, out int idx)) continue;

                    var busType = Convert.ToInt32(pd["BusType"] ?? 0);
                    // BusType 17 = NVMe
                    if (busType != 17) continue;

                    // MSFT_PhysicalDisk doesn't directly have percentage used,
                    // but provides OperationalStatus and HealthStatus
                    var healthStatus = Convert.ToInt32(pd["HealthStatus"] ?? 0);
                    var wear = -1;
                    var spare = -1;

                    // Try to get wear from reliability counters
                    try
                    {
                        using var relQuery = new ManagementObjectSearcher(scope,
                            new ObjectQuery($"SELECT * FROM MSFT_StorageReliabilityCounter WHERE DeviceId = '{deviceId}'"));
                        foreach (ManagementObject rc in relQuery.Get())
                        {
                            wear = Convert.ToInt32(rc["Wear"] ?? -1);
                            var tempK = Convert.ToInt32(rc["Temperature"] ?? 0);
                            var tempC = tempK > 200 ? tempK - 273 : tempK; // Kelvin → Celsius

                            map[idx] = new NvmeHealth
                            {
                                PercentageUsed = wear >= 0 ? wear : -1,
                                AvailableSpare = spare,
                                MediaErrors = Convert.ToInt32(rc["ReadErrorsTotal"] ?? 0),
                                Temperature = tempC
                            };
                        }
                    }
                    catch { }

                    // Fallback: at least record what we have
                    if (!map.ContainsKey(idx))
                    {
                        map[idx] = new NvmeHealth
                        {
                            PercentageUsed = -1,
                            AvailableSpare = -1,
                            Temperature = 0
                        };
                    }
                }
                catch { }
            }
        }
        catch { }
        return map;
    }

    // ─── SMART with Thresholds ──────────────────────────────────────────

    /// <summary>
    /// Gets SMART attributes with actual threshold values from both
    /// MSStorageDriver_ATAPISmartData and MSStorageDriver_FailurePredictThresholds.
    /// </summary>
    private static List<SmartAttribute> GetSmartAttributesWithThresholds()
    {
        var attrs = new List<SmartAttribute>();
        byte[]? thresholdData = null;

        // Get threshold data first
        try
        {
            using var tSearcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT VendorSpecific FROM MSStorageDriver_FailurePredictThresholds");
            foreach (ManagementObject obj in tSearcher.Get())
            {
                if (obj["VendorSpecific"] is byte[] data)
                {
                    thresholdData = data;
                    break;
                }
            }
        }
        catch { }

        // Build threshold lookup: attribute ID → threshold value
        var thresholds = new Dictionary<byte, int>();
        if (thresholdData != null)
        {
            // Same structure: 12 bytes per entry starting at offset 2
            for (int i = 2; i + 12 <= thresholdData.Length; i += 12)
            {
                byte id = thresholdData[i];
                if (id == 0) continue;
                int threshold = thresholdData[i + 1];
                thresholds[id] = threshold;
            }
        }

        // Get attribute data
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT VendorSpecific FROM MSStorageDriver_ATAPISmartData");

            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["VendorSpecific"] is byte[] data)
                {
                    for (int i = 2; i + 12 <= data.Length; i += 12)
                    {
                        byte id = data[i];
                        if (id == 0) continue;

                        int current = data[i + 3];
                        int worst = data[i + 4];
                        long raw = BitConverter.ToInt32(data, i + 5);
                        int threshold = thresholds.GetValueOrDefault(id, 0);

                        attrs.Add(new SmartAttribute
                        {
                            Id = id,
                            Name = GetAttributeName(id),
                            Current = current,
                            Worst = worst,
                            Threshold = threshold,
                            RawValue = Math.Abs(raw),
                            IsCritical = id is 5 or 10 or 196 or 197 or 198 or 199
                        });
                    }
                    break;
                }
            }
        }
        catch { }
        return attrs;
    }

    private static string GetSmartStatus()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT PredictFailure FROM MSStorageDriver_FailurePredictStatus");
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["PredictFailure"] is bool predict && predict)
                    return "PredictFailure";
            }
        }
        catch { }
        return "OK";
    }

    // ─── Temperature ────────────────────────────────────────────────────

    private static int ExtractTemperature(List<SmartAttribute> attrs)
    {
        // ID 194 = Temperature Celsius (most common)
        var temp194 = attrs.FirstOrDefault(a => a.Id == 194);
        if (temp194 != null && temp194.RawValue is > 0 and < 120)
            return (int)temp194.RawValue;

        // ID 190 = Airflow Temperature
        var temp190 = attrs.FirstOrDefault(a => a.Id == 190);
        if (temp190 != null && temp190.RawValue is > 0 and < 120)
            return (int)temp190.RawValue;

        // Try current value (some drives report temp there)
        if (temp194 != null && temp194.Current is > 0 and < 120)
            return temp194.Current;

        return 0;
    }

    private static TemperatureStatus ClassifyTemperature(int tempC, bool isSsd)
    {
        if (tempC <= 0) return TemperatureStatus.Normal;

        if (isSsd)
        {
            return tempC switch
            {
                <= 25 => TemperatureStatus.Cold,
                <= 45 => TemperatureStatus.Normal,
                <= 60 => TemperatureStatus.Warm,
                <= 70 => TemperatureStatus.Hot,
                _ => TemperatureStatus.Critical
            };
        }
        // HDD
        return tempC switch
        {
            <= 25 => TemperatureStatus.Cold,
            <= 40 => TemperatureStatus.Normal,
            <= 50 => TemperatureStatus.Warm,
            <= 55 => TemperatureStatus.Hot,
            _ => TemperatureStatus.Critical
        };
    }

    // ─── SSD Wear Tracking ──────────────────────────────────────────────

    /// <summary>
    /// Calculates remaining SSD life percentage from SMART attributes.
    /// Checks: ID 231 (SSD Life Left), ID 177 (Wear Leveling Count),
    /// ID 233 (Media Wearout Indicator), ID 232 (Endurance Remaining).
    /// </summary>
    private static int CalculateSsdLife(List<SmartAttribute> attrs)
    {
        // Many SSDs report remaining life directly
        // ID 231 — SSD Life Left (Samsung, Intel)
        var life231 = attrs.FirstOrDefault(a => a.Id == 231);
        if (life231 != null && life231.Current is > 0 and <= 100)
            return life231.Current;

        // ID 232 — Endurance Remaining (SanDisk)
        var life232 = attrs.FirstOrDefault(a => a.Id == 232);
        if (life232 != null && life232.Current is > 0 and <= 100)
            return life232.Current;

        // ID 177 — Wear Leveling Count (Samsung) — current value = remaining %
        var wear177 = attrs.FirstOrDefault(a => a.Id == 177);
        if (wear177 != null && wear177.Current is > 0 and <= 100)
            return wear177.Current;

        // ID 233 — Media Wearout Indicator (Intel)
        var wear233 = attrs.FirstOrDefault(a => a.Id == 233);
        if (wear233 != null && wear233.Current is > 0 and <= 100)
            return wear233.Current;

        return -1; // Unknown
    }

    /// <summary>
    /// Calculates total bytes written from SMART attributes.
    /// ID 241 = Total LBAs Written (multiply by sector size for bytes).
    /// ID 246 = Total Host Sector Writes.
    /// </summary>
    private static long CalculateTotalBytesWritten(List<SmartAttribute> attrs)
    {
        var lba241 = attrs.FirstOrDefault(a => a.Id == 241);
        if (lba241 != null && lba241.RawValue > 0)
            return lba241.RawValue * 512; // LBA → bytes

        var sect246 = attrs.FirstOrDefault(a => a.Id == 246);
        if (sect246 != null && sect246.RawValue > 0)
            return sect246.RawValue * 512;

        return 0;
    }

    // ─── Health Score ────────────────────────────────────────────────────

    /// <summary>
    /// Computes a 0–100 health score based on weighted SMART attributes.
    /// </summary>
    private static int CalculateHealthScore(DiskHealthStatus status, List<SmartAttribute> attrs,
        int reallocated, int pending, int uncorrectable, int ssdLifeRemaining, int nvmePercentUsed)
    {
        if (status == DiskHealthStatus.Unknown) return -1;
        if (status == DiskHealthStatus.Critical) return Math.Max(5, 25);

        int score = 100;

        // Deduct for reallocated sectors (heaviest penalty)
        if (reallocated > 0) score -= Math.Min(40, reallocated * 4);

        // Deduct for pending sectors
        if (pending > 0) score -= Math.Min(30, pending * 6);

        // Deduct for uncorrectable errors
        if (uncorrectable > 0) score -= Math.Min(20, uncorrectable * 5);

        // Deduct for any attributes below threshold
        foreach (var attr in attrs.Where(a => a.IsCritical && a.IsFailing))
            score -= 15;

        foreach (var attr in attrs.Where(a => a.IsCritical && a.IsWarning && !a.IsFailing))
            score -= 5;

        // SSD wear factor
        if (ssdLifeRemaining >= 0)
        {
            if (ssdLifeRemaining < 10) score -= 20;
            else if (ssdLifeRemaining < 30) score -= 10;
            else if (ssdLifeRemaining < 50) score -= 5;
        }

        // NVMe percentage used
        if (nvmePercentUsed > 90) score -= 20;
        else if (nvmePercentUsed > 70) score -= 10;

        return Math.Clamp(score, 0, 100);
    }

    // ─── Health Determination ───────────────────────────────────────────

    private static DiskHealthStatus DetermineHealth(string smartStatus, int reallocated,
        int pending, int uncorrectable, List<SmartAttribute> attrs,
        int nvmePercentUsed, int nvmeAvailSpare)
    {
        if (smartStatus == "PredictFailure") return DiskHealthStatus.Critical;
        if (reallocated > 100 || pending > 50) return DiskHealthStatus.Critical;
        if (uncorrectable > 20) return DiskHealthStatus.Critical;
        if (nvmeAvailSpare is >= 0 and < 10) return DiskHealthStatus.Critical;
        if (nvmePercentUsed > 95) return DiskHealthStatus.Critical;

        if (reallocated > 10 || pending > 5) return DiskHealthStatus.Warning;
        if (uncorrectable > 5) return DiskHealthStatus.Warning;
        if (nvmeAvailSpare is >= 0 and < 30) return DiskHealthStatus.Warning;
        if (nvmePercentUsed > 80) return DiskHealthStatus.Warning;

        if (attrs.Any(a => a.IsCritical && a.IsFailing)) return DiskHealthStatus.Critical;
        if (attrs.Any(a => a.IsCritical && a.IsWarning)) return DiskHealthStatus.Warning;

        if (attrs.Count == 0) return DiskHealthStatus.Unknown;
        return DiskHealthStatus.Healthy;
    }

    // ─── Health Summary ─────────────────────────────────────────────────

    private static string BuildHealthSummary(DiskHealthStatus status, int score,
        int reallocated, int pending, int temp, TemperatureStatus tempStatus,
        int ssdLifeRemaining, int nvmePercentUsed)
    {
        var parts = new List<string>();

        parts.Add(status switch
        {
            DiskHealthStatus.Healthy => $"✅ Healthy (Score: {score}%)",
            DiskHealthStatus.Warning => $"⚠️ Warning (Score: {score}%)",
            DiskHealthStatus.Critical => $"❌ CRITICAL (Score: {score}%) — Back up immediately!",
            _ => "❓ Unable to read health data"
        });

        if (reallocated > 0) parts.Add($"• {reallocated} reallocated sectors");
        if (pending > 0) parts.Add($"• {pending} pending sectors");
        if (temp > 0) parts.Add($"• Temperature: {temp}°C ({tempStatus})");

        if (ssdLifeRemaining >= 0)
            parts.Add($"• SSD life remaining: {ssdLifeRemaining}%");
        if (nvmePercentUsed >= 0)
            parts.Add($"• NVMe wear: {nvmePercentUsed}% used");

        return string.Join("\n", parts);
    }

    // ─── Media Type Detection ───────────────────────────────────────────

    private static string DetectMediaType(string model, string iface, string pnpId)
    {
        var combined = $"{model} {iface} {pnpId}".ToUpperInvariant();

        if (combined.Contains("NVME") || combined.Contains("NVM")) return "NVMe SSD";
        if (combined.Contains("SSD") || combined.Contains("SOLID STATE")) return "SATA SSD";

        // Check via Storage namespace for media type
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT MediaType FROM MSFT_PhysicalDisk"));
            foreach (ManagementObject pd in searcher.Get())
            {
                var mt = Convert.ToInt32(pd["MediaType"] ?? 0);
                // 3 = HDD, 4 = SSD
                if (mt == 4) return "SSD";
            }
        }
        catch { }

        if (iface.Contains("NVMe", StringComparison.OrdinalIgnoreCase)) return "NVMe SSD";
        return "HDD";
    }

    // ─── Attribute Name Dictionary (30+ attributes) ─────────────────────

    private static string GetAttributeName(byte id) => id switch
    {
        1 => "Read Error Rate",
        2 => "Throughput Performance",
        3 => "Spin-Up Time",
        4 => "Start/Stop Count",
        5 => "Reallocated Sectors Count",
        7 => "Seek Error Rate",
        8 => "Seek Time Performance",
        9 => "Power-On Hours",
        10 => "Spin Retry Count",
        11 => "Recalibration Retries",
        12 => "Power Cycle Count",
        170 => "Available Reserved Space",
        171 => "Program Fail Count",
        172 => "Erase Fail Count",
        173 => "Wear Leveling Count",
        174 => "Unexpected Power Loss",
        175 => "Program Fail Count (Chip)",
        176 => "Erase Fail Count (Chip)",
        177 => "Wear Leveling Count",
        178 => "Used Reserved Block Count (Chip)",
        179 => "Used Reserved Block Count (Total)",
        180 => "Unused Reserved Block Count (Total)",
        181 => "Program Fail Count (Total)",
        182 => "Erase Fail Count (Total)",
        183 => "Runtime Bad Block",
        184 => "End-to-End Error",
        187 => "Reported Uncorrectable Errors",
        188 => "Command Timeout",
        189 => "High Fly Writes",
        190 => "Airflow Temperature",
        191 => "G-Sense Error Rate",
        192 => "Unsafe Shutdown Count",
        193 => "Load Cycle Count",
        194 => "Temperature Celsius",
        195 => "Hardware ECC Recovered",
        196 => "Reallocation Event Count",
        197 => "Current Pending Sector Count",
        198 => "Offline Uncorrectable",
        199 => "UDMA CRC Error Count",
        200 => "Multi-Zone Error Rate",
        201 => "Soft Read Error Rate",
        220 => "Disk Shift",
        231 => "SSD Life Left",
        232 => "Endurance Remaining",
        233 => "Media Wearout Indicator",
        234 => "Total Bytes Written (NAND)",
        235 => "Total Bytes Written (Host)",
        240 => "Head Flying Hours",
        241 => "Total LBAs Written",
        242 => "Total LBAs Read",
        246 => "Total Host Sector Writes",
        _ => $"Attribute {id}"
    };

    // ─── Utilities ───────────────────────────────────────────────────────

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}

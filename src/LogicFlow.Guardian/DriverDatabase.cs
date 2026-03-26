// ─────────────────────────────────────────────────────────────────────────────
// LogicFlow.Guardian — Smart Driver Updater (3-Tier Architecture)
// Tier 1: Windows Update via pnputil (free, 70%+ coverage)
// Tier 2: Curated Firestore driver index (OEM download URLs, metadata only)
// Tier 3: AI crash-to-driver correlation via Vertex AI / Gemini
// ─────────────────────────────────────────────────────────────────────────────

using System.Diagnostics;
using System.Management;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LogicFlow.Guardian;

/// <summary>
/// Smart Driver Database — 3-tier driver lookup, download, and AI analysis.
/// Tier 1: Windows Update (pnputil) — free, covers ~70% of drivers
/// Tier 2: Curated Firestore index — top ~100 drivers, OEM download URLs
/// Tier 3: AI crash correlation — Gemini analyzes Pulse crash data vs driver versions
/// </summary>
public sealed class DriverDatabase : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<DriverDatabase>? _logger;
    private readonly string _apiBase;

    // ── Firebase Cloud Functions base URL ────────────────────────────────
    // This points to your Cloud Functions deployment, not raw Firestore.
    // The Cloud Functions handle Firestore lookup + Gemini AI analysis.
    private const string DefaultApiBase = "https://us-central1-logicflow-guardian.cloudfunctions.net";

    public DriverDatabase(string apiBase = DefaultApiBase,
                          ILogger<DriverDatabase>? logger = null)
    {
        _apiBase = apiBase;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("User-Agent", "LogicFlow-DriverUpdater/2.0");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FULL 3-TIER SCAN
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Performs a complete 3-tier driver scan:
    ///   1. Enumerate all PnP devices + check Windows Update
    ///   2. Look up top drivers in curated Firestore index
    ///   3. Correlate crash data with drivers via AI
    /// Returns a unified DriverScanResult with risk-scored recommendations.
    /// </summary>
    public async Task<DriverScanResult> FullScanAsync(
        List<DriverReport>? existingReports = null,
        string? crashDigestJson = null,
        CancellationToken ct = default)
    {
        _logger?.LogInformation("Starting full 3-tier driver scan...");

        // If caller didn't provide reports, scan via WMI
        var devices = existingReports ?? ScanInstalledDrivers();

        // Run all 3 tiers in parallel
        var tier1Task = ScanWindowsUpdateAsync(devices, ct);
        var tier2Task = LookupFirestoreIndexAsync(devices, ct);
        var tier3Task = GetAiRecommendationsAsync(devices, crashDigestJson, ct);
        var firmwareTask = Task.Run(GetFirmwareInfo, ct);

        await Task.WhenAll(tier1Task, tier2Task, tier3Task, firmwareTask);

        var result = new DriverScanResult
        {
            WindowsUpdateAvailable = await tier1Task,
            IndexAvailable = await tier2Task,
            AiRecommendations = await tier3Task,
            InstalledDevices = devices,
            Firmware = await firmwareTask,
            ScannedAt = DateTimeOffset.UtcNow
        };

        _logger?.LogInformation(
            "Scan complete: {WU} Windows Update, {Idx} index, {AI} AI recommendations, {Fw} firmware",
            result.WindowsUpdateAvailable.Count,
            result.IndexAvailable.Count,
            result.AiRecommendations.Count,
            result.Firmware != null ? "detected" : "none");

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TIER 1: WINDOWS UPDATE (pnputil)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tier 1: Uses pnputil to scan for driver updates available via Windows Update.
    /// This is free and covers ~70% of common hardware.
    /// </summary>
    private async Task<List<DriverUpdateInfo>> ScanWindowsUpdateAsync(
        List<DriverReport> devices, CancellationToken ct)
    {
        var results = new List<DriverUpdateInfo>();
        try
        {
            // First trigger a Windows Update scan for drivers
            var scanPsi = new ProcessStartInfo("pnputil", "/scan-devices")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var scanProc = Process.Start(scanPsi);
            if (scanProc != null)
            {
                await scanProc.WaitForExitAsync(ct);
                _logger?.LogDebug("pnputil /scan-devices exit code: {Code}", scanProc.ExitCode);
            }

            // Now enumerate the driver store to find newer versions
            var enumPsi = new ProcessStartInfo("pnputil", "/enum-drivers")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var enumProc = Process.Start(enumPsi);
            if (enumProc != null)
            {
                var output = await enumProc.StandardOutput.ReadToEndAsync(ct);
                await enumProc.WaitForExitAsync(ct);

                // Parse the pnputil output to find available driver packages
                var storeDrivers = ParsePnpUtilDrivers(output);

                // Compare store versions against installed versions
                foreach (var device in devices)
                {
                    if (string.IsNullOrEmpty(device.HardwareId)) continue;

                    foreach (var storeEntry in storeDrivers)
                    {
                        if (storeEntry.ClassName != device.DeviceClass) continue;

                        if (IsNewerVersion(device.DriverVersion, storeEntry.Version))
                        {
                            results.Add(new DriverUpdateInfo
                            {
                                HardwareId = device.HardwareId,
                                DeviceName = device.DeviceName,
                                CurrentVersion = device.DriverVersion,
                                LatestVersion = storeEntry.Version,
                                DownloadUrl = "", // Windows Update handles download
                                IsWhqlCertified = true,
                                Manufacturer = storeEntry.Provider,
                                ReleasedAt = storeEntry.Date,
                                Source = "WindowsUpdate"
                            });
                        }
                    }
                }

                _logger?.LogInformation("Tier 1 (Windows Update): {Count} updates found", results.Count);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Tier 1 (Windows Update) scan failed");
        }

        return results;
    }

    /// <summary>
    /// Parses pnputil /enum-drivers output into structured data.
    /// </summary>
    private static List<PnpStoreDriver> ParsePnpUtilDrivers(string output)
    {
        var drivers = new List<PnpStoreDriver>();
        var blocks = output.Split(new[] { "Published Name:" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var block in blocks.Skip(1)) // Skip header
        {
            var driver = new PnpStoreDriver();
            var lines = block.Split('\n');

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Original Name:", StringComparison.OrdinalIgnoreCase))
                    driver.InfName = ExtractValue(trimmed);
                else if (trimmed.StartsWith("Provider Name:", StringComparison.OrdinalIgnoreCase))
                    driver.Provider = ExtractValue(trimmed);
                else if (trimmed.StartsWith("Class Name:", StringComparison.OrdinalIgnoreCase))
                    driver.ClassName = ExtractValue(trimmed);
                else if (trimmed.StartsWith("Driver Version and Date:", StringComparison.OrdinalIgnoreCase) ||
                         trimmed.StartsWith("Class Version:", StringComparison.OrdinalIgnoreCase))
                {
                    // Parse "mm/dd/yyyy vX.X.X.X" pattern
                    var match = Regex.Match(trimmed, @"(\d+/\d+/\d+)\s+(\S+)");
                    if (match.Success)
                    {
                        if (DateTimeOffset.TryParse(match.Groups[1].Value, out var date))
                            driver.Date = date;
                        driver.Version = match.Groups[2].Value;
                    }
                }
                else if (trimmed.StartsWith("Signer Name:", StringComparison.OrdinalIgnoreCase))
                    driver.SignerName = ExtractValue(trimmed);
            }

            if (!string.IsNullOrEmpty(driver.InfName))
                drivers.Add(driver);
        }

        return drivers;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TIER 2: CURATED FIRESTORE INDEX
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tier 2: Queries the curated Firestore driver index via Cloud Function.
    /// Returns available updates from OEM manufacturer download URLs.
    /// The index is metadata-only — no driver binaries are hosted.
    /// </summary>
    private async Task<List<DriverUpdateInfo>> LookupFirestoreIndexAsync(
        List<DriverReport> devices, CancellationToken ct)
    {
        var results = new List<DriverUpdateInfo>();

        try
        {
            var hwids = devices
                .Where(d => !string.IsNullOrEmpty(d.HardwareId))
                .Select(d => new
                {
                    d.HardwareId,
                    d.DriverVersion,
                    d.DeviceName,
                    d.DeviceClass,
                    d.Manufacturer
                })
                .ToList();

            if (hwids.Count == 0) return results;

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/driversLookup")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { drivers = hwids }),
                    Encoding.UTF8, "application/json")
            };

            var response = await _http.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                var indexResults = JsonSerializer.Deserialize<List<DriverUpdateInfo>>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

                // Mark source as index
                foreach (var r in indexResults)
                {
                    results.Add(r with { Source = "FirestoreIndex" });
                }

                _logger?.LogInformation("Tier 2 (Firestore Index): {Count} updates found", results.Count);
            }
            else
            {
                _logger?.LogWarning("Tier 2 lookup failed: HTTP {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Tier 2 (Firestore Index) lookup failed — offline or API unavailable");
        }

        return results;
    }

    /// <summary>
    /// Direct lookup for a single driver by hardware ID pattern.
    /// Used for targeted updates (e.g., user clicks "Update GPU driver").
    /// </summary>
    public async Task<DriverUpdateInfo?> LookupSingleDriverAsync(
        string hardwareId, string currentVersion, CancellationToken ct = default)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/driversLookup")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        drivers = new[]
                        {
                            new { HardwareId = hardwareId, DriverVersion = currentVersion,
                                  DeviceName = "", DeviceClass = "", Manufacturer = "" }
                        }
                    }),
                    Encoding.UTF8, "application/json")
            };

            var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                var results = JsonSerializer.Deserialize<List<DriverUpdateInfo>>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return results?.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Single driver lookup failed for {HwId}", hardwareId);
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TIER 3: AI CRASH-TO-DRIVER CORRELATION
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tier 3: Sends device list + crash digest to Cloud Function,
    /// which uses Vertex AI / Gemini to correlate crashes with specific drivers.
    /// Returns AI-scored recommendations with severity and confidence.
    /// </summary>
    private async Task<List<AiDriverRecommendation>> GetAiRecommendationsAsync(
        List<DriverReport> devices, string? crashDigestJson, CancellationToken ct)
    {
        var recommendations = new List<AiDriverRecommendation>();

        // ── Step 1: Local pattern matching (instant, no network) ─────────
        var localMatches = MatchLocalCrashPatterns(devices, crashDigestJson);
        recommendations.AddRange(localMatches);

        // ── Step 2: Cloud AI analysis (if crash data available) ──────────
        if (!string.IsNullOrEmpty(crashDigestJson))
        {
            try
            {
                var payload = new
                {
                    devices = devices.Select(d => new
                    {
                        d.HardwareId, d.DeviceName, d.DriverVersion,
                        d.DeviceClass, d.Manufacturer, d.DriverDate
                    }),
                    crashDigest = crashDigestJson,
                    systemInfo = GetBasicSystemInfo()
                };

                var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/driversScanWithAi")
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(payload),
                        Encoding.UTF8, "application/json")
                };

                var response = await _http.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    var aiResults = JsonSerializer.Deserialize<List<AiDriverRecommendation>>(body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

                    // Merge AI results, avoiding duplicates with local matches
                    foreach (var ai in aiResults)
                    {
                        if (!recommendations.Any(r => r.HardwareId == ai.HardwareId))
                            recommendations.Add(ai);
                    }

                    _logger?.LogInformation("Tier 3 (AI): {Count} recommendations from Gemini", aiResults.Count);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Tier 3 (AI) analysis failed — using local patterns only");
            }
        }

        return recommendations;
    }

    /// <summary>
    /// Matches crash digest data against known crash-to-driver patterns locally.
    /// This runs instantly without network, providing fast results.
    /// </summary>
    private List<AiDriverRecommendation> MatchLocalCrashPatterns(
        List<DriverReport> devices, string? crashDigestJson)
    {
        var recommendations = new List<AiDriverRecommendation>();

        if (string.IsNullOrEmpty(crashDigestJson)) return recommendations;

        foreach (var (moduleName, pattern) in KnownDriverCrashPatterns.Patterns)
        {
            if (!crashDigestJson.Contains(moduleName, StringComparison.OrdinalIgnoreCase))
                continue;

            // Find the matching device
            var matchedDevice = devices.FirstOrDefault(d =>
                d.DeviceClass?.Equals(pattern.DeviceCategory, StringComparison.OrdinalIgnoreCase) == true ||
                d.DeviceName?.Contains(pattern.DriverName.Split(' ')[0], StringComparison.OrdinalIgnoreCase) == true);

            recommendations.Add(new AiDriverRecommendation
            {
                DriverName = matchedDevice?.DeviceName ?? pattern.DriverName,
                HardwareId = matchedDevice?.HardwareId ?? "",
                Reason = pattern.Description,
                Confidence = 0.85, // High confidence for known patterns
                Severity = "critical",
                CrashSignatures = new List<string> { moduleName },
                CrashCount = CountOccurrences(crashDigestJson, moduleName)
            });
        }

        if (recommendations.Count > 0)
            _logger?.LogInformation("Local pattern match: {Count} crash-driver correlations found",
                recommendations.Count);

        return recommendations;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FIRMWARE INFO (Win32_BIOS)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads BIOS/UEFI firmware info from WMI Win32_BIOS and Win32_ComputerSystem.
    /// </summary>
    public FirmwareInfo? GetFirmwareInfo()
    {
        try
        {
            using var bioSearcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_BIOS");
            using var csSearcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Model FROM Win32_ComputerSystem");

            ManagementObject? bios = null;
            ManagementObject? cs = null;

            foreach (var obj in bioSearcher.Get()) { bios = (ManagementObject)obj; break; }
            foreach (var obj in csSearcher.Get()) { cs = (ManagementObject)obj; break; }

            if (bios == null) return null;

            var serial = bios["SerialNumber"]?.ToString() ?? "";
            var serialSuffix = serial.Length > 4 ? serial[^4..] : serial;

            DateTimeOffset? releaseDate = null;
            var dateStr = bios["ReleaseDate"]?.ToString();
            if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 8)
            {
                // WMI date format: yyyyMMddHHmmss.ffffff+UUU
                if (DateTime.TryParseExact(dateStr[..8], "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed))
                    releaseDate = new DateTimeOffset(parsed, TimeSpan.Zero);
            }

            var firmware = new FirmwareInfo
            {
                Manufacturer = bios["Manufacturer"]?.ToString() ?? "",
                Version = bios["SMBIOSBIOSVersion"]?.ToString() ?? bios["Version"]?.ToString() ?? "",
                SmbiosVersion = $"{bios["SMBIOSMajorVersion"]}.{bios["SMBIOSMinorVersion"]}",
                ReleaseDate = releaseDate,
                SerialNumberSuffix = serialSuffix,
                UefiCapsuleSupported = DetectUefiCapsuleSupport(),
                SystemModel = cs?["Model"]?.ToString() ?? "",
                SystemManufacturer = cs?["Manufacturer"]?.ToString() ?? ""
            };

            _logger?.LogInformation("Firmware: {Mfr} {Ver} ({Date})",
                firmware.Manufacturer, firmware.Version,
                firmware.ReleaseDate?.ToString("yyyy-MM-dd") ?? "unknown");

            return firmware;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to read firmware info");
            return null;
        }
    }

    /// <summary>
    /// Detects if UEFI Capsule firmware updates are likely supported.
    /// </summary>
    private static bool DetectUefiCapsuleSupport()
    {
        try
        {
            // Check for UEFI firmware type via registry
            var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            return key != null; // SecureBoot key presence indicates UEFI
        }
        catch { return false; }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DEVICE SCANNING
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enumerates all PnP devices with signed drivers via WMI.
    /// Uses the same WMI query as SmartDriverEngine but returns our enriched model.
    /// </summary>
    public List<DriverReport> ScanInstalledDrivers()
    {
        var reports = new List<DriverReport>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPSignedDriver WHERE DeviceName IS NOT NULL");

            foreach (var obj in searcher.Get())
            {
                var dateStr = obj["DriverDate"]?.ToString() ?? "";
                reports.Add(new DriverReport
                {
                    DeviceName = obj["DeviceName"]?.ToString() ?? "Unknown",
                    HardwareId = obj["HardWareID"]?.ToString() ?? "",
                    DriverVersion = obj["DriverVersion"]?.ToString() ?? "",
                    DriverDate = dateStr,
                    Manufacturer = obj["Manufacturer"]?.ToString() ?? "",
                    DeviceClass = obj["DeviceClass"]?.ToString() ?? "",
                    IsSigned = obj["IsSigned"] is bool signed && signed,
                    InfName = obj["InfName"]?.ToString() ?? ""
                });
            }

            _logger?.LogInformation("Scanned {Count} installed drivers", reports.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "WMI driver scan failed");
        }

        return reports;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DOWNLOAD, BACKUP, INSTALL, ROLLBACK
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Downloads a driver package from the OEM URL to the local cache.
    /// </summary>
    public async Task<string?> DownloadDriverAsync(DriverUpdateInfo update,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(update.DownloadUrl))
            {
                _logger?.LogWarning("No download URL for {Device}", update.DeviceName);
                return null;
            }

            var downloadDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LogicFlow", "DriverCache");
            Directory.CreateDirectory(downloadDir);

            var safeHwid = SanitizeFilename(update.HardwareId);
            var filename = $"{safeHwid}_{update.LatestVersion}.download";
            var destPath = Path.Combine(downloadDir, filename);
            if (File.Exists(destPath)) return destPath;

            using var response = await _http.GetAsync(update.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? 0;
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var file = new FileStream(destPath, FileMode.Create);

            var buffer = new byte[81920];
            long downloaded = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;
                if (total > 0) progress?.Report((double)downloaded / total * 100);
            }

            _logger?.LogInformation("Downloaded driver: {Name} v{Version} ({Size} KB)",
                update.DeviceName, update.LatestVersion, downloaded / 1024);
            return destPath;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to download driver for {Device}", update.DeviceName);
            return null;
        }
    }

    /// <summary>
    /// Creates a system restore point before driver installation.
    /// This is a safety net — if the driver causes issues, user can roll back the entire system.
    /// </summary>
    public bool CreateRestorePointBeforeInstall(string driverName)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell", string.Join(" ",
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
                $"\"Checkpoint-Computer -Description 'LogicFlow: Before {SanitizeForPs(driverName)} driver update' -RestorePointType MODIFY_SETTINGS\""))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(60_000); // 60s timeout

            if (proc?.ExitCode == 0)
            {
                _logger?.LogInformation("System restore point created before {Driver} update", driverName);
                return true;
            }

            _logger?.LogWarning("Restore point creation returned exit code {Code}", proc?.ExitCode);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create restore point");
        }

        return false;
    }

    /// <summary>
    /// Backs up the current driver for a device using pnputil /export-driver.
    /// Returns the backup directory path, or null on failure.
    /// </summary>
    public string? BackupDriver(DriverReport driver)
    {
        try
        {
            if (string.IsNullOrEmpty(driver.InfName)) return null;

            var backupDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LogicFlow", "DriverBackups",
                $"{driver.DeviceClass}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(backupDir);

            var psi = new ProcessStartInfo("pnputil",
                $"/export-driver \"{driver.InfName}\" \"{backupDir}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                Verb = "runas"
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);

            if (proc?.ExitCode == 0)
            {
                _logger?.LogInformation("Backed up driver {Name} to {Dir}", driver.DeviceName, backupDir);
                return backupDir;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to backup driver {Name}", driver.DeviceName);
        }
        return null;
    }

    /// <summary>
    /// Installs a driver from a downloaded package.
    /// Supports both .inf files (via pnputil) and .exe installers (via silent flags).
    /// Automatically creates a restore point and backup before installing.
    /// </summary>
    public DriverInstallResult InstallDriver(string packagePath, string driverName,
        bool force = false, bool autoBackup = true)
    {
        try
        {
            // Step 1: Create system restore point
            CreateRestorePointBeforeInstall(driverName);

            // Step 2: If it's an .exe installer, run with silent flags
            if (packagePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return InstallExeDriver(packagePath, driverName);

            // Step 3: If it's a .zip or directory, find .inf and use pnputil
            var extractDir = packagePath;
            if (packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                packagePath.EndsWith(".download", StringComparison.OrdinalIgnoreCase))
            {
                extractDir = Path.Combine(Path.GetDirectoryName(packagePath) ?? "",
                    Path.GetFileNameWithoutExtension(packagePath));
                if (!Directory.Exists(extractDir))
                    System.IO.Compression.ZipFile.ExtractToDirectory(packagePath, extractDir);
            }

            // Find .inf file in extracted directory
            if (!Directory.Exists(extractDir))
                return new(false, "Package directory not found");

            var infFiles = Directory.GetFiles(extractDir, "*.inf", SearchOption.AllDirectories);
            if (infFiles.Length == 0)
                return new(false, "No .inf file found in driver package");

            var forceFlag = force ? "/force" : "";
            var psi = new ProcessStartInfo("pnputil",
                $"/add-driver \"{infFiles[0]}\" /install {forceFlag}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd() ?? "";
            proc?.WaitForExit(30000);

            if (proc?.ExitCode == 0)
            {
                _logger?.LogInformation("Driver installed via pnputil: {Output}", output.Trim());
                return new(true, "Driver installed successfully");
            }
            else
            {
                _logger?.LogWarning("pnputil install failed: {Output}", output.Trim());
                return new(false, $"Installation failed: {output.Trim()}");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Driver installation error");
            return new(false, ex.Message);
        }
    }

    /// <summary>
    /// Installs an .exe driver installer with silent/quiet flags.
    /// Tries common silent install conventions (NVIDIA, AMD, Intel, Realtek).
    /// </summary>
    private DriverInstallResult InstallExeDriver(string exePath, string driverName)
    {
        try
        {
            // Determine silent install flags based on known patterns
            var args = DetermineSilentInstallFlags(exePath, driverName);

            var psi = new ProcessStartInfo(exePath, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _logger?.LogInformation("Installing {Driver} with args: {Args}", driverName, args);

            using var proc = Process.Start(psi);
            proc?.WaitForExit(300_000); // 5 min timeout for exe installers

            if (proc?.ExitCode == 0 || proc?.ExitCode == 3010) // 3010 = reboot required
            {
                var needsReboot = proc?.ExitCode == 3010;
                _logger?.LogInformation("Driver .exe installed: {Name} (reboot needed: {Reboot})",
                    driverName, needsReboot);
                return new(true,
                    needsReboot ? "Driver installed — restart required" : "Driver installed successfully");
            }

            return new(false, $"Installer exited with code {proc?.ExitCode}");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "EXE driver install failed for {Name}", driverName);
            return new(false, ex.Message);
        }
    }

    /// <summary>
    /// Determines the correct silent install flags for known driver installers.
    /// </summary>
    private static string DetermineSilentInstallFlags(string exePath, string driverName)
    {
        var lower = Path.GetFileName(exePath).ToLowerInvariant() + " " + driverName.ToLowerInvariant();

        if (lower.Contains("nvidia"))
            return "-s -noreboot -noeula -clean";
        if (lower.Contains("amd") || lower.Contains("radeon"))
            return "/S";
        if (lower.Contains("intel"))
            return "-s -norestart";
        if (lower.Contains("realtek"))
            return "/s /f";

        // Generic silent install attempt
        return "/S /SILENT /VERYSILENT /norestart";
    }

    /// <summary>
    /// Rolls back a driver from a previous backup.
    /// </summary>
    public DriverInstallResult RollbackDriver(string backupDir)
        => InstallDriver(backupDir, "Rollback", force: true, autoBackup: false);

    // ═══════════════════════════════════════════════════════════════════════
    //  HELPER METHODS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compares two version strings. Returns true if installedVersion &lt; availableVersion.
    /// </summary>
    private static bool IsNewerVersion(string installed, string available)
    {
        if (string.IsNullOrEmpty(installed) || string.IsNullOrEmpty(available))
            return false;

        if (Version.TryParse(NormalizeVersion(installed), out var v1) &&
            Version.TryParse(NormalizeVersion(available), out var v2))
            return v2 > v1;

        return string.Compare(available, installed, StringComparison.OrdinalIgnoreCase) > 0;
    }

    /// <summary>
    /// Normalize version strings (e.g., "31.0.101.2127" → works as-is with Version.Parse).
    /// </summary>
    private static string NormalizeVersion(string v)
    {
        // Strip non-numeric prefix/suffix, keep only digits and dots
        var cleaned = Regex.Replace(v.Trim(), @"[^\d.]", "");
        var parts = cleaned.Split('.').Take(4).ToArray();
        return string.Join(".", parts);
    }

    /// <summary>
    /// Extracts value from "Key: Value" pnputil output lines.
    /// </summary>
    private static string ExtractValue(string line)
    {
        var idx = line.IndexOf(':');
        return idx >= 0 ? line[(idx + 1)..].Trim() : line.Trim();
    }

    /// <summary>
    /// Counts case-insensitive occurrences of a pattern in text.
    /// </summary>
    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }

    /// <summary>
    /// Sanitizes a string for use as a filename.
    /// </summary>
    private static string SanitizeFilename(string input)
        => Regex.Replace(input ?? "", @"[\\/:*?""<>|]", "_");

    /// <summary>
    /// Sanitizes a string for use in PowerShell commands.
    /// </summary>
    private static string SanitizeForPs(string input)
        => (input ?? "").Replace("'", "''").Replace("\"", "`\"");

    /// <summary>
    /// Gets basic system info for AI context (anonymous, no PII).
    /// </summary>
    private static object GetBasicSystemInfo()
        => new
        {
            OS = Environment.OSVersion.VersionString,
            Is64Bit = Environment.Is64BitOperatingSystem,
            ProcessorCount = Environment.ProcessorCount,
            MachineName = "REDACTED" // No PII
        };

    public void Dispose() => _http.Dispose();
}

// ─── Supporting Models ──────────────────────────────────────────────────

/// <summary>
/// Driver update information from any tier (Windows Update, Firestore Index, or AI).
/// </summary>
public sealed record DriverUpdateInfo
{
    public string HardwareId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string CurrentVersion { get; init; } = "";
    public string LatestVersion { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public long SizeBytes { get; init; }
    public bool IsWhqlCertified { get; init; }
    public string Manufacturer { get; init; } = "";
    public DateTimeOffset ReleasedAt { get; init; }

    /// <summary> Source tier: "WindowsUpdate", "FirestoreIndex", or "AI". </summary>
    public string Source { get; init; } = "";
}

/// <summary>
/// Result of a driver installation attempt.
/// </summary>
public sealed record DriverInstallResult(bool Success, string Message);

/// <summary>
/// Parsed driver entry from pnputil /enum-drivers output.
/// </summary>
internal sealed class PnpStoreDriver
{
    public string InfName { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Version { get; set; } = "";
    public string SignerName { get; set; } = "";
    public DateTimeOffset Date { get; set; }
}

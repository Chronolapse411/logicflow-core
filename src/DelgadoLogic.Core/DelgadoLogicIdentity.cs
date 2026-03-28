// ─────────────────────────────────────────────────────────────────────────────
// DelgadoLogic.Core — Shared Identity Engine
// Hardware-bound anonymous identity used across all DelgadoLogic products.
// Privacy-first: no PII, no names, no email — only hardware fingerprint.
// Mirrors Apple's device identity model and Adobe's machine fingerprinting.
// ─────────────────────────────────────────────────────────────────────────────

using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DelgadoLogic.Core;

/// <summary>
/// Generates and persists a stable, hardware-bound device identity
/// that is shared across all installed DelgadoLogic products.
///
/// The identity is:
/// - Anonymous: no name, email, or personal data
/// - Persistent: survives reboots and app reinstalls
/// - Hardware-bound: changes if significant hardware changes
/// - Shared: LogicFlow and Aeon Browser read the same identity file
/// </summary>
public sealed class DelgadoLogicIdentity
{
    private const string IdentityFileName = "device_identity.json";

    private static DelgadoLogicIdentity? _instance;
    private readonly DeviceIdentityRecord _record;

    private DelgadoLogicIdentity(DeviceIdentityRecord record)
    {
        _record = record;
    }

    // ─── Public Surface ──────────────────────────────────────────────────

    /// <summary>
    /// Stable hardware device ID — SHA-256 of CPU + board + BIOS + disk serials.
    /// Used for license binding and anonymous telemetry correlation.
    /// </summary>
    public string DeviceId => _record.DeviceId;

    /// <summary>
    /// When this device identity was first established.
    /// </summary>
    public DateTimeOffset FirstSeenAt => _record.FirstSeenAt;

    /// <summary>
    /// How many times has any DelgadoLogic product been launched on this device.
    /// </summary>
    public int TotalLaunchCount => _record.TotalLaunchCount;

    /// <summary>
    /// List of product IDs that have been activated on this device.
    /// </summary>
    public IReadOnlyList<string> RegisteredProducts => _record.RegisteredProducts;

    // ─── Factory ────────────────────────────────────────────────────────

    /// <summary>
    /// Loads or creates the shared device identity. Call once at startup
    /// before any cross-product features are accessed.
    /// </summary>
    public static DelgadoLogicIdentity LoadOrCreate()
    {
        if (_instance is not null) return _instance;

        var sharedRoot = ProductManifest.SharedAppDataRoot;
        Directory.CreateDirectory(sharedRoot);

        var identityFile = Path.Combine(sharedRoot, IdentityFileName);

        DeviceIdentityRecord? record = null;

        if (File.Exists(identityFile))
        {
            try
            {
                var json = File.ReadAllText(identityFile);
                record = JsonSerializer.Deserialize<DeviceIdentityRecord>(json);
            }
            catch { /* corrupt file — regenerate below */ }
        }

        if (record is null)
        {
            record = new DeviceIdentityRecord
            {
                DeviceId     = GenerateHwid(),
                FirstSeenAt  = DateTimeOffset.UtcNow,
                TotalLaunchCount = 0,
                RegisteredProducts = []
            };
        }

        // Increment launch count and persist
        record.TotalLaunchCount++;
        File.WriteAllText(identityFile, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));

        _instance = new DelgadoLogicIdentity(record);
        return _instance;
    }

    /// <summary>
    /// Registers a product as installed/activated on this device.
    /// </summary>
    public void RegisterProduct(DelgadoProduct product)
    {
        var key = product.ToString();
        if (!_record.RegisteredProducts.Contains(key))
        {
            _record.RegisteredProducts.Add(key);
            PersistRecord();
        }
    }

    // ─── HWID Generation ────────────────────────────────────────────────

    private static string GenerateHwid()
    {
        var sb = new StringBuilder();

        AppendWmi(sb, "Win32_Processor", "ProcessorId");
        AppendWmi(sb, "Win32_BaseBoard", "SerialNumber");
        AppendWmi(sb, "Win32_BIOS", "SerialNumber");
        AppendWmi(sb, "Win32_DiskDrive", "SerialNumber");

        // Fallback: use machine GUID from registry if WMI fails
        if (sb.Length == 0)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Cryptography");
                sb.Append(key?.GetValue("MachineGuid")?.ToString() ?? Environment.MachineName);
            }
            catch { sb.Append(Environment.MachineName); }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToBase64String(hash)[..32]; // 32-char truncated base64
    }

    private static void AppendWmi(StringBuilder sb, string wmiClass, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
            foreach (var obj in searcher.Get())
            {
                sb.Append(obj[property]?.ToString()?.Trim() ?? "");
                break;
            }
        }
        catch { /* WMI unavailable — handled by fallback */ }
    }

    private void PersistRecord()
    {
        var sharedRoot = ProductManifest.SharedAppDataRoot;
        var identityFile = Path.Combine(sharedRoot, IdentityFileName);
        Directory.CreateDirectory(sharedRoot);
        File.WriteAllText(identityFile, JsonSerializer.Serialize(_record, new JsonSerializerOptions { WriteIndented = true }));
    }
}

// ─── Internal Record ─────────────────────────────────────────────────────────

internal sealed class DeviceIdentityRecord
{
    public string DeviceId { get; set; } = "";
    public DateTimeOffset FirstSeenAt { get; set; }
    public int TotalLaunchCount { get; set; }
    public List<string> RegisteredProducts { get; set; } = [];
}

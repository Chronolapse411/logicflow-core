// ─────────────────────────────────────────────────────────────────────────────
// LogicFlow.Guardian — Server Role Detection Engine
// Detects Windows Server roles (IIS, SQL, Hyper-V, AD, DNS, DHCP) and
// provides server-specific optimization recommendations.
// ─────────────────────────────────────────────────────────────────────────────

using System.Management;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;

namespace LogicFlow.Guardian;

/// <summary>
/// Detects Windows Server roles and services to provide server-aware optimizations.
/// Prevents dangerous actions like disabling IIS on a web server.
/// </summary>
public sealed class ServerRoleDetector
{
    private readonly ILogger<ServerRoleDetector>? _logger;

    public ServerRoleDetector(ILogger<ServerRoleDetector>? logger = null) => _logger = logger;

    /// <summary>
    /// Detects all server roles and services running on this machine.
    /// </summary>
    public ServerProfile DetectRoles()
    {
        _logger?.LogInformation("Detecting server roles...");

        var profile = new ServerProfile
        {
            IsServer = IsWindowsServer(),
            Hostname = Environment.MachineName,
            OsCaption = GetWmi("Win32_OperatingSystem", "Caption"),
            DetectedAt = DateTimeOffset.UtcNow
        };

        // Detect roles by checking for running services and installed features
        profile.Roles = DetectInstalledRoles();
        profile.CriticalServices = DetectCriticalServices();
        profile.Recommendations = GenerateRecommendations(profile);

        _logger?.LogInformation("Detected {Count} server roles on {Host}",
            profile.Roles.Count, profile.Hostname);

        return profile;
    }

    /// <summary>
    /// Returns a list of services that should NEVER be disabled on this server.
    /// Used by TurboMode and WindowsTweakEngine to avoid breaking server functionality.
    /// </summary>
    public List<string> GetProtectedServices()
    {
        var profile = DetectRoles();
        var protectedServices = new List<string>();

        foreach (var role in profile.Roles)
        {
            protectedServices.AddRange(role.CriticalServices);
        }

        return protectedServices.Distinct().ToList();
    }

    // ─── Detection Logic ─────────────────────────────────────────────────

    private bool IsWindowsServer()
    {
        try
        {
            var productType = GetWmi("Win32_OperatingSystem", "ProductType");
            // ProductType: 1=Workstation, 2=Domain Controller, 3=Server
            return productType == "2" || productType == "3";
        }
        catch { return false; }
    }

    private List<ServerRole> DetectInstalledRoles()
    {
        var roles = new List<ServerRole>();

        // ─── IIS (Web Server) ───
        if (IsServiceRunning("W3SVC") || IsServiceInstalled("W3SVC"))
        {
            roles.Add(new ServerRole
            {
                Name = "IIS Web Server",
                ShortName = "IIS",
                IsActive = IsServiceRunning("W3SVC"),
                CriticalServices = new() { "W3SVC", "WAS", "IISADMIN", "HTTP" },
                Description = "Internet Information Services — hosting websites and web apps"
            });
        }

        // ─── SQL Server ───
        if (IsServiceRunning("MSSQLSERVER") || IsServiceInstalled("MSSQLSERVER") ||
            IsServiceRunning("MSSQL$SQLEXPRESS"))
        {
            var sqlService = IsServiceRunning("MSSQLSERVER") ? "MSSQLSERVER" : "MSSQL$SQLEXPRESS";
            roles.Add(new ServerRole
            {
                Name = "SQL Server",
                ShortName = "SQL",
                IsActive = true,
                CriticalServices = new() { sqlService, "SQLSERVERAGENT", "SQLBrowser" },
                Description = "Microsoft SQL Server database engine"
            });
        }

        // ─── Hyper-V ───
        if (IsServiceRunning("vmms") || IsServiceInstalled("vmms"))
        {
            roles.Add(new ServerRole
            {
                Name = "Hyper-V",
                ShortName = "HyperV",
                IsActive = IsServiceRunning("vmms"),
                CriticalServices = new() { "vmms", "vmcompute", "vmicvss", "vmicshutdown" },
                Description = "Hyper-V virtual machine management"
            });
        }

        // ─── Active Directory ───
        if (IsServiceRunning("NTDS") || IsServiceInstalled("NTDS"))
        {
            roles.Add(new ServerRole
            {
                Name = "Active Directory",
                ShortName = "AD",
                IsActive = IsServiceRunning("NTDS"),
                CriticalServices = new() { "NTDS", "Netlogon", "KDC", "DNS", "DFSR" },
                Description = "Active Directory Domain Services"
            });
        }

        // ─── DNS Server ───
        if (IsServiceRunning("DNS") || IsServiceInstalled("DNS"))
        {
            roles.Add(new ServerRole
            {
                Name = "DNS Server",
                ShortName = "DNS",
                IsActive = IsServiceRunning("DNS"),
                CriticalServices = new() { "DNS" },
                Description = "Domain Name System server"
            });
        }

        // ─── DHCP Server ───
        if (IsServiceRunning("DHCPServer") || IsServiceInstalled("DHCPServer"))
        {
            roles.Add(new ServerRole
            {
                Name = "DHCP Server",
                ShortName = "DHCP",
                IsActive = IsServiceRunning("DHCPServer"),
                CriticalServices = new() { "DHCPServer" },
                Description = "Dynamic Host Configuration Protocol server"
            });
        }

        // ─── File Server ───
        if (IsServiceRunning("LanmanServer"))
        {
            roles.Add(new ServerRole
            {
                Name = "File Server",
                ShortName = "FileServer",
                IsActive = true,
                CriticalServices = new() { "LanmanServer", "LanmanWorkstation" },
                Description = "SMB file sharing services"
            });
        }

        // ─── Print Server ───
        if (IsServiceRunning("Spooler") && HasSharedPrinters())
        {
            roles.Add(new ServerRole
            {
                Name = "Print Server",
                ShortName = "PrintServer",
                IsActive = true,
                CriticalServices = new() { "Spooler" },
                Description = "Shared network printing services"
            });
        }

        return roles;
    }

    private List<CriticalService> DetectCriticalServices()
    {
        var services = new List<CriticalService>();

        try
        {
            var criticalNames = new[] { "W3SVC", "MSSQLSERVER", "vmms", "NTDS", "DNS",
                "DHCPServer", "Spooler", "wuauserv", "WinDefend", "EventLog" };

            foreach (var name in criticalNames)
            {
                try
                {
                    using var sc = new ServiceController(name);
                    services.Add(new CriticalService
                    {
                        Name = sc.ServiceName,
                        DisplayName = sc.DisplayName,
                        Status = sc.Status.ToString(),
                        IsRunning = sc.Status == ServiceControllerStatus.Running
                    });
                }
                catch { /* Service not installed */ }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to enumerate critical services");
        }

        return services;
    }

    private List<string> GenerateRecommendations(ServerProfile profile)
    {
        var recs = new List<string>();

        if (!profile.IsServer)
        {
            recs.Add("This is a workstation — all optimizations are safe to apply.");
            return recs;
        }

        recs.Add("⚠️ Server detected — aggressive optimizations are disabled by default.");

        foreach (var role in profile.Roles.Where(r => r.IsActive))
        {
            recs.Add($"🔒 {role.Name} is active — services [{string.Join(", ", role.CriticalServices)}] are protected.");
        }

        if (profile.Roles.Any(r => r.ShortName == "AD"))
            recs.Add("🔒 Domain Controller — do NOT disable Netlogon, KDC, or DNS services.");

        if (profile.Roles.Any(r => r.ShortName == "SQL"))
            recs.Add("💾 SQL Server — consider increasing memory allocation instead of cleaning RAM.");

        if (profile.Roles.Any(r => r.ShortName == "HyperV"))
            recs.Add("🖥️ Hyper-V — avoid killing vmcompute or vmms processes.");

        return recs;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static bool IsServiceRunning(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch { return false; }
    }

    private static bool IsServiceInstalled(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            _ = sc.Status; // Throws if not installed
            return true;
        }
        catch { return false; }
    }

    private bool HasSharedPrinters()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_Printer WHERE Shared=TRUE");
            return searcher.Get().Count > 0;
        }
        catch { return false; }
    }

    private string GetWmi(string cls, string prop)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {prop} FROM {cls}");
            foreach (var obj in searcher.Get())
                return obj[prop]?.ToString()?.Trim() ?? "";
        }
        catch { }
        return "";
    }
}

// ─── Data Models ────────────────────────────────────────────────────────

public sealed class ServerProfile
{
    public bool IsServer { get; set; }
    public string Hostname { get; set; } = "";
    public string OsCaption { get; set; } = "";
    public List<ServerRole> Roles { get; set; } = new();
    public List<CriticalService> CriticalServices { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
    public DateTimeOffset DetectedAt { get; set; }
}

public sealed class ServerRole
{
    public string Name { get; set; } = "";
    public string ShortName { get; set; } = "";
    public bool IsActive { get; set; }
    public List<string> CriticalServices { get; set; } = new();
    public string Description { get; set; } = "";
}

public sealed class CriticalService
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IsRunning { get; set; }
}

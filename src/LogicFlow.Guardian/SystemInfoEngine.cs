// ─────────────────────────────────────────────────────────────────────────────
// LogicFlow – SystemInfoEngine
// At-a-glance system information: CPU, RAM, GPU, Disk, OS, Uptime, Network
// Classic feature from PC Doctor (1993) and Norton Utilities (1995)
// ─────────────────────────────────────────────────────────────────────────────

using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LogicFlow.Guardian;

/// <summary>
/// Live system information dashboard — the "at-a-glance" panel.
/// Gathers CPU, RAM, GPU, storage, OS, network, and uptime data
/// using WMI, Performance Counters, and native APIs.
/// </summary>
public sealed class SystemInfoEngine
{
    // ─── Snapshot Model ──────────────────────────────────────────────────

    public sealed class SystemSnapshot
    {
        // ── CPU ──
        public string CpuName { get; init; } = "";
        public int CpuCores { get; init; }
        public int CpuThreads { get; init; }
        public double CpuSpeedGHz { get; init; }
        public double CpuUsagePercent { get; set; }
        public double CpuTempCelsius { get; set; } = -1; // -1 = unavailable

        // ── Memory ──
        public long TotalRamBytes { get; init; }
        public long AvailableRamBytes { get; set; }
        public double RamUsagePercent => TotalRamBytes > 0
            ? Math.Round((1.0 - (double)AvailableRamBytes / TotalRamBytes) * 100, 1)
            : 0;
        public string TotalRamFormatted => FormatBytes(TotalRamBytes);
        public string AvailableRamFormatted => FormatBytes(AvailableRamBytes);
        public string UsedRamFormatted => FormatBytes(TotalRamBytes - AvailableRamBytes);

        // ── GPU ──
        public string GpuName { get; init; } = "";
        public string GpuDriverVersion { get; init; } = "";
        public long GpuVramBytes { get; init; }
        public string GpuVramFormatted => FormatBytes(GpuVramBytes);

        // ── Storage ──
        public List<DiskInfo> Disks { get; init; } = new();
        public long TotalDiskBytes => Disks.Sum(d => d.TotalBytes);
        public long FreeDiskBytes => Disks.Sum(d => d.FreeBytes);

        // ── OS ──
        public string OsName { get; init; } = "";
        public string OsBuild { get; init; } = "";
        public string OsArchitecture { get; init; } = "";
        public bool IsServer { get; init; }
        public string ComputerName { get; init; } = "";
        public string UserName { get; init; } = "";

        // ── Uptime ──
        public TimeSpan Uptime { get; set; }
        public string UptimeFormatted => Uptime.Days > 0
            ? $"{Uptime.Days}d {Uptime.Hours}h {Uptime.Minutes}m"
            : $"{Uptime.Hours}h {Uptime.Minutes}m {Uptime.Seconds}s";

        // ── Network ──
        public string ActiveNetworkAdapter { get; init; } = "";
        public string IpAddress { get; init; } = "";
        public long NetworkSpeedMbps { get; init; }

        // ── .NET Info ──
        public string DotNetVersion { get; init; } = "";

        // ── Helpers ──
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

    public sealed class DiskInfo
    {
        public string Name { get; init; } = "";
        public string Label { get; init; } = "";
        public string FileSystem { get; init; } = "";
        public string DriveType { get; init; } = "";
        public long TotalBytes { get; init; }
        public long FreeBytes { get; init; }
        public double UsagePercent => TotalBytes > 0
            ? Math.Round((1.0 - (double)FreeBytes / TotalBytes) * 100, 1)
            : 0;
    }

    // ─── Collect Full Snapshot (Heavy — call on page load) ──────────────

    public SystemSnapshot CollectSnapshot()
    {
        var snapshot = new SystemSnapshot
        {
            // CPU
            CpuName = GetWmiString("Win32_Processor", "Name"),
            CpuCores = Environment.ProcessorCount,
            CpuThreads = GetWmiInt("Win32_Processor", "NumberOfLogicalProcessors"),
            CpuSpeedGHz = GetWmiInt("Win32_Processor", "MaxClockSpeed") / 1000.0,

            // Memory
            TotalRamBytes = GetTotalPhysicalMemory(),
            AvailableRamBytes = GetAvailableMemory(),

            // GPU
            GpuName = GetWmiString("Win32_VideoController", "Name"),
            GpuDriverVersion = GetWmiString("Win32_VideoController", "DriverVersion"),
            GpuVramBytes = GetWmiLong("Win32_VideoController", "AdapterRAM"),

            // Disks
            Disks = CollectDiskInfo(),

            // OS
            OsName = GetFriendlyOsName(),
            OsBuild = Environment.OSVersion.Version.ToString(),
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            IsServer = IsWindowsServer(),
            ComputerName = Environment.MachineName,
            UserName = Environment.UserName,

            // Uptime
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),

            // Network
            ActiveNetworkAdapter = GetActiveNetworkAdapter(),
            IpAddress = GetLocalIpAddress(),
            NetworkSpeedMbps = GetNetworkSpeed(),

            // .NET
            DotNetVersion = RuntimeInformation.FrameworkDescription
        };

        return snapshot;
    }

    // ─── Live Metrics (Lightweight — call on timer) ─────────────────────

    private PerformanceCounter? _cpuCounter;

    /// <summary>
    /// Updates CPU usage and available RAM on an existing snapshot.
    /// Call this on a 1-second timer for live dashboard updates.
    /// </summary>
    public void UpdateLiveMetrics(SystemSnapshot snapshot)
    {
        try
        {
            _cpuCounter ??= new PerformanceCounter("Processor", "% Processor Time", "_Total");
            snapshot.CpuUsagePercent = Math.Round(_cpuCounter.NextValue(), 1);
        }
        catch { snapshot.CpuUsagePercent = -1; }

        snapshot.AvailableRamBytes = GetAvailableMemory();
        snapshot.Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
    }

    // ─── WMI Helpers ─────────────────────────────────────────────────────

    private static string GetWmiString(string wmiClass, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
            foreach (var obj in searcher.Get())
                return obj[property]?.ToString()?.Trim() ?? "";
        }
        catch { /* WMI unavailable */ }
        return "";
    }

    private static int GetWmiInt(string wmiClass, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
            foreach (var obj in searcher.Get())
                return Convert.ToInt32(obj[property]);
        }
        catch { }
        return 0;
    }

    private static long GetWmiLong(string wmiClass, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
            foreach (var obj in searcher.Get())
            {
                var val = obj[property];
                if (val is uint u) return u;
                return Convert.ToInt64(val);
            }
        }
        catch { }
        return 0;
    }

    // ─── Memory ──────────────────────────────────────────────────────────

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static long GetTotalPhysicalMemory()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref mem) ? (long)mem.ullTotalPhys : 0;
    }

    private static long GetAvailableMemory()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref mem) ? (long)mem.ullAvailPhys : 0;
    }

    // ─── Disk ────────────────────────────────────────────────────────────

    private static List<DiskInfo> CollectDiskInfo()
    {
        var disks = new List<DiskInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            disks.Add(new DiskInfo
            {
                Name = drive.Name,
                Label = drive.VolumeLabel,
                FileSystem = drive.DriveFormat,
                DriveType = drive.DriveType.ToString(),
                TotalBytes = drive.TotalSize,
                FreeBytes = drive.TotalFreeSpace
            });
        }
        return disks;
    }

    // ─── OS Detection ────────────────────────────────────────────────────

    private static string GetFriendlyOsName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = key?.GetValue("ProductName")?.ToString() ?? "Windows";
            var display = key?.GetValue("DisplayVersion")?.ToString();
            return display != null ? $"{name} ({display})" : name;
        }
        catch { return RuntimeInformation.OSDescription; }
    }

    private static bool IsWindowsServer()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProductType FROM Win32_OperatingSystem");
            foreach (var obj in searcher.Get())
            {
                var productType = Convert.ToInt32(obj["ProductType"]);
                // 1 = Workstation, 2 = Domain Controller, 3 = Server
                return productType >= 2;
            }
        }
        catch { }
        return false;
    }

    // ─── Network ─────────────────────────────────────────────────────────

    private static string GetActiveNetworkAdapter()
    {
        try
        {
            var active = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
            return active?.Name ?? "No active adapter";
        }
        catch { return "Unknown"; }
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            var ip = host.AddressList.FirstOrDefault(a =>
                a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            return ip?.ToString() ?? "N/A";
        }
        catch { return "N/A"; }
    }

    private static long GetNetworkSpeed()
    {
        try
        {
            var active = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
            return active != null ? active.Speed / 1_000_000 : 0; // bps → Mbps
        }
        catch { return 0; }
    }
}

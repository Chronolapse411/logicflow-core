// LogicFlow.Native — Windows Service Manager
// Proprietary implementation by DelgadoLogic.Tech
// Install/uninstall/manage the LogicFlowAgent Windows service via SCM API

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace LogicFlow.Native;

/// <summary>
/// Manages Windows services using the native Service Control Manager API.
/// Used to install/uninstall the LogicFlowAgent background service.
/// </summary>
public sealed class WindowsServiceManager
{
    private readonly ILogger<WindowsServiceManager> _logger;

    // SCM access rights
    private const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
    private const uint SERVICE_ALL_ACCESS = 0xF01FF;
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    private const uint SERVICE_AUTO_START = 0x00000002;
    private const uint SERVICE_DEMAND_START = 0x00000003;
    private const uint SERVICE_ERROR_NORMAL = 0x00000001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_STOP = 0x0020;
    private const uint DELETE = 0x10000;

    // Service state
    private const uint SERVICE_STOPPED = 0x00000001;
    private const uint SERVICE_RUNNING = 0x00000004;

    // Control codes
    private const uint SERVICE_CONTROL_STOP = 0x00000001;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint dwAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateService(
        IntPtr hSCManager, string lpServiceName, string lpDisplayName,
        uint dwDesiredAccess, uint dwServiceType, uint dwStartType, uint dwErrorControl,
        string lpBinaryPathName, string? lpLoadOrderGroup, IntPtr lpdwTagId,
        string? lpDependencies, string? lpServiceStartName, string? lpPassword);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr hService);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(IntPtr hService, uint dwNumServiceArgs, IntPtr lpServiceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr hService, ref SERVICE_STATUS dwServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(IntPtr hService, uint dwInfoLevel, ref SERVICE_DESCRIPTION lpInfo);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    public WindowsServiceManager(ILogger<WindowsServiceManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Installs the LogicFlowAgent as a Windows service with auto-start.
    /// </summary>
    public bool InstallService(string serviceName, string displayName, string description, string exePath)
    {
        _logger.LogInformation("Installing service: {Name}", serviceName);

        var scManager = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (scManager == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open SCM. Run as Administrator.");

        try
        {
            var service = CreateService(scManager, serviceName, displayName,
                SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START,
                SERVICE_ERROR_NORMAL, exePath, null, IntPtr.Zero, null, null, null);

            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 1073) // ERROR_SERVICE_EXISTS
                {
                    _logger.LogInformation("Service already exists: {Name}", serviceName);
                    return true;
                }
                throw new Win32Exception(error, "Failed to create service");
            }

            try
            {
                // Set service description
                var desc = new SERVICE_DESCRIPTION { lpDescription = description };
                ChangeServiceConfig2(service, 1, ref desc); // SERVICE_CONFIG_DESCRIPTION = 1

                _logger.LogInformation("Service installed successfully: {Name}", serviceName);
                return true;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scManager); }
    }

    /// <summary>
    /// Uninstalls the LogicFlowAgent Windows service.
    /// </summary>
    public bool UninstallService(string serviceName)
    {
        _logger.LogInformation("Uninstalling service: {Name}", serviceName);

        var scManager = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (scManager == IntPtr.Zero) return false;

        try
        {
            var service = OpenService(scManager, serviceName, SERVICE_ALL_ACCESS | DELETE);
            if (service == IntPtr.Zero) return false;

            try
            {
                // Stop if running
                var status = new SERVICE_STATUS();
                if (QueryServiceStatus(service, ref status) && status.dwCurrentState == SERVICE_RUNNING)
                {
                    ControlService(service, SERVICE_CONTROL_STOP, ref status);
                    _logger.LogInformation("Stopped service: {Name}", serviceName);
                }

                var result = DeleteService(service);
                _logger.LogInformation("Service uninstalled: {Name} (result={Result})", serviceName, result);
                return result;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scManager); }
    }

    /// <summary>
    /// Checks if the LogicFlowAgent service is currently running.
    /// </summary>
    public bool IsServiceRunning(string serviceName)
    {
        var scManager = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (scManager == IntPtr.Zero) return false;

        try
        {
            var service = OpenService(scManager, serviceName, SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero) return false;

            try
            {
                var status = new SERVICE_STATUS();
                QueryServiceStatus(service, ref status);
                return status.dwCurrentState == SERVICE_RUNNING;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scManager); }
    }

    /// <summary>
    /// Starts the service.
    /// </summary>
    public bool StartServiceByName(string serviceName)
    {
        var scManager = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (scManager == IntPtr.Zero) return false;

        try
        {
            var service = OpenService(scManager, serviceName, SERVICE_ALL_ACCESS);
            if (service == IntPtr.Zero) return false;

            try
            {
                return StartService(service, 0, IntPtr.Zero);
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scManager); }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SERVICE_STATUS
{
    public uint dwServiceType;
    public uint dwCurrentState;
    public uint dwControlsAccepted;
    public uint dwWin32ExitCode;
    public uint dwServiceSpecificExitCode;
    public uint dwCheckPoint;
    public uint dwWaitHint;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SERVICE_DESCRIPTION
{
    public string lpDescription;
}

// LogicFlow.Lazarus — Deep-Sector Data Recovery Module
// Proprietary implementation by DelgadoLogic.Tech
// Raw disk sector scanning, file carving, USB/MTP recovery

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace LogicFlow.Lazarus;

/// <summary>
/// Low-level disk sector scanner using Windows API P/Invoke.
/// Reads raw sectors from physical drives for file recovery.
/// </summary>
public sealed class SectorScanner : IDisposable
{
    private readonly ILogger<SectorScanner> _logger;
    private IntPtr _driveHandle = IntPtr.Zero;
    private const int DefaultSectorSize = 512;
    private const int ModernSectorSize = 4096;

    // P/Invoke declarations for raw disk access
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(
        IntPtr hFile, long liDistanceToMove,
        out long lpNewFilePointer, uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagNoBuffering = 0x20000000;

    public SectorScanner(ILogger<SectorScanner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Opens a physical drive for raw sector reading.
    /// Requires administrator privileges.
    /// </summary>
    public bool OpenDrive(int driveNumber)
    {
        var path = $@"\\.\PhysicalDrive{driveNumber}";
        _driveHandle = CreateFile(path, GenericRead,
            FileShareRead | FileShareWrite, IntPtr.Zero,
            OpenExisting, FileFlagNoBuffering, IntPtr.Zero);

        if (_driveHandle == IntPtr.Zero || _driveHandle == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            _logger.LogError("Failed to open {Drive}. Error: {Error}. Run as administrator.", path, error);
            return false;
        }

        _logger.LogInformation("Opened {Drive} for sector scanning", path);
        return true;
    }

    /// <summary>
    /// Reads a specific sector from the opened drive.
    /// </summary>
    public byte[]? ReadSector(long sectorOffset, int sectorSize = DefaultSectorSize)
    {
        if (_driveHandle == IntPtr.Zero) return null;

        var buffer = new byte[sectorSize];
        var byteOffset = sectorOffset * sectorSize;

        if (!SetFilePointerEx(_driveHandle, byteOffset, out _, 0))
        {
            _logger.LogWarning("Failed to seek to sector {Offset}", sectorOffset);
            return null;
        }

        if (!ReadFile(_driveHandle, buffer, (uint)sectorSize, out var bytesRead, IntPtr.Zero) || bytesRead == 0)
        {
            _logger.LogWarning("Failed to read sector {Offset}", sectorOffset);
            return null;
        }

        return buffer;
    }

    /// <summary>
    /// Reads a contiguous range of sectors into a streaming buffer.
    /// Optimized for sequential scanning with minimal allocations.
    /// </summary>
    public async IAsyncEnumerable<SectorChunk> ScanRangeAsync(
        long startSector, long endSector, int chunkSectors = 256)
    {
        var chunkSize = chunkSectors * DefaultSectorSize;
        var buffer = new byte[chunkSize];

        for (long sector = startSector; sector < endSector; sector += chunkSectors)
        {
            var byteOffset = sector * DefaultSectorSize;
            if (!SetFilePointerEx(_driveHandle, byteOffset, out _, 0)) continue;

            if (ReadFile(_driveHandle, buffer, (uint)chunkSize, out var bytesRead, IntPtr.Zero) && bytesRead > 0)
            {
                yield return new SectorChunk
                {
                    StartSector = sector,
                    Data = buffer[..(int)bytesRead],
                    ByteOffset = byteOffset
                };
            }

            await Task.Yield(); // Prevent UI thread starvation
        }
    }

    public void Dispose()
    {
        if (_driveHandle != IntPtr.Zero && _driveHandle != new IntPtr(-1))
        {
            CloseHandle(_driveHandle);
            _driveHandle = IntPtr.Zero;
        }
    }
}

/// <summary>
/// File Carver: recovers files by scanning raw sectors for known file signatures (magic bytes).
/// Pure signature-based approach — works even when file system metadata is destroyed.
/// </summary>
public sealed class FileCarver
{
    private readonly ILogger<FileCarver> _logger;

    /// <summary>
    /// Known file signatures for carving. Header bytes → file type info.
    /// </summary>
    private static readonly FileSignature[] Signatures =
    [
        new([0xFF, 0xD8, 0xFF, 0xE0], "jpg", "JPEG Image", null),
        new([0xFF, 0xD8, 0xFF, 0xE1], "jpg", "JPEG Image (EXIF)", null),
        new([0x89, 0x50, 0x4E, 0x47], "png", "PNG Image", [0x49, 0x45, 0x4E, 0x44]),
        new([0x25, 0x50, 0x44, 0x46], "pdf", "PDF Document", null),
        new([0x50, 0x4B, 0x03, 0x04], "zip", "ZIP Archive / Office Document", null),
        new([0x52, 0x61, 0x72, 0x21], "rar", "RAR Archive", null),
        new([0x47, 0x49, 0x46, 0x38], "gif", "GIF Image", [0x00, 0x3B]),
        new([0x49, 0x44, 0x33], "mp3", "MP3 Audio", null),
        new([0x53, 0x51, 0x4C, 0x69], "sqlite", "SQLite Database", null),
        new([0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70], "mp4", "MP4 Video", null),
        new([0x00, 0x00, 0x00, 0x1C, 0x66, 0x74, 0x79, 0x70], "mp4", "MP4 Video", null),
        new([0x52, 0x49, 0x46, 0x46], "avi", "AVI/WAV Media", null),
        new([0xD0, 0xCF, 0x11, 0xE0], "doc", "MS Office Legacy (DOC/XLS/PPT)", null),
    ];

    public FileCarver(ILogger<FileCarver> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Scans a byte buffer for known file signatures and returns found headers.
    /// </summary>
    public List<CarvedFileHeader> ScanBuffer(ReadOnlySpan<byte> buffer, long baseOffset)
    {
        var found = new List<CarvedFileHeader>();

        for (int i = 0; i < buffer.Length - 8; i++)
        {
            foreach (var sig in Signatures)
            {
                if (i + sig.HeaderBytes.Length > buffer.Length) continue;

                var slice = buffer.Slice(i, sig.HeaderBytes.Length);
                if (slice.SequenceEqual(sig.HeaderBytes))
                {
                    found.Add(new CarvedFileHeader
                    {
                        Extension = sig.Extension,
                        FileType = sig.Description,
                        ByteOffset = baseOffset + i,
                        HeaderSize = sig.HeaderBytes.Length
                    });
                    _logger.LogDebug("Found {Type} at offset {Offset}", sig.Description, baseOffset + i);
                }
            }
        }

        return found;
    }
}

/// <summary>
/// MTP Bridge: enables recovery from iOS/Android devices via Windows Portable Devices API.
/// </summary>
public sealed class MtpBridge
{
    private readonly ILogger<MtpBridge> _logger;

    public MtpBridge(ILogger<MtpBridge> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Enumerates connected MTP devices (phones, tablets, cameras).
    /// </summary>
    public List<MtpDevice> EnumerateDevices()
    {
        var devices = new List<MtpDevice>();

        // Use WMI to detect PnP portable devices
        using var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT * FROM Win32_PnPEntity WHERE Service = 'WUDFRd' OR Service = 'usbstor'");

        foreach (var obj in searcher.Get())
        {
            var name = obj["Name"]?.ToString() ?? "";
            var deviceId = obj["DeviceID"]?.ToString() ?? "";

            if (string.IsNullOrEmpty(name)) continue;

            devices.Add(new MtpDevice
            {
                Name = name,
                DeviceId = deviceId,
                Manufacturer = obj["Manufacturer"]?.ToString() ?? "",
                Status = obj["Status"]?.ToString() ?? "Unknown"
            });
        }

        _logger.LogInformation("Found {Count} portable devices", devices.Count);
        return devices;
    }
}

// ─── Data Models ───────────────────────────────────────────────
public sealed record SectorChunk
{
    public long StartSector { get; init; }
    public byte[] Data { get; init; } = [];
    public long ByteOffset { get; init; }
}

public sealed record FileSignature(byte[] HeaderBytes, string Extension, string Description, byte[]? FooterBytes);

public sealed record CarvedFileHeader
{
    public string Extension { get; init; } = "";
    public string FileType { get; init; } = "";
    public long ByteOffset { get; init; }
    public int HeaderSize { get; init; }
}

public sealed record MtpDevice
{
    public string Name { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Status { get; init; } = "";
}

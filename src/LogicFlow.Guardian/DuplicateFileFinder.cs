// ─────────────────────────────────────────────────────────────────────────────
// LogicFlow – DuplicateFileFinder
// Finds duplicate files by hash — classic feature from Glary Utilities
// ─────────────────────────────────────────────────────────────────────────────

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace LogicFlow.Guardian;

/// <summary>
/// Scans directories for duplicate files using size pre-filter + SHA-256 hash.
/// Returns grouped duplicates with total wasted space calculations.
/// </summary>
public sealed class DuplicateFileFinder
{
    private readonly ILogger<DuplicateFileFinder>? _logger;

    public DuplicateFileFinder(ILogger<DuplicateFileFinder>? logger = null)
    {
        _logger = logger;
    }

    // ─── Data Models ─────────────────────────────────────────────────────

    public sealed class DuplicateGroup
    {
        public string Hash { get; init; } = "";
        public long FileSize { get; init; }
        public List<DuplicateFile> Files { get; init; } = new();
        public long WastedBytes => FileSize * (Files.Count - 1);
        public string WastedFormatted => FormatBytes(WastedBytes);
        public string SizeFormatted => FormatBytes(FileSize);
    }

    public sealed class DuplicateFile
    {
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public DateTime LastModified { get; init; }
        public bool IsSelected { get; set; }
    }

    public sealed class ScanProgress
    {
        public int FilesScanned { get; set; }
        public int DuplicateGroupsFound { get; set; }
        public long WastedBytes { get; set; }
        public string CurrentFile { get; set; } = "";
        public string Phase { get; set; } = "";
    }

    public sealed class DuplicateReport
    {
        public List<DuplicateGroup> Groups { get; init; } = new();
        public int TotalFilesScanned { get; init; }
        public int TotalDuplicates { get; init; }
        public long TotalWastedBytes { get; init; }
        public TimeSpan ScanDuration { get; init; }
        public string WastedFormatted => FormatBytes(TotalWastedBytes);
    }

    // ─── Scan ────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans directories for duplicate files.
    /// </summary>
    /// <param name="paths">Directories to scan</param>
    /// <param name="minSizeBytes">Minimum file size to consider (default 1KB)</param>
    /// <param name="extensions">Optional file extension filter (e.g. ".jpg", ".mp4")</param>
    /// <param name="progress">Optional progress callback</param>
    public DuplicateReport Scan(
        IEnumerable<string> paths,
        long minSizeBytes = 1024,
        string[]? extensions = null,
        Action<ScanProgress>? progress = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var prog = new ScanProgress { Phase = "Enumerating files..." };
        progress?.Invoke(prog);

        _logger?.LogInformation("Starting duplicate scan...");

        // Phase 1: Group by file size (fast pre-filter)
        var sizeGroups = new Dictionary<long, List<string>>();
        int totalFiles = 0;

        foreach (var rootPath in paths)
        {
            if (!Directory.Exists(rootPath)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (!info.Exists || info.Length < minSizeBytes) continue;
                        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                        // Extension filter
                        if (extensions != null && extensions.Length > 0)
                        {
                            var ext = info.Extension.ToLowerInvariant();
                            if (!extensions.Contains(ext)) continue;
                        }

                        if (!sizeGroups.ContainsKey(info.Length))
                            sizeGroups[info.Length] = new List<string>();
                        sizeGroups[info.Length].Add(file);
                        totalFiles++;
                    }
                    catch { }
                }
            }
            catch { }
        }

        prog.Phase = "Hashing potential duplicates...";
        prog.FilesScanned = totalFiles;
        progress?.Invoke(prog);

        // Phase 2: For sizes with 2+ files, compute SHA-256
        var hashGroups = new Dictionary<string, DuplicateGroup>();
        int filesHashed = 0;

        foreach (var (size, files) in sizeGroups.Where(kv => kv.Value.Count >= 2))
        {
            foreach (var file in files)
            {
                try
                {
                    var hash = ComputeHash(file);
                    if (!hashGroups.ContainsKey(hash))
                    {
                        hashGroups[hash] = new DuplicateGroup { Hash = hash, FileSize = size };
                    }

                    var info = new FileInfo(file);
                    hashGroups[hash].Files.Add(new DuplicateFile
                    {
                        Path = file,
                        Name = info.Name,
                        LastModified = info.LastWriteTime
                    });

                    filesHashed++;
                    prog.CurrentFile = Path.GetFileName(file);
                    prog.FilesScanned = filesHashed;
                    if (filesHashed % 50 == 0) progress?.Invoke(prog);
                }
                catch { }
            }
        }

        // Filter to actual duplicates (2+ files with same hash)
        var duplicates = hashGroups.Values
            .Where(g => g.Files.Count >= 2)
            .OrderByDescending(g => g.WastedBytes)
            .ToList();

        sw.Stop();

        var report = new DuplicateReport
        {
            Groups = duplicates,
            TotalFilesScanned = totalFiles,
            TotalDuplicates = duplicates.Sum(g => g.Files.Count - 1),
            TotalWastedBytes = duplicates.Sum(g => g.WastedBytes),
            ScanDuration = sw.Elapsed
        };

        _logger?.LogInformation("Duplicate scan complete. {Groups} groups, {Wasted} wasted in {Time:0.0}s",
            duplicates.Count, report.WastedFormatted, sw.Elapsed.TotalSeconds);

        return report;
    }

    /// <summary>
    /// Deletes selected duplicate files from scan results.
    /// </summary>
    public (int deleted, long bytesFreed) DeleteSelected(DuplicateReport report)
    {
        int deleted = 0;
        long freed = 0;

        foreach (var group in report.Groups)
        {
            foreach (var file in group.Files.Where(f => f.IsSelected))
            {
                try
                {
                    if (File.Exists(file.Path))
                    {
                        var size = new FileInfo(file.Path).Length;
                        File.Delete(file.Path);
                        deleted++;
                        freed += size;
                    }
                }
                catch { }
            }
        }

        _logger?.LogInformation("Deleted {Count} duplicates, freed {Size}", deleted, FormatBytes(freed));
        return (deleted, freed);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static string ComputeHash(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192);
        var hashBytes = SHA256.HashData(stream);
        return Convert.ToHexString(hashBytes);
    }

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

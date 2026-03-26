// ─────────────────────────────────────────────────────────────────────────────
// LogicFlow – DiskSpaceAnalyzer
// Visual disk space analysis — find what's eating your storage.
// Classic feature from TuneUp Disk Space Explorer (2007)
// ─────────────────────────────────────────────────────────────────────────────

using Microsoft.Extensions.Logging;

namespace LogicFlow.Guardian;

/// <summary>
/// Analyzes disk space usage by folder/file, producing data for
/// treemap or sunburst visualizations in the dashboard.
/// </summary>
public sealed class DiskSpaceAnalyzer
{
    private readonly ILogger<DiskSpaceAnalyzer>? _logger;

    public DiskSpaceAnalyzer(ILogger<DiskSpaceAnalyzer>? logger = null)
    {
        _logger = logger;
    }

    // ─── Data Models ─────────────────────────────────────────────────────

    public sealed class FolderNode
    {
        public string Name { get; init; } = "";
        public string FullPath { get; init; } = "";
        public long SizeBytes { get; set; }
        public int FileCount { get; set; }
        public int FolderCount { get; set; }
        public double Percentage { get; set; }
        public bool IsFile { get; init; }
        public List<FolderNode> Children { get; init; } = new();
        public string SizeFormatted => FormatBytes(SizeBytes);
    }

    public sealed class LargeFile
    {
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public string Extension { get; init; } = "";
        public long SizeBytes { get; init; }
        public DateTime LastModified { get; init; }
        public string SizeFormatted => FormatBytes(SizeBytes);
    }

    public sealed class SpaceReport
    {
        public string DrivePath { get; init; } = "";
        public long TotalBytes { get; init; }
        public long UsedBytes { get; init; }
        public long FreeBytes { get; init; }
        public FolderNode RootNode { get; init; } = new();
        public List<LargeFile> LargestFiles { get; init; } = new();
        public Dictionary<string, long> ByExtension { get; init; } = new();
        public int TotalFilesScanned { get; set; }
        public int TotalFoldersScanned { get; set; }
        public TimeSpan ScanDuration { get; set; }
    }

    // ─── Analyze a Drive ─────────────────────────────────────────────────

    /// <summary>
    /// Analyzes disk space usage for a given drive root (e.g. "C:\").
    /// Returns a tree structure suitable for treemap visualization.
    /// </summary>
    /// <param name="drivePath">Drive root path, e.g. "C:\"</param>
    /// <param name="maxDepth">How many folder levels deep to scan (default 4)</param>
    /// <param name="topFilesCount">Number of largest files to track (default 50)</param>
    public SpaceReport Analyze(string drivePath, int maxDepth = 4, int topFilesCount = 50)
    {
        _logger?.LogInformation("Analyzing disk space for {Drive}...", drivePath);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var driveInfo = new DriveInfo(drivePath.TrimEnd('\\'));
        var largestFiles = new SortedList<long, LargeFile>(new DescendingComparer());
        var extensionMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        int totalFiles = 0, totalFolders = 0;

        var rootNode = ScanFolder(drivePath, 0, maxDepth, largestFiles, topFilesCount,
            extensionMap, ref totalFiles, ref totalFolders);

        // Calculate percentages
        if (rootNode.SizeBytes > 0)
            CalculatePercentages(rootNode, rootNode.SizeBytes);

        sw.Stop();

        var report = new SpaceReport
        {
            DrivePath = drivePath,
            TotalBytes = driveInfo.TotalSize,
            UsedBytes = driveInfo.TotalSize - driveInfo.TotalFreeSpace,
            FreeBytes = driveInfo.TotalFreeSpace,
            RootNode = rootNode,
            LargestFiles = largestFiles.Values.Take(topFilesCount).ToList(),
            ByExtension = extensionMap.OrderByDescending(kv => kv.Value)
                .Take(20)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            TotalFilesScanned = totalFiles,
            TotalFoldersScanned = totalFolders,
            ScanDuration = sw.Elapsed
        };

        _logger?.LogInformation("Disk analysis complete. {Files} files, {Folders} folders in {Time:0.0}s",
            totalFiles, totalFolders, sw.Elapsed.TotalSeconds);

        return report;
    }

    // ─── Recursive Scanner ───────────────────────────────────────────────

    private FolderNode ScanFolder(string path, int depth, int maxDepth,
        SortedList<long, LargeFile> largestFiles, int maxLargest,
        Dictionary<string, long> extensionMap, ref int totalFiles, ref int totalFolders)
    {
        var node = new FolderNode
        {
            Name = Path.GetFileName(path) ?? path,
            FullPath = path,
            IsFile = false
        };

        totalFolders++;

        // Scan files in this directory
        try
        {
            foreach (var file in Directory.EnumerateFiles(path))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists) continue;

                    var size = info.Length;
                    node.SizeBytes += size;
                    node.FileCount++;
                    totalFiles++;

                    // Track extension sizes
                    var ext = info.Extension.ToLowerInvariant();
                    if (string.IsNullOrEmpty(ext)) ext = "(no extension)";
                    extensionMap[ext] = extensionMap.GetValueOrDefault(ext) + size;

                    // Track largest files
                    if (largestFiles.Count < maxLargest || size > largestFiles.Keys.Last())
                    {
                        // Use a unique key to avoid collisions
                        var key = size * 1000 + (totalFiles % 1000);
                        largestFiles[key] = new LargeFile
                        {
                            Path = file,
                            Name = info.Name,
                            Extension = ext,
                            SizeBytes = size,
                            LastModified = info.LastWriteTime
                        };
                        if (largestFiles.Count > maxLargest)
                            largestFiles.RemoveAt(largestFiles.Count - 1);
                    }
                }
                catch { /* Skip inaccessible files */ }
            }
        }
        catch { /* Skip inaccessible directories */ }

        // Recurse into subdirectories
        if (depth < maxDepth)
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(path))
                {
                    try
                    {
                        // Skip system junctions and reparse points
                        var dirInfo = new DirectoryInfo(dir);
                        if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                        var child = ScanFolder(dir, depth + 1, maxDepth,
                            largestFiles, maxLargest, extensionMap, ref totalFiles, ref totalFolders);

                        node.SizeBytes += child.SizeBytes;
                        node.FileCount += child.FileCount;
                        node.FolderCount += child.FolderCount + 1;
                        node.Children.Add(child);
                    }
                    catch { /* Skip inaccessible subdirectories */ }
                }
            }
            catch { }
        }

        // Sort children by size descending
        node.Children.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));

        return node;
    }

    private static void CalculatePercentages(FolderNode node, long totalBytes)
    {
        node.Percentage = totalBytes > 0 ? Math.Round((double)node.SizeBytes / totalBytes * 100, 2) : 0;
        foreach (var child in node.Children)
            CalculatePercentages(child, totalBytes);
    }

    // ─── Quick Summary (Lighter) ─────────────────────────────────────────

    /// <summary>
    /// Gets a quick summary of all drives without deep folder scanning.
    /// </summary>
    public List<DriveSummary> GetDriveSummaries()
    {
        var summaries = new List<DriveSummary>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            summaries.Add(new DriveSummary
            {
                Name = drive.Name,
                Label = drive.VolumeLabel,
                FileSystem = drive.DriveFormat,
                Type = drive.DriveType.ToString(),
                TotalBytes = drive.TotalSize,
                FreeBytes = drive.TotalFreeSpace,
                UsedBytes = drive.TotalSize - drive.TotalFreeSpace,
                UsagePercent = Math.Round((1.0 - (double)drive.TotalFreeSpace / drive.TotalSize) * 100, 1)
            });
        }
        return summaries;
    }

    public sealed class DriveSummary
    {
        public string Name { get; init; } = "";
        public string Label { get; init; } = "";
        public string FileSystem { get; init; } = "";
        public string Type { get; init; } = "";
        public long TotalBytes { get; init; }
        public long FreeBytes { get; init; }
        public long UsedBytes { get; init; }
        public double UsagePercent { get; init; }
        public string TotalFormatted => FormatBytes(TotalBytes);
        public string FreeFormatted => FormatBytes(FreeBytes);
        public string UsedFormatted => FormatBytes(UsedBytes);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private class DescendingComparer : IComparer<long>
    {
        public int Compare(long x, long y) => y.CompareTo(x);
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

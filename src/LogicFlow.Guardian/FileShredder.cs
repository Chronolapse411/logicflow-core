// ─────────────────────────────────────────────────────────────────────────────
// LogicFlow – FileShredder
// DoD 5220.22-M compliant secure file deletion.
// Classic feature from Norton Utilities (1995) and TuneUp Shredder (2003)
// ─────────────────────────────────────────────────────────────────────────────

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace LogicFlow.Guardian;

/// <summary>
/// Securely deletes files by overwriting content with multiple passes,
/// making recovery impossible even with forensic tools.
/// </summary>
public sealed class FileShredder
{
    private readonly ILogger<FileShredder>? _logger;

    public FileShredder(ILogger<FileShredder>? logger = null)
    {
        _logger = logger;
    }

    // ─── Shred Methods ───────────────────────────────────────────────────

    public enum ShredMethod
    {
        /// <summary>1 pass — fast, sufficient for SSDs</summary>
        QuickZero,
        /// <summary>3 passes — DoD 5220.22-M standard</summary>
        DoD3Pass,
        /// <summary>7 passes — DoD 5220.22-M ECE variant</summary>
        DoD7Pass,
        /// <summary>3 passes of cryptographic random data</summary>
        Random3Pass
    }

    public sealed class ShredResult
    {
        public int FilesShredded { get; set; }
        public int FilesFailed { get; set; }
        public long TotalBytesOverwritten { get; set; }
        public List<string> Errors { get; init; } = new();
        public ShredMethod MethodUsed { get; init; }
    }

    // ─── Shred Files ─────────────────────────────────────────────────────

    /// <summary>
    /// Securely shreds one or more files.
    /// </summary>
    public ShredResult Shred(IEnumerable<string> filePaths, ShredMethod method = ShredMethod.DoD3Pass)
    {
        var result = new ShredResult { MethodUsed = method };
        int passes = method switch
        {
            ShredMethod.QuickZero => 1,
            ShredMethod.DoD3Pass => 3,
            ShredMethod.DoD7Pass => 7,
            ShredMethod.Random3Pass => 3,
            _ => 3
        };

        foreach (var path in filePaths)
        {
            try
            {
                if (!File.Exists(path))
                {
                    result.FilesFailed++;
                    result.Errors.Add($"File not found: {path}");
                    continue;
                }

                var fileInfo = new FileInfo(path);
                var size = fileInfo.Length;

                // Remove read-only attribute if present
                if (fileInfo.IsReadOnly)
                    fileInfo.IsReadOnly = false;

                // Overwrite passes
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[Math.Min(65536, size > 0 ? size : 1)];

                    for (int pass = 0; pass < passes; pass++)
                    {
                        stream.Position = 0;
                        long remaining = size;

                        while (remaining > 0)
                        {
                            int chunk = (int)Math.Min(buffer.Length, remaining);
                            FillBuffer(buffer, chunk, method, pass);
                            stream.Write(buffer, 0, chunk);
                            remaining -= chunk;
                        }

                        stream.Flush();
                    }
                }

                // Rename to random name, then delete
                var dir = Path.GetDirectoryName(path) ?? ".";
                var tempName = Path.Combine(dir, Path.GetRandomFileName());
                File.Move(path, tempName);
                File.Delete(tempName);

                result.FilesShredded++;
                result.TotalBytesOverwritten += size * passes;
            }
            catch (Exception ex)
            {
                result.FilesFailed++;
                if (result.Errors.Count < 20)
                    result.Errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        _logger?.LogInformation("Shredded {Count} files ({Method}), {Failed} failed",
            result.FilesShredded, method, result.FilesFailed);

        return result;
    }

    /// <summary>
    /// Securely shreds an entire directory and all its contents.
    /// </summary>
    public ShredResult ShredDirectory(string directoryPath, ShredMethod method = ShredMethod.DoD3Pass)
    {
        if (!Directory.Exists(directoryPath))
            return new ShredResult { MethodUsed = method, Errors = [$"Directory not found: {directoryPath}"] };

        var allFiles = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories);
        var result = Shred(allFiles, method);

        // Remove empty directories
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(directoryPath, "*", SearchOption.AllDirectories).Reverse())
            {
                try { Directory.Delete(dir); } catch { }
            }
            Directory.Delete(directoryPath);
        }
        catch { }

        return result;
    }

    // ─── Buffer Fill ─────────────────────────────────────────────────────

    private static void FillBuffer(byte[] buffer, int count, ShredMethod method, int pass)
    {
        switch (method)
        {
            case ShredMethod.QuickZero:
                Array.Clear(buffer, 0, count);
                break;

            case ShredMethod.DoD3Pass:
                // Pass 0: all zeros, Pass 1: all ones, Pass 2: random
                if (pass == 0) Array.Clear(buffer, 0, count);
                else if (pass == 1) Array.Fill(buffer, (byte)0xFF, 0, count);
                else RandomNumberGenerator.Fill(buffer.AsSpan(0, count));
                break;

            case ShredMethod.DoD7Pass:
                // Alternating patterns + random
                if (pass % 2 == 0) RandomNumberGenerator.Fill(buffer.AsSpan(0, count));
                else Array.Fill(buffer, (byte)(pass * 37), 0, count); // Deterministic pattern
                break;

            case ShredMethod.Random3Pass:
                RandomNumberGenerator.Fill(buffer.AsSpan(0, count));
                break;
        }
    }
}

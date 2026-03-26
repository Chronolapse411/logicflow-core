// LogicFlow.Dashboard — Auto-Update Engine
// Proprietary implementation by DelgadoLogic.Tech
// Version check, download, and self-update mechanism

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LogicFlow.Dashboard;

/// <summary>
/// Handles automatic updates for LogicFlow.
/// Checks delgadologic.tech API for new versions, downloads, and applies updates.
/// </summary>
public sealed class AutoUpdateEngine : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<AutoUpdateEngine>? _logger;
    private readonly string _currentVersion;
    private readonly string _updateDir;
    private readonly string _updateApi;

    public event Action<UpdateInfo>? UpdateAvailable;
    public event Action<double>? DownloadProgress;
    public event Action<string>? UpdateFailed;

    public AutoUpdateEngine(string currentVersion = "1.0.0")
    {
        _currentVersion = currentVersion;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("User-Agent", $"LogicFlow/{currentVersion}");
        _updateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LogicFlow", "Updates");
        _updateApi = "https://delgadologic.tech/api/logicflow/version";
        Directory.CreateDirectory(_updateDir);
    }

    /// <summary>
    /// Checks the update API for a newer version.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            var response = await _http.GetStringAsync(_updateApi);
            var info = JsonSerializer.Deserialize<UpdateInfo>(response, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (info != null && IsNewerVersion(info.Version))
            {
                UpdateAvailable?.Invoke(info);
                return info;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Update check failed");
        }

        return null;
    }

    /// <summary>
    /// Downloads the update package with progress reporting.
    /// </summary>
    public async Task<string?> DownloadUpdateAsync(UpdateInfo info, CancellationToken ct = default)
    {
        try
        {
            var destPath = Path.Combine(_updateDir, $"LogicFlowSetup_v{info.Version}.exe");
            if (File.Exists(destPath)) return destPath;

            using var response = await _http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            var buffer = new byte[81920];
            long downloaded = 0;

            using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                downloaded += bytesRead;
                if (totalBytes > 0)
                    DownloadProgress?.Invoke((double)downloaded / totalBytes * 100);
            }

            return destPath;
        }
        catch (Exception ex)
        {
            UpdateFailed?.Invoke(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Launches the update installer and exits the current process.
    /// </summary>
    public void ApplyUpdate(string setupPath)
    {
        if (!File.Exists(setupPath)) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = setupPath,
            Arguments = "/SILENT /RESTARTAPPLICATIONS",
            UseShellExecute = true,
            Verb = "runas"
        });

        System.Windows.Application.Current.Shutdown();
    }

    private bool IsNewerVersion(string remoteVersion)
    {
        if (Version.TryParse(remoteVersion, out var remote) &&
            Version.TryParse(_currentVersion, out var current))
            return remote > current;
        return false;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Update information from the version API.
/// </summary>
public sealed class UpdateInfo
{
    public string Version { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public string Sha256Hash { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset ReleasedAt { get; set; }
    public bool IsCritical { get; set; }
}

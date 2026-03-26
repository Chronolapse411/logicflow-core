// LogicFlow.Dashboard — Auto-Update Engine
// Proprietary implementation by DelgadoLogic.Tech
// Version check, SHA-256 hash verification, and self-update mechanism
// Sovereign update endpoint: aeon-update-server (Cloud Run / GCP)

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LogicFlow.Dashboard;

/// <summary>
/// Handles automatic updates for LogicFlow via the DelgadoLogic sovereign update server.
/// Verifies SHA-256 hashes on downloaded packages before applying them.
/// Endpoint: https://aeon-update-server-y2r5ogip6q-ue.a.run.app/v1/update/logicflow/{channel}
/// </summary>
public sealed class AutoUpdateEngine : IDisposable
{
    // ─── Sovereign Server ──────────────────────────────────────────────────
    private const string SovereignBase    = "https://aeon-update-server-y2r5ogip6q-ue.a.run.app";
    private const string ProductId        = "logicflow";
    private const int    MaxRetries       = 3;
    private const int    RetryDelayMs     = 2000;

    private readonly HttpClient _http;
    private readonly ILogger<AutoUpdateEngine>? _logger;
    private readonly string _currentVersion;
    private readonly string _channel;
    private readonly string _updateDir;

    public event Action<UpdateInfo>? UpdateAvailable;
    public event Action<double>?     DownloadProgress;
    public event Action<string>?     UpdateFailed;

    public AutoUpdateEngine(string currentVersion = "1.0.0", string channel = "stable")
    {
        _currentVersion = currentVersion;
        _channel        = channel;
        _http           = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("User-Agent", $"LogicFlow/{currentVersion} (DelgadoLogic Sovereign)");
        _updateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LogicFlow", "Updates");
        Directory.CreateDirectory(_updateDir);
    }

    // ─── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Checks the sovereign update server for a newer version.
    /// Returns null if up-to-date or the server is unreachable.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var endpoint = $"{SovereignBase}/v1/update/{ProductId}/{_channel}";

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var json     = await _http.GetStringAsync(endpoint, ct);
                var envelope = JsonSerializer.Deserialize<UpdateEnvelope>(json, JsonOpts);
                if (envelope?.Manifest == null) return null;

                var info = MapToUpdateInfo(envelope.Manifest);

                if (info != null && IsNewerVersion(info.Version))
                {
                    _logger?.LogInformation("Update available: {Version} (critical={Critical})",
                        info.Version, info.IsCritical);
                    UpdateAvailable?.Invoke(info);
                    return info;
                }

                _logger?.LogDebug("No update — current={Current}, remote={Remote}",
                    _currentVersion, info?.Version ?? "0.0.0");
                return null;
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Update check attempt {Attempt} failed", attempt);
                if (attempt < MaxRetries)
                    await Task.Delay(RetryDelayMs * attempt, ct);
            }
        }

        return null;
    }

    /// <summary>
    /// Downloads the update package with progress reporting and SHA-256 verification.
    /// </summary>
    public async Task<string?> DownloadUpdateAsync(UpdateInfo info, CancellationToken ct = default)
    {
        try
        {
            var destPath = Path.Combine(_updateDir, $"LogicFlowSetup_v{info.Version}.exe");
            if (File.Exists(destPath) && await VerifyHashAsync(destPath, info.Sha256Hash, ct))
            {
                _logger?.LogInformation("Installer already present and verified: {Path}", destPath);
                return destPath;
            }

            using var response = await _http.GetAsync(
                info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            var buffer     = new byte[81920];
            long downloaded = 0;

            using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            using var fileStream    = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                downloaded += bytesRead;
                if (totalBytes > 0)
                    DownloadProgress?.Invoke((double)downloaded / totalBytes * 100);
            }

            fileStream.Close();

            // SHA-256 verification
            if (!string.IsNullOrWhiteSpace(info.Sha256Hash))
            {
                if (!await VerifyHashAsync(destPath, info.Sha256Hash, ct))
                {
                    File.Delete(destPath);
                    var msg = "SHA-256 hash verification failed — download discarded";
                    _logger?.LogError(msg);
                    UpdateFailed?.Invoke(msg);
                    return null;
                }
            }

            _logger?.LogInformation("Download verified: {Path}", destPath);
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
            FileName         = setupPath,
            Arguments        = "/SILENT /RESTARTAPPLICATIONS",
            UseShellExecute  = true,
            Verb             = "runas"
        });

        System.Windows.Application.Current.Shutdown();
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    private bool IsNewerVersion(string remoteVersion)
    {
        if (Version.TryParse(remoteVersion, out var remote) &&
            Version.TryParse(_currentVersion, out var current))
            return remote > current;
        return false;
    }

    private static UpdateInfo? MapToUpdateInfo(SovereignManifest m)
    {
        if (string.IsNullOrEmpty(m.Version) || m.Status == "no_update")
            return new UpdateInfo { Version = m.Version ?? "0.0.0" };

        return new UpdateInfo
        {
            Version      = m.Version,
            DownloadUrl  = m.DownloadUrl ?? "",
            ReleaseNotes = m.ReleaseNotes ?? "",
            Sha256Hash   = m.Sha256Hash ?? "",
            SizeBytes    = m.SizeBytes,
            ReleasedAt   = m.ReleasedAt,
            IsCritical   = m.IsCritical,
        };
    }

    private static async Task<bool> VerifyHashAsync(string filePath, string expectedHash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedHash)) return true;
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = await Task.Run(() => sha256.ComputeHash(stream), ct);
        var actual = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        return string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _http.Dispose();
}

// ─── Sovereign Manifest envelope ──────────────────────────────────────────

internal sealed class UpdateEnvelope
{
    public SovereignManifest? Manifest   { get; set; }
    public string?            Signature  { get; set; }
    public string?            PubkeyUrl  { get; set; }
}

internal sealed class SovereignManifest
{
    public string?          Product      { get; set; }
    public string?          Channel      { get; set; }
    public string?          Version      { get; set; }
    public string?          Status       { get; set; }
    public string?          DownloadUrl  { get; set; }
    public string?          ReleaseNotes { get; set; }
    public string?          Sha256Hash   { get; set; }
    public long             SizeBytes    { get; set; }
    public DateTimeOffset   ReleasedAt   { get; set; }
    public bool             IsCritical   { get; set; }
    public string?          Ts           { get; set; }
}

// ─── Public UpdateInfo contract (backwards-compatible) ────────────────────

/// <summary>Update information returned to callers of CheckForUpdateAsync.</summary>
public sealed class UpdateInfo
{
    public string           Version      { get; set; } = "";
    public string           DownloadUrl  { get; set; } = "";
    public string           ReleaseNotes { get; set; } = "";
    public string           Sha256Hash   { get; set; } = "";
    public long             SizeBytes    { get; set; }
    public DateTimeOffset   ReleasedAt   { get; set; }
    public bool             IsCritical   { get; set; }
}

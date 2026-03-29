// ─────────────────────────────────────────────────────────────────────────────
// VoiceSessionGateway.cs — Abuse Prevention & License-Gated Token Proxy
//
// SECURITY MODEL:
//   The Gemini API key is NEVER shipped inside the LogicFlow client binary.
//   Instead, this class:
//     1. Contacts api.delgadologic.tech to validate the user's license key
//     2. Receives a short-lived, scoped session token (TTL: 1 hour max)
//     3. Uses that token to open a proxied WebSocket through the sovereign server
//        (the sovereign server holds the real Gemini API key and enforces quotas)
//
//   Quota enforcement happens server-side:
//     - Community Edition : 0 voice sessions (text-only fallback)
//     - Pro Edition       : 30 minutes of voice per calendar month
//     - Enterprise Edition: 200 minutes per month
//   
//   Abuse prevention:
//     - Sessions are bound to the machine's LicenseFingerprint (Ed25519-pinned)
//     - Max concurrent sessions per license: 1
//     - Max session duration: 10 minutes (hard kill by server)
//     - Cold-start rate limiting: max 5 session starts per hour per license
//     - Server logs every session start/end for billing and anomaly detection
//
//   If the server is unreachable, the Voice Agent degrades gracefully
//   (text-only mode) rather than crashing.
// ─────────────────────────────────────────────────────────────────────────────

using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LogicFlow.VoiceAgent;

// ── Models ─────────────────────────────────────────────────────────────────

public sealed class SessionTokenRequest
{
    [JsonPropertyName("license_key")]
    public string LicenseKey { get; init; } = "";

    [JsonPropertyName("machine_fingerprint")]
    public string MachineFingerprint { get; init; } = "";

    [JsonPropertyName("client_version")]
    public string ClientVersion { get; init; } = "1.0.0";
}

public sealed class SessionTokenResponse
{
    /// <summary>Short-lived WebSocket auth token (JWT, expires in 1 hour).</summary>
    [JsonPropertyName("session_token")]
    public string SessionToken { get; set; } = "";

    /// <summary>Remaining voice minutes this billing cycle.</summary>
    [JsonPropertyName("quota_remaining_seconds")]
    public int QuotaRemainingSeconds { get; set; }

    /// <summary>The edition (community / pro / enterprise).</summary>
    [JsonPropertyName("edition")]
    public string Edition { get; set; } = "community";

    /// <summary>Max allowed session duration in seconds (enforced server-side).</summary>
    [JsonPropertyName("max_session_seconds")]
    public int MaxSessionSeconds { get; set; } = 600; // 10 minutes default

    /// <summary>Proxied WSS endpoint to connect to (points to delgadologic.tech, not Google directly).</summary>
    [JsonPropertyName("proxy_wss_url")]
    public string ProxyWssUrl { get; set; } = "";

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class SessionEndRequest
{
    [JsonPropertyName("session_token")]
    public string SessionToken { get; init; } = "";

    [JsonPropertyName("duration_seconds")]
    public int DurationSeconds { get; init; }

    [JsonPropertyName("function_calls_made")]
    public int FunctionCallsMade { get; init; }
}

// ── Gateway ────────────────────────────────────────────────────────────────

/// <summary>
/// Contacts api.delgadologic.tech to:
///   - Validate license + machine fingerprint
///   - Receive a scoped session token + proxy WebSocket URL
///   - Report session end for billing ledger
/// </summary>
public sealed class VoiceSessionGateway
{
    private const string GatewayBase = "https://api.delgadologic.tech/v1/voice";

    private readonly HttpClient _http;
    private readonly ILogger<VoiceSessionGateway> _log;

    public VoiceSessionGateway(HttpClient http, ILogger<VoiceSessionGateway> log)
    {
        _http = http;
        _log  = log;
    }

    /// <summary>
    /// Validates the license and returns a short-lived session token.
    /// Returns null if the license is invalid, quota is exhausted, or server unreachable.
    /// </summary>
    public async Task<SessionTokenResponse?> RequestSessionAsync(
        string licenseKey,
        string machineFingerprint,
        CancellationToken ct)
    {
        try
        {
            var body = new SessionTokenRequest
            {
                LicenseKey         = licenseKey,
                MachineFingerprint = machineFingerprint,
                ClientVersion      = "1.0.0",
            };

            using var resp = await _http.PostAsJsonAsync(
                $"{GatewayBase}/session/start", body, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var raw = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("[VoiceGateway] Session start failed: {Status} {Body}",
                    (int)resp.StatusCode, raw);
                return null;
            }

            var token = await resp.Content.ReadFromJsonAsync<SessionTokenResponse>(
                cancellationToken: ct);

            if (token?.Error is { Length: > 0 } err)
            {
                _log.LogWarning("[VoiceGateway] Server refused session: {Error}", err);
                return null;
            }

            _log.LogInformation(
                "[VoiceGateway] Session granted. Edition={Edition} Quota={Quota}s MaxDuration={Max}s",
                token?.Edition, token?.QuotaRemainingSeconds, token?.MaxSessionSeconds);

            return token;
        }
        catch (TaskCanceledException)
        {
            throw; // propagate cancellation
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[VoiceGateway] Could not reach sovereign server — voice unavailable");
            return null; // degrade gracefully: text-only mode
        }
    }

    /// <summary>
    /// Reports session end to the billing ledger on the sovereign server.
    /// Fire-and-forget — does not block the UI.
    /// </summary>
    public void ReportSessionEnd(string sessionToken, int durationSeconds, int functionCalls)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _http.PostAsJsonAsync(
                    $"{GatewayBase}/session/end",
                    new SessionEndRequest
                    {
                        SessionToken      = sessionToken,
                        DurationSeconds   = durationSeconds,
                        FunctionCallsMade = functionCalls,
                    });

                _log.LogInformation(
                    "[VoiceGateway] Session end reported. Duration={D}s Functions={F}",
                    durationSeconds, functionCalls);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[VoiceGateway] Failed to report session end (non-fatal)");
            }
        });
    }
}

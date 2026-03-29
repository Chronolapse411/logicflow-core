// ─────────────────────────────────────────────────────────────────────────────
// VoiceAgentEngine.cs — Top-Level Orchestrator
//
// This is what the Dashboard calls to start/stop a voice session.
// It wires together:
//   1. VoiceSessionGateway   → license check + token + proxy WSS URL
//   2. MicrophoneReader      → captures PCM16 audio from default mic
//   3. GeminiLiveSession     → WebSocket to proxy (not directly to Google)
//   4. LogicFlowFunctionDispatcher → runs real LogicFlow modules
//   5. AudioPlayback         → plays AI audio response through speakers
//
// Usage from Dashboard:
//   var engine = services.GetRequiredService<VoiceAgentEngine>();
//   await engine.StartAsync(licenseKey, fingerprint, ct);
//   // ... user talks, AI responds in real-time ...
//   await engine.StopAsync();
// ─────────────────────────────────────────────────────────────────────────────

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using DelgadoLogic.Core;

namespace LogicFlow.VoiceAgent;

public enum VoiceAgentState
{
    Idle,
    Connecting,
    Live,
    Stopping,
    Error,
}

public sealed class VoiceAgentEngine : IAsyncDisposable
{
    private readonly VoiceSessionGateway _gateway;
    private readonly MicrophoneReader _mic;
    private readonly AudioPlayback _speaker;
    private readonly LogicFlowFunctionDispatcher _dispatcher;
    private readonly ILogger<VoiceAgentEngine> _log;

    private GeminiLiveSession? _session;
    private CancellationTokenSource? _sessionCts;
    private readonly Stopwatch _sessionTimer = new();
    private int _functionCallCount;

    private string _activeSessionToken = "";

    public VoiceAgentState State { get; private set; } = VoiceAgentState.Idle;

    /// <summary>Fired when the AI speaks a text chunk (for transcript display in Dashboard).</summary>
    public event Action<string>? TranscriptChunkReceived;

    /// <summary>Fired when state changes (for UI button enable/disable).</summary>
    public event Action<VoiceAgentState>? StateChanged;

    /// <summary>Fired when a function call completes (to animate Dashboard panels).</summary>
    public event Action<string>? FunctionExecuted;

    /// <summary>Fired with a user-facing error message when session fails.</summary>
    public event Action<string>? ErrorOccurred;

    public VoiceAgentEngine(
        VoiceSessionGateway gateway,
        MicrophoneReader mic,
        AudioPlayback speaker,
        LogicFlowFunctionDispatcher dispatcher,
        ILogger<VoiceAgentEngine> log)
    {
        _gateway    = gateway;
        _mic        = mic;
        _speaker    = speaker;
        _dispatcher = dispatcher;
        _log        = log;
    }

    // ── Start session ────────────────────────────────────────────────────

    public async Task StartAsync(string licenseKey, string machineFingerprint, SystemContextPayload systemMetrics, CancellationToken ct)
    {
        if (State != VoiceAgentState.Idle)
        {
            _log.LogWarning("[VoiceEngine] StartAsync called while not idle (state={State})", State);
            return;
        }

        SetState(VoiceAgentState.Connecting);

        // 1. License check via sovereign server — gets proxy WSS URL + session token
        var tokenResp = await _gateway.RequestSessionAsync(licenseKey, machineFingerprint, ct);
        if (tokenResp is null)
        {
            SetState(VoiceAgentState.Error);
            ErrorOccurred?.Invoke("Voice Agent requires an active Pro or Enterprise license. " +
                                  "Visit delgadologic.tech/pricing to upgrade.");
            return;
        }

        // 2. Build Gemini Live setup with function declarations and system context
        string systemPrompt = $"You are LogicFlow Guardian OS. Your system state is:\n{systemMetrics.ToJson()}";

        var setup = new LiveSetup
        {
            Setup = new LiveSetup.SetupBody
            {
                Model = "models/gemini-3.1-flash-live",
                GenerationConfig = new GenerationConfig
                {
                    ThinkingConfig = new ThinkingConfig { ThinkingBudget = "low" },
                },
                SystemInstruction = new SystemInstruction { Parts = [ new Part { Text = systemPrompt } ] },
                Tools =
                [
                    new ToolDeclaration
                    {
                        FunctionDeclarations = LogicFlowFunctionDispatcher.GetDeclarations()
                    }
                ],
            }
        };

        // 3. Connect WebSocket to THE PROXY (not to Google directly — no API key in client)
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Enforce server-side max duration locally too (belt + suspenders)
        _sessionCts.CancelAfter(TimeSpan.FromSeconds(tokenResp.MaxSessionSeconds));

        _session = new GeminiLiveSession(
            apiKey: tokenResp.SessionToken, // This is the short-lived proxy token, not the real Google key
            setupMsg: setup,
            log: _log);

        // Patch: use proxy WSS url from server, not the real Google endpoint
        // The GeminiLiveSession uses the apiKey param as a bearer token to the proxy.
        // The proxy holds the real Gemini API key (in GCP Secret Manager).

        _session.TextReceived    += txt   => TranscriptChunkReceived?.Invoke(txt);
        _session.AudioReceived   += bytes => _speaker.EnqueueBytes(bytes);
        _session.TurnComplete    += ()    => _log.LogDebug("[VoiceEngine] Turn complete");
        _session.FunctionCallReceived += async call =>
        {
            _functionCallCount++;
            FunctionExecuted?.Invoke(call.Name);
            return await _dispatcher.DispatchAsync(call);
        };

        try
        {
            await _session.ConnectAsync(_sessionCts.Token);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[VoiceEngine] Failed to connect to proxy");
            SetState(VoiceAgentState.Error);
            ErrorOccurred?.Invoke("Failed to connect to LogicFlow Voice Server. Check your connection.");
            return;
        }

        _activeSessionToken = tokenResp.SessionToken;
        _functionCallCount  = 0;
        _sessionTimer.Restart();

        // 4. Start mic streaming
        _mic.Start(_session, _sessionCts.Token);

        SetState(VoiceAgentState.Live);
        _log.LogInformation("[VoiceEngine] Voice session is LIVE. Max={Max}s Quota={Q}s",
            tokenResp.MaxSessionSeconds, tokenResp.QuotaRemainingSeconds);
    }

    // ── Stop session ─────────────────────────────────────────────────────

    public async Task StopAsync()
    {
        if (State == VoiceAgentState.Idle) return;
        SetState(VoiceAgentState.Stopping);

        _mic.Stop();
        _sessionCts?.Cancel();

        if (_session is not null)
            await _session.CloseAsync(CancellationToken.None);

        _sessionTimer.Stop();
        var duration = (int)_sessionTimer.Elapsed.TotalSeconds;

        // Report billing to sovereign server (fire-and-forget)
        if (!string.IsNullOrEmpty(_activeSessionToken))
            _gateway.ReportSessionEnd(_activeSessionToken, duration, _functionCallCount);

        _log.LogInformation("[VoiceEngine] Session ended. Duration={D}s FunctionCalls={F}",
            duration, _functionCallCount);

        _session = null;
        SetState(VoiceAgentState.Idle);
    }

    private void SetState(VoiceAgentState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _sessionCts?.Dispose();
    }
}

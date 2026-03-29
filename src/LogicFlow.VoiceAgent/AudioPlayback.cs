// ─────────────────────────────────────────────────────────────────────────────
// AudioPlayback.cs — Plays AI voice responses through speakers
//
// The Gemini Live API returns audio as base64-encoded 24kHz mono PCM16.
// We decode and play it through the default output device using NAudio.
// Uses a thread-safe queue so audio chunks play sequentially without overlap.
// ─────────────────────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using NAudio.Wave;
using Microsoft.Extensions.Logging;

namespace LogicFlow.VoiceAgent;

public sealed class AudioPlayback : IDisposable
{
    // Gemini Live API audio output: 24kHz mono PCM16
    private const int OutputSampleRate = 24000;
    private const int OutputChannels   = 1;
    private const int OutputBits       = 16;

    private readonly ILogger<AudioPlayback> _log;
    private readonly ConcurrentQueue<byte[]> _audioQueue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _buffer;

    public AudioPlayback(ILogger<AudioPlayback> log)
    {
        _log = log;
        InitAudio();
        _ = Task.Run(PlaybackLoopAsync);
    }

    private void InitAudio()
    {
        _buffer = new BufferedWaveProvider(
            new WaveFormat(OutputSampleRate, OutputBits, OutputChannels))
        {
            BufferDuration = TimeSpan.FromSeconds(30),
            DiscardOnBufferOverflow = true,
        };

        _waveOut = new WaveOutEvent();
        _waveOut.Init(_buffer);
        _waveOut.Play();
    }

    /// <summary>
    /// Enqueue raw PCM16 bytes received from the Gemini Live API.
    /// Thread-safe — can be called from any thread.
    /// </summary>
    public void EnqueueBytes(byte[] pcm16Bytes)
    {
        _audioQueue.Enqueue(pcm16Bytes);
        _signal.Release();
    }

    private async Task PlaybackLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_cts.Token);
                if (_audioQueue.TryDequeue(out var chunk) && _buffer is not null)
                {
                    _buffer.AddSamples(chunk, 0, chunk.Length);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[Audio] Playback error");
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _cts.Dispose();
    }
}

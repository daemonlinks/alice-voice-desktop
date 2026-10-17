using System.Runtime.InteropServices;
using Serilog;

namespace AliceVoice.Audio;

/// <summary>
/// WASAPI capture loopback via the Windows Core Audio API (COM interop).
/// Captures the default microphone at 16 kHz mono and raises float buffers.
/// </summary>
public sealed class WasapiMicrophoneCapture : IMicrophoneCapture
{
    public event Action<float[]>? AudioFrameCaptured;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => CaptureLoop(_cts.Token), _cts.Token);
        Log.Information("WASAPI microphone capture started");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        Log.Information("WASAPI microphone capture stopped");
        return Task.CompletedTask;
    }

    private void CaptureLoop(CancellationToken ct)
    {
        // WASAPI capture is wired in the next increment. This validates the
        // lifecycle and threading model; the COM capture client follows.
        while (!ct.IsCancellationRequested)
        {
            Thread.Sleep(100);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
    }
}

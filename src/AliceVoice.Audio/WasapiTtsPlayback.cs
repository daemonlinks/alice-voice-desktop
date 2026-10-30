using NAudio.Wave;
using Serilog;

namespace AliceVoice.Audio;

/// <summary>
/// Plays TTS audio through the default output device (NAudio WaveOut,
/// event callback). Buffers Int16 PCM chunks without discarding (the old
/// 5-second discard-on-overflow buffer silently truncated long replies) and
/// re-creates the output after <see cref="Stop"/> so playback works again
/// on the next reply.
/// </summary>
public sealed class WasapiTtsPlayback : ITtsPlayback
{
    private readonly object _lock = new();
    private BufferedWaveProvider? _buffer;
    private WaveOut? _output;
    private int _sampleRate;

    public void EnqueuePcm(byte[] pcmBytes, int sampleRate)
    {
        if (pcmBytes.Length == 0) return;
        lock (_lock)
        {
            if (_output is null || _buffer is null || _sampleRate != sampleRate)
            {
                DisposeOutput();
                _sampleRate = sampleRate;
                _buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1), TimeSpan.FromMinutes(10))
                {
                    DiscardOnBufferOverflow = false,
                    ReadFully = true
                };
                _output = new WaveOut();
                _output.Init(_buffer);
            }
            _buffer.AddSamples(pcmBytes, 0, pcmBytes.Length);
            if (_output.PlaybackState != PlaybackState.Playing) _output.Play();
        }
    }

    public async Task WaitForDrainAsync(CancellationToken ct = default)
    {
        try
        {
            while (true)
            {
                TimeSpan left;
                lock (_lock) left = _buffer?.BufferedDuration ?? TimeSpan.Zero;
                if (left <= TimeSpan.Zero) break;
                await Task.Delay(left > TimeSpan.FromMilliseconds(200) ? TimeSpan.FromMilliseconds(200) : left, ct);
            }
            // Let the device play out its last hardware buffer.
            await Task.Delay(150, ct);
        }
        catch (OperationCanceledException)
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        lock (_lock) DisposeOutput();
    }

    private void DisposeOutput()
    {
        try { _output?.Stop(); } catch (Exception ex) { Log.Warning(ex, "Playback stop failed"); }
        _output?.Dispose();
        _output = null;
        _buffer = null;
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}

using NAudio.Wave;
using Serilog;

namespace AliceVoice.Audio;

/// <summary>
/// Plays TTS audio via WASAPI (NAudio). Buffers raw Int16 PCM chunks and
/// plays them through the default output device.
/// </summary>
public sealed class WasapiTtsPlayback : ITtsPlayback
{
    private readonly object _lock = new();
    private BufferedWaveProvider? _buffer;
    private WaveOutEvent? _output;
    private int _sampleRate;

    public void EnqueuePcm(byte[] pcmBytes, int sampleRate)
    {
        lock (_lock)
        {
            if (_output is null || _sampleRate != sampleRate)
            {
                _output?.Stop();
                _output?.Dispose();

                _sampleRate = sampleRate;
                _buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
                {
                    DiscardOnBufferOverflow = true
                };
                _output = new WaveOutEvent();
                _output.Init(_buffer);
                _output.Play();
            }

            _buffer?.AddSamples(pcmBytes, 0, pcmBytes.Length);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _output?.Stop();
            _buffer?.ClearBuffer();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            _output?.Stop();
            _output?.Dispose();
            _output = null;
            _buffer = null;
        }
        await Task.CompletedTask;
    }
}

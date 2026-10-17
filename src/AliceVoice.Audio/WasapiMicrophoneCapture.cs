using NAudio.Wave;
using Serilog;

namespace AliceVoice.Audio;

/// <summary>
/// Low-latency microphone capture via WASAPI (NAudio). Captures the default
/// input device, resamples to 16 kHz mono, and raises float buffers ready for
/// Whisper transcription.
/// </summary>
public sealed class WasapiMicrophoneCapture : IMicrophoneCapture
{
    public event Action<float[]>? AudioFrameCaptured;

    private WaveInEvent? _waveIn;
    private readonly object _lock = new();

    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_waveIn is not null) return Task.CompletedTask;

            _waveIn = new WaveInEvent
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(48000, 16, 1),
                BufferMilliseconds = 50
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += (_, _) => Log.Information("WASAPI capture stopped");
            _waveIn.StartRecording();
            Log.Information("WASAPI microphone capture started (48kHz -> 16kHz mono)");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_lock)
        {
            if (_waveIn is null) return Task.CompletedTask;
            _waveIn.StopRecording();
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.Dispose();
            _waveIn = null;
            Log.Information("WASAPI microphone capture stopped");
        }
        return Task.CompletedTask;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Convert 16-bit PCM bytes to float, resample 48k -> 16k (decimate by 3).
        var samples = e.BytesRecorded / 2;
        var pcm = new short[samples];
        Buffer.BlockCopy(e.Buffer, 0, pcm, 0, e.BytesRecorded);

        var floats = new float[samples];
        for (int i = 0; i < samples; i++)
            floats[i] = pcm[i] / 32768f;

        var target = new float[samples / 3];
        for (int i = 0; i < target.Length; i++)
            target[i] = floats[i * 3];

        AudioFrameCaptured?.Invoke(target);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}

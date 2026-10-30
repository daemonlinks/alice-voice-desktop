using NAudio.Wave;
using Serilog;

namespace AliceVoice.Audio;

/// <summary>
/// Microphone capture via NAudio WaveIn (event callback) on the Windows
/// default input device (WAVE_MAPPER, device -1 — the old code pinned device
/// 0, which is not necessarily the default). Captures 16 kHz mono directly so
/// Windows does the resampling properly (the old 48k→16k decimation had no
/// low-pass filter and aliased). Raises float frames in -1..1.
/// </summary>
public sealed class WasapiMicrophoneCapture : IMicrophoneCapture
{
    public const int SampleRate = 16000;
    public event Action<float[]>? AudioFrameCaptured;

    private WaveIn? _waveIn;
    private readonly object _lock = new();

    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_waveIn is not null) return Task.CompletedTask;

            var waveIn = new WaveIn
            {
                DeviceNumber = -1,
                WaveFormat = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 50
            };
            waveIn.DataAvailable += OnDataAvailable;
            waveIn.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) Log.Warning(e.Exception, "Microphone capture stopped with an error");
            };
            try { waveIn.StartRecording(); }
            catch
            {
                waveIn.DataAvailable -= OnDataAvailable;
                waveIn.Dispose();
                throw;
            }
            _waveIn = waveIn;
            Log.Information("Microphone capture started (default device, 16 kHz mono)");
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
            Log.Information("Microphone capture stopped");
        }
        return Task.CompletedTask;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var samples = e.BytesRecorded / 2;
        var floats = new float[samples];
        for (int i = 0; i < samples; i++)
            floats[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
        AudioFrameCaptured?.Invoke(floats);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}

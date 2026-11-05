using Android.Media;
using AliceVoice.Universal.Audio;

namespace AliceVoice.AppU;

/// <summary>16 kHz mono capture, the same rate the desktop mic hands to speech-to-text.</summary>
public sealed class AndroidMicrophone : IPcmMicrophone
{
    public const int SampleRate = 16000;
    public event Action<float[]>? AudioFrameCaptured;

    private AudioRecord? _record;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_record is not null) return Task.CompletedTask;
        var min = AudioRecord.GetMinBufferSize(SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
        if (min <= 0) throw new InvalidOperationException("This device cannot capture 16 kHz mono audio.");
        var record = new AudioRecord(AudioSource.Mic, SampleRate, ChannelIn.Mono, Encoding.Pcm16bit, Math.Max(min * 2, SampleRate / 5 * 2));
        if (record is null || record.State != State.Initialized)
        {
            record?.Release();
            throw new InvalidOperationException("Microphone permission was denied, or the microphone is busy.");
        }
        record.StartRecording();
        _record = record;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => Loop(record, token), token);
        return Task.CompletedTask;
    }

    private void Loop(AudioRecord record, CancellationToken ct)
    {
        var buffer = new short[1600];
        while (!ct.IsCancellationRequested)
        {
            var n = record.Read(buffer, 0, buffer.Length);
            if (n <= 0) continue;
            var floats = new float[n];
            for (var i = 0; i < n; i++) floats[i] = buffer[i] / 32768f;
            AudioFrameCaptured?.Invoke(floats);
        }
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }
        if (_loop is not null) { try { await _loop; } catch { } }
        _loop = null;
        _cts?.Dispose();
        _cts = null;
        var record = _record;
        _record = null;
        if (record is null) return;
        try { record.Stop(); } catch { }
        record.Release();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

/// <summary>Plays Int16 mono PCM through the music stream.</summary>
public sealed class AndroidPcmPlayer : IPcmPlayer
{
    private readonly object _gate = new();
    private AudioTrack? _track;
    private int _rate;
    private int _queuedBytes;

    public void EnqueuePcm(byte[] pcmBytes, int sampleRate)
    {
        if (pcmBytes.Length == 0) return;
        lock (_gate)
        {
            if (_track is null || _rate != sampleRate)
            {
                StopLocked();
                _rate = sampleRate;
                var min = AudioTrack.GetMinBufferSize(sampleRate, ChannelOut.Mono, Encoding.Pcm16bit);
                var attributes = new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media).SetContentType(AudioContentType.Speech).Build();
                var format = new AudioFormat.Builder().SetEncoding(Encoding.Pcm16bit).SetSampleRate(sampleRate).SetChannelMask(ChannelOut.Mono).Build();
                if (attributes is null || format is null) throw new InvalidOperationException("Could not open the audio output.");
                _track = new AudioTrack.Builder()
                    .SetAudioAttributes(attributes)
                    .SetAudioFormat(format)
                    .SetBufferSizeInBytes(Math.Max(min, pcmBytes.Length))
                    .SetTransferMode(AudioTrackMode.Stream)
                    .Build();
                _track?.Play();
            }
            _track?.Write(pcmBytes, 0, pcmBytes.Length);
            _queuedBytes += pcmBytes.Length;
        }
    }

    public async Task WaitForDrainAsync(CancellationToken ct = default)
    {
        int rate, bytes;
        lock (_gate) { rate = _rate; bytes = _queuedBytes; _queuedBytes = 0; }
        if (rate <= 0 || bytes <= 0) return;
        try
        {
            var ms = (int)Math.Clamp(bytes * 1000L / (rate * 2), 0, 120_000);
            await Task.Delay(ms + 80, ct);
        }
        catch (OperationCanceledException)
        {
            Stop();
            throw;
        }
    }

    public void Stop() { lock (_gate) StopLocked(); }

    private void StopLocked()
    {
        try { _track?.Pause(); _track?.Flush(); } catch { }
        try { _track?.Stop(); } catch { }
        _track?.Release();
        _track = null;
        _queuedBytes = 0;
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}

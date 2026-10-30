using System.Speech.Synthesis;
using Serilog;
using AliceVoice.Core.Voice;

namespace AliceVoice.Audio;

/// <summary>
/// Instant local TTS via Windows SAPI (System.Speech). Uses the synthesizer's
/// asynchronous SpeakAsync so the calling (UI) thread is never blocked;
/// utterances are serialised and cancellable. Logs only text length.
/// </summary>
public sealed class SapiLocalTts : ILocalTts, IDisposable
{
    private readonly SpeechSynthesizer _synth = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SapiLocalTts()
    {
        _synth.Rate = 0;
        _synth.SetOutputToDefaultAudioDevice();
        Log.Information("SAPI local TTS ready (voice: {Voice})", _synth.Voice?.Name ?? "default");
    }

    public async Task SpeakAsync(string text, string? voiceName = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(voiceName))
            {
                try { _synth.SelectVoice(voiceName); }
                catch (Exception ex) { Log.Warning(ex, "SAPI voice {Voice} unavailable, using default", voiceName); }
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Prompt? prompt = null;
            void Done(object? s, SpeakCompletedEventArgs e)
            {
                if (!ReferenceEquals(e.Prompt, prompt)) return;
                if (e.Cancelled) tcs.TrySetCanceled();
                else if (e.Error is not null) tcs.TrySetException(e.Error);
                else tcs.TrySetResult();
            }
            _synth.SpeakCompleted += Done;
            try
            {
                Log.Information("SAPI speaking {Chars} chars ({Voice})", text.Length, voiceName is { Length: > 0 } ? voiceName : "default");
                prompt = _synth.SpeakAsync(text);
                using (ct.Register(() => { try { _synth.SpeakAsyncCancel(prompt); } catch { } tcs.TrySetCanceled(ct); }))
                    await tcs.Task;
            }
            finally { _synth.SpeakCompleted -= Done; }
        }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<string> AvailableVoices()
    {
        try
        {
            return _synth.GetInstalledVoices()
                .Where(v => v.Enabled)
                .Select(v => v.VoiceInfo.Name)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SAPI voice enumeration failed");
            return new List<string>();
        }
    }

    public void Stop()
    {
        try { _synth.SpeakAsyncCancelAll(); } catch (Exception ex) { Log.Warning(ex, "SAPI stop failed"); }
    }

    public void Dispose()
    {
        Stop();
        _synth.Dispose();
        _gate.Dispose();
    }
}

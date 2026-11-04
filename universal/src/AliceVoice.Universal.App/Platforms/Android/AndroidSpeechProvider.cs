using Android.Speech.Tts;
using AliceVoice.Core.Voice;
using AndroidTts = Android.Speech.Tts.TextToSpeech;

namespace AliceVoice.AppU;

/// <summary>
/// On-device speech. It fills the SAPI slot: instant, local, no network.
/// AllTalk and Piper keep their own identities and still come from the Beast.
/// </summary>
public sealed class AndroidSpeechProvider : Java.Lang.Object, IVoiceProvider, AndroidTts.IOnInitListener
{
    private readonly TaskCompletionSource<bool> _ready = new();
    private AndroidTts? _tts;

    public AndroidSpeechProvider()
    {
        _tts = new AndroidTts(Android.App.Application.Context, this);
    }

    public VoiceProviderKind Kind => VoiceProviderKind.Sapi;
    public string DisplayName => "Android speech";

    public void OnInit(OperationResult status) => _ready.TrySetResult(status == OperationResult.Success);

    public async Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct = default)
    {
        if (!await _ready.Task.WaitAsync(ct) || _tts is null) return Array.Empty<string>();
        var voices = _tts.Voices;
        if (voices is null || voices.Count == 0) return new[] { "" };
        return voices.Select(v => v.Name).Where(n => !string.IsNullOrEmpty(n)).Cast<string>().OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    public async Task SpeakAsync(string text, string voice, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!await _ready.Task.WaitAsync(ct) || _tts is null)
            throw new InvalidOperationException("Android speech engine failed to start.");
        if (!string.IsNullOrEmpty(voice))
        {
            var match = _tts.Voices?.FirstOrDefault(v => v.Name == voice);
            if (match is not null) _tts.SetVoice(match);
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Guid.NewGuid().ToString("N");
        _tts.SetOnUtteranceProgressListener(new Listener(id, done));
        using var reg = ct.Register(() =>
        {
            try { _tts.Stop(); } catch { }
            done.TrySetCanceled(ct);
        });
        var result = _tts.Speak(text, QueueMode.Flush, null, id);
        if (result == OperationResult.Error) throw new InvalidOperationException("Android speech failed to start.");
        await done.Task;
    }

    private sealed class Listener : UtteranceProgressListener
    {
        private readonly string _id;
        private readonly TaskCompletionSource _done;
        public Listener(string id, TaskCompletionSource done) { _id = id; _done = done; }
        public override void OnStart(string? utteranceId) { }
        public override void OnDone(string? utteranceId) { if (utteranceId == _id) _done.TrySetResult(); }
        [Obsolete("Android still requires this override; OnError(utteranceId, errorCode) is not abstract on every API level we target.")]
        public override void OnError(string? utteranceId) { if (utteranceId == _id) _done.TrySetException(new InvalidOperationException("Android speech error")); }
    }
}

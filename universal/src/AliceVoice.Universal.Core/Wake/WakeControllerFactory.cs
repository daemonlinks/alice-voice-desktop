using AliceVoice.Core.Config;
using AliceVoice.Core.WakeWord;
using Serilog;

namespace AliceVoice.Universal.Wake;

/// <summary>
/// Resolves and SHA-256-checks the wake models, then asks the head for a detector
/// (ONNX Runtime CPU on Windows and, when the runtime is linked, on Android).
/// </summary>
public static class WakeControllerFactory
{
    public static IWakeController Create(AliceVoiceOptions options, Func<WakeWordModelInfo, IWakeWordDetector> detectorFactory, string? baseDir = null)
    {
        var model = WakeWordModelLocator.TryResolve(options.WakeWordDir, options.SharedDataDir, options.WakeWordModel, out var reason, baseDir);
        if (model is null)
        {
            Log.Information("Wake word unavailable: {Reason}", reason);
            return new UnavailableWakeController(reason);
        }

        try
        {
            var detector = detectorFactory(model);
            var settings = WakeWordSetup.Create(options, model);
            Log.Information("Wake word ready: {Model} (“{Phrase}”) from {Dir}, threshold {Threshold}, patience {Patience}",
                model.Id, model.Phrase, model.Directory, settings.Threshold, settings.Patience);
            return new SessionWakeController(model, settings, detector);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Wake-word engine failed to load");
            return new UnavailableWakeController($"Wake-word engine failed to load: {ex.Message}");
        }
    }
}

/// <summary>The WinUI wake service: inference off the audio thread, paused while Alice speaks.</summary>
public sealed class SessionWakeController : IWakeController
{
    private readonly WakeWordService _service;
    private bool _disposed;

    public SessionWakeController(WakeWordModelInfo model, WakeWordSessionSettings settings, IWakeWordDetector detector)
    {
        Model = model;
        _service = new WakeWordService(new WakeWordSession(detector, settings), model);
        _service.Session.WakeDetected += score => WakeDetected?.Invoke(score);
        _service.Session.UtteranceReady += pcm => UtteranceReady?.Invoke(pcm);
        _service.Session.UtteranceAbandoned += _ => UtteranceAbandoned?.Invoke();
        _service.Faulted += ex => Faulted?.Invoke(ex);
    }

    public WakeWordModelInfo Model { get; }
    public bool IsAvailable => true;
    public string UnavailableReason => "";
    public string Phrase => Model.Phrase;
    public bool Interim => Model.Interim;
    public string ModelId => Model.Id;
    public WakeWordSessionState State => _service.Session.State;
    public float LastScore => _service.Session.LastScore;
    public float Threshold => _service.Session.Threshold;

    public void SetThreshold(float threshold) => _service.Session.Threshold = threshold;
    public void Enqueue(float[] pcm) => _service.Enqueue(pcm);
    public void Pause() => _service.Session.Pause();
    public void Resume() => _service.Session.Resume();

    public event Action<float>? WakeDetected;
    public event Action<short[]>? UtteranceReady;
    public event Action? UtteranceAbandoned;
    public event Action<Exception>? Faulted;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.Dispose();
    }
}

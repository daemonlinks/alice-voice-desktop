using AliceVoice.Core.WakeWord;

namespace AliceVoice.Universal.Wake;

/// <summary>Hands-free "hey alice" listener. The ONNX runner is supplied by the head; this interface is what the session drives.</summary>
public interface IWakeController : IDisposable
{
    bool IsAvailable { get; }
    string UnavailableReason { get; }
    string Phrase { get; }
    bool Interim { get; }
    string ModelId { get; }
    WakeWordSessionState State { get; }
    float LastScore { get; }
    float Threshold { get; }
    void SetThreshold(float threshold);
    void Enqueue(float[] pcm);
    void Pause();
    void Resume();
    event Action<float>? WakeDetected;
    event Action<short[]>? UtteranceReady;
    event Action? UtteranceAbandoned;
    event Action<Exception>? Faulted;
}

public interface IWakeSource
{
    Task<IWakeController> OpenAsync(CancellationToken ct = default);
}

public sealed class StaticWakeSource : IWakeSource
{
    private readonly IWakeController _controller;
    public StaticWakeSource(IWakeController controller) => _controller = controller;
    public Task<IWakeController> OpenAsync(CancellationToken ct = default) => Task.FromResult(_controller);
}

public sealed class UnavailableWakeController : IWakeController
{
    public UnavailableWakeController(string reason) => UnavailableReason = reason;
    public bool IsAvailable => false;
    public string UnavailableReason { get; }
    public string Phrase => "";
    public bool Interim => false;
    public string ModelId => "";
    public WakeWordSessionState State => WakeWordSessionState.Paused;
    public float LastScore => 0;
    public float Threshold => 0.5f;
    public void SetThreshold(float threshold) { }
    public void Enqueue(float[] pcm) { }
    public void Pause() { }
    public void Resume() { }
    public event Action<float>? WakeDetected { add { } remove { } }
    public event Action<short[]>? UtteranceReady { add { } remove { } }
    public event Action? UtteranceAbandoned { add { } remove { } }
    public event Action<Exception>? Faulted { add { } remove { } }
    public void Dispose() { }
}

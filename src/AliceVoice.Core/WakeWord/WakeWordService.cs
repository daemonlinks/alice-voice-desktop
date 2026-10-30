using System.Threading.Channels;

namespace AliceVoice.Core.WakeWord;

/// <summary>
/// Runs a <see cref="WakeWordSession"/> on its own background task so ONNX
/// inference never runs on the audio or UI thread. Mic buffers are queued
/// (bounded: if inference ever falls behind, the oldest audio is dropped).
/// </summary>
public sealed class WakeWordService : IDisposable
{
    private readonly Channel<float[]> _queue = Channel.CreateBounded<float[]>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    public WakeWordService(WakeWordSession session, WakeWordModelInfo model)
    {
        Session = session;
        Model = model;
        _worker = Task.Run(RunAsync);
    }

    public WakeWordSession Session { get; }
    public WakeWordModelInfo Model { get; }

    /// <summary>Last error from the worker, if inference failed.</summary>
    public Exception? LastError { get; private set; }
    public event Action<Exception>? Faulted;

    /// <summary>Queue a mic buffer (called on the audio thread; never blocks).</summary>
    public void Enqueue(float[] audio)
    {
        if (Session.State == WakeWordSessionState.Paused) return;
        _queue.Writer.TryWrite(audio);
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var buf in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                try { Session.Process(buf); }
                catch (Exception ex)
                {
                    LastError = ex;
                    Session.Pause();
                    Faulted?.Invoke(ex);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
        Session.Dispose();
    }
}

namespace AliceVoice.Universal.Voice;

/// <summary>One Alice LOCAL turn in the canonical conversation. The same list is what the PC window and the phone render.</summary>
public sealed record SharedTurn(long Id, string User, string Assistant, DateTimeOffset At, string Origin);

/// <summary>
/// The Beast-side Alice LOCAL transcript. Model history stays inside the one
/// <c>AliceLocalBackend</c> the window and the bridge share; this list is the
/// part both screens show. Turns are appended only after a reply returns, and
/// callers serialize asks so two devices cannot interleave a turn.
/// </summary>
public sealed class SharedTranscript : IAliceConversation
{
    private readonly object _gate = new();
    private readonly List<SharedTurn> _turns = new();
    private long _next = 1;

    public long Epoch { get; private set; } = 1;
    public bool NeedsPoll => false;
    public event Action? Changed;

    public IReadOnlyList<SharedTurn> Snapshot()
    {
        lock (_gate) return _turns.ToArray();
    }

    public SharedTurn Append(string user, string assistant, string origin, DateTimeOffset at)
    {
        SharedTurn turn;
        lock (_gate)
        {
            turn = new SharedTurn(_next++, user, assistant, at, origin);
            _turns.Add(turn);
        }
        Changed?.Invoke();
        return turn;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _turns.Clear();
            Epoch++;
        }
        Changed?.Invoke();
    }

    public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public interface IAliceConversation
{
    long Epoch { get; }
    bool NeedsPoll { get; }
    IReadOnlyList<SharedTurn> Snapshot();
    event Action? Changed;
    Task RefreshAsync(CancellationToken ct = default);
}

/// <summary>Which device is appending the turn that is in flight on this async flow.</summary>
public static class ConversationOrigin
{
    public const string Pc = "pc";
    public const string Phone = "phone";
    private static readonly AsyncLocal<string?> CurrentValue = new();

    public static string Current => string.IsNullOrEmpty(CurrentValue.Value) ? Pc : CurrentValue.Value!;

    public static IDisposable Use(string origin)
    {
        var previous = CurrentValue.Value;
        CurrentValue.Value = origin;
        return new Pop(previous);
    }

    private sealed class Pop(string? previous) : IDisposable
    {
        public void Dispose() => CurrentValue.Value = previous;
    }
}

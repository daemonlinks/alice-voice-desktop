using AliceVoice.Universal.Voice;

namespace AliceVoice.Universal.Bridge;

/// <summary>The phone's view of the Beast's one Alice LOCAL conversation. Refresh pulls the canonical turns.</summary>
public sealed class RemoteAliceConversation : IAliceConversation
{
    private readonly BeastBridgeClient _client;
    private readonly object _gate = new();
    private List<SharedTurn> _turns = new();

    public RemoteAliceConversation(BeastBridgeClient client) => _client = client;

    public long Epoch { get; private set; }
    public bool NeedsPoll => true;
    public event Action? Changed;

    public IReadOnlyList<SharedTurn> Snapshot()
    {
        lock (_gate) return _turns.ToArray();
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var remote = await _client.TranscriptAsync(ct);
        var changed = false;
        lock (_gate)
        {
            if (remote.Epoch != Epoch || remote.Turns.Count != _turns.Count || (remote.Turns.Count > 0 && remote.Turns[^1].Id != (_turns.Count > 0 ? _turns[^1].Id : 0)))
            {
                Epoch = remote.Epoch;
                _turns = remote.Turns;
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }
}

namespace AliceVoice.Universal.Voice;

public sealed record TurnTiming(string Target, long SttMs, long LlmMs, long TtftMs, long TtsMs, long TotalMs);

/// <summary>Last 20 turns, same summary lines as the WinUI latency panel.</summary>
public sealed class LatencyBook
{
    private readonly List<TurnTiming> _items = new();

    public IReadOnlyList<TurnTiming> Items => _items;
    public TurnTiming? Last => _items.Count == 0 ? null : _items[^1];

    public void Add(TurnTiming timing)
    {
        _items.Add(timing);
        if (_items.Count > 20) _items.RemoveAt(0);
    }

    public string TotalText => Last is null ? "—" : $"{Last.TotalMs} ms";
    public string SttText => Last is null || Last.SttMs <= 0 ? "—" : $"{Last.SttMs} ms";
    public string LlmText => Last is null ? "—" : $"{Last.LlmMs} ms";
    public string TtftText => Last is null ? "—" : $"{Last.TtftMs} ms";
    public string TtsText => Last is null ? "—" : $"{Last.TtsMs} ms";

    public string Summary => _items.Count == 0
        ? "waiting for data"
        : $"{_items.Count} turns · avg E2E {_items.Average(x => (double)x.TotalMs):F0} ms · avg LLM {_items.Average(x => (double)x.LlmMs):F0} ms";

    public string Table => _items.Count == 0
        ? "Waiting for first utterance…"
        : string.Join("\n", _items.TakeLast(5).Reverse()
            .Select(x => $"{x.Target}: E2E {x.TotalMs} · STT {x.SttMs} · LLM {x.LlmMs} · TTFT {x.TtftMs} · TTS {x.TtsMs} ms"));

    public string LastTurnLine => Last is null
        ? "Last turn: E2E — · think — · speak —"
        : $"Last turn: E2E {Last.TotalMs} ms · think {Last.LlmMs} ms · speak {Last.TtsMs} ms";
}

namespace AliceVoice.Core.Voice;

/// <summary>One chat message in OpenAI / Ollama shape.</summary>
public sealed record ChatMessage(string Role, string Content);

/// <summary>
/// Bounded, thread-safe rolling conversation history (user/assistant pairs).
/// The oldest messages are dropped once <see cref="MaxMessages"/> is reached,
/// always keeping whole user→assistant turns.
/// </summary>
public sealed class ConversationHistory
{
    private readonly List<ChatMessage> _messages = new();
    private readonly object _lock = new();

    public ConversationHistory(int maxMessages = 20)
    {
        if (maxMessages < 2) throw new ArgumentOutOfRangeException(nameof(maxMessages));
        MaxMessages = maxMessages;
    }

    public int MaxMessages { get; }

    public int Count { get { lock (_lock) return _messages.Count; } }

    /// <summary>Record a completed turn.</summary>
    public void AddTurn(string user, string assistant)
    {
        lock (_lock)
        {
            _messages.Add(new ChatMessage("user", user));
            _messages.Add(new ChatMessage("assistant", assistant));
            while (_messages.Count > MaxMessages)
                _messages.RemoveRange(0, Math.Min(2, _messages.Count));
        }
    }

    public IReadOnlyList<ChatMessage> Snapshot()
    {
        lock (_lock) return _messages.ToArray();
    }

    public void Clear()
    {
        lock (_lock) _messages.Clear();
    }
}

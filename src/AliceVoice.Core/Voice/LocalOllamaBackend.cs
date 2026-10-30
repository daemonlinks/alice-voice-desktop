using System.Text;
using AliceVoice.Core.Config;
using Serilog;

namespace AliceVoice.Core.Voice;

/// <summary>
/// Plain local Ollama chat — no SOUL, no tools. Streamed /api/chat with a
/// rolling conversation history.
/// </summary>
public sealed class LocalOllamaBackend : IVoiceBackend
{
    private readonly OllamaChatClient _ollama;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConversationHistory _history = new(20);

    public LocalOllamaBackend(AliceVoiceOptions options, OllamaChatClient? ollama = null)
    {
        _ollama = ollama ?? new OllamaChatClient();
        Settings = VoiceBackendSettings.For(VoiceBackendKind.LocalOllama, options);
    }

    public VoiceBackendKind Kind => VoiceBackendKind.LocalOllama;
    public VoiceBackendSettings Settings { get; private set; }
    public event Action<BackendEvent>? EventReceived;
    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(VoiceBackendSettings settings, CancellationToken ct = default)
    {
        Settings = settings;
        IsConnected = false;
        await _ollama.CheckReachableAsync(settings.OllamaUrl, ct);
        IsConnected = true;
        Log.Information("Local Ollama backend connected at {Url}, model {Model}", settings.OllamaUrl, settings.Model);
        EventReceived?.Invoke(new BackendEvent("backend_ready", $"Ollama backend ready — model {Settings.Model}"));
    }

    public void ResetConversation() => _history.Clear();

    public Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default)
    {
        switch (type)
        {
            case "reset":
                ResetConversation();
                EventReceived?.Invoke(new BackendEvent("reset_ack", "Ollama conversation reset"));
                break;
            case "switch_model":
                if (AliceLocalBackend.ReadString(payload, "model") is { Length: > 0 } m) Settings = Settings with { Model = m };
                EventReceived?.Invoke(new BackendEvent("model_info", $"Model: {Settings.Model}"));
                break;
            default:
                Log.Information("Ollama backend command {Type} (no-op)", type);
                break;
        }
        return Task.CompletedTask;
    }

    public async Task<string> AskAsync(string prompt, AskOptions? options = null, CancellationToken ct = default)
    {
        options ??= AskOptions.Default;
        await _lock.WaitAsync(ct);
        try
        {
            var messages = new List<ChatMessage>();
            if (!string.IsNullOrWhiteSpace(options.StyleHint)) messages.Add(new ChatMessage("system", options.StyleHint));
            messages.AddRange(_history.Snapshot());
            messages.Add(new ChatMessage("user", prompt));

            var acc = new StringBuilder();
            var reply = await _ollama.ChatAsync(Settings.OllamaUrl, Settings.Model, messages, options.MaxTokens, d =>
            {
                acc.Append(d);
                EventReceived?.Invoke(new BackendEvent("reply_partial", acc.ToString()));
            }, ct);

            _history.AddTurn(prompt, reply);
            EventReceived?.Invoke(new BackendEvent("reply_text", reply));
            return reply;
        }
        finally { _lock.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        _ollama.Dispose();
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }
}

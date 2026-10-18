namespace AliceVoice.Core.Voice;

/// <summary>
/// Instant local TTS (Windows SAPI). No network, no relay — magnitude faster
/// than streaming TTS from the relay. Used by the local Ollama backend state.
/// Implemented in the Audio project (SapiLocalTts).
/// </summary>
public interface ILocalTts
{
    /// <summary>Speak text synchronously through the default SAPI voice.</summary>
    void Speak(string text);

    /// <summary>Stop any in-progress speech.</summary>
    void Stop();
}

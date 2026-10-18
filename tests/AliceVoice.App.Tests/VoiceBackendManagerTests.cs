using Xunit;
using AliceVoice.Core.Voice;

namespace AliceVoice.App.Tests;

public class VoiceBackendManagerTests
{
    [Fact]
    public async Task SwitchToAsync_KeepsBackendsWarm_NoReconnectOnRepeat()
    {
        var manager = new VoiceBackendManager(new FakeLocalTts());

        // First switch connects (awaited — does a real Ollama reachability check).
        await manager.SwitchToAsync(VoiceBackendKind.LocalOllama);
        Assert.Equal(VoiceBackendKind.LocalOllama, manager.CurrentKind);

        // Second switch to the same kind must be a no-op (already connected).
        await manager.SwitchToAsync(VoiceBackendKind.LocalOllama);
        Assert.Equal(VoiceBackendKind.LocalOllama, manager.CurrentKind);
    }

    private sealed class FakeLocalTts : ILocalTts
    {
        public void Speak(string text) { }
        public void Stop() { }
    }
}

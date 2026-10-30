using AliceVoice.Core.Config;
using AliceVoice.Core.Voice;
using Xunit;

namespace AliceVoice.App.Tests;

/// <summary>A fact that skips cleanly when no Ollama server is listening.</summary>
public sealed class OllamaFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return http.GetAsync("http://localhost:11434/api/tags").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch { return false; }
    });

    public OllamaFactAttribute()
    {
        if (!Available.Value) Skip = "Ollama is not running on localhost:11434";
    }
}

public class OllamaLiveTests
{
    [OllamaFact]
    public async Task LocalOllamaConnects()
    {
        await using var backend = new LocalOllamaBackend(new AliceVoiceOptions());
        await backend.ConnectAsync(VoiceBackendSettings.For(VoiceBackendKind.LocalOllama));
        Assert.True(backend.IsConnected);
    }

    [Fact]
    public async Task ConnectFailsCleanlyWhenServerAbsent()
    {
        var o = new AliceVoiceOptions { OllamaUrl = "http://127.0.0.1:9" };
        await using var backend = new LocalOllamaBackend(o);
        var settings = VoiceBackendSettings.For(VoiceBackendKind.LocalOllama, o);
        await Assert.ThrowsAnyAsync<Exception>(() => backend.ConnectAsync(settings));
        Assert.False(backend.IsConnected);
    }
}

using System.Net;
using System.Net.Sockets;
using AliceVoice.Core.Chat;
using AliceVoice.Core.Config;
using AliceVoice.Core.Stt;
using AliceVoice.Core.Voice;
using AliceVoice.Universal.Audio;
using AliceVoice.Universal.Bridge;
using AliceVoice.Universal.Host;
using AliceVoice.Universal.Voice;
using Xunit;

namespace AliceVoice.Universal.Core.Tests;

public class BridgeTests
{
    [Fact]
    public async Task PhoneClientReachesSttSoulAskFleetAndRememberOnTheLoopbackBridge()
    {
        var port = FreePort();
        var stt = new ScriptedStt();
        var soul = new MemorySoul();
        var local = new ScriptedLocal();
        var fleet = new ScriptedFleet();
        var hub = new SoulReloadHub();
        var reloads = 0;
        hub.Register(_ => { reloads++; return Task.CompletedTask; });
        await using var bridge = new BeastBridge(new AliceVoiceOptions(), stt, soul, new EmptyPiper(), "127.0.0.1", port, hub,
            () => local, () => fleet, allowUnsafeBind: true);
        await bridge.StartAsync();

        using var client = new BeastBridgeClient(new BeastServiceUrls("127.0.0.1", port));
        var health = await client.HealthAsync();
        Assert.True(health.Ok);
        Assert.True(health.HasApiKey);

        var warm = await client.WarmSttAsync();
        Assert.Contains("GPU 1", warm);
        var heard = await client.TranscribeAsync(new short[] { 1, 2, 3 });
        Assert.Equal("hey alice", heard.Text);

        var doc = await client.GetSoulAsync();
        Assert.Equal("old", doc.Text);
        var saved = await client.SaveSoulAsync("new soul text");
        Assert.Equal("new soul text", soul.Text);
        Assert.Equal("/data/SOUL.md", saved.Path);
        Assert.Equal(1, reloads);

        var events = new List<string?>();
        var reply = await client.AskLocalAsync("Master?", new AskOptions { MaxTokens = 160, StyleHint = "playful" }, e => events.Add(e.Type), default);
        Assert.Equal("Hello", reply);
        Assert.Contains("reply_partial", events);
        Assert.Contains("reply_text", events);
        Assert.Equal("Master?", local.Prompt);
        Assert.Equal(160, local.Options!.MaxTokens);

        Assert.True(await client.RememberAsync("likes tea"));
        Assert.Equal("likes tea", local.Remembered);

        var text = await client.FleetSendAsync("alice_voice", "hi");
        Assert.Equal("from-hermes", text);
        var reach = await client.FleetReachableAsync("alice_voice");
        Assert.True(reach.Ok);
        Assert.True(reach.HasApiKey);
        Assert.Null(await client.PiperAsync("hello", "voice"));
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class ScriptedStt : ISttClient
    {
        public bool IsReady => true;
        public string Status => "faster-whisper large-v3 · GPU 1";
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct = default) => Task.FromResult(new SttResult("hey alice", 5));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MemorySoul : ISoulGateway
    {
        public string Text = "old";
        public Task<SoulDocument> LoadAsync(CancellationToken ct = default) => Task.FromResult(new SoulDocument(Text, "/data/SOUL.md", false));
        public Task<SoulSaveResult> SaveAsync(string text, CancellationToken ct = default)
        {
            Text = text;
            return Task.FromResult(new SoulSaveResult("/data/SOUL.md", "/data/backups/SOUL.1.md"));
        }
    }

    private sealed class ScriptedLocal : IVoiceBackend, IAliceExtras
    {
        public VoiceBackendKind Kind => VoiceBackendKind.AliceLocal;
        public VoiceBackendSettings Settings { get; private set; } = VoiceBackendSettings.For(VoiceBackendKind.AliceLocal);
        public bool IsConnected { get; private set; }
        public string? Prompt;
        public AskOptions? Options;
        public string? Remembered;
        public event Action<BackendEvent>? EventReceived;
        public Task ConnectAsync(VoiceBackendSettings settings, CancellationToken ct = default) { Settings = settings; IsConnected = true; return Task.CompletedTask; }
        public Task<string> AskAsync(string prompt, AskOptions? options = null, CancellationToken ct = default)
        {
            Prompt = prompt;
            Options = options;
            EventReceived?.Invoke(new BackendEvent("reply_partial", "He"));
            EventReceived?.Invoke(new BackendEvent("reply_text", "Hello"));
            return Task.FromResult("Hello");
        }
        public void ResetConversation() { }
        public Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RememberAsync(string content, CancellationToken ct = default) { Remembered = content; return Task.FromResult(true); }
        public Task ReloadSoulAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedFleet : IFleetChat
    {
        public bool HasApiKey => true;
        public Task<string> SendAsync(FleetMember member, string message, CancellationToken ct = default) => Task.FromResult("from-hermes");
        public Task<bool> IsReachableAsync(FleetMember member, CancellationToken ct = default) => Task.FromResult(true);
        public void ResetConversation(FleetMember? member = null) { }
    }

    private sealed class EmptyPiper : IPiperSynth
    {
        public IReadOnlyList<string> ListVoices() => Array.Empty<string>();
        public Task<byte[]?> SynthesizeAsync(string text, string voice, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    }
}

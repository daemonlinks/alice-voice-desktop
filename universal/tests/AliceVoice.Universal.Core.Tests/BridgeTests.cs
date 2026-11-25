using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AliceVoice.Universal.Media;
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

    /// <summary>
    /// The service surface, driven the way a frontend or an agent would: interrogate, then control,
    /// then read the real result. This is the foundation the frontends are meant to sit on, so it is
    /// tested over the actual bridge rather than in-process.
    /// </summary>
    [Fact]
    public async Task AnyFrontendCanInterrogateAndDriveTheServiceOverTheBridge()
    {
        var port = FreePort();
        var root = Directory.CreateTempSubdirectory("av-bridge-").FullName;
        File.WriteAllBytes(Path.Combine(root, "beach-morning.png"), MediaTests.Png());
        var media = new MediaSettings { Roots = new[] { root } };
        await using var bridge = new BeastBridge(new AliceVoiceOptions(), new ScriptedStt(), new MemorySoul(), new EmptyPiper(),
            "127.0.0.1", port, null, () => new ScriptedLocal(), () => new ScriptedFleet(), allowUnsafeBind: true, media: media);
        await bridge.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        static StringContent Payload(string json) => new(json, null, "application/json");

        // 1. Interrogate: what this service is, and what it may do - without ever leaking a path.
        var state = await http.GetStringAsync("/v1/app");
        using (var doc = JsonDocument.Parse(state))
        {
            Assert.Equal("alice-voice-bridge", doc.RootElement.GetProperty("service").GetString());
            Assert.Equal(port, doc.RootElement.GetProperty("port").GetInt32());
            var m = doc.RootElement.GetProperty("media");
            Assert.True(m.GetProperty("enabled").GetBoolean());
            Assert.Equal(1, m.GetProperty("roots").GetInt32());
            Assert.Equal("image", m.GetProperty("kinds")[0].GetString());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("on_screen").ValueKind);
        }
        Assert.DoesNotContain(root, state);

        // 2. Discovery: a caller learns the surface instead of being told about it.
        Assert.Contains("gui_show_image", await http.GetStringAsync("/v1/app/tools"));

        // 3. The catalogue, names only.
        var list = await http.GetStringAsync("/v1/app/media?n=5");
        using (var doc = JsonDocument.Parse(list))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
            Assert.Equal("beach-morning", doc.RootElement.GetProperty("names")[0].GetString());
        }
        Assert.DoesNotContain(root, list);

        // 4. A refusal is a real answer in the web page's words, not silence.
        var refused = await http.PostAsync("/v1/app/display", Payload("{\"tool\":\"gui_show_image\",\"args\":{\"file\":\"nope\"}}"));
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        using (var doc = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()))
        {
            Assert.Equal("not a file", doc.RootElement.GetProperty("error").GetString());
            Assert.Contains("beach-morning", doc.RootElement.GetProperty("hint").GetString());
        }

        // 5. Control, and then the proof it actually took: the service holds it.
        var shown = await http.PostAsync("/v1/app/display", Payload("{\"tool\":\"gui_show_image\",\"args\":{\"file\":\"beach-morning\",\"mode\":\"stage\",\"caption\":\"hello\"}}"));
        using (var doc = JsonDocument.Parse(await shown.Content.ReadAsStringAsync()))
            Assert.True(doc.RootElement.GetProperty("accepted").GetBoolean());

        var deadline = DateTime.UtcNow.AddSeconds(5);
        var onScreen = false;
        while (!onScreen && DateTime.UtcNow < deadline)
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync("/v1/app"));
            onScreen = doc.RootElement.GetProperty("on_screen").ValueKind == JsonValueKind.Object;
            if (!onScreen) await Task.Delay(50);
        }
        Assert.True(onScreen, "the service never reported the picture as on screen");

        // 6. A tool this service does not run is refused by name, so a typo reads as a typo.
        var wrong = await http.PostAsync("/v1/app/display", Payload("{\"tool\":\"honcho_search\",\"args\":{\"query\":\"x\"}}"));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("not a display tool: honcho_search", await wrong.Content.ReadAsStringAsync());

        var cleared = await http.PostAsync("/v1/app/display", Payload("{\"tool\":\"gui_image_clear\",\"args\":{}}"));
        using (var doc = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync()))
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

        Directory.Delete(root, true);
    }

    [Fact]
    public async Task ABlankOrFailedBridgeAskIsAnErrorNotSilence()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var blank = await listener.GetContextAsync();
            blank.Response.StatusCode = 200;
            blank.Response.Close();
            var failed = await listener.GetContextAsync();
            var bytes = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"error\",\"text\":\"model unloaded\"}\n");
            failed.Response.StatusCode = 200;
            failed.Response.ContentType = "application/x-ndjson";
            await failed.Response.OutputStream.WriteAsync(bytes);
            failed.Response.Close();
        });

        using var client = new BeastBridgeClient(new BeastServiceUrls("127.0.0.1", port));
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => client.AskLocalAsync("hi", null, null, CancellationToken.None));
        Assert.Contains("no reply", missing.Message);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.AskLocalAsync("hi", null, null, CancellationToken.None));
        Assert.Equal("model unloaded", error.Message);
        listener.Stop();
        await serve;
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

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AliceVoice.Core.Config;
using AliceVoice.Core.Voice;
using AliceVoice.Universal.Bridge;
using AliceVoice.Universal.Media;
using AliceVoice.Universal.Voice;
using Xunit;

namespace AliceVoice.Universal.Core.Tests;

public sealed class MediaTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("av-root-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("av-out-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        try { Directory.Delete(_outside, true); } catch { }
        try { Directory.Delete(_root + "-evil", true); } catch { }
    }

    internal static byte[] Png(int w = 4, int h = 3)
    {
        var b = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), h);
        return b;
    }

    private string Put(string dir, string name, byte[]? bytes = null)
    {
        var p = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes ?? Png());
        return p;
    }

    private MediaShare Share(long max = MediaSettings.CeilingBytes) => new(new[] { _root }, max);

    // ---- path validation ----

    [Fact]
    public void FileInsideARootIsAllowed()
    {
        var c = Share().Validate(Put(_root, "sub/a.png"));
        Assert.True(c.Ok, c.Error);
        Assert.Equal(MediaKind.Image, c.Kind);
    }

    [Fact]
    public void DotDotTraversalOutOfTheRootIsRefused()
    {
        Put(_outside, "x.png");
        var sneaky = Path.Combine(_root, "..", Path.GetFileName(_outside), "x.png");
        Assert.Equal(MediaShare.PathNotAllowed, Share().Validate(sneaky).Error);
    }

    [Fact]
    public void SiblingFolderSharingTheRootPrefixIsRefused()
    {
        var evil = Put(_root + "-evil", "x.png");
        Assert.Equal(MediaShare.PathNotAllowed, Share().Validate(evil).Error);
    }

    [Fact]
    public void SymlinkInsideARootPointingOutsideIsRefused()
    {
        var target = Put(_outside, "x.png");
        var link = Path.Combine(_root, "link.png");
        try { File.CreateSymbolicLink(link, target); } catch { return; } // no symlink rights on this host
        Assert.Equal(MediaShare.PathNotAllowed, Share().Validate(link).Error);
    }

    [Theory]
    [InlineData("relative/a.png")]
    [InlineData("")]
    [InlineData("a\0.png")]
    public void RelativeEmptyOrNulPathsAreRefused(string p)
    {
        Assert.False(Share().Validate(p).Ok);
    }

    [Fact]
    public void DevicePathsAreRefused()
    {
        Assert.False(Share().Validate(@"\\?\C:\Windows\win.ini").Ok);
        Assert.False(Share().Validate(@"\\.\PhysicalDrive0").Ok);
    }

    [Fact]
    public void WrongTypeMissingFileAndOversizeAreRefusedWithRelayErrors()
    {
        Assert.Equal(MediaShare.UnsupportedType, Share().Validate(Put(_root, "a.txt")).Error);
        Assert.Equal(MediaShare.NotAFile, Share().Validate(Path.Combine(_root, "nope.png")).Error);
        Assert.Equal(MediaShare.TooLarge, Share(max: 10).Validate(Put(_root, "big.png")).Error);
    }

    [Fact]
    public void IdRoundTripsAndJunkIdsAreRejected()
    {
        var p = Path.Combine(_root, "ä b.png");
        Assert.True(MediaShare.TryFromId(MediaShare.ToId(p), out var back));
        Assert.Equal(p, back);
        Assert.False(MediaShare.TryFromId("!!!", out _));
        Assert.False(MediaShare.TryFromId("", out _));
    }

    [Fact]
    public void SettingsDefaultToOnRootsOnlyAndClampTheCap()
    {
        var s = MediaSettings.Load(null, _ => null, relayConfig: Path.Combine(_outside, "none.json"));
        Assert.True(s.Enabled);
        Assert.False(s.AllowHttps);
        Assert.Equal(MediaSettings.CeilingBytes, s.MaxBytes);
        var env = new Dictionary<string, string> { ["ALICEVOICE_MEDIA_MAX_MB"] = "500", ["ALICEVOICE_MEDIA"] = "off", ["ALICEVOICE_MEDIA_SOURCES"] = "roots+https" };
        var t = MediaSettings.Load(null, k => env.GetValueOrDefault(k), relayConfig: Path.Combine(_outside, "none.json"));
        Assert.False(t.Enabled);
        Assert.True(t.AllowHttps);
        Assert.Equal(MediaSettings.CeilingBytes, t.MaxBytes);
    }

    // ---- overlay ----

    private static OverlayItem Item(string id, OverlayMode mode, bool keep = true, double dur = 20, DateTime? at = null) =>
        new(id, mode, "", Png(), MediaKind.Image, "t", keep, dur, at ?? new DateTime(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc), 4, 3);

    [Fact]
    public void OverlayShowReplaceClearAndExpire()
    {
        var s = new OverlayState();
        s.Show(Item("a", OverlayMode.Stage));
        s.Show(Item("b", OverlayMode.Card));
        Assert.Equal("b", s.Current!.Id);
        Assert.Null(s.Clear("a"));           // a target that is not showing clears nothing
        Assert.Equal("b", s.Current!.Id);
        Assert.NotNull(s.Clear(""));         // empty id clears whatever is showing
        Assert.Null(s.Current);

        var t0 = new DateTime(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc);
        s.Show(Item("c", OverlayMode.Stage, keep: false, dur: 5, at: t0));
        Assert.Empty(s.Tick(t0.AddSeconds(4)));
        Assert.Single(s.Tick(t0.AddSeconds(6)));
        Assert.Null(s.Current);
    }

    [Fact]
    public void BackdropIsSeparateFromTheForegroundPicture()
    {
        var s = new OverlayState();
        s.Show(Item("bg", OverlayMode.Backdrop));
        s.Show(Item("fg", OverlayMode.Stage));
        Assert.Equal("bg", s.Backdrop!.Id);
        Assert.Equal("fg", s.Current!.Id);
        s.ClearBackdrop();
        Assert.Null(s.Backdrop);
        Assert.Equal("fg", s.Current!.Id);
    }

    // ---- image display ----

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        { Urls.Add(r.RequestUri!.ToString()); return Task.FromResult(f(r)); }
    }

    private static ToolCommand Show(string src, string mode = "stage", string? id = null, bool? keep = null) =>
        new(DisplayCommands.ShowImage, JsonSerializer.Serialize(new Dictionary<string, object?> { ["src"] = src, ["mode"] = mode, ["id"] = id, ["keep"] = keep }.Where(kv => kv.Value is not null).ToDictionary()));

    [Fact]
    public async Task LocalPictureInARootIsShownAndRefusalsAreLogged()
    {
        var log = new List<string>();
        using var d = new ImageDisplay(new MediaSettings { Roots = new[] { _root } }, Share(), null, log.Add);
        d.Submit(Show(Put(_root, "a.png"), "card", "p1"));
        await d.WhenIdle();
        Assert.Equal("p1", d.State.Current!.Id);
        Assert.Equal(OverlayMode.Card, d.State.Current.Mode);
        Assert.Equal(4, d.State.Current.Width);

        d.Submit(Show(Put(_outside, "x.png")));
        await d.WhenIdle();
        Assert.Equal("p1", d.State.Current!.Id);
        Assert.Contains(log, l => l.Contains(MediaShare.PathNotAllowed));

        d.Submit(new ToolCommand(DisplayCommands.ClearImage, "{\"id\":\"p1\"}"));
        Assert.Null(d.State.Current);
    }

    [Fact]
    public async Task HttpsIsOffByDefaultAndNothingIsFetched()
    {
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png()) });
        var log = new List<string>();
        using var d = new ImageDisplay(new MediaSettings { Roots = new[] { _root } }, Share(), null, log.Add, http: new HttpClient(h));
        d.Submit(Show("https://example.com/a.png"));
        await d.WhenIdle();
        Assert.Null(d.State.Current);
        Assert.Empty(h.Urls);
        Assert.Contains(log, l => l.Contains("allowed roots only"));
    }

    [Fact]
    public async Task HttpsWorksOnlyWhenTheSettingAllowsIt()
    {
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png()) });
        using var d = new ImageDisplay(new MediaSettings { Sources = MediaSources.AllowedRootsAndHttps }, null, null, _ => { }, http: new HttpClient(h));
        d.Submit(Show("http://example.com/a.png"));
        await d.WhenIdle();
        Assert.Null(d.State.Current);
        d.Submit(Show("https://example.com/a.png"));
        await d.WhenIdle();
        Assert.NotNull(d.State.Current);
    }

    [Fact]
    public async Task PhoneFetchesThroughTheBridgeAndMapsRefusals()
    {
        var h = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith(MediaShare.ToId("/p/ok.png"))
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png()) }
            : new HttpResponseMessage(HttpStatusCode.Forbidden));
        var log = new List<string>();
        using var d = new ImageDisplay(new MediaSettings(), null, id => "http://beast:8765/v1/media/" + id, log.Add, http: new HttpClient(h));
        d.Submit(Show("/p/ok.png"));
        await d.WhenIdle();
        Assert.NotNull(d.State.Current);
        d.Submit(Show("/p/other.png"));
        await d.WhenIdle();
        Assert.Contains(log, l => l.Contains(MediaShare.PathNotAllowed));
        Assert.All(h.Urls, u => Assert.StartsWith("http://beast:8765/v1/media/", u));
    }

    [Fact]
    public async Task VideoIsRefusedForNow()
    {
        var log = new List<string>();
        using var d = new ImageDisplay(new MediaSettings { Roots = new[] { _root } }, Share(), null, log.Add);
        d.Submit(Show(Put(_root, "a.mp4")));
        await d.WhenIdle();
        Assert.Null(d.State.Current);
        Assert.Contains(log, l => l.Contains("video"));
    }

    [Fact]
    public async Task MediaOffIgnoresPictures()
    {
        using var d = new ImageDisplay(new MediaSettings { Enabled = false, Roots = new[] { _root } }, Share(), null, _ => { });
        d.Submit(Show(Put(_root, "a.png")));
        await d.WhenIdle();
        Assert.Null(d.State.Current);
    }

    // ---- bridge endpoint ----

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [Fact]
    public async Task BridgeServesOnlyAllowedMediaWithRelayStatusCodes()
    {
        var port = FreePort();
        await using var bridge = new BeastBridge(new AliceVoiceOptions(), new NullStt(), new NullSoul(), new NullPiper(), "127.0.0.1", port,
            allowUnsafeBind: true, media: new MediaSettings { Roots = new[] { _root }, MaxBytes = 1000 });
        await bridge.StartAsync();
        using var http = new HttpClient();
        string U(string p) => $"http://127.0.0.1:{port}/v1/media/{MediaShare.ToId(p)}";

        var ok = await http.GetAsync(U(Put(_root, "a.png")));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("image/png", ok.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png(), await ok.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(U(Put(_outside, "x.png")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(U(Path.Combine(_root, "..", Path.GetFileName(_outside), "x.png")))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await http.GetAsync(U(Put(_root, "a.txt")))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await http.GetAsync(U(Put(_root, "big.png", new byte[2000])))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(U(Path.Combine(_root, "nope.png")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync($"http://127.0.0.1:{port}/v1/media/!!!")).StatusCode);
    }

    [Fact]
    public async Task BridgeMediaOffAnswersNotFound()
    {
        var port = FreePort();
        await using var bridge = new BeastBridge(new AliceVoiceOptions(), new NullStt(), new NullSoul(), new NullPiper(), "127.0.0.1", port,
            allowUnsafeBind: true, media: new MediaSettings { Enabled = false, Roots = new[] { _root } });
        await bridge.StartAsync();
        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://127.0.0.1:{port}/v1/media/{MediaShare.ToId(Put(_root, "a.png"))}")).StatusCode);
    }

    private sealed class NullStt : AliceVoice.Core.Stt.ISttClient
    {
        public bool IsReady => true;
        public string Status => "test";
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<AliceVoice.Core.Stt.SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct = default) => Task.FromResult(new AliceVoice.Core.Stt.SttResult("", 0));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullSoul : ISoulGateway
    {
        public Task<SoulDocument> LoadAsync(CancellationToken ct = default) => Task.FromResult(new SoulDocument("", "/data/SOUL.md", false));
        public Task<SoulSaveResult> SaveAsync(string text, CancellationToken ct = default) => Task.FromResult(new SoulSaveResult("/data/SOUL.md", "/data/b.md"));
    }

    private sealed class NullPiper : AliceVoice.Universal.Audio.IPiperSynth
    {
        public IReadOnlyList<string> ListVoices() => Array.Empty<string>();
        public Task<byte[]?> SynthesizeAsync(string text, string voice, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    }
}

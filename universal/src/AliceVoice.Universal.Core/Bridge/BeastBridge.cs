using System.Net;
using System.Text;
using System.Text.Json;
using AliceVoice.Core.Chat;
using AliceVoice.Core.Config;
using AliceVoice.Core.Stt;
using AliceVoice.Core.Voice;
using AliceVoice.Universal.Audio;
using AliceVoice.Universal.Voice;
using Serilog;

namespace AliceVoice.Universal.Bridge;

/// <summary>
/// Windows-only loopback (or Tailscale) bridge so the phone can use the Beast's
/// GPU speech-to-text, Alice LOCAL tools (Honcho on port 8001, run by the
/// existing Python bridge — keys stay in the PC environment), fleet chat
/// (API key stays in the Hermes .env), SOUL file, and Piper.
/// There is no authentication on this socket. Leave it unset unless
/// <c>ALICEVOICE_BRIDGE_BIND</c> is the Tailscale address, not the public internet.
/// The original WinUI app does not open this port.
/// </summary>
public sealed class BeastBridge : IAsyncDisposable
{
    public const int MaxBodyBytes = 8_000_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AliceVoiceOptions _options;
    private readonly ISttClient _stt;
    private readonly ISoulGateway _soul;
    private readonly IPiperSynth _piper;
    private readonly SoulReloadHub? _hub;
    private readonly Func<IVoiceBackend> _localFactory;
    private readonly Func<IFleetChat> _fleetFactory;
    private readonly string _host;
    private readonly int _port;
    private readonly SemaphoreSlim _localLock = new(1, 1);

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IVoiceBackend? _local;
    private IFleetChat? _fleet;

    public BeastBridge(AliceVoiceOptions options, ISttClient stt, ISoulGateway soul, IPiperSynth piper, string host, int port, SoulReloadHub? hub = null,
        Func<IVoiceBackend>? localFactory = null, Func<IFleetChat>? fleetFactory = null)
    {
        _options = options;
        _stt = stt;
        _soul = soul;
        _piper = piper;
        _hub = hub;
        _host = host;
        _port = port;
        _localFactory = localFactory ?? (() => new AliceLocalBackend(options));
        _fleetFactory = fleetFactory ?? (() => new HermesApiFleetChat(options.ResolvedHermesApiUrl));
    }

    public string Prefix => $"http://{_host}:{_port}/";
    public bool IsListening => _listener is { IsListening: true };

    public Task StartAsync(CancellationToken ct = default)
    {
        if (IsListening) return Task.CompletedTask;
        var listener = new HttpListener();
        listener.Prefixes.Add(Prefix);
        listener.Start();
        _listener = listener;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => AcceptLoop(_cts.Token));
        Log.Information("Beast bridge listening on {Prefix} (phone STT, SOUL, Alice LOCAL, fleet, Piper). No auth — bind this to Tailscale, not the public internet.", Prefix);
        return Task.CompletedTask;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null) return;
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().WaitAsync(ct); }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => HandleSafe(ctx, ct), CancellationToken.None);
        }
    }

    private async Task HandleSafe(HttpListenerContext ctx, CancellationToken ct)
    {
        try { await Handle(ctx, ct); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Beast bridge request failed");
            try { await WriteJson(ctx.Response, 500, new OkResponse(false, "bridge error")); } catch { try { ctx.Response.Close(); } catch { } }
        }
    }

    private async Task Handle(HttpListenerContext ctx, CancellationToken ct)
    {
        var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
        var method = ctx.Request.HttpMethod;
        if (method == "GET" && path == "/v1/health")
        {
            await WriteJson(ctx.Response, 200, new BridgeHealth(true, _stt.Status, Fleet().HasApiKey));
            return;
        }
        if (method == "POST" && path == "/v1/stt/warmup")
        {
            await _stt.WarmUpAsync(ct);
            await WriteJson(ctx.Response, 200, new SttResponse(null, 0, null, _stt.Status));
            return;
        }
        if (method == "POST" && path == "/v1/stt")
        {
            var body = await ReadBody(ctx.Request, MaxBodyBytes, ct);
            if (body.Length % 2 != 0) { await WriteJson(ctx.Response, 400, new SttResponse(null, 0, "PCM length must be even", null)); return; }
            var pcm = new short[body.Length / 2];
            Buffer.BlockCopy(body, 0, pcm, 0, body.Length);
            try
            {
                var result = await _stt.TranscribeAsync(pcm, ct);
                await WriteJson(ctx.Response, 200, new SttResponse(result.Text, result.ElapsedMs, null, _stt.Status));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await WriteJson(ctx.Response, 500, new SttResponse(null, 0, ex.Message, _stt.Status));
            }
            return;
        }
        if (method == "GET" && path == "/v1/soul")
        {
            var doc = await _soul.LoadAsync(ct);
            await WriteJson(ctx.Response, 200, new SoulResponse(doc.Text, doc.Path, doc.CreatedFromTemplate, null));
            return;
        }
        if (method == "PUT" && path == "/v1/soul")
        {
            var text = Encoding.UTF8.GetString(await ReadBody(ctx.Request, MaxBodyBytes, ct));
            try
            {
                var saved = await _soul.SaveAsync(text, ct);
                if (_hub is not null) await _hub.ReloadAllAsync(ct);
                else if (_local is AliceLocalBackend local) await local.ReloadSoulAsync(ct);
                else if (_local is IAliceExtras extras) await extras.ReloadSoulAsync(ct);
                await WriteJson(ctx.Response, 200, new SoulSaveResponse(saved.Path, saved.BackupPath, null));
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
            {
                await WriteJson(ctx.Response, 400, new SoulSaveResponse(null, null, ex.Message));
            }
            return;
        }
        if (method == "POST" && path == "/v1/local/connect")
        {
            try
            {
                await LocalAsync(ct);
                await WriteJson(ctx.Response, 200, new OkResponse(true, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await WriteJson(ctx.Response, 503, new OkResponse(false, ex.Message));
            }
            return;
        }
        if (method == "POST" && path == "/v1/local/ask")
        {
            await Ask(ctx, ct);
            return;
        }
        if (method == "POST" && path == "/v1/local/remember")
        {
            var req = await ReadJson<RememberRequest>(ctx.Request, ct) ?? new RememberRequest(null);
            var backend = await LocalAsync(ct);
            bool ok;
            if (backend is AliceLocalBackend local) ok = await local.RememberAsync(req.Content ?? "", ct);
            else if (backend is IAliceExtras extras) ok = await extras.RememberAsync(req.Content ?? "", ct);
            else ok = false;
            await WriteJson(ctx.Response, 200, new OkResponse(ok, ok ? null : "remember failed"));
            return;
        }
        if (method == "POST" && path == "/v1/local/command")
        {
            var req = await ReadJson<CommandRequest>(ctx.Request, ct) ?? new CommandRequest(null);
            var backend = await LocalAsync(ct);
            await backend.SendCommandAsync(req.Type ?? "", null, ct);
            await WriteJson(ctx.Response, 200, new OkResponse(true, null));
            return;
        }
        if (method == "POST" && path == "/v1/fleet/send")
        {
            var req = await ReadJson<FleetSendRequest>(ctx.Request, ct) ?? new FleetSendRequest(null, null);
            var member = FleetRoster.Find(req.Member ?? "");
            if (member is null) { await WriteJson(ctx.Response, 404, new TextResponse(null, "unknown fleet member")); return; }
            try
            {
                var text = await Fleet().SendAsync(member, req.Message ?? "", ct);
                await WriteJson(ctx.Response, 200, new TextResponse(text, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await WriteJson(ctx.Response, 502, new TextResponse(null, ex.Message));
            }
            return;
        }
        if (method == "POST" && path == "/v1/fleet/reachable")
        {
            var req = await ReadJson<MemberRequest>(ctx.Request, ct) ?? new MemberRequest(null);
            var member = FleetRoster.Find(req.Member ?? "");
            var chat = Fleet();
            var ok = member is not null && await chat.IsReachableAsync(member, ct);
            await WriteJson(ctx.Response, 200, new ReachableResponse(ok, chat.HasApiKey));
            return;
        }
        if (method == "POST" && path == "/v1/fleet/reset")
        {
            var req = await ReadJson<MemberRequest>(ctx.Request, ct) ?? new MemberRequest(null);
            var member = string.IsNullOrEmpty(req.Member) ? null : FleetRoster.Find(req.Member);
            Fleet().ResetConversation(member);
            await WriteJson(ctx.Response, 200, new OkResponse(true, null));
            return;
        }
        if (method == "GET" && path == "/v1/tts/piper/voices")
        {
            await WriteJson(ctx.Response, 200, new VoicesResponse(_piper.ListVoices().ToList()));
            return;
        }
        if (method == "POST" && path == "/v1/tts/piper")
        {
            var req = await ReadJson<PiperRequest>(ctx.Request, ct) ?? new PiperRequest(null, null);
            var wav = await _piper.SynthesizeAsync(req.Text ?? "", req.Voice ?? "", ct);
            if (wav is null) { await WriteJson(ctx.Response, 404, new OkResponse(false, "Piper voice or executable not available on the Beast")); return; }
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "audio/wav";
            ctx.Response.ContentLength64 = wav.Length;
            await ctx.Response.OutputStream.WriteAsync(wav, ct);
            ctx.Response.Close();
            return;
        }

        await WriteJson(ctx.Response, 404, new OkResponse(false, "not found"));
    }

    private async Task Ask(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = await ReadJson<AskRequest>(ctx.Request, ct) ?? new AskRequest(null, null, null);
        IVoiceBackend backend;
        try { backend = await LocalAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteJson(ctx.Response, 503, new BridgeEvent("error", ex.Message));
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
        ctx.Response.SendChunked = true;
        await using var writer = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        void OnEvent(BackendEvent evt)
        {
            try
            {
                lock (writer) writer.WriteLine(JsonSerializer.Serialize(new BridgeEvent(evt.Type, evt.Text), Json));
            }
            catch (Exception ex) { Log.Debug(ex, "Bridge event write failed"); }
        }

        backend.EventReceived += OnEvent;
        try
        {
            var options = new AskOptions { MaxTokens = req.MaxTokens, StyleHint = string.IsNullOrWhiteSpace(req.StyleHint) ? null : req.StyleHint };
            await backend.AskAsync(req.Prompt ?? "", options, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new BridgeEvent("error", ex.Message), Json).AsMemory(), ct);
        }
        finally
        {
            backend.EventReceived -= OnEvent;
            ctx.Response.Close();
        }
    }

    private async Task<IVoiceBackend> LocalAsync(CancellationToken ct)
    {
        if (_local is { IsConnected: true }) return _local;
        await _localLock.WaitAsync(ct);
        try
        {
            if (_local is { IsConnected: true }) return _local;
            IVoiceBackend? created = null;
            try
            {
                created = _localFactory();
                await created.ConnectAsync(VoiceBackendSettings.For(VoiceBackendKind.AliceLocal, _options), ct);
                if (created is AliceLocalBackend alice) _hub?.Register(alice.ReloadSoulAsync);
                else if (created is IAliceExtras extras) _hub?.Register(extras.ReloadSoulAsync);
                _local = created;
                created = null;
                return _local;
            }
            finally
            {
                if (created is not null) await created.DisposeAsync();
            }
        }
        finally { _localLock.Release(); }
    }

    private IFleetChat Fleet() => _fleet ??= _fleetFactory();

    private static async Task<T?> ReadJson<T>(HttpListenerRequest req, CancellationToken ct)
    {
        var body = await ReadBody(req, MaxBodyBytes, ct);
        if (body.Length == 0) return default;
        return JsonSerializer.Deserialize<T>(body, Json);
    }

    private static async Task<byte[]> ReadBody(HttpListenerRequest req, int max, CancellationToken ct)
    {
        await using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var n = await req.InputStream.ReadAsync(buffer, ct);
            if (n == 0) break;
            if (ms.Length + n > max) throw new InvalidOperationException("Request body is too large");
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    private static async Task WriteJson(HttpListenerResponse res, int status, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        if (_loop is not null) { try { await _loop; } catch { } }
        _cts?.Dispose();
        if (_local is not null) await _local.DisposeAsync();
        if (_fleet is IDisposable d) d.Dispose();
        _localLock.Dispose();
    }
}

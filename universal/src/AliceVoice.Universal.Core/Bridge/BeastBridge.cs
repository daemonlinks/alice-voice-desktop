using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AliceVoice.Core.Chat;
using AliceVoice.Core.Config;
using AliceVoice.Core.Logging;
using AliceVoice.Core.Stt;
using AliceVoice.Core.Voice;
using AliceVoice.Universal.Audio;
using AliceVoice.Universal.Host;
using AliceVoice.Universal.Interaction;
using AliceVoice.Universal.Media;
using AliceVoice.Universal.Tools;
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
    private readonly SharedTranscript? _transcript;
    private readonly bool _allowUnsafeBind;
    private readonly string _host;
    private readonly int _port;
    private readonly SemaphoreSlim _localLock = new(1, 1);
    private readonly MediaSettings _media;
    private readonly MediaShare _share;

    /// <summary>What should be on screen, held by the service and served over /v1/media. A frontend paints it.</summary>
    private readonly ImageDisplay _images;

    /// <summary>The service's own tools - the same implementation App U's local leg calls.</summary>
    private readonly AppToolHost _tools;

    /// <summary>The shared middle of the conversation screen, served over /v1/app/surface.</summary>
    private readonly SharedSurface _surface;

    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IVoiceBackend? _local;
    private IFleetChat? _fleet;

    public BeastBridge(AliceVoiceOptions options, ISttClient stt, ISoulGateway soul, IPiperSynth piper, string host, int port, SoulReloadHub? hub = null,
        Func<IVoiceBackend>? localFactory = null, Func<IFleetChat>? fleetFactory = null, SharedTranscript? transcript = null, bool allowUnsafeBind = false,
        MediaSettings? media = null, SharedSurface? surface = null)
    {
        _media = media ?? MediaSettings.Default;
        _share = new MediaShare(_media);
        // The service owns what should be on screen, and the rules that decide it. Pictures are held
        // here and served over /v1/media, so any frontend - App U's own window, the web page, a phone,
        // something not written yet - can show the same thing from the same place. App U's window
        // still paints its own copy until it is switched to read this surface; that is the one
        // follow-up, and it is deliberately not half-done here.
        _images = new ImageDisplay(_media, _share, null, note => Log.Information("App display: {Note}", note));
        _tools = new AppToolHost(_images, _surface = surface ?? new SharedSurface());
        _options = options;
        _stt = stt;
        _soul = soul;
        _piper = piper;
        _hub = hub;
        _host = host;
        _port = port;
        _transcript = transcript;
        _allowUnsafeBind = allowUnsafeBind;
        _localFactory = localFactory ?? (() => new AliceLocalBackend(options));
        _fleetFactory = fleetFactory ?? (() => new HermesApiFleetChat(options.ResolvedHermesApiUrl));
    }

    public string Prefix => $"http://{_host}:{_port}/";
    public bool IsListening => _listener is { IsListening: true };

    public Task StartAsync(CancellationToken ct = default)
    {
        if (IsListening) return Task.CompletedTask;
        var decision = BridgeBindPolicy.Evaluate(_host, _allowUnsafeBind);
        if (!decision.Allowed)
            throw new InvalidOperationException(decision.Reason);
        if (decision.Warned) Log.Warning("{Reason}", decision.Reason);
        var listener = new HttpListener();
        listener.Prefixes.Add(Prefix);
        listener.Start();
        _listener = listener;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => AcceptLoop(_cts.Token));
        OperationTrail.Leave("bridge.listening", "port=" + _port);
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
            OperationTrail.Leave("bridge.request.failed", ex.GetType().Name);
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
        // ---- the app surface: what any frontend or agent may ask this service --------------------
        // Interrogate, then control, then a real result - the same shape the web page's tool bridge
        // returns, so a frontend written against this works against either.
        if (method == "GET" && path == "/v1/app")
        {
            await WriteJson(ctx.Response, 200, AppState());
            return;
        }
        if (method == "GET" && path == "/v1/app/tools")
        {
            await WriteJson(ctx.Response, 200, AppTools());
            return;
        }
        if (method == "GET" && path == "/v1/app/media")
        {
            var asked = int.TryParse(ctx.Request.QueryString["n"], out var n) ? n : 12;
            var listed = await _tools.RunAsync(AppToolHost.ListPictures, new JsonObject { ["n"] = asked }.ToJsonString(), ct);
            await WriteJson(ctx.Response, 200, Node(listed ?? "{}"));
            return;
        }
        if (method == "GET" && path == "/v1/app/surface")
        {
            await WriteJson(ctx.Response, 200, Node(_surface.ToJson().ToJsonString()));
            return;
        }
        if (method == "POST" && path == "/v1/app/display")
        {
            var body = Encoding.UTF8.GetString(await ReadBody(ctx.Request, MaxBodyBytes, ct));
            var call = ParseCall(body);
            if (call is null)
            {
                await WriteJson(ctx.Response, 400, Node(new JsonObject { ["error"] = "bad args: tool" }.ToJsonString()));
                return;
            }
            var result = await _tools.RunAsync(call.Value.Tool, call.Value.ArgsJson, ct);
            if (result is null)
            {
                // Honest refusal rather than silence: this service runs the display tools, and it says
                // so, so a caller can tell a typo from a fault.
                await WriteJson(ctx.Response, 400, Node(new JsonObject
                {
                    ["error"] = "not a display tool: " + call.Value.Tool,
                    ["tools"] = new JsonArray("gui_list_pictures", "gui_show_image", "gui_image_clear", "gui_surface", "gui_surface_clear"),
                }.ToJsonString()));
                return;
            }
            await WriteJson(ctx.Response, 200, Node(result));
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
        if (method == "GET" && path == "/v1/local/transcript")
        {
            var turns = _transcript?.Snapshot() ?? Array.Empty<SharedTurn>();
            await WriteJson(ctx.Response, 200, new TranscriptResponse(_transcript?.Epoch ?? 0, turns.ToList()));
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
        if (method == "GET" && path.StartsWith(MediaPrefix, StringComparison.Ordinal))
        {
            await ServeMedia(ctx, path[MediaPrefix.Length..], ct);
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

    /// <summary>
    /// What this service is and what it is allowed to do. Deliberately no filesystem paths: a frontend
    /// or an agent is told how many roots there are, never where they are.
    /// </summary>
    private JsonObject AppState()
    {
        var kinds = new JsonArray();
        foreach (var kind in _media.AllowedKinds.OrderBy(k => k.ToString())) kinds.Add(kind.ToString().ToLowerInvariant());
        var onScreen = _images.State.Current is { } current
            ? new JsonObject { ["id"] = current.Id, ["mode"] = current.Mode.ToString().ToLowerInvariant(), ["kind"] = current.Kind.ToString().ToLowerInvariant(), ["caption"] = current.Caption }
            : null;
        return new JsonObject
        {
            ["service"] = "alice-voice-bridge",
            ["version"] = typeof(BeastBridge).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            ["host"] = _host,
            ["port"] = _port,
            ["uptime_s"] = (int)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
            ["stt"] = _stt.Status,
            ["piper_voices"] = _piper.ListVoices().Count,
            ["media"] = new JsonObject
            {
                ["enabled"] = _media.Enabled,
                ["sources"] = _media.Sources.ToString(),
                ["https"] = _media.AllowHttps,
                ["max_mb"] = _media.MaxBytes / (1024 * 1024),
                ["duration_s"] = _media.DefaultDurationS,
                ["kinds"] = kinds,
                ["roots"] = _media.Roots.Count,
            },
            ["on_screen"] = onScreen,
            ["tools"] = AppTools()["tools"]?.DeepClone(),
        };
    }

    /// <summary>What a caller may ask for, and what each one does. Discovery, not documentation that drifts.</summary>
    private static JsonObject AppTools() => new()
    {
        ["tools"] = new JsonArray
        {
            new JsonObject { ["name"] = "gui_list_pictures", ["args"] = new JsonObject { ["n"] = "1..40, default 12" }, ["does"] = "names of pictures that may be shown, never paths" },
            new JsonObject { ["name"] = "gui_show_image", ["args"] = new JsonObject { ["src"] = "absolute path or https URL", ["file"] = "a name from gui_list_pictures", ["mode"] = "stage|card|backdrop", ["caption"] = "short line", ["keep"] = "true|false", ["duration_s"] = "seconds" }, ["does"] = "put a picture on screen, held by this service" },
            new JsonObject { ["name"] = "gui_image_clear", ["args"] = new JsonObject { ["id"] = "optional" }, ["does"] = "take a picture back off the screen" },
        },
    };

    /// <summary>A tool's own JSON, written through unchanged so the shape matches the web page exactly.</summary>
    private static JsonNode Node(string json)
    {
        try { return JsonNode.Parse(json) ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject { ["error"] = "bad tool result" }; }
    }

    private static (string Tool, string ArgsJson)? ParseCall(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject obj) return null;
            var tool = obj["tool"]?.ToString();
            if (string.IsNullOrWhiteSpace(tool)) return null;
            return (tool, obj["args"] is { } args ? args.ToJsonString() : "{}");
        }
        catch (JsonException) { return null; }
    }

    private const string MediaPrefix = "/v1/media/";

    /// <summary>
    /// GET /v1/media/{id}: a picture for a phone. The id is base64url of an absolute path
    /// and grants nothing: every request is checked against the relay's share rules
    /// (allowed roots, extension list, 40 MB cap) before a byte is read. Same Tailscale-only
    /// socket as every other route; this adds no listener.
    /// </summary>
    private async Task ServeMedia(HttpListenerContext ctx, string id, CancellationToken ct)
    {
        if (!_media.Enabled) { await WriteJson(ctx.Response, 404, new OkResponse(false, "media display is off")); return; }
        if (!MediaShare.TryFromId(id, out var path)) { await WriteJson(ctx.Response, 400, new OkResponse(false, "bad args: id")); return; }
        var check = _share.Validate(path);
        if (!check.Ok)
        {
            var status = check.Error switch
            {
                MediaShare.PathNotAllowed => 403,
                MediaShare.UnsupportedType => 415,
                MediaShare.TooLarge => 413,
                _ => 404
            };
            OperationTrail.Leave("bridge.media.refused", check.Error ?? "");
            await WriteJson(ctx.Response, status, new OkResponse(false, check.Error));
            return;
        }
        await using var file = new FileStream(check.FullPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        if (file.Length > MediaSettings.CeilingBytes) { await WriteJson(ctx.Response, 413, new OkResponse(false, MediaShare.TooLarge)); return; }
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = check.ContentType ?? "application/octet-stream";
        ctx.Response.ContentLength64 = file.Length;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        await file.CopyToAsync(ctx.Response.OutputStream, ct);
        ctx.Response.Close();
        OperationTrail.Leave("bridge.media.served", "bytes=" + file.Length);
    }

    private async Task Ask(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = await ReadJson<AskRequest>(ctx.Request, ct) ?? new AskRequest(null, null, null);
        using var origin = ConversationOrigin.Use(ConversationOrigin.Phone);
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

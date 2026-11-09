using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AliceVoice.Universal.Host;

/// <summary>
/// Where a client dials the Beast. This is not the listen address: clients never
/// set <c>BridgeBind</c>. The short MagicDNS name is last because a LAN search
/// domain can resolve it to a private address where the bridge is not listening.
/// </summary>
public static class BridgeClientDirectory
{
    public const string TailscaleHost = "100.64.237.3";
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    public static IReadOnlyList<string> Candidates(string? configuredHost)
    {
        var list = new List<string>();
        Add(list, configuredHost);
        Add(list, TailscaleHost);
        Add(list, BridgeBindPolicy.DefaultBeastHost);
        return list;
    }

    public static async Task<BridgeDial> PickAsync(IReadOnlyList<string> hosts, int port, Func<string, int, CancellationToken, Task<ProbeResult>> probe, CancellationToken ct)
    {
        if (port is < 1 or > 65535) throw new InvalidOperationException($"Bridge port {port} is not valid.");
        var tried = new List<string>();
        foreach (var host in hosts)
        {
            ProbeResult result;
            try { result = await probe(host, port, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || ct.IsCancellationRequested)
            {
                if (ct.IsCancellationRequested) throw;
                result = new ProbeResult(false, ex.Message);
            }
            if (result.Ok) return new BridgeDial(host, port, tried);
            tried.Add($"{host}:{port} ({result.Detail})");
        }
        throw new InvalidOperationException("Could not reach the Beast bridge. Tried " + string.Join("; ", tried) + ".");
    }

    public static async Task<ProbeResult> ProbeTcpAsync(string host, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            return new ProbeResult(client.Connected, client.Connected ? "connected" : "no connection");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeResult(false, "timed out");
        }
        catch (SocketException ex)
        {
            return new ProbeResult(false, string.IsNullOrWhiteSpace(ex.Message) ? ex.SocketErrorCode.ToString() : ex.Message);
        }
    }

    /// <summary>Writes BeastHost and BridgePort. Leaves BridgeBind and every other key as they are.</summary>
    public static string? SaveClient(string? existingJson, string host, int port)
    {
        JsonObject obj;
        if (string.IsNullOrWhiteSpace(existingJson))
            obj = new JsonObject();
        else
        {
            try
            {
                if (JsonNode.Parse(existingJson, documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                }) is not JsonObject parsed)
                    return null;
                obj = parsed;
            }
            catch (JsonException)
            {
                return null;
            }
        }
        Set(obj, "BeastHost", host);
        Set(obj, "BridgePort", port);
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void Set(JsonObject obj, string name, JsonNode? value)
    {
        string? existing = null;
        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                existing = pair.Key;
                break;
            }
        }
        if (existing is not null && !string.Equals(existing, name, StringComparison.Ordinal))
            obj.Remove(existing);
        obj[existing ?? name] = value;
    }

    public static void RetargetServices(AliceVoice.Core.Config.AliceVoiceOptions options, string host)
    {
        var chosen = BeastServiceUrls.HostOnly(host);
        options.OllamaUrl = Retarget(options.OllamaUrl, 11434, chosen);
        options.HermesApiUrl = Retarget(options.HermesApiUrl, 8643, chosen);
        options.AllTalkUrl = Retarget(options.AllTalkUrl, 7851, chosen);
    }

    private static string Retarget(string? current, int port, string host)
    {
        var fallback = $"http://{host}:{port}";
        if (string.IsNullOrWhiteSpace(current)) return fallback;
        if (!Uri.TryCreate(current, UriKind.Absolute, out var uri)) return fallback;
        if (uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)) return current;
        if (uri.Host.Equals(TailscaleHost, StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals(BridgeBindPolicy.DefaultBeastHost, StringComparison.OrdinalIgnoreCase))
            return $"http://{host}:{port}";
        return current;
    }

    private static void Add(List<string> list, string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        var trimmed = host.Trim();
        if (list.Exists(item => string.Equals(item, trimmed, StringComparison.OrdinalIgnoreCase))) return;
        list.Add(trimmed);
    }

}

public readonly record struct ProbeResult(bool Ok, string Detail);

public readonly record struct BridgeDial(string Host, int Port, IReadOnlyList<string> Failed);

public sealed class BridgeEndpoint
{
    public BridgeEndpoint(string host, int port)
    {
        Host = host;
        Port = port;
    }

    public string Host { get; private set; }
    public int Port { get; private set; }
    public BeastServiceUrls Urls => new(Host, Port);

    public void Set(string host, int port)
    {
        Host = BeastServiceUrls.HostOnly(host);
        Port = port;
    }
}

public sealed class BridgeClientSettings
{
    private readonly BridgeEndpoint _endpoint;
    private readonly string? _settingsFile;
    private readonly AliceVoice.Core.Config.AliceVoiceOptions _options;

    public BridgeClientSettings(BridgeEndpoint endpoint, string? settingsFile, AliceVoice.Core.Config.AliceVoiceOptions options)
    {
        _endpoint = endpoint;
        _settingsFile = settingsFile;
        _options = options;
        Probe = BridgeClientDirectory.ProbeTcpAsync;
    }

    public Func<string, int, CancellationToken, Task<ProbeResult>> Probe { get; set; }
    public string Host => _endpoint.Host;
    public int Port => _endpoint.Port;

    public async Task EnsureReachableAsync(CancellationToken ct)
    {
        var chosen = await BridgeClientDirectory.PickAsync(BridgeClientDirectory.Candidates(Host), Port, Probe, ct);
        var changed = !string.Equals(chosen.Host, Host, StringComparison.OrdinalIgnoreCase) || chosen.Port != Port;
        Apply(chosen.Host, chosen.Port, changed);
    }

    public void Save(string host, int port) => Apply(host, port, persist: true);

    private void Apply(string host, int port, bool persist)
    {
        _endpoint.Set(host, port);
        BridgeClientDirectory.RetargetServices(_options, _endpoint.Host);
        if (!persist || string.IsNullOrWhiteSpace(_settingsFile)) return;
        var existing = File.Exists(_settingsFile) ? File.ReadAllText(_settingsFile) : null;
        var updated = BridgeClientDirectory.SaveClient(existing, _endpoint.Host, _endpoint.Port);
        if (updated is null) return;
        var directory = Path.GetDirectoryName(_settingsFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(_settingsFile, updated);
    }
}

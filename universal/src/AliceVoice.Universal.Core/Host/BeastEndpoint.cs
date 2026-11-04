namespace AliceVoice.Universal.Host;

/// <summary>
/// Where the phone reaches services that only exist on the Beast PC.
/// Nothing here is a secret: hosts and ports only. API keys stay on the PC.
/// </summary>
public sealed record BeastServiceUrls(string Host, int BridgePort)
{
    public const int DefaultBridgePort = 8765;

    public string Base => $"http://{Host}:{BridgePort}";
    public string Health => Base + "/v1/health";
    public string Stt => Base + "/v1/stt";
    public string SttWarmup => Base + "/v1/stt/warmup";
    public string Soul => Base + "/v1/soul";
    public string LocalConnect => Base + "/v1/local/connect";
    public string LocalAsk => Base + "/v1/local/ask";
    public string LocalRemember => Base + "/v1/local/remember";
    public string LocalCommand => Base + "/v1/local/command";
    public string FleetSend => Base + "/v1/fleet/send";
    public string FleetReachable => Base + "/v1/fleet/reachable";
    public string FleetReset => Base + "/v1/fleet/reset";
    public string Piper => Base + "/v1/tts/piper";
    public string PiperVoices => Base + "/v1/tts/piper/voices";

    /// <summary>Hostname or IP. A scheme or port on the input is stripped; service ports stay the well-known ones.</summary>
    public static string HostOnly(string beast)
    {
        if (string.IsNullOrWhiteSpace(beast)) throw new ArgumentException("Beast host is empty", nameof(beast));
        var s = beast.Trim();
        if (!s.Contains("://", StringComparison.Ordinal)) s = "http://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host))
            throw new ArgumentException($"Not a host: {beast}", nameof(beast));
        return u.Host;
    }

    /// <summary>
    /// Point Ollama, Hermes and AllTalk at the Beast when the user has not
    /// set those URLs explicitly (env and appsettings already won in <c>Load</c>).
    /// </summary>
    public static void ApplyServiceDefaults(AliceVoice.Core.Config.AliceVoiceOptions options, string beastHost)
    {
        var host = HostOnly(beastHost);
        options.OllamaUrl ??= $"http://{host}:11434";
        options.HermesApiUrl ??= $"http://{host}:8643";
        options.AllTalkUrl ??= $"http://{host}:7851";
    }

    public static bool TryParseBind(string text, out string host, out int port)
    {
        host = "";
        port = DefaultBridgePort;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        var colon = s.LastIndexOf(':');
        if (colon <= 0 || colon == s.Length - 1) return false;
        if (!int.TryParse(s[(colon + 1)..], out port) || port is < 1 or > 65535) return false;
        host = s[..colon].Trim();
        if (host.Length == 0) return false;
        return true;
    }
}

using AliceVoice.Core.Config;

namespace AliceVoice.Universal.Host;

/// <summary>Paths and Beast coordinates for one running head. Built from the same appsettings + env rules as the WinUI app, plus App U-only keys.</summary>
public sealed record UniversalHost(
    PlatformHostKind Kind,
    AliceVoiceOptions Options,
    string LogDirectory,
    string AssignmentPath,
    string? BackgroundsDirectory,
    BeastServiceUrls? Remote,
    string? BridgeBindHost,
    int? BridgeBindPort,
    string? BridgeBindRefusal = null,
    bool AllowUnsafeBridgeBind = false)
{
    public bool BridgeEnabled => BridgeBindHost is not null && BridgeBindPort is not null;

    /// <summary>
    /// <paramref name="env"/> wins over the settings file, matching
    /// <see cref="AliceVoiceOptions.Load"/>. App U keys (not in the WinUI options
    /// object, so the original file schema is unchanged):
    /// <c>ALICEVOICE_BEAST_HOST</c>, <c>ALICEVOICE_BRIDGE_PORT</c>,
    /// <c>ALICEVOICE_BRIDGE_BIND</c> (host:port). The settings file may also
    /// carry <c>BeastHost</c>, <c>BridgePort</c> and <c>BridgeBind</c>.
    /// </summary>
    public static UniversalHost Prepare(PlatformHostKind kind, HostLayout layout, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var options = AliceVoiceOptions.Load(layout.SettingsFile, env);
        var extra = ReadExtra(layout.SettingsFile);

        var beast = First(env, "ALICEVOICE_BEAST_HOST", extra?.BeastHost);
        if (kind == PlatformHostKind.Android && string.IsNullOrWhiteSpace(beast))
            beast = BridgeBindPolicy.DefaultBeastHost;
        var bridgePortText = First(env, "ALICEVOICE_BRIDGE_PORT", extra?.BridgePort?.ToString());
        var bridgeBind = First(env, "ALICEVOICE_BRIDGE_BIND", extra?.BridgeBind);
        var allowUnsafe = BridgeBindPolicy.IsOverrideEnabled(First(env, BridgeBindPolicy.OverrideEnvironmentVariable, extra?.AllowUnsafeBridgeBind));

        if (kind == PlatformHostKind.Android && !string.IsNullOrWhiteSpace(beast))
            BeastServiceUrls.ApplyServiceDefaults(options, beast);

        if (kind == PlatformHostKind.Android
            && string.IsNullOrWhiteSpace(options.BackgroundsDir)
            && string.IsNullOrWhiteSpace(env("ALICEVOICE_BACKGROUNDS"))
            && !string.IsNullOrWhiteSpace(layout.BackgroundsDirectory))
        {
            options.BackgroundsDir = layout.BackgroundsDirectory;
        }

        BeastServiceUrls? remote = null;
        if (!string.IsNullOrWhiteSpace(beast))
        {
            var port = BeastServiceUrls.DefaultBridgePort;
            if (int.TryParse(bridgePortText, out var p) && p is > 0 and < 65536) port = p;
            remote = new BeastServiceUrls(BeastServiceUrls.HostOnly(beast), port);
        }

        string? bindHost = null;
        int? bindPort = null;
        string? refusal = null;
        if (kind == PlatformHostKind.Windows && bridgeBind is not null && BeastServiceUrls.TryParseBind(bridgeBind, out var bh, out var bp))
        {
            var decision = BridgeBindPolicy.Evaluate(bh, allowUnsafe);
            if (!decision.Allowed)
            {
                refusal = decision.Reason;
                Serilog.Log.Warning("{Reason}", decision.Reason);
            }
            else
            {
                if (decision.Warned) Serilog.Log.Warning("{Reason}", decision.Reason);
                bindHost = bh;
                bindPort = bp;
            }
        }

        return new UniversalHost(kind, options, layout.LogDirectory, layout.AssignmentPath, options.BackgroundsDir ?? layout.BackgroundsDirectory, remote, bindHost, bindPort, refusal, allowUnsafe);
    }

    private static string? First(Func<string, string?> env, string name, string? fromFile)
    {
        if (env(name) is { Length: > 0 } v) return v;
        return string.IsNullOrWhiteSpace(fromFile) ? null : fromFile;
    }

    private static ExtraSettings? ReadExtra(string? settingsFile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settingsFile) || !File.Exists(settingsFile)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<ExtraSettings>(File.ReadAllText(settingsFile),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not read App U keys from {File}", settingsFile);
            return null;
        }
    }

    private sealed class ExtraSettings
    {
        public string? BeastHost { get; set; }
        public int? BridgePort { get; set; }
        public string? BridgeBind { get; set; }
        public string? AllowUnsafeBridgeBind { get; set; }
    }
}

/// <summary>Directories the head already resolved (MAUI app data on Android, the usual AliceVoice folders on Windows).</summary>
public sealed record HostLayout(string? SettingsFile, string LogDirectory, string AssignmentPath, string? BackgroundsDirectory);

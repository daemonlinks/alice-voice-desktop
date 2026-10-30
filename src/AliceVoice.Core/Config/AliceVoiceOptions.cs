using System.Text.Json;
using System.Text.Json.Serialization;

namespace AliceVoice.Core.Config;

/// <summary>
/// Machine-specific settings for the app. Nothing here is hardcoded to one
/// user profile: every path has an environment-derived default, can be set in
/// <c>%LOCALAPPDATA%\AliceVoice\appsettings.json</c>, and can be overridden by
/// an <c>ALICEVOICE_*</c> environment variable (env wins).
/// </summary>
public sealed class AliceVoiceOptions
{
    /// <summary>python.exe that has faster-whisper installed (ALICEVOICE_PYTHON).</summary>
    public string? PythonPath { get; set; }

    /// <summary>Folder holding faster_whisper_stt.py / alice_local_tools.py (ALICEVOICE_SCRIPTS).</summary>
    public string? ScriptsDir { get; set; }

    /// <summary>The SOUL.md the Alice LOCAL backend uses and the SOUL editor edits (ALICEVOICE_SOUL_LOCAL).</summary>
    public string? AliceLocalSoulPath { get; set; }

    /// <summary>Read-only SOUL used as Alice LOCAL's prompt until it has its own copy (ALICEVOICE_SOUL_FALLBACK).</summary>
    public string? SoulFallbackPath { get; set; }

    /// <summary>Background image folder (ALICEVOICE_BACKGROUNDS).</summary>
    public string? BackgroundsDir { get; set; }

    /// <summary>piper.exe (ALICEVOICE_PIPER).</summary>
    public string? PiperExe { get; set; }

    /// <summary>Piper voice model folder (ALICEVOICE_PIPER_VOICES).</summary>
    public string? PiperVoicesDir { get; set; }

    public string? AllTalkUrl { get; set; }
    public string? OllamaUrl { get; set; }
    public string? HermesApiUrl { get; set; }

    /// <summary>faster-whisper model name (ALICEVOICE_STT_MODEL). Default large-v3.</summary>
    public string? SttModel { get; set; }

    /// <summary>CTranslate2 compute type on GPU (default int8_float32 — Pascal has no fast fp16).</summary>
    public string? SttComputeType { get; set; }

    /// <summary>Force a specific NVIDIA GPU index for STT (ALICEVOICE_STT_GPU). 0 is always refused.</summary>
    public int? SttGpuIndex { get; set; }

    /// <summary>Model used if no non-render GPU is free and STT falls back to CPU.</summary>
    public string? SttCpuModel { get; set; }

    public string? UpdateManifestUrl { get; set; }

    // ── Resolved values (defaults applied) ──────────────────────────────

    [JsonIgnore] public string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    [JsonIgnore] public string AppDataDir => Path.Combine(LocalAppData, "AliceVoice");
    [JsonIgnore] public string HermesHome => Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } h ? h : Path.Combine(LocalAppData, "hermes");

    [JsonIgnore] public string ResolvedAliceLocalSoulPath => AliceLocalSoulPath ?? Path.Combine(AppDataDir, "alice_local", "SOUL.md");
    [JsonIgnore] public string ResolvedSoulFallbackPath => SoulFallbackPath ?? Path.Combine(HermesHome, "profiles", "alice_voice", "SOUL.md");
    [JsonIgnore] public string ResolvedBackgroundsDir => BackgroundsDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ahc-the-beast", "voice", "Mirrored");
    [JsonIgnore] public string ResolvedPiperExe => PiperExe ?? Path.Combine(HermesHome, "hermes-agent", "venv", "Scripts", "piper.exe");
    [JsonIgnore] public string ResolvedPiperVoicesDir => PiperVoicesDir ?? Path.Combine(HermesHome, "cache", "piper-voices");
    [JsonIgnore] public string ResolvedAllTalkUrl => AllTalkUrl ?? "http://127.0.0.1:7851";
    [JsonIgnore] public string ResolvedOllamaUrl => OllamaUrl ?? "http://localhost:11434";
    [JsonIgnore] public string ResolvedHermesApiUrl => HermesApiUrl ?? "http://127.0.0.1:8643";
    [JsonIgnore] public string ResolvedSttModel => SttModel ?? "large-v3";
    [JsonIgnore] public string ResolvedSttComputeType => SttComputeType ?? "int8_float32";
    [JsonIgnore] public string ResolvedSttCpuModel => SttCpuModel ?? "base";
    [JsonIgnore] public string ResolvedUpdateManifestUrl => UpdateManifestUrl ?? "https://raw.githubusercontent.com/daemonlinks/alice-voice-desktop/main/version.json";
    [JsonIgnore] public string LogsDir => Path.Combine(AppDataDir, "logs");
    [JsonIgnore] public string CrashDir => Path.Combine(AppDataDir, "crash");

    [JsonIgnore] public static string DefaultSettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AliceVoice", "appsettings.json");

    /// <summary>Load appsettings.json (if present) then apply ALICEVOICE_* env overrides.</summary>
    public static AliceVoiceOptions Load(string? settingsFile = null, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        settingsFile ??= DefaultSettingsFile;

        var o = new AliceVoiceOptions();
        try
        {
            if (File.Exists(settingsFile))
            {
                o = JsonSerializer.Deserialize<AliceVoiceOptions>(File.ReadAllText(settingsFile),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                    ?? new AliceVoiceOptions();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not read {File}; using defaults", settingsFile);
        }

        o.PythonPath = Env(env, "ALICEVOICE_PYTHON") ?? o.PythonPath;
        o.ScriptsDir = Env(env, "ALICEVOICE_SCRIPTS") ?? o.ScriptsDir;
        o.AliceLocalSoulPath = Env(env, "ALICEVOICE_SOUL_LOCAL") ?? o.AliceLocalSoulPath;
        o.SoulFallbackPath = Env(env, "ALICEVOICE_SOUL_FALLBACK") ?? o.SoulFallbackPath;
        o.BackgroundsDir = Env(env, "ALICEVOICE_BACKGROUNDS") ?? o.BackgroundsDir;
        o.PiperExe = Env(env, "ALICEVOICE_PIPER") ?? o.PiperExe;
        o.PiperVoicesDir = Env(env, "ALICEVOICE_PIPER_VOICES") ?? o.PiperVoicesDir;
        o.AllTalkUrl = Env(env, "ALICEVOICE_ALLTALK_URL") ?? o.AllTalkUrl;
        o.OllamaUrl = Env(env, "ALICEVOICE_OLLAMA_URL") ?? o.OllamaUrl;
        o.HermesApiUrl = Env(env, "ALICEVOICE_HERMES_URL") ?? o.HermesApiUrl;
        o.SttModel = Env(env, "ALICEVOICE_STT_MODEL") ?? o.SttModel;
        o.SttComputeType = Env(env, "ALICEVOICE_STT_COMPUTE") ?? o.SttComputeType;
        o.SttCpuModel = Env(env, "ALICEVOICE_STT_CPU_MODEL") ?? o.SttCpuModel;
        o.UpdateManifestUrl = Env(env, "ALICEVOICE_UPDATE_URL") ?? o.UpdateManifestUrl;
        if (int.TryParse(Env(env, "ALICEVOICE_STT_GPU"), out var gi)) o.SttGpuIndex = gi;
        return o;
    }

    private static string? Env(Func<string, string?> env, string name)
        => env(name) is { Length: > 0 } v ? v : null;
}

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

    /// <summary>
    /// Alice LOCAL's own SOUL.md: the only SOUL Alice LOCAL reads and the only
    /// file the SOUL editor edits (ALICEVOICE_SOUL_LOCAL). Default is the
    /// machine-wide <c>%ProgramData%\AliceVoice\alice_local\SOUL.md</c> so the
    /// desktop app and the web relay (which runs as a service) share one file.
    /// Never an agent (Hermes profile) SOUL.
    /// </summary>
    public string? AliceLocalSoulPath { get; set; }

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

    // ── Wake word (openWakeWord) ─────────────────────────────────────────

    /// <summary>Folder with wakeword-models.json + ONNX files (ALICEVOICE_WAKEWORD_DIR).
    /// Default search: %ProgramData%\AliceVoice\wakeword, then models\wakeword next to the exe.</summary>
    public string? WakeWordDir { get; set; }

    /// <summary>Wake-word model id from the manifest, e.g. hey_jarvis (ALICEVOICE_WAKEWORD_MODEL). Default: manifest default.</summary>
    public string? WakeWordModel { get; set; }

    /// <summary>Detection threshold 0..1 (ALICEVOICE_WAKEWORD_THRESHOLD). Default: the model's (0.5). UI sensitivity = 1 - threshold.</summary>
    public float? WakeWordThreshold { get; set; }

    /// <summary>Consecutive 80 ms frames above threshold needed (ALICEVOICE_WAKEWORD_PATIENCE). Default: the model's manifest value (1 if unset).</summary>
    public int? WakeWordPatience { get; set; }

    /// <summary>Silence that ends the command after the wake word, ms (ALICEVOICE_WAKE_END_SILENCE_MS). Default 900.</summary>
    public int? WakeEndSilenceMs { get; set; }

    /// <summary>RMS (0..1) that counts as speech for end-of-speech detection (ALICEVOICE_WAKE_SPEECH_RMS). Default 0.015.</summary>
    public float? WakeSpeechRms { get; set; }

    /// <summary>Give up if nothing is said this long after the wake word, ms (ALICEVOICE_WAKE_NO_SPEECH_MS). Default 5000.</summary>
    public int? WakeNoSpeechTimeoutMs { get; set; }

    /// <summary>Longest command after the wake word, seconds (ALICEVOICE_WAKE_MAX_SECONDS). Default 30.</summary>
    public int? WakeMaxUtteranceSeconds { get; set; }

    // ── Resolved values (defaults applied) ──────────────────────────────

    [JsonIgnore] public string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    [JsonIgnore] public string AppDataDir => Path.Combine(LocalAppData, "AliceVoice");
    /// <summary>Machine-wide Alice Voice data shared with the web relay (%ProgramData%\AliceVoice).</summary>
    [JsonIgnore] public string SharedDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AliceVoice");
    [JsonIgnore] public string HermesHome => Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } h ? h : Path.Combine(LocalAppData, "hermes");

    [JsonIgnore] public string ResolvedAliceLocalSoulPath => AliceLocalSoulPath ?? Path.Combine(SharedDataDir, "alice_local", "SOUL.md");
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
    [JsonIgnore] public int ResolvedWakeWordPatience => Math.Max(1, WakeWordPatience ?? 1);
    [JsonIgnore] public int ResolvedWakeEndSilenceMs => Math.Clamp(WakeEndSilenceMs ?? 900, 200, 5000);
    [JsonIgnore] public float ResolvedWakeSpeechRms => Math.Clamp(WakeSpeechRms ?? 0.015f, 0.001f, 0.5f);
    [JsonIgnore] public int ResolvedWakeNoSpeechTimeoutMs => Math.Clamp(WakeNoSpeechTimeoutMs ?? 5000, 1000, 30000);
    [JsonIgnore] public int ResolvedWakeMaxUtteranceSeconds => Math.Clamp(WakeMaxUtteranceSeconds ?? 30, 3, 120);
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
        o.WakeWordDir = Env(env, "ALICEVOICE_WAKEWORD_DIR") ?? o.WakeWordDir;
        o.WakeWordModel = Env(env, "ALICEVOICE_WAKEWORD_MODEL") ?? o.WakeWordModel;
        if (float.TryParse(Env(env, "ALICEVOICE_WAKEWORD_THRESHOLD"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wt)) o.WakeWordThreshold = wt;
        if (int.TryParse(Env(env, "ALICEVOICE_WAKEWORD_PATIENCE"), out var wp)) o.WakeWordPatience = wp;
        if (int.TryParse(Env(env, "ALICEVOICE_WAKE_END_SILENCE_MS"), out var ws)) o.WakeEndSilenceMs = ws;
        if (float.TryParse(Env(env, "ALICEVOICE_WAKE_SPEECH_RMS"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wr)) o.WakeSpeechRms = wr;
        if (int.TryParse(Env(env, "ALICEVOICE_WAKE_NO_SPEECH_MS"), out var wn)) o.WakeNoSpeechTimeoutMs = wn;
        if (int.TryParse(Env(env, "ALICEVOICE_WAKE_MAX_SECONDS"), out var wm)) o.WakeMaxUtteranceSeconds = wm;
        return o;
    }

    private static string? Env(Func<string, string?> env, string name)
        => env(name) is { Length: > 0 } v ? v : null;
}

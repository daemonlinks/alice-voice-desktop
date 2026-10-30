using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AliceVoice.Core.Config;
using AliceVoice.Core.Gpu;
using Serilog;

namespace AliceVoice.Core.Stt;

/// <summary>
/// One long-lived, warm faster-whisper process (scripts/faster_whisper_stt.py
/// --server). Requests are serialised; the process is restarted automatically
/// if it dies or times out. The process is pinned to the GPU chosen by
/// <see cref="IGpuSelector"/> through CUDA_VISIBLE_DEVICES, so it can never
/// touch the render GPU 0; with no usable voice GPU it runs on the CPU.
/// </summary>
public sealed class FasterWhisperSttClient : ISttClient
{
    private const string ScriptName = "faster_whisper_stt.py";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);

    private readonly AliceVoiceOptions _options;
    private readonly IGpuSelector _gpu;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _proc;
    private string _status = "STT not started";

    public FasterWhisperSttClient(AliceVoiceOptions options, IGpuSelector gpu)
    {
        _options = options;
        _gpu = gpu;
    }

    public bool IsReady => _proc is { HasExited: false };
    public string Status => _status;

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await EnsureStartedAsync(ct); }
        finally { _gate.Release(); }
    }

    public async Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct = default)
    {
        if (pcm16k.Length == 0) return new SttResult("", 0);

        await _gate.WaitAsync(ct);
        try
        {
            var proc = await EnsureStartedAsync(ct);
            var bytes = new byte[pcm16k.Length * 2];
            Buffer.BlockCopy(pcm16k, 0, bytes, 0, bytes.Length);
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                var stdin = proc.StandardInput.BaseStream;
                await stdin.WriteAsync(header, timeout.Token);
                await stdin.WriteAsync(bytes, timeout.Token);
                await stdin.FlushAsync(timeout.Token);

                var line = await proc.StandardOutput.ReadLineAsync(timeout.Token)
                           ?? throw new SttException("STT process exited during transcription");
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err))
                    throw new SttException($"faster-whisper error: {err.GetString()}");
                var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                var ms = root.TryGetProperty("ms", out var m) ? m.GetInt64() : 0;
                // Privacy: log size/timing only, never the transcript.
                Log.Information("STT {Seconds:F1}s audio -> {Chars} chars in {Ms} ms", pcm16k.Length / 16000.0, text.Length, ms);
                return new SttResult(text, ms);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonException or SttException)
            {
                // The process state is unknown after a failed/cancelled request
                // (it may still emit a late response) — restart it next time.
                KillProcess();
                if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
                if (ex is SttException) throw;
                throw new SttException(ex is OperationCanceledException ? "STT timed out" : $"STT failed: {ex.Message}", ex);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Must be called with <see cref="_gate"/> held.</summary>
    private async Task<Process> EnsureStartedAsync(CancellationToken ct)
    {
        if (_proc is { HasExited: false } running) return running;
        KillProcess();

        var python = RuntimeLocator.FindPython(_options)
            ?? throw new SttException("No Python interpreter found. Set ALICEVOICE_PYTHON to a python.exe with faster-whisper installed.");
        var script = RuntimeLocator.FindScript(ScriptName, _options.ScriptsDir)
            ?? throw new SttException($"{ScriptName} not found next to the app or in any parent scripts\\ folder. Set ALICEVOICE_SCRIPTS.");

        var gpu = _gpu.PreferredInferenceGpu;
        var psi = new ProcessStartInfo(python)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("--server");
        psi.ArgumentList.Add("--lang"); psi.ArgumentList.Add("en");
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        // Map CUDA ordinals to nvidia-smi (PCI bus) order, then expose ONLY the
        // chosen GPU so the render GPU 0 is invisible to this process.
        psi.Environment["CUDA_DEVICE_ORDER"] = "PCI_BUS_ID";
        if (gpu is not null)
        {
            psi.Environment["CUDA_VISIBLE_DEVICES"] = gpu.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            psi.ArgumentList.Add("--device"); psi.ArgumentList.Add("cuda");
            psi.ArgumentList.Add("--device_index"); psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(_options.ResolvedSttModel);
            psi.ArgumentList.Add("--compute"); psi.ArgumentList.Add(_options.ResolvedSttComputeType);
        }
        else
        {
            psi.Environment["CUDA_VISIBLE_DEVICES"] = "";
            psi.ArgumentList.Add("--device"); psi.ArgumentList.Add("cpu");
            psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(_options.ResolvedSttCpuModel);
            psi.ArgumentList.Add("--compute"); psi.ArgumentList.Add("int8");
        }

        var where = gpu is null ? "CPU" : $"GPU {gpu.Index}";
        var model = gpu is null ? _options.ResolvedSttCpuModel : _options.ResolvedSttModel;
        _status = $"faster-whisper {model} · loading on {where}";
        Log.Information("Starting STT server: {Python} {Script} ({Where}, model {Model})", python, script, where, model);

        Process proc;
        try { proc = Process.Start(psi) ?? throw new SttException("Failed to start Python"); }
        catch (System.ComponentModel.Win32Exception ex) { throw new SttException($"Failed to start {python}: {ex.Message}", ex); }

        var stderrTail = new StringBuilder();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderrTail)
            {
                stderrTail.AppendLine(e.Data);
                if (stderrTail.Length > 4000) stderrTail.Remove(0, stderrTail.Length - 4000);
            }
            Log.Debug("stt: {Line}", e.Data);
        };
        proc.BeginErrorReadLine();
        _proc = proc;

        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startup.CancelAfter(StartupTimeout);
        try
        {
            var ready = await proc.StandardOutput.ReadLineAsync(startup.Token);
            if (ready is null || !ready.Contains("\"ready\""))
            {
                string tail;
                lock (stderrTail) tail = stderrTail.ToString();
                var last = tail.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
                throw new SttException($"STT server failed to start: {(last.Length > 0 ? last : "no output")}");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            KillProcess();
            throw new SttException("STT server did not become ready within 3 minutes");
        }
        catch
        {
            KillProcess();
            throw;
        }

        _status = $"faster-whisper {model} · {where}";
        Log.Information("STT server ready ({Status})", _status);
        return proc;
    }

    private void KillProcess()
    {
        var p = _proc;
        _proc = null;
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                try { p.StandardInput.Close(); } catch { }
                if (!p.WaitForExit(1500)) p.Kill(true);
            }
        }
        catch { }
        finally { p.Dispose(); }
    }

    public ValueTask DisposeAsync()
    {
        KillProcess();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

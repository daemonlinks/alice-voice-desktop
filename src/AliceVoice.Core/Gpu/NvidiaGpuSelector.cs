using System.Diagnostics;
using System.Globalization;
using Serilog;

namespace AliceVoice.Core.Gpu;

/// <summary>
/// Selects the inference GPU via nvidia-smi. GPU 0 (render farm) is never
/// returned. The parsing and selection rules are static so they are unit
/// tested directly (no duplicated logic in tests).
/// </summary>
public sealed class NvidiaGpuSelector : IGpuSelector
{
    /// <summary>GPU index reserved for the render farm. Never used for voice.</summary>
    public const int ReservedRenderGpu = 0;

    private readonly int? _forcedIndex;

    public NvidiaGpuSelector() : this(null) { }

    public NvidiaGpuSelector(int? forcedIndex) => _forcedIndex = forcedIndex;

    public GpuInfo? PreferredInferenceGpu
    {
        get
        {
            var gpus = Enumerate();
            var chosen = Select(gpus, _forcedIndex);
            if (chosen is null)
                Log.Warning("No usable non-render GPU (found {Count}); voice inference will use the CPU", gpus.Count);
            else
                Log.Information("Inference GPU {Index} ({Name}, {Free} MiB free)", chosen.Index, chosen.Name, chosen.FreeMemoryMiB);
            return chosen;
        }
    }

    /// <summary>
    /// Selection rule: never <see cref="ReservedRenderGpu"/>. A forced index is
    /// honoured if it exists and is not 0. Otherwise the usable GPU with the
    /// most free VRAM among indices ≠ 0. Null if none qualifies.
    /// </summary>
    public static GpuInfo? Select(IEnumerable<GpuInfo> gpus, int? forcedIndex = null)
    {
        var candidates = gpus.Where(g => g.Index != ReservedRenderGpu).ToList();
        if (forcedIndex is { } f)
        {
            if (f == ReservedRenderGpu)
                Log.Warning("Configured STT GPU 0 refused: GPU 0 is reserved for the render farm");
            else if (candidates.FirstOrDefault(g => g.Index == f) is { } forced)
                return forced;
        }
        return candidates
            .Where(g => g.IsUsable)
            .OrderByDescending(g => g.FreeMemoryMiB)
            .ThenBy(g => g.Index)
            .FirstOrDefault();
    }

    /// <summary>Parse <c>--query-gpu=index,name,memory.total,memory.used,memory.free --format=csv,noheader,nounits</c>.</summary>
    public static IReadOnlyList<GpuInfo> Parse(string csv)
    {
        var list = new List<GpuInfo>();
        foreach (var raw in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Trim().Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length < 5) continue;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)) continue;
            long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total);
            long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var used);
            long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var free);
            list.Add(new GpuInfo(idx, parts[1], total, used, free));
        }
        return list;
    }

    public IReadOnlyList<GpuInfo> Enumerate()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi",
                "--query-gpu=index,name,memory.total,memory.used,memory.free --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return Array.Empty<GpuInfo>();

            // Read asynchronously so a hung nvidia-smi can't block us forever.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(true); } catch { }
                Log.Warning("nvidia-smi timed out");
                return Array.Empty<GpuInfo>();
            }
            return Parse(stdout.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "nvidia-smi enumeration failed");
            return Array.Empty<GpuInfo>();
        }
    }
}

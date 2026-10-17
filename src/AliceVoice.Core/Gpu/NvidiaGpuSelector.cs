using System.Diagnostics;
using System.Text.RegularExpressions;
using Serilog;

namespace AliceVoice.Core.Gpu;

/// <summary>
/// Reads GPU state from nvidia-smi. Falls back to a sensible default if the
/// tool is unavailable. The inference GPU is chosen as the one with the most
/// free VRAM that is NOT the farm render GPU (index 0).
/// </summary>
public sealed class NvidiaGpuSelector : IGpuSelector
{
    private const string NvidiaSmi = "nvidia-smi";

    public GpuInfo PreferredInferenceGpu
    {
        get
        {
            var gpus = Enumerate();
            if (gpus.Count == 0)
                return new GpuInfo(0, "Unknown", 0, 0, 0);

            // Prefer the GPU with the most free VRAM, skipping index 0 (farm render).
            return gpus
                .Where(g => g.Index != 0)
                .OrderByDescending(g => g.FreeMemoryMiB)
                .FirstOrDefault(g => g.IsUsable)
                ?? gpus.OrderByDescending(g => g.FreeMemoryMiB).First();
        }
    }

    public IReadOnlyList<GpuInfo> Enumerate()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = NvidiaSmi,
                Arguments = "--query-gpu=index,name,memory.total,memory.used,memory.free --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return Array.Empty<GpuInfo>();

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            var result = new List<GpuInfo>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length < 5) continue;

                if (int.TryParse(parts[0], out var idx) &&
                    long.TryParse(parts[2], out var total) &&
                    long.TryParse(parts[3], out var used) &&
                    long.TryParse(parts[4], out var free))
                {
                    result.Add(new GpuInfo(idx, parts[1], total, used, free));
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "nvidia-smi unavailable; GPU selection degraded");
            return Array.Empty<GpuInfo>();
        }
    }
}

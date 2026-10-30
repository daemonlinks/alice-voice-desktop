namespace AliceVoice.Core.Gpu;

/// <summary>A physical NVIDIA GPU as reported by nvidia-smi (PCI bus order).</summary>
public sealed record GpuInfo(
    int Index,
    string Name,
    long TotalMemoryMiB,
    long UsedMemoryMiB,
    long FreeMemoryMiB)
{
    /// <summary>Enough free VRAM for faster-whisper large-v3 (int8) plus headroom.</summary>
    public bool IsUsable => FreeMemoryMiB > 2048;
}

/// <summary>
/// Chooses which GPU runs voice inference. GPU 0 is the render-farm GPU and is
/// NEVER selected — if no other GPU is usable the answer is "none" (callers
/// fall back to CPU) rather than GPU 0.
/// </summary>
public interface IGpuSelector
{
    /// <summary>The GPU voice inference should use, or null = use the CPU.</summary>
    GpuInfo? PreferredInferenceGpu { get; }

    /// <summary>All detected GPUs.</summary>
    IReadOnlyList<GpuInfo> Enumerate();
}

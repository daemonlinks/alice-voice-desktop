namespace AliceVoice.Core.Gpu;

/// <summary>
/// Describes a GPU available for inference.
/// </summary>
public sealed record GpuInfo(
    int Index,
    string Name,
    long TotalMemoryMiB,
    long UsedMemoryMiB,
    long FreeMemoryMiB)
{
    public bool IsUsable => FreeMemoryMiB > 2048;
}

/// <summary>
/// Selects which GPU inference workloads use. The farm render GPU (index 0)
/// is kept for rendering; voice inference is pinned to the free GPU (index 1).
/// </summary>
public interface IGpuSelector
{
    /// <summary>The GPU voice inference should run on.</summary>
    GpuInfo PreferredInferenceGpu { get; }

    /// <summary>Enumerate all GPUs on the machine.</summary>
    IReadOnlyList<GpuInfo> Enumerate();
}

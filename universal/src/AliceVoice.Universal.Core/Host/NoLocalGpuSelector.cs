using AliceVoice.Core.Gpu;

namespace AliceVoice.Universal.Host;

/// <summary>
/// Phone (and any host with no NVIDIA voice GPU). Never reports GPU 0 — there
/// is no local GPU at all, so speech-to-text must run on the Beast.
/// </summary>
public sealed class NoLocalGpuSelector : IGpuSelector
{
    public GpuInfo? PreferredInferenceGpu => null;
    public IReadOnlyList<GpuInfo> Enumerate() => Array.Empty<GpuInfo>();
}

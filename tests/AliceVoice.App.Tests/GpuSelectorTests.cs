using Xunit;
using AliceVoice.Core.Gpu;

namespace AliceVoice.App.Tests;

public class GpuSelectorTests
{
    [Fact]
    public void PreferredInferenceGpu_SkipsIndexZero_WhenFreeGpuExists()
    {
        var selector = new FakeGpuSelector(new[]
        {
            new GpuInfo(0, "Render", 11264, 9449, 1659),   // farm render, nearly full
            new GpuInfo(1, "Inference", 11264, 2053, 9055) // free
        });

        var gpu = selector.PreferredInferenceGpu;

        Assert.Equal(1, gpu.Index);
        Assert.True(gpu.IsUsable);
    }

    [Fact]
    public void PreferredInferenceGpu_FallsBack_WhenOnlyIndexZeroUsable()
    {
        var selector = new FakeGpuSelector(new[]
        {
            new GpuInfo(0, "Render", 11264, 2000, 9000),
            new GpuInfo(1, "Inference", 11264, 11000, 0) // full, unusable
        });

        var gpu = selector.PreferredInferenceGpu;

        Assert.Equal(0, gpu.Index);
    }

    private sealed class FakeGpuSelector : IGpuSelector
    {
        private readonly IReadOnlyList<GpuInfo> _gpus;
        public FakeGpuSelector(IReadOnlyList<GpuInfo> gpus) => _gpus = gpus;
        public GpuInfo PreferredInferenceGpu =>
            _gpus.Where(g => g.Index != 0)
                 .OrderByDescending(g => g.FreeMemoryMiB)
                 .FirstOrDefault(g => g.IsUsable)
            ?? _gpus.OrderByDescending(g => g.FreeMemoryMiB).First();
        public IReadOnlyList<GpuInfo> Enumerate() => _gpus;
    }
}

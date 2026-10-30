using Xunit;
using AliceVoice.Core.Gpu;

namespace AliceVoice.App.Tests;

/// <summary>Tests the REAL selection/parsing logic in NvidiaGpuSelector.</summary>
public class GpuSelectorTests
{
    [Fact]
    public void Select_SkipsIndexZero_WhenFreeGpuExists()
    {
        var gpu = NvidiaGpuSelector.Select(new[]
        {
            new GpuInfo(0, "Render", 11264, 9449, 1659),
            new GpuInfo(1, "Inference", 11264, 2053, 9055)
        });

        Assert.NotNull(gpu);
        Assert.Equal(1, gpu!.Index);
    }

    [Fact]
    public void Select_NeverReturnsGpuZero_EvenIfItIsTheOnlyUsableOne()
    {
        var gpu = NvidiaGpuSelector.Select(new[]
        {
            new GpuInfo(0, "Render", 11264, 2000, 9000),
            new GpuInfo(1, "Inference", 11264, 11000, 0) // full
        });

        Assert.Null(gpu); // caller falls back to CPU, never GPU 0
    }

    [Fact]
    public void Select_NeverReturnsGpuZero_EvenWhenForced()
    {
        var gpus = new[]
        {
            new GpuInfo(0, "Render", 11264, 0, 11264),
            new GpuInfo(1, "Inference", 11264, 2000, 9000)
        };
        Assert.Equal(1, NvidiaGpuSelector.Select(gpus, forcedIndex: 0)!.Index);
        Assert.Null(NvidiaGpuSelector.Select(new[] { gpus[0] }, forcedIndex: 0));
    }

    [Fact]
    public void Select_HonoursForcedNonZeroIndex()
    {
        var gpus = new[]
        {
            new GpuInfo(0, "Render", 11264, 0, 11264),
            new GpuInfo(1, "A", 11264, 0, 11000),
            new GpuInfo(2, "B", 11264, 8000, 3000)
        };
        Assert.Equal(2, NvidiaGpuSelector.Select(gpus, forcedIndex: 2)!.Index);
        Assert.Equal(1, NvidiaGpuSelector.Select(gpus)!.Index); // most free, non-zero
    }

    [Fact]
    public void Select_NoGpus_ReturnsNull()
        => Assert.Null(NvidiaGpuSelector.Select(Array.Empty<GpuInfo>()));

    [Fact]
    public void Parse_ReadsNvidiaSmiCsv()
    {
        var gpus = NvidiaGpuSelector.Parse(
            "0, NVIDIA GeForce GTX 1080 Ti, 11264, 9449, 1659\r\n1, NVIDIA GeForce GTX 1080 Ti, 11264, 2053, 9055\r\n\r\ngarbage\n");

        Assert.Equal(2, gpus.Count);
        Assert.Equal(1, gpus[1].Index);
        Assert.Equal("NVIDIA GeForce GTX 1080 Ti", gpus[1].Name);
        Assert.Equal(9055, gpus[1].FreeMemoryMiB);
        Assert.Equal(1, NvidiaGpuSelector.Select(gpus)!.Index);
    }
}

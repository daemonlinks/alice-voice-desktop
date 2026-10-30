using Xunit;
using AliceVoice.Core.Config;

namespace AliceVoice.App.Tests;

public class RuntimeLocatorTests
{
    [Fact]
    public void FindScript_PrefersExeDirScripts_ThenWalksUp()
    {
        var root = Path.Combine(Path.GetTempPath(), "av-test-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "src", "App", "bin", "x64", "Debug");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        try
        {
            File.WriteAllText(Path.Combine(root, "scripts", "x.py"), "");
            // Only the repo copy exists → found by walking up.
            Assert.Equal(Path.Combine(root, "scripts", "x.py"), RuntimeLocator.FindScript("x.py", baseDir: bin));

            // A copy next to the exe wins.
            Directory.CreateDirectory(Path.Combine(bin, "scripts"));
            File.WriteAllText(Path.Combine(bin, "scripts", "x.py"), "");
            Assert.Equal(Path.Combine(bin, "scripts", "x.py"), RuntimeLocator.FindScript("x.py", baseDir: bin));

            Assert.Null(RuntimeLocator.FindScript("missing.py", baseDir: bin));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FindPython_RejectsStoreStub_AndPrefersConfigured()
    {
        var stub = @"C:\Users\x\AppData\Local\Microsoft\WindowsApps\python.exe";
        Assert.True(RuntimeLocator.IsStoreStub(stub));
        Assert.False(RuntimeLocator.IsStoreStub(@"C:\Python311\python.exe"));

        var opts = new AliceVoiceOptions { PythonPath = stub };
        // The stub "exists" but must be skipped.
        var found = RuntimeLocator.FindPython(opts, p => p == stub);
        Assert.Null(found);

        var good = @"D:\py\python.exe";
        opts.PythonPath = good;
        Assert.Equal(good, RuntimeLocator.FindPython(opts, p => p == good || p == stub));
    }

    [Fact]
    public void Options_EnvOverridesSettingsFile()
    {
        var file = Path.Combine(Path.GetTempPath(), "av-settings-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, "{ \"PythonPath\": \"from-file\", \"SttModel\": \"base\", }");
        try
        {
            var env = new Dictionary<string, string> { ["ALICEVOICE_PYTHON"] = "from-env", ["ALICEVOICE_STT_GPU"] = "1" };
            var o = AliceVoiceOptions.Load(file, k => env.TryGetValue(k, out var v) ? v : null);
            Assert.Equal("from-env", o.PythonPath);
            Assert.Equal("base", o.ResolvedSttModel);
            Assert.Equal(1, o.SttGpuIndex);
            Assert.EndsWith(Path.Combine("AliceVoice", "alice_local", "SOUL.md"), o.ResolvedAliceLocalSoulPath);
        }
        finally { File.Delete(file); }
    }
}

using AliceVoice.Universal.Host;
using Xunit;

namespace AliceVoice.Universal.Core.Tests;

public class LauncherTests
{
    [Fact]
    public void ShortcutsLaunchTheExeWithItsOwnIconAndNoScript()
    {
        var install = Path.Combine(Path.GetTempPath(), "AliceVoice.AppU");
        var exe = Path.Combine(install, "AliceVoice.Universal.App.exe");
        var desktop = Path.Combine(Path.GetTempPath(), "Desktop");
        var startMenu = Path.Combine(Path.GetTempPath(), "Start Menu", "Programs");
        var specs = AppLaunch.Shortcuts(exe, desktop, startMenu);

        Assert.Equal(2, specs.Count);
        Assert.Equal(Path.Combine(desktop, AppLaunch.ShortcutFileName), specs[0].LinkPath);
        Assert.Equal(Path.Combine(startMenu, AppLaunch.ShortcutFileName), specs[1].LinkPath);
        Assert.All(specs, spec =>
        {
            Assert.Equal(Path.GetFullPath(exe), spec.TargetPath);
            Assert.EndsWith("AliceVoice.Universal.App.exe", spec.TargetPath);
            Assert.Equal("", spec.Arguments);
            Assert.Equal(spec.TargetPath + ",0", spec.IconLocation);
            Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(exe)), spec.WorkingDirectory);
            Assert.Equal(AppLaunch.DisplayName, spec.Description);
            Assert.DoesNotContain(".ps1", spec.TargetPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".cmd", spec.TargetPath, StringComparison.OrdinalIgnoreCase);
            Assert.False(spec.LinkPath.Contains("TaskBar", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void InstallRequestDoesNotOpenASecondWindowAndARunningAppIsReused()
    {
        Assert.Equal(LaunchChoice.InstallShortcuts, AppLaunch.Choose(["AliceVoice.Universal.App.exe", "--install-shortcuts"], anotherInstanceRunning: true));
        Assert.Equal(LaunchChoice.ActivateExisting, AppLaunch.Choose(["AliceVoice.Universal.App.exe"], anotherInstanceRunning: true));
        Assert.Equal(LaunchChoice.Run, AppLaunch.Choose(["AliceVoice.Universal.App.exe"], anotherInstanceRunning: false));
        Assert.Equal(AppLaunch.WindowTitle, "Alice Voice App U");
    }

    [Fact]
    public void BeastBindIsSavedOnlyWhenMissingAndTailscale()
    {
        const string beast = "100.64.237.3:8765";
        Assert.Equal(beast, LauncherBridgeConfig.BindToSave(existingBind: null, environmentBind: beast));
        Assert.Null(LauncherBridgeConfig.BindToSave(existingBind: beast, environmentBind: "100.64.1.2:8765"));
        Assert.Null(LauncherBridgeConfig.BindToSave(existingBind: null, environmentBind: null));
        Assert.Null(LauncherBridgeConfig.BindToSave(existingBind: null, environmentBind: "192.168.1.20:8765"));
        Assert.Null(LauncherBridgeConfig.BindToSave(existingBind: null, environmentBind: "0.0.0.0:8765"));
        Assert.Null(LauncherBridgeConfig.BindToSave(existingBind: null, environmentBind: "127.0.0.1:8765"));
    }

    [Fact]
    public void SettingsKeepAnExistingBindAndOtherKeys()
    {
        var existing = """
            {
              "OllamaUrl": "http://localhost:11434",
              "bridgeBind": "100.64.237.3:8765"
            }
            """;
        Assert.Equal("100.64.237.3:8765", LauncherBridgeConfig.ReadBind(existing));
        Assert.Null(LauncherBridgeConfig.JsonWithBind(existing, "100.64.1.2:8765"));

        var added = LauncherBridgeConfig.JsonWithBind("""{ "OllamaUrl": "http://localhost:11434" }""", "100.64.237.3:8765");
        Assert.NotNull(added);
        Assert.Contains("http://localhost:11434", added);
        Assert.Contains("100.64.237.3:8765", added);
        Assert.Null(LauncherBridgeConfig.JsonWithBind("{ not json", "100.64.237.3:8765"));
    }

    [Fact]
    public void ListenAddressIsNotPrefilledForEveryInstall()
    {
        var src = Path.Combine(RepoRoot(), "universal", "src");
        var hits = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("\"BridgeBind\": \"100.64.237.3:8765\"", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(hits);
    }

    [Fact]
    public void PackageUsesTheSharedMarkAndLauncherEntries()
    {
        var root = RepoRoot();
        var app = Path.Combine(root, "universal", "src", "AliceVoice.Universal.App");
        var project = File.ReadAllText(Path.Combine(app, "AliceVoice.Universal.App.csproj"));
        Assert.Contains("alice-voice-master.png", project);
        Assert.Contains("alice-voice.ico", project);
        Assert.Contains("WinExe", project);
        Assert.DoesNotContain("appicon.svg", project);

        var manifest = File.ReadAllText(Path.Combine(app, "Platforms", "Android", "AndroidManifest.xml"));
        Assert.Contains("android:icon=\"@mipmap/appicon\"", manifest);
        Assert.Contains("android:roundIcon=\"@mipmap/appicon_round\"", manifest);
        Assert.Contains("android:banner=\"@drawable/tv_banner\"", manifest);
        Assert.Contains("android:label=\"Alice Voice\"", manifest);
        Assert.Contains("android.software.leanback", manifest);
        Assert.Contains("android.hardware.touchscreen\" android:required=\"false\"", manifest);

        var activity = File.ReadAllText(Path.Combine(app, "Platforms", "Android", "MainActivity.cs"));
        Assert.Contains("CategoryLeanbackLauncher", activity);
        Assert.Contains("LaunchMode.SingleTop", activity);

        var banner = File.ReadAllText(Path.Combine(app, "Platforms", "Android", "Resources", "drawable", "tv_banner.xml"));
        Assert.Contains("@mipmap/appicon", banner);

        Assert.True(File.Exists(Path.Combine(root, "assets", "alice-voice-master.png")));
        Assert.True(File.Exists(Path.Combine(root, "assets", "alice-voice.ico")));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "universal", "src", "AliceVoice.Universal.App", "AliceVoice.Universal.App.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Repo root was not found above the test output.");
    }
}

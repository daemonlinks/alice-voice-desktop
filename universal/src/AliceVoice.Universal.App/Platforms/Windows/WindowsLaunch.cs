using System.Reflection;
using System.Runtime.InteropServices;
using AliceVoice.Core.Config;
using AliceVoice.Universal.Host;

namespace AliceVoice.AppU.WinUI;

/// <summary>
/// Desktop and Start menu shortcuts for the unpackaged exe, plus a single-instance gate.
/// Shortcuts point at the exe. They do not pin the taskbar and they do not open a script.
/// </summary>
internal static class WindowsLaunch
{
    public static LaunchChoice Choose(bool anotherInstanceRunning)
        => AppLaunch.Choose(Environment.GetCommandLineArgs(), anotherInstanceRunning);

    public static void InstallShortcuts()
    {
        var exe = Environment.ProcessPath ?? Environment.GetCommandLineArgs().FirstOrDefault();
        if (string.IsNullOrWhiteSpace(exe))
            throw new InvalidOperationException("App U could not find its own executable path.");

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs");
        var settings = AliceVoiceOptions.DefaultSettingsFile;
        var existing = File.Exists(settings) ? File.ReadAllText(settings) : null;
        var specs = AppLaunch.Shortcuts(exe, desktop, startMenu);
        foreach (var spec in specs)
        {
            var folder = Path.GetDirectoryName(spec.LinkPath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            WindowsShortcutWriter.Write(spec);
        }

        var bind = LauncherBridgeConfig.BindToSave(LauncherBridgeConfig.ReadBind(existing), Environment.GetEnvironmentVariable("ALICEVOICE_BRIDGE_BIND"));
        if (bind is null) return;
        var updated = LauncherBridgeConfig.JsonWithBind(existing, bind);
        if (updated is null) return;
        var settingsDir = Path.GetDirectoryName(settings);
        if (!string.IsNullOrEmpty(settingsDir)) Directory.CreateDirectory(settingsDir);
        File.WriteAllText(settings, updated);
    }
}

internal static class WindowsSingleInstance
{
    private static Mutex? _mutex;

    public static bool TryBecomePrimary()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: false, AppLaunch.SingleInstanceName);
            try
            {
                if (_mutex.WaitOne(TimeSpan.Zero)) return true;
            }
            catch (AbandonedMutexException)
            {
                // The previous process crashed while holding the mutex. This launch owns it now.
                return true;
            }
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        catch
        {
            // A mutex the session cannot create must not stop the only launch.
            return true;
        }
    }
}

internal static class WindowsWindow
{
    private const int Restore = 9;

    public static void TryActivate(string title)
    {
        try
        {
            var handle = FindWindow(null, title);
            if (handle == IntPtr.Zero) return;
            ShowWindow(handle, Restore);
            SetForegroundWindow(handle);
        }
        catch
        {
            // The second process still exits. The first one keeps the bridge.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);
}

internal static class WindowsShortcutWriter
{
    public static void Write(ShortcutSpec spec)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is not available.");
        var shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("WScript.Shell did not start.");
        try
        {
            var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { spec.LinkPath });
            var type = shortcut?.GetType() ?? throw new InvalidOperationException("CreateShortcut returned nothing.");
            Set(type, shortcut, "TargetPath", spec.TargetPath);
            Set(type, shortcut, "WorkingDirectory", spec.WorkingDirectory);
            Set(type, shortcut, "Arguments", spec.Arguments);
            Set(type, shortcut, "IconLocation", spec.IconLocation);
            Set(type, shortcut, "Description", spec.Description);
            Set(type, shortcut, "WindowStyle", 1);
            type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, Array.Empty<object>());
        }
        finally
        {
            if (shell is IDisposable disposable) disposable.Dispose();
            else if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void Set(Type type, object? target, string name, object? value)
        => type.InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });
}

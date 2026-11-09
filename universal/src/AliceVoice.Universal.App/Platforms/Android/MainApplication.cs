using AliceVoice.Universal.Host;
using Android.App;
using Android.Runtime;

namespace AliceVoice.AppU;

[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership)
    {
        try
        {
            CrashDiagnostics.Install();
            AndroidEnvironment.UnhandledExceptionRaiser += OnAndroidUnhandled;
        }
        catch
        {
            // A failure while arming diagnostics must not block construction.
        }
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    private static void OnAndroidUnhandled(object? sender, RaiseThrowableEventArgs e)
    {
        Exception? exception = null;
        try { exception = e.Exception; } catch { /* still record the signal */ }
        CrashDiagnostics.Report("android", exception, "AndroidEnvironment.UnhandledExceptionRaiser");
        // Same rule as WinUI: do not mark the exception handled.
        try { e.Handled = CrashDiagnostics.DecideUiException().MarkHandled; }
        catch { /* the default leaves the process on its normal failure path */ }
    }
}

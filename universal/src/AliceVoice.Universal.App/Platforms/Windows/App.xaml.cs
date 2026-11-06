using AliceVoice.Universal.Host;
using Microsoft.UI.Xaml;

namespace AliceVoice.AppU.WinUI;

/// <summary>
/// WinUI application entry. UI-thread exceptions are stowed by Microsoft.UI.Xaml
/// and then terminate the process (0xc000027b). They do not reach
/// <see cref="AppDomain.UnhandledException"/> and do not write a .NET Runtime
/// event. This handler records them first. It does not mark them handled, so
/// the process still terminates.
/// </summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        if (WindowsLaunch.Choose(anotherInstanceRunning: false) == LaunchChoice.InstallShortcuts)
        {
            try { WindowsLaunch.InstallShortcuts(); }
            catch (Exception ex)
            {
                try { CrashDiagnostics.Install(); CrashDiagnostics.Report("startup", ex, "WindowsLaunch.InstallShortcuts", fatal: false); }
                catch { /* the process is exiting either way */ }
                Environment.Exit(1);
            }
            Environment.Exit(0);
        }

        if (!WindowsSingleInstance.TryBecomePrimary())
        {
            WindowsWindow.TryActivate(AppLaunch.WindowTitle);
            Environment.Exit(0);
        }

        try
        {
            CrashDiagnostics.Install();
            UnhandledException += OnWinUiUnhandled;
        }
        catch
        {
            // A failure while arming diagnostics must not block construction.
        }

        this.InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    private static void OnWinUiUnhandled(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Exception? exception = null;
        string? message = null;
        try { exception = e.Exception; } catch { /* still write whatever we have */ }
        try { message = e.Message; } catch { /* the exception object is the record */ }
        CrashDiagnostics.Report(CrashDiagnostics.UiSource, exception, CrashDiagnostics.UiContext(message));
        try { e.Handled = CrashDiagnostics.DecideUiException().MarkHandled; }
        catch { /* leaving the default (false) still terminates */ }
    }
}

using System.Runtime.CompilerServices;
using AliceVoice.Universal.Host;
using AliceVoice.Universal.Voice;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Handlers;

namespace AliceVoice.AppU;

/// <summary>
/// Enables highlight and copy on <see cref="SelectableLabel"/> only.
/// Windows: drag to highlight, Ctrl+C copies the highlight, and the context
/// menu copies the highlight or the whole line. Android: the platform
/// selection (long-press, then the system copy action).
/// </summary>
public static class SelectableTranscript
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;
        LabelHandler.Mapper.AppendToMapping(nameof(SelectableLabel), (handler, view) =>
        {
            if (view is not SelectableLabel) return;
            Enable(handler);
        });
    }

    private static void Enable(ILabelHandler handler)
    {
#if WINDOWS
        var block = handler.PlatformView;
        block.IsTextSelectionEnabled = true;
        block.IsHitTestVisible = true;
        if (block.ContextFlyout is null)
        {
            var captured = "";
            var copy = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = TranscriptCopy.Action };
            var menu = new Microsoft.UI.Xaml.Controls.MenuFlyout();
            menu.Opening += (_, _) => captured = block.SelectedText ?? "";
            copy.Click += async (_, _) =>
            {
                try
                {
                    var selected = string.IsNullOrEmpty(block.SelectedText) ? captured : block.SelectedText;
                    var text = TranscriptCopy.Text(block.Text, selected);
                    if (text.Length == 0) return;
                    await Clipboard.Default.SetTextAsync(text);
                }
                catch (Exception ex)
                {
                    try { CrashDiagnostics.Report("ui", ex, "SelectableTranscript.Copy", fatal: false); }
                    catch { /* the selection itself still stands */ }
                }
            };
            menu.Items.Add(copy);
            block.ContextFlyout = menu;
        }
#elif ANDROID
        var widget = handler.PlatformView;
        widget.SetTextIsSelectable(true);
        if (Hooked.TryAdd(widget, Marker))
        {
            widget.Touch += (_, args) =>
            {
                // Once a selection exists, a drag adjusts it. The conversation
                // scroller must not take that drag. A drag with no selection
                // still scrolls.
                if (widget.HasSelection)
                    DisallowScrollIntercept(widget);
                args.Handled = false;
            };
        }
#endif
    }

#if ANDROID
    private static readonly object Marker = new();
    private static readonly ConditionalWeakTable<Android.Views.View, object> Hooked = new();

    private static void DisallowScrollIntercept(Android.Views.View view)
    {
        for (Android.Views.IViewParent? parent = view.Parent; parent is not null; parent = parent.Parent)
            parent.RequestDisallowInterceptTouchEvent(true);
    }
#endif
}

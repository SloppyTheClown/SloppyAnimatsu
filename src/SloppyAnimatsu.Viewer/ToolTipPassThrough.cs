using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// Tooltips never take a click. On Windows a tooltip is its own popup window, so a tip that opens
/// under the pointer used to swallow the press meant for the button below it (the user lost three
/// clicks on "Switch to Rigging"). Each tooltip's popup window answers
/// <c>WM_NCHITTEST</c> with <c>HTTRANSPARENT</c>, which hands the mouse to the window under it on
/// the same thread - the main window - so the press lands on the control beneath the tip. Elsewhere
/// the tip is only made not hit-testable.
/// </summary>
internal static class ToolTipPassThrough
{
    private const uint WmNcHitTest = 0x0084;
    private static readonly IntPtr HtTransparent = new(-1);
    private static readonly ConditionalWeakTable<TopLevel, object> Hooked = new();

    public static void Install() =>
        Control.LoadedEvent.AddClassHandler<ToolTip>((tip, _) => PassThrough(tip), RoutingStrategies.Direct, handledEventsToo: true);

    private static void PassThrough(ToolTip tip)
    {
        tip.IsHitTestVisible = false;
        if (!OperatingSystem.IsWindows() || TopLevel.GetTopLevel(tip) is not { } host || Hooked.TryGetValue(host, out _)) return;
        Hooked.Add(host, new object());
        Win32Properties.AddWndProcHookCallback(host, Hook);
    }

    private static IntPtr Hook(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmNcHitTest) return IntPtr.Zero;
        handled = true;
        return HtTransparent;
    }
}

using System.Linq;
using System.Runtime.InteropServices;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The viewer is a console program - its command line prints status and errors, which scripts read -
/// so Windows gives it a console window beside the viewer when Explorer starts it, and no one types
/// into that window. Hide it unless the run asks for it with <c>--console</c>. A console shared with
/// the shell that started the viewer is left alone: that is the user's own terminal, not ours.
/// </summary>
internal static class ConsoleWindow
{
    private const int SwHide = 0;

    /// <summary>Keeps the console window up (a scripted run started in its own console).</summary>
    public const string Option = "--console";

    public static void HideUnlessRequested(IReadOnlyList<string> args)
    {
        if (!OperatingSystem.IsWindows() || args.Contains(Option)) return;
        IntPtr window = GetConsoleWindow();
        if (window == IntPtr.Zero) return;
        // One process in the console's list means the console was created for this launch (the
        // Explorer double-click). More means a terminal or a .bat owns it as well, and hiding it
        // would take the prompt with it.
        var processes = new uint[8];
        if (GetConsoleProcessList(processes, (uint)processes.Length) != 1) return;
        ShowWindow(window, SwHide);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processes, uint count);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
}
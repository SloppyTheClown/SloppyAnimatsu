using System.Linq;
using Avalonia;
using SloppyAnimatsu.Core.Diagnostics;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // One log file per run, three kept, 4 MiB each (SloppyAnimatsu.Core.Diagnostics.AppLog). It is
        // opened before Avalonia starts so a crash during start-up is recorded with its full stack.
        AppLog.Start("viewer", args);
        MemoryLog.Start();
        try
        {
            // A double-click launch is not a console run: hide the window Windows gives a console program
            // unless --console asks for it (ConsoleWindow.cs). Strip the switch first: it belongs to the
            // launch, and the scene loader would take an unknown option for a scene path.
            ConsoleWindow.HideUnlessRequested(args);
            // Shortcut labels show the character the user's keyboard prints
            // the stored shortcut stays the virtual-key code.
            if (OperatingSystem.IsWindows()) Shortcuts.LayoutKeyName = WindowsKeyNames.Name;
            string[] sceneArgs = args.Where(arg => arg != ConsoleWindow.Option).ToArray();
            int code = BuildAvaloniaApp().StartWithClassicDesktopLifetime(sceneArgs);
            AppLog.Info($"exit code {code}");
            return code;
        }
        catch (Exception e)
        {
            // The Windows "Application Error" dialog would wait for a click; the log holds the
            // full stack trace instead and the exit code says what happened.
            AppLog.Crash(e, "the viewer stopped on an exception");
            try
            {
                Console.Error.WriteLine("fatal: " + e);
            }
            catch (IOException)
            {
            }

            return 1;
        }
        finally
        {
            AppLog.Shutdown();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
        // Avalonia blacklists software GL renderers (llvmpipe, softpipe) on X11; the viewer's
        // viewport is an OpenGL control, so on a machine without GPU acceleration it stays black
        // and --screenshot writes nothing. Allow it when asked
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("SLOPPYANIMATSU_SOFTWARE_GL") == "1")
            builder = builder.With(new X11PlatformOptions { GlxRendererBlacklist = [] });
        return builder;
    }
}
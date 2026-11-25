using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using SloppyAnimatsu.Core.Diagnostics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed class App : Application
{
    public override void Initialize()
    {
        // An exception that escapes the UI thread is a crash: the log gets its full stack trace
        // before the window loop goes down. Program.Main's AppDomain handler sees it too, and
        // AppLog drops the duplicate.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
            AppLog.Crash(e.Exception, "unhandled exception on the UI thread");
        Styles.Add(new FluentTheme());
        ToolTipPassThrough.Install();
        DisabledHint.Install(this);
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, e) => AppLog.Info($"the window loop exited with code {e.ApplicationExitCode}");
            desktop.MainWindow = new MainWindow(ViewerOptions.Parse(desktop.Args ?? []));
        }

        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>
/// Command line: <c>[file.akt] [--character name] [--animation name] [--frame n] [--hide-others] [--screenshot out.png]
/// [--select controller] [--rotate x|y|z:degrees]… [--move x|y|z:units]… [--scale x|y|z|u:factor]…
/// [--set tx|ty|tz|rx|ry|rz|sx|sy|sz:value]… [--keep-children]
/// [--resolve convert|keep|return] [--bank action]… [--curves] [--layout name] [--view name] [--save-as out.akt]
/// [--context rigging|animation] [--hide name]… [--show name]… [--lock mesh]… [--unlock mesh]…
/// [--show-all] [--hide-all] [--window]</c>.
/// With <c>--screenshot</c> the viewer renders one frame, saves it and exits. <c>--select</c> picks
/// controllers of the character (comma-separated; the last is the current object, the others follow
/// its drags); each <c>--rotate</c>/<c>--move</c>/<c>--scale</c> then keys it at the
/// frame as one gizmo drag would (rotation about its own axis, translation along its parent's, scale
/// along its own axis or, with <c>u</c>, all three). Each <c>--set</c> types one Spinner value
/// (t/r/s and axis) for every selected controller at the frame, after the drags; <c>--keep-children</c> makes every drag (and
/// Mirror Pose) keep the object's direct children in place. With <c>--animation "(rest pose)"</c>
/// they edit the character's rest pose instead, and <c>--resolve</c> answers the reference pose dialog.
/// Each <c>--bank</c> runs an Anim Bank, Mixer or key command after <c>--select</c>, as its menu item would:
/// new-animation, copy-animation, delete-animation, rename-animation:name, new-stack (from the
/// selection), duplicate-stack, delete-stack, stack-up, stack-down, rename-stack:name,
/// select-animation:name (the animation the timeline shows), select-stack:name (the timeline's
/// active stack), select-stacks:a,b (the Mixer's multi-selection), cycle, enabled, additive,
/// opacity:0..1, merge (bake the selected stacks), extract (the selected objects' channels) and
/// audio:file.wav (a new audio stack),
/// select-keys:i,j,… (keys of the active stack), copy-keys, paste-keys, paste-keys-after,
/// mirror-keys, mirror-pose, cycle-step (the active stack), cycle-side (the selected controllers),
/// add-camera[:name] (an animated camera, which becomes the selected object), key:n, frame:n and
/// fov:degrees (the selected camera's FOV at the current frame), undo, redo. In the Rigging context
/// (--context rigging, --select a,b to pick the two joints) it also runs new-joint[:name],
/// insert-joint[:name], delete-joints, orient-joints, add-ik, add-reverse-foot, bind-skin,
/// unbind-skin, snap-ltm and select:a,b (re-select after an edit), as the Rig menu items do
/// <c>--save-as</c> saves the result. <c>--layout</c>
/// switches the viewport layout.
/// <c>--view</c> looks through a scene camera by name (Perspective, Front … Photo), an animated camera, or
/// <c>perspective</c> for the orbit view; it applies to the focused view. <c>--curves</c> shows the Curveboard with all its keys selected; with <c>--screenshot</c>
/// the whole window is also saved, as out.window.png.
/// <c>--context</c> switches the interface context (the rigging context opens the rest pose entry),
/// and <c>--hide</c>/<c>--show</c>/<c>--lock</c>/<c>--unlock</c> (names, repeatable or
/// comma-separated) and <c>--show-all</c>/<c>--hide-all</c> run the hierarchy panel's commands
/// <c>--window</c> also saves the whole window with
/// <c>--screenshot</c>. <c>--new</c> starts on a new, empty scene (File &gt; New Scene);
/// <c>--welcome</c> opens the Welcome window even when the preferences say to skip it (a scripted
/// run never shows it otherwise);
/// <c>--preferences</c> reads the recent scenes, autosave interval and Spinner settings from another
/// preferences file instead of the user's <c>preferences.slopa</c>,
/// and <c>--shortcuts</c> reads another key map instead of <c>shortcuts.slopa</c>
/// </summary>
public sealed record ViewerOptions(string? Path, string? Character, string? Animation, float? Frame, bool HideOthers, string? Screenshot)
{
    public string? Select { get; init; }
    public IReadOnlyList<(GizmoMode Mode, int Axis, float Amount)> Edits { get; init; } = [];
    public IReadOnlyList<(int Channel, float Value)> Values { get; init; } = [];
    public string? SaveAs { get; init; }
    public RestPoseResolution? Resolve { get; init; }
    public IReadOnlyList<string> BankActions { get; init; } = [];
    public bool Curves { get; init; }
    public bool KeepChildren { get; init; }
    public string? Layout { get; init; }
    public string? View { get; init; }
    public InterfaceContext? Context { get; init; }
    public IReadOnlyList<string> Hide { get; init; } = [];
    public IReadOnlyList<string> Show { get; init; } = [];
    public IReadOnlyList<string> Lock { get; init; } = [];
    public IReadOnlyList<string> Unlock { get; init; } = [];
    public bool ShowAll { get; init; }
    public bool HideAll { get; init; }
    public bool Window { get; init; }

    /// <summary>The preferences file to read the recent scenes and autosave interval from (default: the user's).</summary>
    public string? PreferencesPath { get; init; }

    /// <summary>The shortcuts file to read the key bindings from (default: the user's shortcuts.slopa).</summary>
    public string? ShortcutsPath { get; init; }

    /// <summary>Start on a new, empty scene (File > New Scene) instead of opening a file.</summary>
    public bool NewScene { get; init; }

    /// <summary>Show the Welcome window even when the preferences say to skip it (a check of the window).</summary>
    public bool Welcome { get; init; }

    /// <summary>
    /// A run that drives the viewer from the command line. It may not stop on a startup dialog:
    /// nobody is there to answer it.
    /// </summary>
    public bool Scripted => Screenshot != null || SaveAs != null;

    public static ViewerOptions Parse(string[] args)
    {
        string? path = null, character = null, animation = null, screenshot = null, select = null, saveAs = null, layout = null, view = null, preferencesPath = null, shortcutsPath = null;
        float? frame = null;
        bool hideOthers = false, curves = false, keepChildren = false, showAll = false, hideAll = false, window = false, newScene = false, welcome = false;
        RestPoseResolution? resolve = null;
        InterfaceContext? context = null;
        var edits = new List<(GizmoMode, int, float)>();
        var values = new List<(int, float)>();
        var bank = new List<string>();
        var hide = new List<string>();
        var show = new List<string>();
        var lockMeshes = new List<string>();
        var unlockMeshes = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--character": character = Next(); break;
                case "--animation": animation = Next(); break;
                case "--frame": frame = float.TryParse(Next(), CultureInfo.InvariantCulture, out var f) ? f : null; break;
                case "--hide-others": hideOthers = true; break;
                case "--curves": curves = true; break;
                case "--new": newScene = true; break;
                case "--welcome": welcome = true; break;
                case "--keep-children": keepChildren = true; break;
                case "--screenshot": screenshot = Next(); break;
                case "--select": select = Next(); break;
                case "--view": view = Next(); break;
                case "--layout": layout = Next(); break;
                case "--context":
                    string? contextValue = Next();
                    context = contextValue switch
                    {
                        "rigging" => InterfaceContext.Rigging,
                        "animation" => InterfaceContext.Animation,
                        _ => null,
                    };
                    if (context == null) Console.Error.WriteLine($"--context: expected rigging|animation, got '{contextValue}'");
                    break;
                case "--hide": AddNames(hide, Next()); break;
                case "--show": AddNames(show, Next()); break;
                case "--lock": AddNames(lockMeshes, Next()); break;
                case "--unlock": AddNames(unlockMeshes, Next()); break;
                case "--show-all": showAll = true; break;
                case "--hide-all": hideAll = true; break;
                case "--window": window = true; break;
                case "--rotate": AddEdit(GizmoMode.Rotate, args[i], Next()); break;
                case "--move": AddEdit(GizmoMode.Translate, args[i], Next()); break;
                case "--scale": AddEdit(GizmoMode.Scale, args[i], Next()); break;
                case "--set": AddSet(Next()); break;
                case "--save-as": saveAs = Next(); break;
                case "--preferences": preferencesPath = Next(); break;
                case "--shortcuts": shortcutsPath = Next(); break;
                case "--bank": if (Next() is { } action) bank.Add(action); break;
                case "--resolve":
                    string? value = Next();
                    resolve = value switch
                    {
                        "convert" => RestPoseResolution.ConvertAnimations,
                        "keep" => RestPoseResolution.KeepAnimations,
                        "return" => RestPoseResolution.ReturnToPrevious,
                        _ => null,
                    };
                    if (resolve == null) Console.Error.WriteLine($"--resolve: expected convert|keep|return, got '{value}'");
                    break;
                default: path ??= args[i]; break;
            }
        }
        return new ViewerOptions(path, character, animation, frame, hideOthers, screenshot)
        {
            Select = select, Edits = edits, Values = values, SaveAs = saveAs, Resolve = resolve, BankActions = bank, Curves = curves,
            KeepChildren = keepChildren, Layout = layout, View = view, Context = context, Hide = hide, Show = show, Lock = lockMeshes,
            Unlock = unlockMeshes, ShowAll = showAll, HideAll = hideAll, Window = window,
            PreferencesPath = preferencesPath, ShortcutsPath = shortcutsPath, NewScene = newScene,
            Welcome = welcome,
        };

        void AddNames(List<string> into, string? value)
        {
            if (value == null) return;
            foreach (var name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                into.Add(name);
        }

        void AddEdit(GizmoMode mode, string option, string? value)
        {
            // u (uniform) is TransformGizmo.Uniform, scale only.
            string axes = mode == GizmoMode.Scale ? "xyzu" : "xyz";
            int axis = value is { Length: > 2 } && value[1] == ':' ? axes.IndexOf(char.ToLowerInvariant(value[0])) : -1;
            if (axis < 0 || !float.TryParse(value![2..], CultureInfo.InvariantCulture, out float amount))
            {
                Console.Error.WriteLine($"{option}: expected {string.Join('|', axes.ToCharArray())}:amount, got '{value}'");
                return;
            }
            edits.Add((mode, axis, amount));
        }

        void AddSet(string? value)
        {
            string[] channels = ["tx", "ty", "tz", "rx", "ry", "rz", "sx", "sy", "sz"];
            int colon = value?.IndexOf(':') ?? -1;
            int channel = colon > 0 ? Array.IndexOf(channels, value![..colon].ToLowerInvariant()) : -1;
            if (channel < 0 || !float.TryParse(value![(colon + 1)..], CultureInfo.InvariantCulture, out float amount))
            {
                Console.Error.WriteLine($"--set: expected tx|ty|tz|rx|ry|rz|sx|sy|sz:value, got '{value}'");
                return;
            }
            values.Add((channel, amount));
        }
    }
}

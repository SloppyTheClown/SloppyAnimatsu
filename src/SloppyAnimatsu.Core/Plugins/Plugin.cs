using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Core.Plugins;

/// <summary>What the Plugins menu and the Manage plugins list show about a plugin.</summary>
/// <param name="Id">Stable id, e.g. <c>sloppy.ezexports</c>; the enabled state and the scene data are stored under it.</param>
public sealed record PluginInfo(string Id, string Name, string Version, string Description);

/// <summary>One row a plugin adds to its submenu under Plugins, below its Enabled box.</summary>
/// <param name="Id">Passed back to <see cref="IPlugin.Run"/>; unique within the plugin.</param>
/// <param name="Label">The row's text; it may change between menu refreshes ("Open window" / "Close window").</param>
public sealed record PluginCommand(string Id, string Label, string? ToolTip = null, bool Enabled = true);

/// <summary>
/// A plugin. Built-in plugins are created at start-up and handed to the <see cref="PluginRegistry"/>,
/// which enables the ones the user has not turned off. A disabled plugin keeps no window open and
/// listens to nothing; whatever it stored in a scene stays there untouched.
/// </summary>
public interface IPlugin
{
    PluginInfo Info { get; }

    /// <summary>Starts the plugin: subscribe to the host's events, show nothing yet.</summary>
    void Enable(IPluginHost host);

    /// <summary>Stops the plugin: close its windows, unsubscribe from the host.</summary>
    void Disable();

    /// <summary>The rows under the plugin's Enabled box while it is enabled.</summary>
    IReadOnlyList<PluginCommand> Commands { get; }

    /// <summary>Runs the command with this <see cref="PluginCommand.Id"/>.</summary>
    void Run(string commandId);
}

/// <summary>What the application offers a plugin.</summary>
public interface IPluginHost
{
    /// <summary>The scene being edited (the active document), or null while none is open.</summary>
    SceneModel? Scene { get; }

    /// <summary>The file the active scene was loaded from or last saved to; null for an unsaved scene.</summary>
    string? ScenePath { get; }

    /// <summary>Another scene became active, or the active one was closed or reloaded.</summary>
    event EventHandler? SceneChanged;

    /// <summary>The active scene is about to be written to disk (a save or an autosave).</summary>
    event EventHandler? SceneSaving;

    /// <summary>The Anim Bank's selection changed.</summary>
    event EventHandler? AnimBankSelectionChanged;

    /// <summary>The Anim Bank's selected rows: <see cref="Animation"/>s and <see cref="Folder"/>s, in bank order.</summary>
    IReadOnlyList<SceneObject> AnimBankSelection { get; }

    /// <summary>The scene's selection (the TREE's selected objects), in tree order.</summary>
    IReadOnlyList<SceneObject> SceneSelection { get; }

    /// <summary>The active character: the one the Anim Bank shows. Null while the scene has none.</summary>
    Character? ActiveCharacter { get; }

    /// <summary>Shows a line in the status bar (and the log).</summary>
    void ShowStatus(string message);

    /// <summary>Marks the active scene as changed, so it is saved, autosaved and asked about on close.</summary>
    void MarkSceneDirty();

    /// <summary>Rebuilds the menus, for a plugin whose command rows changed.</summary>
    void RefreshMenu();

    /// <summary>
    /// Opens the Export Settings window in its settings-only form (OK instead of Export, no file
    /// picker, no scope rows). Completes with the edited settings, or null when cancelled.
    /// </summary>
    Task<FbxExportSettings?> EditExportSettings(FbxExportSettings initial);

    /// <summary>Runs <paramref name="work"/> off the UI thread behind the progress dialog; rethrows its exception.</summary>
    Task RunWithProgress(string title, string message, Action<IProgress<double>> work);
}

/// <summary>What the Plugins menu needs of one plugin (<see cref="MenuState.Plugins"/>).</summary>
public sealed record PluginMenuState(PluginInfo Info, bool Enabled, IReadOnlyList<PluginCommand> Commands);

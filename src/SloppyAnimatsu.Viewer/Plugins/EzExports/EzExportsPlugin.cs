using System.Runtime.CompilerServices;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Core.Plugins;
using SloppyAnimatsu.Core.Plugins.EzExports;

namespace SloppyAnimatsu.Viewer.Plugins.EzExports;

/// <summary>
/// EZ Exports: named presets of animations and Anim Bank folders that write their FBX files to a set
/// folder with set names and settings in one click. The presets live in the scene (.slopa) and
/// follow renames and moves of what they hold.
/// </summary>
/// <remarks>
/// Preset edits are not undo steps. The plugin keeps each scene's presets in memory and writes them
/// into the scene on every edit and again just before every save, so an undo that restores the
/// scene object's stored fields cannot take a preset edit back with it.
/// </remarks>
public sealed class EzExportsPlugin : IPlugin
{
    private const string WindowCommand = "window";
    private const string RunAllCommand = "run-all";

    private readonly ConditionalWeakTable<SceneModel, List<EzPreset>> _presets = new();
    private IViewerPluginHost? _host;
    private EzExportsWindow? _window;

    public PluginInfo Info { get; } = new(EzPresetStore.PluginId, "EZ Exports", "1.0",
        "Export presets: named lists of animations and Anim Bank folders, each with its own folder, file name key and export settings, written in one click. Presets are saved in the scene.");

    public IReadOnlyList<PluginCommand> Commands =>
    [
        new(WindowCommand, _window == null ? "_Open window" : "_Close window",
            "The EZ Exports window: the scene's presets, what each holds and writes, and the Run buttons."),
        new(RunAllCommand, "_Run all presets",
            "Writes the files of every preset in the scene, overwriting files already there.",
            _host?.Scene is { } scene && Presets(scene).Count > 0),
    ];

    internal IViewerPluginHost Host => _host ?? throw new InvalidOperationException("EZ Exports is not enabled.");

    public void Enable(IPluginHost host)
    {
        _host = host as IViewerPluginHost
            ?? throw new InvalidOperationException("EZ Exports needs the SloppyAnimatsu viewer.");
        _host.SceneChanged += OnSceneChanged;
        _host.SceneSaving += OnSceneSaving;
        _host.AnimBankSelectionChanged += OnBankSelectionChanged;
    }

    public void Disable()
    {
        _window?.Close();
        _window = null;
        if (_host == null) return;
        _host.SceneChanged -= OnSceneChanged;
        _host.SceneSaving -= OnSceneSaving;
        _host.AnimBankSelectionChanged -= OnBankSelectionChanged;
        _host = null;
    }

    public void Run(string commandId)
    {
        switch (commandId)
        {
            case WindowCommand when _window != null:
                _window.Close();
                break;
            case WindowCommand:
                _window = new EzExportsWindow(this);
                _window.Closed += delegate
                {
                    _window = null;
                    _host?.RefreshMenu();
                };
                _window.Show(Host.Owner);
                break;
            case RunAllCommand:
                if (Host.Scene is { } scene) _ = RunPresets(scene, Presets(scene));
                break;
        }
        Host.RefreshMenu();
    }

    /// <summary>The scene's presets, read from it the first time they are asked for.</summary>
    internal List<EzPreset> Presets(SceneModel model)
    {
        if (_presets.TryGetValue(model, out var list)) return list;
        list = EzPresetStore.Load(model, out string? error);
        if (error != null) _host?.ShowStatus(error);
        _presets.Add(model, list);
        return list;
    }

    /// <summary>After an edit of <paramref name="model"/>'s presets: writes them into the scene and marks it changed.</summary>
    internal void Changed(SceneModel model)
    {
        if (EzPresetStore.Save(model, Presets(model)) && ReferenceEquals(model, _host?.Scene))
            _host?.MarkSceneDirty();
        _host?.RefreshMenu();
    }

    /// <summary>Runs <paramref name="presets"/> of <paramref name="model"/> behind the progress dialog and reports each.</summary>
    internal async Task RunPresets(SceneModel model, IReadOnlyList<EzPreset> presets)
    {
        if (presets.Count == 0)
        {
            Host.ShowStatus("EZ Exports: no preset to run.");
            return;
        }
        string? path = Host.ScenePath;
        IReadOnlyList<EzRunResult> results = [];
        try
        {
            await Host.RunWithProgress("EZ Exports", presets.Count == 1 ? $"Exporting \"{presets[0].Name}\"…" : $"Exporting {presets.Count} presets…",
                progress => results = EzExportRunner.Run(model, path, presets, progress));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Host.ShowStatus("EZ Exports failed: " + e.Message);
            return;
        }
        foreach (var result in results) Console.WriteLine(result.StatusLine());
        int written = results.Sum(r => r.Written.Count);
        int failed = results.Count(r => r.Error != null);
        Host.ShowStatus(results.Count == 1 ? results[0].StatusLine()
            : $"EZ Exports: {written} file(s) written by {results.Count - failed} of {results.Count} presets"
              + (failed > 0 ? "; failed: " + string.Join("; ", results.Where(r => r.Error != null).Select(r => $"\"{r.Preset.Name}\": {r.Error}")) : "."));
        _window?.Refresh();
    }

    private void OnSceneChanged(object? sender, EventArgs e)
    {
        _window?.ShowScene();
        _host?.RefreshMenu();
    }

    // Every scene this plugin read presets from gets them written back: an undo may have restored
    // older stored data, and the save is about to write it.
    private void OnSceneSaving(object? sender, EventArgs e)
    {
        foreach (var (model, list) in _presets)
            EzPresetStore.Save(model, list);
    }

    private void OnBankSelectionChanged(object? sender, EventArgs e) => _window?.BankSelectionChanged();
}

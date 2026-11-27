using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Core.Plugins;
using SloppyAnimatsu.Viewer.Plugins.EzExports;
using SloppyAnimatsu.Viewer.Plugins;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The window's side of the plugin system: the installed (built-in) plugins, the host they talk to,
/// and the Plugins menu's commands (Enabled boxes, plugin rows, Manage plugins).
/// </summary>
public sealed partial class MainWindow
{
    private readonly PluginRegistry _plugins = CreatePlugins();
    private PluginHost? _pluginHost;

    /// <summary>The built-in plugins, with the enabled state saved beside the other settings.</summary>
    private static PluginRegistry CreatePlugins()
    {
        var registry = new PluginRegistry(PluginRegistry.DefaultPath);
        registry.Add(new EzExportsPlugin());
        return registry;
    }

    /// <summary>Starts the enabled plugins once the window is built; stops them when it closes.</summary>
    private void StartPlugins()
    {
        _pluginHost = new PluginHost(this);
        _animations.SelectionChanged += delegate { _pluginHost?.RaiseAnimBankSelectionChanged(); };
        _plugins.Start(_pluginHost);
        if (_plugins.LastError != null) _status.Text = _plugins.LastError;
        RefreshMenu();
    }

    private void StopPlugins()
    {
        _plugins.Stop();
        _pluginHost = null;
    }

    /// <summary>Tells the plugins the active scene changed (shown, switched, closed, reloaded).</summary>
    private void PluginsSceneChanged() => _pluginHost?.RaiseSceneChanged();

    /// <summary>Lets the plugins write what they keep in the scene before it is saved or autosaved.</summary>
    private void PluginsSceneSaving() => _pluginHost?.RaiseSceneSaving();

    private void TogglePlugin(string? id)
    {
        if (id == null || _plugins.Find(id) is not { } plugin) return;
        bool on = !_plugins.IsRunning(id);
        _plugins.SetEnabled(id, on);
        _status.Text = _plugins.LastError ?? $"{plugin.Info.Name} {(on ? "enabled" : "disabled")}.";
        RefreshMenu();
    }

    private void RunPluginCommand(string? argument)
    {
        if (PluginRegistry.ParseCommandArgument(argument) is not { } command) return;
        try
        {
            if (!_plugins.Run(command.PluginId, command.CommandId))
                _status.Text = "That plugin is turned off.";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _status.Text = "Plugin command failed: " + e.Message;
            Console.Error.WriteLine(e);
        }
        RefreshMenu();
    }

    /// <summary>Plugins > Manage plugins: every installed plugin with its version, description and Enabled box.</summary>
    private void ShowPluginManager()
    {
        var rows = new StackPanel { Spacing = 12 };
        foreach (var plugin in _plugins.Plugins)
        {
            var info = plugin.Info;
            var box = new CheckBox { Content = "Enabled", IsChecked = _plugins.IsRunning(info.Id) };
            ToolTip.SetTip(box, "Turns the plugin on or off; the choice is kept for next time.");
            box.IsCheckedChanged += delegate
            {
                bool on = box.IsChecked == true;
                if (on == _plugins.IsRunning(info.Id)) return;
                _plugins.SetEnabled(info.Id, on);
                _status.Text = _plugins.LastError ?? $"{info.Name} {(on ? "enabled" : "disabled")}.";
                RefreshMenu();
            };
            rows.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock { Text = info.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                            new TextBlock { Text = "v" + info.Version, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center },
                        },
                    },
                    new TextBlock { Text = info.Description, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Opacity = 0.85 },
                    box,
                },
            });
        }
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        var window = new Window
        {
            Title = "Plugins",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 14,
                MinWidth = 360,
                Children = { rows, close },
            },
        };
        close.Click += delegate { window.Close(); };
        window.ShowDialog(this);
    }

    /// <summary>What the main window offers a plugin (<see cref="IPluginHost"/>).</summary>
    private sealed class PluginHost(MainWindow window) : Plugins.IViewerPluginHost
    {
        public SceneModel? Scene => window._pose?.Model;

        public string? ScenePath => window._pose == null ? null : window._path;

        public event EventHandler? SceneChanged;
        public event EventHandler? SceneSaving;
        public event EventHandler? AnimBankSelectionChanged;

        public IReadOnlyList<SceneObject> AnimBankSelection => window.SelectedBankItems();

        public IReadOnlyList<SceneObject> SceneSelection => window._outliner.SelectedObjects;

        public Character? ActiveCharacter => window._character;

        /// <summary>The window plugins open their own windows over.</summary>
        public Window Owner => window;

        public void ShowStatus(string message)
        {
            window._status.Text = message;
            Console.WriteLine(message);
        }

        public void MarkSceneDirty()
        {
            if (window._pose == null) return;
            window._dirty = true;
            window.UpdateTitle();
        }

        public void RefreshMenu() => window.RefreshMenu();

        public Task<FbxExportSettings?> EditExportSettings(FbxExportSettings initial) =>
            ExportSettingsDialog.AskSettingsOnly(window, initial);

        public Task RunWithProgress(string title, string message, Action<IProgress<double>> work) =>
            ProgressDialog.Run(window, title, message, work);

        public void RaiseSceneChanged() => Raise(SceneChanged);
        public void RaiseSceneSaving() => Raise(SceneSaving);
        public void RaiseAnimBankSelectionChanged() => Raise(AnimBankSelectionChanged);

        // A plugin that throws from a handler costs a status line, never the window.
        private void Raise(EventHandler? handler)
        {
            if (handler == null) return;
            foreach (EventHandler h in handler.GetInvocationList())
            {
                try
                {
                    h(this, EventArgs.Empty);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    window._status.Text = "A plugin failed: " + e.Message;
                    Console.Error.WriteLine(e);
                }
            }
        }
    }
}

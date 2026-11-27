using System.Text.Json;

namespace SloppyAnimatsu.Core.Plugins;

/// <summary>
/// The installed plugins and whether each is enabled. The enabled state is kept per plugin id in
/// <c>%APPDATA%/SloppyAnimatsu/plugins.json</c>; a plugin the file does not name yet (newly
/// installed) starts enabled. A plugin that throws while enabling is left disabled and reported in
/// <see cref="LastError"/>, so one broken plugin cannot stop the application from starting.
/// </summary>
public sealed class PluginRegistry
{
    public const string FileName = "plugins.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly List<IPlugin> _plugins = [];
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _enabled;
    private IPluginHost? _host;

    /// <param name="settingsPath">The enabled-state file; null keeps the state in memory only (tests, the CLI).</param>
    public PluginRegistry(string? settingsPath)
    {
        SettingsPath = settingsPath;
        _enabled = Read(settingsPath);
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SloppyAnimatsu", FileName);

    public string? SettingsPath { get; }

    /// <summary>The last failure to save the settings or to enable/disable a plugin; null after a success.</summary>
    public string? LastError { get; private set; }

    public IReadOnlyList<IPlugin> Plugins => _plugins;

    public IPlugin? Find(string id) => _plugins.FirstOrDefault(p => p.Info.Id == id);

    /// <summary>Whether the user wants the plugin on (the saved state; true for one never seen before).</summary>
    public bool IsEnabled(string id) => !_enabled.TryGetValue(id, out bool on) || on;

    /// <summary>Whether the plugin is enabled and running now.</summary>
    public bool IsRunning(string id) => _running.Contains(id);

    /// <summary>Adds a plugin; refuses a second plugin with the same id.</summary>
    public void Add(IPlugin plugin)
    {
        if (Find(plugin.Info.Id) != null)
            throw new InvalidOperationException($"A plugin with the id '{plugin.Info.Id}' is already installed.");
        _plugins.Add(plugin);
        if (_host != null && IsEnabled(plugin.Info.Id)) Start(plugin);
    }

    /// <summary>Enables every plugin the user has not turned off, against <paramref name="host"/>.</summary>
    public void Start(IPluginHost host)
    {
        _host = host;
        foreach (var plugin in _plugins)
            if (IsEnabled(plugin.Info.Id) && !IsRunning(plugin.Info.Id)) Start(plugin);
    }

    /// <summary>Disables every running plugin (application exit). The saved state is not changed.</summary>
    public void Stop()
    {
        foreach (var plugin in _plugins)
            if (IsRunning(plugin.Info.Id)) Halt(plugin);
        _host = null;
    }

    /// <summary>Turns a plugin on or off, saves the choice and starts or stops it.</summary>
    public void SetEnabled(string id, bool enabled)
    {
        var plugin = Find(id) ?? throw new ArgumentException($"No plugin with the id '{id}' is installed.", nameof(id));
        _enabled[id] = enabled;
        Save();
        if (enabled && _host != null && !IsRunning(id)) Start(plugin);
        else if (!enabled && IsRunning(id)) Halt(plugin);
    }

    /// <summary>The Plugins menu rows: every installed plugin with its state and, when running, its commands.</summary>
    public IReadOnlyList<PluginMenuState> MenuState() =>
        _plugins.Select(p => new PluginMenuState(p.Info, IsRunning(p.Info.Id),
            IsRunning(p.Info.Id) ? p.Commands : [])).ToList();

    /// <summary>Runs a plugin command (the menu's <c>pluginId|commandId</c> argument); false when the plugin is not running.</summary>
    public bool Run(string pluginId, string commandId)
    {
        if (!IsRunning(pluginId) || Find(pluginId) is not { } plugin) return false;
        plugin.Run(commandId);
        return true;
    }

    /// <summary>The menu argument of a plugin command.</summary>
    public static string CommandArgument(string pluginId, string commandId) => pluginId + "|" + commandId;

    /// <summary>Splits a <see cref="CommandArgument"/>; null when it is not one.</summary>
    public static (string PluginId, string CommandId)? ParseCommandArgument(string? argument)
    {
        int bar = argument?.IndexOf('|') ?? -1;
        return bar > 0 ? (argument![..bar], argument[(bar + 1)..]) : null;
    }

    private void Start(IPlugin plugin)
    {
        try
        {
            plugin.Enable(_host!);
            _running.Add(plugin.Info.Id);
        }
        catch (Exception e)
        {
            LastError = $"The plugin {plugin.Info.Name} could not be enabled: {e.Message}";
        }
    }

    private void Halt(IPlugin plugin)
    {
        _running.Remove(plugin.Info.Id);
        try { plugin.Disable(); }
        catch (Exception e) { LastError = $"The plugin {plugin.Info.Name} did not stop cleanly: {e.Message}"; }
    }

    private void Save()
    {
        if (SettingsPath == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new SettingsFile { Enabled = _enabled }, Json));
            LastError = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LastError = "Could not save the plugin settings: " + e.Message;
        }
    }

    private static Dictionary<string, bool> Read(string? path)
    {
        try
        {
            if (path != null && File.Exists(path) &&
                JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(path)) is { Enabled: { } enabled })
                return new Dictionary<string, bool>(enabled, StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new Dictionary<string, bool>(StringComparer.Ordinal);
    }

    private sealed class SettingsFile
    {
        public Dictionary<string, bool>? Enabled { get; set; }
    }
}

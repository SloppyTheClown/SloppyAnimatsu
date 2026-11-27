using System.Text.Json;
using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Core.Plugins.EzExports;

/// <summary>
/// One EZ Exports preset: a named list of characters, animations and Anim Bank folders, the folder
/// their FBX files go to (the Destination), the file name key (the Filename) and the export settings
/// to write them with.
/// </summary>
public sealed class EzPreset
{
    public string Name { get; set; } = "Preset";

    /// <summary>Ticked in the preset list ("Run checked" runs the ticked ones).</summary>
    public bool Checked { get; set; } = true;

    /// <summary>
    /// <see cref="Character"/>s (their model, written without animations) and the Anim Bank's
    /// <see cref="Animation"/>s and <see cref="Folder"/>s, in the order they were added. A null entry
    /// is an item whose object was deleted; it shows as missing and is skipped.
    /// </summary>
    public List<SceneObject?> Items { get; } = [];

    /// <summary>Where the files go: an absolute folder, or one relative to the scene file's folder (empty = that folder).</summary>
    public string Folder { get; set; } = "";

    /// <summary>The file name (the window's Filename): <c>&lt;Key&gt;.fbx</c> for one file, <c>&lt;Key&gt;_&lt;Animation&gt;.fbx</c> / <c>&lt;Key&gt;_&lt;Character&gt;.fbx</c> for several. Empty = the preset's name.</summary>
    public string Key { get; set; } = "";

    /// <summary>The export settings. Scope, "Only selected animations" and the file-splitting toggles are not used: the preset decides those.</summary>
    public FbxExportSettings Settings { get; set; } = FbxExportSettings.FromFlags(FbxExportSettings.DefaultFlags);

    /// <summary>A copy with its own item list (Duplicate).</summary>
    public EzPreset Clone()
    {
        var copy = new EzPreset { Name = Name, Checked = Checked, Folder = Folder, Key = Key, Settings = Settings };
        copy.Items.AddRange(Items);
        return copy;
    }
}

/// <summary>
/// Reads and writes a scene's EZ Exports presets through <see cref="PluginSceneData"/>: the items are
/// references (they follow renames and moves) and the rest is a small JSON text that refers to them
/// by index.
/// </summary>
public static class EzPresetStore
{
    public const string PluginId = "sloppy.ezexports";

    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The scene's presets; empty when it has none. Unreadable text gives no presets and an <paramref name="error"/>.</summary>
    public static List<EzPreset> Load(SceneModel model) => Load(model, out _);

    public static List<EzPreset> Load(SceneModel model, out string? error)
    {
        error = null;
        var (text, refs) = PluginSceneData.Read(model, PluginId);
        var presets = new List<EzPreset>();
        if (string.IsNullOrWhiteSpace(text)) return presets;
        FileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<FileDto>(text, Json);
        }
        catch (JsonException e)
        {
            error = "The scene's EZ Exports presets could not be read: " + e.Message;
            return presets;
        }
        foreach (var dto in file?.Presets ?? [])
        {
            var preset = new EzPreset
            {
                Name = dto.Name ?? "",
                Checked = dto.Checked,
                Folder = dto.Folder ?? "",
                Key = dto.Key ?? "",
                Settings = FbxExportSettings.FromFlags(dto.Flags) with
                {
                    FolderSeparator = dto.Separator ?? FbxExportSettings.DefaultFolderSeparator,
                    ScaleFactor = FbxExportSettings.IsValidScaleFactor(dto.Scale) ? dto.Scale : 1f,
                },
            };
            foreach (int index in dto.Items ?? [])
                preset.Items.Add(index >= 0 && index < refs.Count && refs[index] is Animation or Folder or Character ? refs[index] : null);
            presets.Add(preset);
        }
        return presets;
    }

    /// <summary>
    /// Stores <paramref name="presets"/> in the scene. An empty list removes the plugin's row; a scene
    /// that never had presets is left untouched. Returns whether the stored data changed.
    /// </summary>
    public static bool Save(SceneModel model, IReadOnlyList<EzPreset> presets)
    {
        var (oldText, oldRefs) = PluginSceneData.Read(model, PluginId);
        if (presets.Count == 0)
        {
            if (oldText == null) return false;
            PluginSceneData.Write(model, PluginId, null, []);
            return true;
        }

        var refs = new List<SceneObject?>();
        var index = new Dictionary<SceneObject, int>(ReferenceEqualityComparer.Instance);
        int IndexOf(SceneObject? item)
        {
            if (item != null && index.TryGetValue(item, out int i)) return i;
            refs.Add(item);
            if (item != null) index[item] = refs.Count - 1;
            return refs.Count - 1;
        }

        var file = new FileDto
        {
            Version = FormatVersion,
            Presets = presets.Select(p => new PresetDto
            {
                Name = p.Name,
                Checked = p.Checked,
                Items = p.Items.Select(i => IndexOf(i is null || !PluginSceneData.IsAttached(model, i.Source) ? null : i)).ToList(),
                Folder = p.Folder,
                Key = p.Key,
                Flags = p.Settings.ToFlags(),
                Separator = p.Settings.FolderSeparator,
                Scale = p.Settings.ScaleFactor,
            }).ToList(),
        };
        string text = JsonSerializer.Serialize(file, Json);
        if (text == oldText && refs.Count == oldRefs.Count && refs.Zip(oldRefs).All(z => ReferenceEquals(z.First, z.Second)))
            return false;
        PluginSceneData.Write(model, PluginId, text, refs);
        return true;
    }

    private sealed class FileDto
    {
        public int Version { get; set; }
        public List<PresetDto>? Presets { get; set; }
    }

    private sealed class PresetDto
    {
        public string? Name { get; set; }
        public bool Checked { get; set; } = true;
        public List<int>? Items { get; set; }
        public string? Folder { get; set; }
        public string? Key { get; set; }
        public uint Flags { get; set; } = FbxExportSettings.DefaultFlags;
        public string? Separator { get; set; }
        public float Scale { get; set; } = 1f;
    }
}

using System.Text;
using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Core.Plugins.EzExports;

/// <summary>
/// One FBX file a preset writes: one animation with the character it belongs to, or (a null
/// <see cref="Animation"/>) a character's model without animations.
/// </summary>
public sealed record EzExportFile(string Path, Character Character, Animation? Animation, FbxExportOptions Options);

/// <summary>What running a preset would write. <see cref="Error"/> set means it writes nothing.</summary>
public sealed record EzExportPlan(EzPreset Preset, string? Folder, IReadOnlyList<EzExportFile> Files,
    int MissingItems, IReadOnlyList<string> Warnings, string? Error);

/// <summary>
/// Turns a preset into the files it writes: its items resolved to animations (a folder gives every
/// animation under it, sub-folders included; duplicates once; deleted items skipped) and models (a
/// character item; with "No animations" also the characters of the animation items), its folder
/// resolved against the scene file, and one file per animation or model named from its key.
/// </summary>
public static class EzExportPlanner
{
    /// <summary>What a character item is called in lists and paths: <c>Hero (model)</c>.</summary>
    public const string ModelSuffix = " (model)";

    /// <summary>The name an item shows in the preset's list: an animation's name, a folder's name with a trailing slash, a character's name with " (model)".</summary>
    public static string ItemName(SceneObject? item) => item switch
    {
        null => "(missing)",
        Character c => (c.Name ?? "") + ModelSuffix,
        Folder f => (f.IsInternal ? "Anim Bank" : f.Name ?? "") + "/",
        _ => item.Name ?? "",
    };

    /// <summary>The item's full Anim Bank path, e.g. <c>Hero: Locomotion/Run/Run_Fast</c>; a character's is <c>Hero (model)</c>.</summary>
    public static string ItemPath(SceneObject? item)
    {
        if (item == null) return "(missing)";
        if (item is Character) return ItemName(item);
        var parts = new List<string>();
        if (item is Folder { IsInternal: true }) parts.Add("Anim Bank");
        else parts.Add(item.Name ?? "");
        for (var o = item.Parent; o is Folder f && !f.IsInternal; o = o.Parent) parts.Insert(0, f.Name ?? "");
        string path = string.Join("/", parts) + (item is Folder ? "/" : "");
        string? character = (item as Animation)?.Character?.Name ?? item.Ancestor<Character>()?.Name;
        return character is { Length: > 0 } ? character + ": " + path : path;
    }

    /// <summary>The character an item belongs to: itself, or the one whose Anim Bank holds it.</summary>
    public static Character? CharacterOf(SceneObject? item) =>
        item as Character ?? (item as Animation)?.Character ?? item?.Ancestor<Character>();

    /// <summary>Whether <paramref name="item"/> still exists in <paramref name="model"/>.</summary>
    public static bool IsLive(SceneModel model, SceneObject? item) =>
        item != null && item.Model == model && PluginSceneData.IsAttached(model, item.Source);

    /// <summary>
    /// The animations the preset exports, in item order (a folder's animations in bank order), each
    /// once. <paramref name="missing"/> counts items that no longer exist.
    /// </summary>
    public static IReadOnlyList<Animation> Animations(SceneModel model, EzPreset preset, out int missing)
    {
        missing = 0;
        var seen = new HashSet<Animation>(ReferenceEqualityComparer.Instance);
        var result = new List<Animation>();
        foreach (var item in preset.Items)
        {
            if (!IsLive(model, item))
            {
                missing++;
                continue;
            }
            IEnumerable<Animation> found = item switch
            {
                Animation a => [a],
                Folder f => f.Descendants<Animation>(),
                _ => [],
            };
            foreach (var a in found)
                if (seen.Add(a)) result.Add(a);
        }
        return result;
    }

    /// <summary>
    /// The preset's output folder: an absolute <see cref="EzPreset.Folder"/> as it is, a relative one
    /// under the scene file's folder, an empty one the scene file's folder. Null with an
    /// <paramref name="error"/> when a relative folder has no saved scene to resolve against.
    /// </summary>
    public static string? ResolveFolder(string folder, string? scenePath, out string? error)
    {
        error = null;
        folder = folder.Trim();
        if (folder.Length > 0 && Path.IsPathFullyQualified(folder)) return Path.GetFullPath(folder);
        string? sceneFolder = string.IsNullOrEmpty(scenePath) ? null : Path.GetDirectoryName(Path.GetFullPath(scenePath));
        if (sceneFolder == null)
        {
            error = folder.Length == 0
                ? "No export folder: save the scene first, or choose a folder."
                : $"The folder \"{folder}\" is relative to the scene file: save the scene first, or choose an absolute folder.";
            return null;
        }
        return folder.Length == 0 ? sceneFolder : Path.GetFullPath(Path.Combine(sceneFolder, folder));
    }

    /// <summary>The file name stem: the key, else the preset's name, else "export".</summary>
    public static string Stem(EzPreset preset) =>
        SafeFileName(preset.Key.Trim() is { Length: > 0 } key ? key
            : preset.Name.Trim() is { Length: > 0 } name ? name : "export");

    /// <summary>
    /// The characters whose model the preset writes, each once, in item order: every character item,
    /// and with "No animations" on also the character of every animation and folder item (the
    /// animations themselves are then not written).
    /// </summary>
    public static IReadOnlyList<Character> Models(SceneModel model, EzPreset preset)
    {
        var result = new List<Character>();
        foreach (var item in preset.Items)
        {
            if (!IsLive(model, item)) continue;
            var character = item is Character c ? c : preset.Settings.WithoutAnimations ? CharacterOf(item) : null;
            if (character != null && !result.Contains(character)) result.Add(character);
        }
        return result;
    }

    /// <summary>What running <paramref name="preset"/> writes in <paramref name="model"/> saved at <paramref name="scenePath"/>.</summary>
    public static EzExportPlan Plan(SceneModel model, string? scenePath, EzPreset preset)
    {
        var s = preset.Settings;
        var all = Animations(model, preset, out int missing);
        // "No animations" turns the animation items into their characters' models (see Models).
        IReadOnlyList<Animation> animations = s.WithoutAnimations ? [] : all;
        var models = Models(model, preset);
        var warnings = new List<string>();
        if (missing > 0) warnings.Add(missing == 1 ? "1 missing item skipped" : $"{missing} missing items skipped");
        string? folder = ResolveFolder(preset.Folder, scenePath, out string? error);
        if (folder == null) return new EzExportPlan(preset, null, [], missing, warnings, error);
        if (animations.Count == 0 && models.Count == 0)
            return new EzExportPlan(preset, folder, [], missing, warnings,
                preset.Items.Count == 0 ? "The preset has no items: add a character (its model) or animations."
                    : "None of the preset's items holds a character or an animation.");

        string stem = Stem(preset);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<EzExportFile>();
        // One file in all: the key alone. Several: the key with the character or animation name.
        bool single = animations.Count + models.Count == 1;
        string Unique(string name)
        {
            string unique = name;
            for (int n = 2; !taken.Add(unique); n++) unique = name + "_" + n;
            return Path.Combine(folder, unique + ".fbx");
        }
        foreach (var character in models)
        {
            string name = single ? stem : stem + "_" + SafeFileName(character.Name ?? "character");
            var options = s.ToOptions([character], [], [], scenePath, folder);
            files.Add(new EzExportFile(Unique(name), character, null, options));
        }
        foreach (var a in animations)
        {
            if (a.Character is not { } character)
            {
                warnings.Add($"\"{a.Name}\" belongs to no character, skipped");
                continue;
            }
            string name = single ? stem : stem + "_" + SafeFileName(s.AnimationFileName(a));
            var options = s.ToOptions([character], [], [a], scenePath, folder);
            files.Add(new EzExportFile(Unique(name), character, a, options));
        }
        return new EzExportPlan(preset, folder, files, missing, warnings,
            files.Count == 0 ? "None of the preset's animations belongs to a character." : null);
    }

    /// <summary><paramref name="name"/> with path separators and characters a file name cannot hold replaced by '-'.</summary>
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char ch in name)
            sb.Append(ch == '/' || ch == '\\' || Array.IndexOf(invalid, ch) >= 0 ? '-' : ch);
        return sb.Length == 0 ? "unnamed" : sb.ToString();
    }
}

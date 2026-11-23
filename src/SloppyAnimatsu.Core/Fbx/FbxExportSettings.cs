using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Core.Fbx;

/// <summary>Which target the Export Settings window's preset row prepares the file for.</summary>
public enum FbxExportTarget { Default = 0, Blender = 1, Unity = 2, Godot = 3 }

/// <summary>What the Export Settings window exports: the whole scene, or the selected characters.</summary>
public enum FbxExportScope { Scene = 0, Selection = 1 }

/// <summary>
/// The choices of the Export Settings window, stored as one flags word: the low byte keeps the
/// classic eight-box layout (bake 0x01, ASCII 0x02, embed media 0x04, no animations 0x08,
/// character node 0x10, one file per animation 0x20, camera targets 0x40 stored inverted, one file
/// per character 0x80); bits 8-9 hold the target, bit 10 the selection scope, bit 11 "only
/// selected animations" and bit 12 "folder path in file names". The folder separator is text and
/// is stored beside the word.
/// </summary>
public sealed record FbxExportSettings
{
    public const uint DefaultFlags = 0x55;

    /// <summary>The folder separator until the user types another one.</summary>
    public const string DefaultFolderSeparator = "_";

    public FbxExportTarget Target { get; init; }
    public FbxExportScope Scope { get; init; }
    public bool OnlySelectedAnimations { get; init; }
    public bool Baked { get; init; } = true;
    public bool OneFilePerAnimation { get; init; }

    /// <summary>Per-animation files are named with the animation's Anim Bank folders as well
    /// (<c>&lt;file&gt;_Folder1_Folder2_Run.fbx</c>); off, with its name alone (<c>&lt;file&gt;_Run.fbx</c>).</summary>
    public bool FolderPathInName { get; init; }

    /// <summary>What joins the folders and the animation name when <see cref="FolderPathInName"/> is on.</summary>
    public string FolderSeparator { get; init; } = DefaultFolderSeparator;
    public bool OneFilePerCharacter { get; init; }
    public bool WithoutAnimations { get; init; }
    public bool CameraTargets { get; init; }
    public bool CharacterNode { get; init; } = true;
    public bool EmbedMedia { get; init; } = true;
    public bool Ascii { get; init; }

    /// <summary>The settings for <paramref name="target"/>. From <paramref name="keep"/> (a preset
    /// button pressed in the window) only the target changes: the target picks the exporter preset
    /// (hierarchy, key interpolation and clip naming) and every option keeps its value, with
    /// <see cref="Warnings"/> saying when one does not suit the target. Without it, the base settings:
    /// baked for Default (engine targets write fitted keys instead, which play the same with far fewer
    /// keys), with the character node and embedded media, in binary.</summary>
    public static FbxExportSettings For(FbxExportTarget target, FbxExportSettings? keep = null) =>
        keep is not null
            ? keep with { Target = target }
            : FromFlags(DefaultFlags) with { Target = target, Baked = target == FbxExportTarget.Default };

    /// <summary>
    /// What in these settings the target reads badly, or what has no effect, one sentence each;
    /// empty when nothing is wrong. The window shows them under the options; exporting still works.
    /// </summary>
    public IReadOnlyList<string> Warnings()
    {
        var warnings = ToOptions(null, null, null, null, null).CompatibilityWarnings().ToList();
        if (WithoutAnimations)
        {
            if (OneFilePerAnimation)
                warnings.Add("One file per animation does nothing while exporting without animations.");
            if (OnlySelectedAnimations)
                warnings.Add("Only Selected Animations does nothing while exporting without animations.");
        }
        else
        {
            if (!Baked && Target == FbxExportTarget.Default)
                warnings.Add("Not baked with the Default target: the scene's own keys are written, and Unity, Godot and Blender blend rotation keys differently, so joints can twist or flip between keys there. Pick that program's target, or tick Baked.");
            if (FolderPathInName && !OneFilePerAnimation)
                warnings.Add("Folder path in file names only applies with one file per animation.");
        }
        return warnings;
    }

    /// <summary>The name a per-animation file gets after <c>&lt;file&gt;_</c>: the animation's name, or
    /// with <see cref="FolderPathInName"/> its Anim Bank folders and name joined by
    /// <see cref="FolderSeparator"/>. Not yet made safe for a file name.</summary>
    public string AnimationFileName(Animation animation) => AnimationFileName(animation.Name, animation.FolderPath);

    /// <summary><see cref="AnimationFileName(Animation)"/> for an animation called
    /// <paramref name="name"/> inside <paramref name="folders"/> (outermost first).</summary>
    public string AnimationFileName(string? name, IEnumerable<string> folders)
    {
        string shown = string.IsNullOrEmpty(name) ? "unnamed" : name;
        return FolderPathInName
            ? string.Join(FolderSeparator ?? "", folders.Append(shown))
            : shown;
    }

    /// <summary>The preset buttons' labels and hover text, in <see cref="FbxExportTarget"/> order.</summary>
    public static IReadOnlyList<(FbxExportTarget Target, string Label, string Tip)> Targets { get; } =
    [
        (FbxExportTarget.Default, "Default",
            "A general-purpose FBX: the scene's hierarchy and keys as they are (tick Baked so other programs play the motion the same). Changes only the target; the options below keep their values."),
        (FbxExportTarget.Blender, "Blender",
            "Prepared for Blender's FBX importer: a plain joint hierarchy so bone positions come out exact, fitted linear keys (only the keys the motion needs), no zero scales, unique clip names, binary FBX (Blender cannot read ASCII FBX). Changes only the target; the options below keep their values."),
        (FbxExportTarget.Unity, "Unity",
            "Prepared for Unity: a plain joint hierarchy so bone positions come out exact, fitted keys with tangents (only the keys the motion needs), no zero scales and unique clip names, so the rig and every clip import directly. Changes only the target; the options below keep their values."),
        (FbxExportTarget.Godot, "Godot",
            "Prepared for Godot 4's FBX import: a plain joint hierarchy, fitted keys with tangents, no zero scales, unique animation names and embedded textures. Changes only the target; the options below keep their values."),
    ];

    /// <summary>The scope drop-down's rows and hover text, in <see cref="FbxExportScope"/> order.</summary>
    public static IReadOnlyList<(FbxExportScope Scope, string Label, string Tip)> Scopes { get; } =
    [
        (FbxExportScope.Scene, "Export Scene", "Export every character and mesh in the scene."),
        (FbxExportScope.Selection, "Export Selection",
            "Export only the characters that own a selected object (the character, one of its joints or controllers, or a mesh bound to it), plus selected unbound meshes."),
    ];

    /// <summary>One toggle of the Export Settings window.</summary>
    public sealed record Option(string Label, string Tip, Func<FbxExportSettings, bool> Get,
        Func<FbxExportSettings, bool, FbxExportSettings> Set);

    /// <summary>The window's toggles, one per line, in display order.</summary>
    public static IReadOnlyList<Option> Options { get; } =
    [
        new("Only Selected Animations",
            "Export only the animations selected in the Anim Bank. A selected folder exports every animation inside it, sub-folders included. Off: every animation of the exported characters.",
            s => s.OnlySelectedAnimations, (s, v) => s with { OnlySelectedAnimations = v }),
        new("Baked Animations",
            "Write a key on every frame for every joint, so the motion plays the same in any program (largest file). Off: for the Unity, Godot and Blender presets, write only the keys the motion needs, with explicit tangents, so engines play it within a small tolerance with far fewer keys; for akeytsu, write the scene's own keys and tangents.",
            s => s.Baked, (s, v) => s with { Baked = v }),
        new("Multi-FBX (one file per animation)",
            "Write one FBX per animation, named <file>_<animation name>.fbx; each holds the exported characters and that one animation. Two animations with the same name get _2, _3… added.",
            s => s.OneFilePerAnimation, (s, v) => s with { OneFilePerAnimation = v }),
        new("Folder path in file names",
            "With one file per animation, name each file with the animation's Anim Bank folders too: <file>_<Folder1><sep><Folder2><sep><animation>.fbx. The box beside it is the separator (underscore by default). Off: the animation's name alone.",
            s => s.FolderPathInName, (s, v) => s with { FolderPathInName = v }),
        new("Multi-FBX (one file per character)",
            "Write one FBX per character, named <file>_<character>.fbx. With one file per animation as well, each character's animations get a file each.",
            s => s.OneFilePerCharacter, (s, v) => s with { OneFilePerCharacter = v }),
        new("Export without animations",
            "Write the characters and meshes in their rest pose with no animation takes: a rig/skin-only file.",
            s => s.WithoutAnimations, (s, v) => s with { WithoutAnimations = v }),
        new("Export camera targets",
            "Also export the camera rigs' target objects as empty (Null) nodes, with their animation. Off: they are left out.",
            s => s.CameraTargets, (s, v) => s with { CameraTargets = v }),
        new("Export Character node (as dummy)",
            "Write each character as an empty root node above its joints and meshes. Off: the joints and meshes sit at the top of the file, with the character's own transform folded into them.",
            s => s.CharacterNode, (s, v) => s with { CharacterNode = v }),
        new("Embed Media",
            "Store the texture images inside the FBX. Off: the FBX names the texture files, and the images are written beside it (an existing file of the same name is kept).",
            s => s.EmbedMedia, (s, v) => s with { EmbedMedia = v }),
        new("Use ASCII format",
            "Write the human-readable text form of FBX instead of binary. Larger and slower to load; Blender cannot import ASCII FBX.",
            s => s.Ascii, (s, v) => s with { Ascii = v }),
    ];

    /// <summary>Where "Folder path in file names" sits in <see cref="Options"/>; the window puts the
    /// separator box beside it.</summary>
    public static int FolderPathOptionIndex { get; } =
        Options.ToList().FindIndex(o => o.Label == "Folder path in file names");

    /// <summary>The settings a flags word holds.</summary>
    public static FbxExportSettings FromFlags(uint flags) => new()
    {
        Baked = (flags & 0x01) != 0,
        Ascii = (flags & 0x02) != 0,
        EmbedMedia = (flags & 0x04) != 0,
        WithoutAnimations = (flags & 0x08) != 0,
        CharacterNode = (flags & 0x10) != 0,
        OneFilePerAnimation = (flags & 0x20) != 0,
        CameraTargets = (flags & 0x40) == 0,
        OneFilePerCharacter = (flags & 0x80) != 0,
        Target = (FbxExportTarget)((flags >> 8) & 3),
        Scope = (flags & 0x400) != 0 ? FbxExportScope.Selection : FbxExportScope.Scene,
        OnlySelectedAnimations = (flags & 0x800) != 0,
        FolderPathInName = (flags & 0x1000) != 0,
    };

    /// <summary>The flags word these settings store as.</summary>
    public uint ToFlags() =>
        (Baked ? 0x01u : 0) | (Ascii ? 0x02u : 0) | (EmbedMedia ? 0x04u : 0) | (WithoutAnimations ? 0x08u : 0) |
        (CharacterNode ? 0x10u : 0) | (OneFilePerAnimation ? 0x20u : 0) | (CameraTargets ? 0 : 0x40u) |
        (OneFilePerCharacter ? 0x80u : 0) | ((uint)Target & 3) << 8 |
        (Scope == FbxExportScope.Selection ? 0x400u : 0) | (OnlySelectedAnimations ? 0x800u : 0) |
        (FolderPathInName ? 0x1000u : 0);

    /// <summary>The exporter preset the target uses.</summary>
    public FbxExportPreset Preset => Target switch
    {
        FbxExportTarget.Blender => FbxExportPreset.Blender,
        FbxExportTarget.Unity => FbxExportPreset.Unity,
        FbxExportTarget.Godot => FbxExportPreset.Godot,
        _ => FbxExportPreset.Plain,
    };

    /// <summary>
    /// The characters the scope exports: every character for the scene, otherwise each character
    /// that owns a selected object (the character itself, one of its joints or controllers, or a
    /// mesh bound to it).
    /// </summary>
    public IReadOnlyList<Character> Characters(SceneModel model, IEnumerable<Object3D> selection)
    {
        if (Scope == FbxExportScope.Scene) return model.Scene.Characters.ToList();
        var owners = new HashSet<Character>(ReferenceEqualityComparer.Instance);
        foreach (var o in selection)
            if (OwnerOf(o) is { } c) owners.Add(c);
        return model.Scene.Characters.Where(owners.Contains).ToList();
    }

    /// <summary>The loose meshes the scope exports: every one for the scene, the selected ones otherwise.</summary>
    public IReadOnlyList<Mesh>? LooseMeshes(IEnumerable<Object3D> selection) =>
        Scope == FbxExportScope.Scene ? null
            : selection.OfType<Mesh>().Where(m => m.BindPose?.Character == null).ToList();

    /// <summary>The character an object belongs to: itself, its ancestor, or the one a mesh is bound to.</summary>
    public static Character? OwnerOf(Object3D o) =>
        o as Character ?? o.Ancestor<Character>() ?? (o as Mesh)?.BindPose?.Character;

    /// <summary>
    /// The exporter options for one file. <paramref name="characters"/> and
    /// <paramref name="animations"/> are what that file holds (null = what the scope and
    /// <see cref="WithoutAnimations"/> give).
    /// </summary>
    public FbxExportOptions ToOptions(IReadOnlyCollection<Character>? characters, IReadOnlyCollection<Mesh>? looseMeshes,
        IReadOnlyCollection<Animation>? animations, string? documentPath, string? textureDirectory) => new()
    {
        Preset = Preset,
        Baked = Baked,
        Characters = characters,
        LooseMeshes = looseMeshes,
        Animations = WithoutAnimations ? [] : animations,
        ExportCharacterNode = CharacterNode,
        ExportCameraTargets = CameraTargets,
        EmbedMedia = EmbedMedia,
        Ascii = Ascii,
        DocumentPath = documentPath,
        TextureDirectory = textureDirectory,
    };
}

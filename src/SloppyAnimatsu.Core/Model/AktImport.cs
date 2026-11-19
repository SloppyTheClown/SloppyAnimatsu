using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>How <see cref="AktImport.Import(SceneModel, AktDocument, AktImportOptions?)"/> merges a file.</summary>
public sealed class AktImportOptions
{
    /// <summary>
    /// Import a file whose version header is not the running version. The app refuses those and
    /// asks for the file to be re-saved; this override is a SloppyAnimatsu convenience (<c>--force</c>).
    /// </summary>
    public bool AllowDifferentVersion { get; init; }

    /// <summary>Receives each warning as it is found.</summary>
    public Action<string>? Warning { get; init; }
}

/// <summary>What an <see cref="AktImport"/> did.</summary>
public sealed class AktImportResult
{
    /// <summary>The imported roots and the materials/textures added to the scene's pools, in attach order.</summary>
    public required IReadOnlyList<SceneObject> Objects { get; init; }
    /// <summary>The imported characters (there may be more than one, and names may repeat).</summary>
    public required IReadOnlyList<Character> Characters { get; init; }
    /// <summary>The imported characters' user animations (the Anim Bank contents, not <c>__RigAnim</c>).</summary>
    public required IReadOnlyList<Animation> Animations { get; init; }
    public required IReadOnlyList<Material> Materials { get; init; }
    public required IReadOnlyList<Texture> Textures { get; init; }
    /// <summary>Direct children of the imported scene that were <c>CCameraBase</c> subclasses and were left out.</summary>
    public int SkippedCameras { get; init; }
    /// <summary>Direct children of the imported scene that were <c>CBlendShapesSight</c> objects and were left out.</summary>
    public int SkippedBlendShapeSights { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Merges another `.akt` scene into an open one, as the File &gt; Import of an `.akt` does
/// the source scene's direct children are attached to the open
/// scene, except cameras and blendshape sights; the children of the source's <c>__Materials</c>
/// and <c>__Textures</c> dummies are appended to the open scene's pools. The imported character
/// brings its rig, meshes, poses and whole Anim Bank, which is how an animation from another
/// scene comes across.
/// </summary>
public static class AktImport
{
    public const string MaterialDummyName = "__Materials";
    public const string TextureDummyName = "__Textures";

    /// <summary>The message shown when an import is refused for its format version.</summary>
    public const string VersionMismatchMessage =
        "This file was saved with a different format version, so it cannot be imported. " +
        "Open it, save it again, then import it.";

    private const string SceneClass = "class CScene";
    private const string CharacterClass = "class CCharacter";
    private const string CameraBaseClass = "class CCameraBase";
    private const string BlendShapeSightClass = "class CBlendShapesSight";
    private const string DummyClass = "class CDummy";

    public static AktImportResult Import(SceneModel model, string path, AktImportOptions? options = null) =>
        Import(model, AktDocument.Load(path), options, path);

    /// <summary>Merges <paramref name="source"/> into <paramref name="model"/>'s document.</summary>
    /// <param name="importedFile">
    /// The file the source was read from, when it came from one: the merge then points the open
    /// scene's <c>CScene.FBXPath</c> at that file's folder, as the Import does
    /// </param>
    public static AktImportResult Import(SceneModel model, AktDocument source, AktImportOptions? options = null,
        string? importedFile = null)
    {
        options ??= new AktImportOptions();
        var warnings = new List<string>();
        void Warn(string message)
        {
            warnings.Add(message);
            options.Warning?.Invoke(message);
        }

        if (!VersionMatches(source))
        {
            if (!options.AllowDifferentVersion)
                throw new InvalidOperationException(VersionMismatchMessage);
            Warn($"The file is version {(source.HasHeader ? source.Version.ToString() : "legacy (no header)")}, " +
                 $"not {AktVersion.Current}; importing it anyway.");
        }

        var sourceScene = source.Roots.FirstOrDefault(r => IsA(source, r, SceneClass))
                          ?? throw new InvalidDataException("The file has no CScene root, so there is nothing to import.");
        var targetScene = model.Document.Root;

        // Classify the source scene's direct children.
        var roots = new List<AktObject>();
        var materialObjects = new List<AktObject>();
        var textureObjects = new List<AktObject>();
        bool hasMaterialDummy = false, hasTextureDummy = false;
        int skippedCameras = 0, skippedSights = 0;
        foreach (var child in sourceScene.Children.ToList())
        {
            if (IsA(source, child, CameraBaseClass)) { skippedCameras++; continue; }
            if (IsA(source, child, BlendShapeSightClass)) { skippedSights++; continue; }
            if (IsNamedDummy(source, child, MaterialDummyName))
            {
                hasMaterialDummy = true;
                materialObjects.AddRange(child.Children);
                continue;
            }
            if (IsNamedDummy(source, child, TextureDummyName))
            {
                hasTextureDummy = true;
                textureObjects.AddRange(child.Children);
                continue;
            }
            roots.Add(child);
        }

        // Clone the accepted subtrees and map every source object to its copy. Cloning keeps the
        // source document untouched; the app moves the objects, which builds the same target tree.
        var map = new Dictionary<AktObject, AktObject>(ReferenceEqualityComparer.Instance);
        var rootCopies = roots.Select(r => Clone(r, map)).ToList();
        var materialCopies = materialObjects.Select(o => Clone(o, map)).ToList();
        var textureCopies = textureObjects.Select(o => Clone(o, map)).ToList();
        var allCopies = rootCopies.Concat(materialCopies).Concat(textureCopies).ToList();
        var copySet = new HashSet<AktObject>(
            allCopies.SelectMany(c => c.PreOrder()), ReferenceEqualityComparer.Instance);

        bool schemaChanged = AktSchemaMerge.Merge(model.Document.Schema, source.Schema, allCopies, "the imported .akt");

        // References to an imported object point at its copy; anything else (the source scene, a
        // dropped camera) becomes null. Struct values and lists are walked too. A subtree's internal
        // references already point at copies after CloneHierarchy, so a copy maps to itself.
        foreach (var copy in allCopies)
            foreach (var obj in copy.PreOrder())
                foreach (var field in obj.Fields)
                    AktRefs.Remap(field.Value, target =>
                        copySet.Contains(target) ? target : map.TryGetValue(target, out var mapped) ? mapped : null);

        // A file from an older version may lack members the target's schema knows (schema evolution).
        foreach (var copy in allCopies)
            foreach (var obj in copy.PreOrder())
                AktSchemaMerge.FillMissingMembers(model.Document.Schema, obj);

        if (hasMaterialDummy)
        {
            var dummy = GetOrCreateDummy(model, targetScene, MaterialDummyName);
            foreach (var copy in materialCopies)
            {
                // The app makes an imported name unique against the target's own children, so a
                // material the open scene already has becomes `X_02`
                copy.Set("Name", UniqueNames.For(dummy, copy.Name ?? ""));
                dummy.AddChild(copy);
            }
        }
        if (hasTextureDummy)
        {
            var dummy = GetOrCreateDummy(model, targetScene, TextureDummyName);
            foreach (var copy in textureCopies)
            {
                // The texture pool numbers like a file name (e.g. Dup_b_NoTrans.png ->
                // Dup_b_NoTrans_02.png, and ..._02.png -> ..._02_02.png).
                copy.Set("Name", UniqueNames.ForFile(dummy, copy.Name ?? ""));
                dummy.AddChild(copy);
            }
        }
        foreach (var copy in rootCopies)
        {
            copy.Set("Name", UniqueNames.For(targetScene, copy.Name ?? ""));
            targetScene.AddChild(copy);
        }

        // If a class gained members from the source, every object already in the target must carry
        // them as well or the writer cannot write the class's member list.
        if (schemaChanged)
            foreach (var obj in targetScene.PreOrder())
                AktSchemaMerge.FillMissingMembers(model.Document.Schema, obj);

        // The.akt import points the open scene's FBX folder at the imported file's folder
        // Save leaves FBXPath alone, so the import is
        // what writes it; it was the only member in which the merged file differed from the.
        if (!string.IsNullOrEmpty(importedFile))
            ScenePaths.Remember(model, ScenePaths.FbxPath, importedFile);

        var characters = rootCopies
            .Where(c => IsA(model.Document, c, CharacterClass))
            .Select(c => (Character)model.Wrap(c))
            .ToList();
        var materials = materialCopies.Select(c => (Material)model.Wrap(c)).ToList();
        var textures = textureCopies.Select(c => (Texture)model.Wrap(c)).ToList();
        return new AktImportResult
        {
            Objects = rootCopies.Concat(materialCopies).Concat(textureCopies).Select(model.Wrap).ToList(),
            Characters = characters,
            Animations = characters.SelectMany(c => c.Animations).ToList(),
            Materials = materials,
            Textures = textures,
            SkippedCameras = skippedCameras,
            SkippedBlendShapeSights = skippedSights,
            Warnings = warnings,
        };
    }

    /// <summary>The app compares the first four header fields with the running version (the fifth is ignored).</summary>
    private static bool VersionMatches(AktDocument document)
    {
        if (!document.HasHeader) return false;
        var v = document.Version;
        var current = AktVersion.Current;
        return v.Major == current.Major && v.Minor == current.Minor
               && v.Build == current.Build && v.Revision == current.Revision;
    }

    private static AktObject Clone(AktObject source, Dictionary<AktObject, AktObject> map)
    {
        var copy = source.CloneHierarchy();
        var originals = source.PreOrder().ToList();
        var copies = copy.PreOrder().ToList();
        if (originals.Count != copies.Count)
            throw new InvalidOperationException($"{source} did not clone to the same shape.");
        for (int i = 0; i < originals.Count; i++) map[originals[i]] = copies[i];
        return copy;
    }

    /// <summary>The scene's direct <c>CDummy</c> with that name, created (as the original does) when absent.</summary>
    private static AktObject GetOrCreateDummy(SceneModel model, AktObject scene, string name)
    {
        foreach (var child in scene.Children)
            if (IsNamedDummy(model.Document, child, name)) return child;
        var dummy = model.Document.Schema.NewObject(DummyClass);
        dummy.Set("Name", name);
        scene.AddChild(dummy);
        return dummy;
    }

    private static bool IsNamedDummy(AktDocument document, AktObject obj, string name) =>
        IsA(document, obj, DummyClass) && obj.Name == name;

    private static bool IsA(AktDocument document, AktObject obj, string classKey)
    {
        var key = AktNames.Key(classKey);
        for (var c = document.Schema.FindClass(obj.TypeName); c != null; c = c.Parent)
            if (c.Key == key) return true;
        return obj.ClassKey == key;
    }
}

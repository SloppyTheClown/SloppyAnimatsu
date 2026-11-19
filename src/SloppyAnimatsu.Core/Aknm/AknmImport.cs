using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Core.Aknm;

/// <summary>An `.aknm` opened for import: its character root and the animations it carries.</summary>
public sealed class AknmFile
{
    public AktDocument Document { get; }
    /// <summary>The <c>CCharacterAKNM</c> root object.</summary>
    public AktObject Character { get; }
    public string Name => Character.Name ?? "Character";
    /// <summary>The user animations, in file order; <c>__RigAnim</c> is excluded.</summary>
    public IReadOnlyList<AktObject> Animations { get; }
    /// <summary><c>__RigAnim</c>, when the file has one.</summary>
    public AktObject? RigAnimation { get; }

    private AknmFile(AktDocument document, AktObject character, IReadOnlyList<AktObject> animations, AktObject? rig)
    {
        Document = document;
        Character = character;
        Animations = animations;
        RigAnimation = rig;
    }

    public static AknmFile Read(string path) => Read(AktDocument.Load(path));

    public static AknmFile Read(AktDocument document)
    {
        if (document.Roots.Count != 1)
            throw new InvalidDataException($"An .aknm has one root object; this file has {document.Roots.Count}.");
        var root = document.Roots[0];
        if (!AknmFormat.IsA(document, root, AknmFormat.CharacterClass))
            throw new InvalidDataException($"The root of the file is {root.TypeName}, not a CCharacterAKNM.");

        var animations = new List<AktObject>();
        AktObject? rig = null;
        foreach (var obj in root.PreOrder().Skip(1))
        {
            if (!AknmFormat.IsA(document, obj, AknmFormat.AnimationClass)) continue;
            if (obj.Name == AknmFormat.RigAnimationName) rig = obj;
            else animations.Add(obj);
        }
        return new AknmFile(document, root, animations, rig);
    }
}

/// <summary>How <see cref="AknmImport.Import"/> copies a file onto a scene's character.</summary>
public sealed class AknmImportOptions
{
    /// <summary>The target character; default: the scene's current character.</summary>
    public Character? Character { get; init; }
    /// <summary>Put the animations in a new "Retargeted From &lt;source name&gt;" folder.</summary>
    public bool RetargetFolder { get; init; } = true;
    /// <summary>Import even when the two rigs' reference poses differ (the keys are rest-pose deltas).</summary>
    public bool AllowDifferentPose { get; init; }
    /// <summary>Receives each warning as it is found.</summary>
    public Action<string>? Warning { get; init; }
}

/// <summary>What an import did.</summary>
public sealed class AknmImportResult
{
    public required Character Character { get; init; }
    public required IReadOnlyList<Animation> Animations { get; init; }
    /// <summary>The folder the animations went into (the "Retargeted From ..." folder, or the bank root).</summary>
    public required Folder Destination { get; init; }
    /// <summary>Source controllers that found no counterpart in the target character.</summary>
    public int UnmatchedControllers { get; init; }
    /// <summary>Controller pairs whose reference-pose world translations differ.</summary>
    public int MismatchedControllers { get; init; }
    /// <summary>Controller pairs that could be compared (both sides have a reference pose).</summary>
    public int ComparedControllers { get; init; }
    /// <summary>Tracks that were dropped because their target controller had no counterpart.</summary>
    public int DroppedTracks { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Reads an `.aknm` and copies its animations onto a character of the open scene
/// pairs the two <c>Controllers</c> lists, checks the reference
/// poses, recreates the source Anim Bank folder path under "Retargeted From &lt;source&gt;" and
/// clones the animation subtrees with every reference mapped onto the target character.
/// </summary>
public static class AknmImport
{
    public static AknmImportResult Import(SceneModel model, AknmFile file, AknmImportOptions? options = null)
    {
        options ??= new AknmImportOptions();
        var warnings = new List<string>();
        void Warn(string message)
        {
            warnings.Add(message);
            options.Warning?.Invoke(message);
        }

        var target = options.Character ?? model.Scene.CurrentCharacter
            ?? throw new InvalidOperationException("The scene has no current character; name the target character.");

        var sourceControllers = AknmFormat.ReferenceTargets(file.Character, "Controllers").ToList();
        var targetControllers = target.Controllers.Select(c => c.Source).ToList();
        var map = MapControllers(sourceControllers, targetControllers, out int unmatched);
        if (map.Count == 0)
            throw new InvalidOperationException(
                $"No controller of '{file.Name}' matches a controller of '{target.Name}'; the file does not belong to this rig.");

        var (mismatches, compared) = AknmRestPose.Compare(
            AknmRestPose.WorldPositions(file.Character), AknmRestPose.WorldPositions(target.Source), map);
        if (compared == 0)
            Warn("The .aknm has no usable reference pose; the rest-pose comparison was skipped.");
        if (mismatches > 0)
        {
            string message = $"{mismatches} of {compared} matched controllers of '{file.Name}' have a different " +
                             $"reference pose than '{target.Name}'. Animation keys are rest-pose deltas, so the " +
                             "imported motion will not be exact.";
            if (!options.AllowDifferentPose)
                throw new InvalidOperationException(
                    message + " Import anyway with AllowDifferentPose (--force on the command line).");
            Warn(message);
        }
        if (unmatched > 0)
            Warn($"{unmatched} controller(s) of '{file.Name}' have no counterpart in '{target.Name}'; their tracks are dropped.");

        AknmSchema.Merge(model.Document.Schema, file.Document.Schema, file.Animations);

        var bank = target.AnimBank ?? CreateBank(model, target);
        Folder destination = bank;
        if (options.RetargetFolder)
            destination = CreateFolder(model, bank, UniqueNames.For(bank.Source, $"Retargeted From {file.Name}"));

        var external = BuildExternalMap(file, target, map);
        var imported = new List<Animation>();
        int droppedTracks = 0;
        foreach (var sourceAnimation in file.Animations)
        {
            var copy = CloneAnimation(file.Document, model.Document.Schema, sourceAnimation, external, out int dropped);
            droppedTracks += dropped;
            var folder = ResolvePath(model, destination, FolderPath(file, sourceAnimation));
            copy.Set("Name", UniqueNames.For(folder.Source, sourceAnimation.Name ?? "Animation"));
            folder.Source.AddChild(copy);
            imported.Add((Animation)model.Wrap(copy));
        }

        if (imported.Count > 0) target.CurrentAnimation = imported[^1];
        return new AknmImportResult
        {
            Character = target,
            Animations = imported,
            Destination = destination,
            UnmatchedControllers = unmatched,
            MismatchedControllers = mismatches,
            ComparedControllers = compared,
            DroppedTracks = droppedTracks,
            Warnings = warnings,
        };
    }

    // ---- matching -------------------------------------------------------------------------

    /// <summary>
    /// Source controller -> target controller: by unique name first, then by position in the two
    /// <c>Controllers</c> arrays (the pairing is by position only).
    /// </summary>
    private static Dictionary<AktObject, AktObject> MapControllers(
        IReadOnlyList<AktObject?> source, IReadOnlyList<AktObject> target, out int unmatched)
    {
        var map = new Dictionary<AktObject, AktObject>(ReferenceEqualityComparer.Instance);
        var byName = new Dictionary<string, List<AktObject>>(StringComparer.Ordinal);
        foreach (var obj in target)
            if (obj.Name is { } name)
            {
                if (!byName.TryGetValue(name, out var list)) byName[name] = list = [];
                list.Add(obj);
            }

        var used = new HashSet<AktObject>(ReferenceEqualityComparer.Instance);
        foreach (var from in source)
            if (from?.Name is { } name && byName.TryGetValue(name, out var candidates)
                && candidates.Count == 1 && used.Add(candidates[0]))
                map[from] = candidates[0];

        for (int i = 0; i < source.Count && i < target.Count; i++)
        {
            var from = source[i];
            if (from == null || map.ContainsKey(from)) continue;
            if (used.Add(target[i])) map[from] = target[i];
        }

        unmatched = source.Count(c => c != null) - map.Count;
        return map;
    }

    /// <summary>
    /// Every object a copied animation may point at, mapped into the target: the controllers, the
    /// character itself, and any descendant of the target with the same class and name (audio
    /// entries, poses, ...). A <c>CObject3D</c> is only ever mapped through the controller pairing,
    /// so a controller the target lost stays unmapped. Ambiguous names are left out.
    /// </summary>
    private static Dictionary<AktObject, AktObject> BuildExternalMap(
        AknmFile file, Character target, Dictionary<AktObject, AktObject> controllers)
    {
        var external = new Dictionary<AktObject, AktObject>(ReferenceEqualityComparer.Instance);
        foreach (var (from, to) in controllers) external[from] = to;
        external[file.Character] = target.Source;

        var targetByName = new Dictionary<(string, string), AktObject?>();
        foreach (var obj in target.Source.PreOrder().Skip(1))
            if (obj.Name is { } name)
            {
                var key = (obj.ClassKey, name);
                targetByName[key] = targetByName.ContainsKey(key) ? null : obj;
            }

        foreach (var obj in file.Character.PreOrder().Skip(1))
        {
            if (obj.Name is not { } name || external.ContainsKey(obj)) continue;
            if (AknmFormat.IsA(file.Document, obj, AknmFormat.Object3DClass)) continue;
            if (targetByName.TryGetValue((obj.ClassKey, name), out var match) && match != null)
                external[obj] = match;
        }
        return external;
    }

    // ---- copying --------------------------------------------------------------------------

    /// <summary>A deep copy of the animation with every reference mapped; tracks without a target are dropped.</summary>
    private static AktObject CloneAnimation(
        AktDocument sourceDocument, AktSchema targetSchema, AktObject source,
        Dictionary<AktObject, AktObject> external, out int droppedTracks)
    {
        var copy = source.CloneHierarchy();
        var copies = copy.PreOrder().ToList();
        var copySet = new HashSet<AktObject>(copies, ReferenceEqualityComparer.Instance);
        foreach (var field in copies.SelectMany(c => c.Fields))
            AknmFormat.Remap(field.Value, target =>
                copySet.Contains(target) ? target
                : external.TryGetValue(target, out var mapped) ? mapped
                : null);

        // Runtime selection markers are never written by the original and are not animation data.
        foreach (var marker in copies.Where(o => AknmFormat.IsA(sourceDocument, o, AknmFormat.AnimSelectionClass)).ToList())
            marker.Parent?.RemoveChild(marker);

        droppedTracks = 0;
        foreach (var track in copies.Where(o => AknmFormat.IsA(sourceDocument, o, AknmFormat.TrackClass)).ToList())
        {
            if ((track["Target"] as AktReference)?.Target != null) continue;
            track.Parent?.RemoveChild(track);
            droppedTracks++;
        }

        // A file from an older version may lack members the target's schema knows (schema evolution).
        foreach (var obj in copy.PreOrder()) AktSchemaMerge.FillMissingMembers(targetSchema, obj);
        return copy;
    }

    // ---- Anim Bank ------------------------------------------------------------------------

    private static Folder CreateBank(SceneModel model, Character character)
    {
        var source = model.Document.Schema.NewObject("CDummy");
        source.Set("Name", "__Anims");
        character.Source.AddChild(source);
        return (Folder)model.Wrap(source);
    }

    private static Folder CreateFolder(SceneModel model, SceneObject parent, string name)
    {
        var source = model.Document.Schema.NewObject("CDummy");
        source.Set("Name", name);
        parent.Source.AddChild(source);
        return (Folder)model.Wrap(source);
    }

    /// <summary>The source animation's folder path below <c>__Anims</c>, top level first.</summary>
    private static IReadOnlyList<string> FolderPath(AknmFile file, AktObject animation)
    {
        var names = new List<string>();
        for (var parent = animation.Parent; parent != null; parent = parent.Parent)
        {
            if (parent == file.Character || parent.Name == "__Anims") break;
            if (parent.Name is { } name) names.Insert(0, name);
        }
        return names;
    }

    /// <summary>
    /// Walks/creates the named folders under <paramref name="root"/>; a folder with the same name is
    /// reused, as the original does.
    /// </summary>
    private static SceneObject ResolvePath(SceneModel model, SceneObject root, IReadOnlyList<string> path)
    {
        var current = root;
        foreach (string name in path)
        {
            var existing = current.Children.OfType<Folder>().FirstOrDefault(f => f.Name == name);
            current = existing ?? CreateFolder(model, current, name);
        }
        return current;
    }
}

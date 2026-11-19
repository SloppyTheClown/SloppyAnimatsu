using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Core.Aknm;

/// <summary>
/// The `.aknm` container: an `.akt` file whose only root is the
/// exported character, written as <c>class CCharacterAKNM</c>, with every <c>CMesh</c> written as
/// a plain <c>CObject3D</c> and only the selected animations kept.
/// </summary>
public static class AknmFormat
{
    /// <summary>The class the export writes for the character (and for any nested character).</summary>
    public const string AknmCharacterClass = "class CCharacterAKNM";
    public const string CharacterClass = "class CCharacter";
    public const string MeshClass = "class CMesh";
    public const string Object3DClass = "class CObject3D";
    public const string AnimationClass = "class CAnimation";
    public const string AnimSelectionClass = "class CAnimSelection";
    public const string TrackClass = "class CAnimTrackBase";

    /// <summary>The hidden rig-pose animation, always kept by the export.</summary>
    public const string RigAnimationName = "__RigAnim";

    /// <summary>Whether <paramref name="obj"/> is of <paramref name="classKey"/> or derives from it (per the file's schema).</summary>
    public static bool IsA(AktDocument document, AktObject obj, string classKey)
    {
        var key = AktNames.Key(classKey);
        for (var c = document.Schema.FindClass(obj.TypeName); c != null; c = c.Parent)
            if (c.Key == key) return true;
        return false;
    }

    /// <summary>The target of a member holding a <c>Reference</c>, or null.</summary>
    public static AktObject? ReferenceTarget(AktFieldContainer container, string member) =>
        (container[member] as AktReference)?.Target;

    /// <summary>The targets of a member holding a <c>RefArray</c>, in order (null entries stay null).</summary>
    public static IReadOnlyList<AktObject?> ReferenceTargets(AktFieldContainer container, string member) =>
        container[member] is List<AktReference> refs ? refs.Select(r => r.Target).ToList() : [];

    /// <summary>Deep-copies a value: structs and lists are copied, references stay references to the same objects.</summary>
    internal static object? CopyValue(object? value) => value switch
    {
        AktStruct s => s.Clone(),
        AktReference r => new AktReference(r.Target) { RawIndex = r.RawIndex },
        List<AktReference> refs => refs.Select(r => (AktReference)CopyValue(r)!).ToList(),
        List<object?> list => list.Select(CopyValue).ToList(),
        object?[] tuple => tuple.Select(CopyValue).ToArray(),
        Array array => array.Clone(),
        _ => value,
    };

    /// <summary>Replaces every reference in <paramref name="value"/> with the mapped object, or null when there is none.</summary>
    internal static void Remap(object? value, Func<AktObject, AktObject?> lookup) => AktRefs.Remap(value, lookup);
}

/// <summary>Builds the class and struct schemas a `.aknm` document needs.</summary>
public static class AknmSchema
{
    /// <summary>A copy of <paramref name="source"/> that can be extended without touching the scene's schema.</summary>
    public static AktSchema Clone(AktSchema source) => source.Clone();

    /// <summary>Adds <c>class CCharacterAKNM</c> as a member-less subclass of <c>CCharacter</c>, once.</summary>
    public static AktClassDef AddAknmCharacter(AktSchema schema)
    {
        if (schema.FindClass(AknmFormat.AknmCharacterClass) is { } existing) return existing;
        var parent = schema.FindClass(AknmFormat.CharacterClass)
                     ?? throw new InvalidOperationException($"The schema has no {AknmFormat.CharacterClass}.");
        var def = new AktClassDef(AknmFormat.AknmCharacterClass);
        schema.AddClass(def, parent);
        return def;
    }

    /// <summary>
    /// Adds to <paramref name="target"/> every class and struct of <paramref name="source"/> that
    /// <paramref name="objects"/> (and their ancestors) need and the target does not have yet, so a
    /// cloned animation writes with the target document's schema.
    /// </summary>
    public static void Merge(AktSchema target, AktSchema source, IEnumerable<AktObject> objects) =>
        AktSchemaMerge.Merge(target, source, objects, "the .aknm");
}

/// <summary>
/// Reference-pose world translations within a rig, from the character's <c>RefPose</c> matrices
/// and the controller hierarchy. The character's own transform is left out, so two copies of one
/// rig compare equal wherever they sit in a scene.
/// </summary>
public static class AknmRestPose
{
    /// <summary>Controller -> world translation, in the order the pose stores them.</summary>
    public static Dictionary<AktObject, Vector3> WorldPositions(AktObject character)
    {
        var result = new Dictionary<AktObject, Vector3>(ReferenceEqualityComparer.Instance);
        var controllers = AknmFormat.ReferenceTargets(character, "Controllers")
            .Where(c => c != null).Select(c => c!).ToList();
        var pose = AknmFormat.ReferenceTarget(character, "RefPose");
        if (pose == null || pose["Matrices"] is not List<object?> matrices) return result;

        var local = new Dictionary<AktObject, Matrix4x4>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < Math.Min(controllers.Count, matrices.Count); i++)
            if (matrices[i] is float[] m && m.Length == 16)
                local[controllers[i]] = AktMath.ToMatrix(m);

        foreach (var (obj, matrix) in local)
        {
            var world = matrix;
            for (var parent = obj.Parent; parent != null; parent = parent.Parent)
                if (local.TryGetValue(parent, out var parentWorld)) { world = world * parentWorld; break; }
            result[obj] = world.Translation;
        }
        return result;
    }

    /// <summary>
    /// The "same rig" test: relative translation distance
    /// <c>d*0.1 / max(1, |a|+|b|)</c> below 1e-5. Returns the number of mismatching pairs and the
    /// number compared.
    /// </summary>
    public static (int Mismatches, int Compared) Compare(
        IReadOnlyDictionary<AktObject, Vector3> source, IReadOnlyDictionary<AktObject, Vector3> target,
        IReadOnlyDictionary<AktObject, AktObject> map)
    {
        int mismatches = 0, compared = 0;
        foreach (var (from, to) in map)
        {
            if (!source.TryGetValue(from, out var a) || !target.TryGetValue(to, out var b)) continue;
            compared++;
            float relative = Vector3.Distance(a, b) * 0.1f / MathF.Max(1f, a.Length() + b.Length());
            if (MathF.Abs(relative) >= 1e-5f) mismatches++;
        }
        return (mismatches, compared);
    }
}

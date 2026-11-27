using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Plugins;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// A character's Paint &amp; Check values: one optional local transform per controller, in the order
/// of <see cref="Controllers"/> (the character's controller list when they were set).
/// </summary>
internal sealed class CheckPoseSlots(Object3D[] controllers, Transform?[] values)
{
    public Object3D[] Controllers { get; } = controllers;
    public Transform?[] Values { get; } = values;

    /// <summary>Each posed reverse foot's roll, twist and bank (radians), which its shoe locators' slots follow.</summary>
    public Dictionary<Object3D, Vector3> FootAngles { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The same values for <paramref name="list"/>, matched by controller (a removed one is dropped).</summary>
    public CheckPoseSlots Remap(IReadOnlyList<Object3D> list)
    {
        var at = new Dictionary<Object3D, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < Controllers.Length; i++) at.TryAdd(Controllers[i], i);
        var values = new Transform?[list.Count];
        for (int i = 0; i < list.Count; i++)
            if (at.TryGetValue(list[i], out int from)) values[i] = Values[from];
        var remapped = new CheckPoseSlots(list.ToArray(), values);
        foreach (var (foot, angles) in FootAngles)
            if (at.ContainsKey(foot)) remapped.FootAngles[foot] = angles;
        return remapped;
    }
}

/// <summary>
/// Saves Paint &amp; Check's pose with the scene, so it is there again when the file is reopened.
/// Storage: on <c>CScene</c>, <see cref="MaterialSchema.CheckPoseTargetsMember"/> (the posed
/// controllers of every character) and <see cref="MaterialSchema.CheckPoseValuesMember"/> (ten floats
/// per controller: translation xyz, rotation quaternion xyzw, scale xyz), plus
/// <see cref="MaterialSchema.CheckPoseFeetMember"/> and <see cref="MaterialSchema.CheckPoseFootAnglesMember"/>
/// (each posed reverse foot and its roll, twist and bank in radians). An <c>.akt</c> save drops them all.
/// </summary>
public static class CheckPoseStore
{
    private const int FloatsPer = 10;

    /// <summary>
    /// Writes every character's Paint &amp; Check pose into the scene before a save. Not an edit:
    /// the members are rewritten from the characters each time. Characters never read are read first,
    /// so a pose saved earlier and not looked at this session is kept.
    /// </summary>
    public static void Store(SceneModel model)
    {
        var refs = new List<AktReference>();
        var floats = new List<object?>();
        var feet = new List<AktReference>();
        var footAngles = new List<object?>();
        foreach (var character in model.Scene.Characters)
        {
            if (Read(character) is not { } slots) continue;
            foreach (var (foot, angles) in slots.FootAngles)
            {
                feet.Add(new AktReference(foot.Source));
                footAngles.AddRange([angles.X, angles.Y, angles.Z]);
            }
            for (int i = 0; i < slots.Controllers.Length; i++)
            {
                if (slots.Values[i] is not { } t) continue;
                refs.Add(new AktReference(slots.Controllers[i].Source));
                floats.AddRange([t.Translation.X, t.Translation.Y, t.Translation.Z,
                    t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W,
                    t.Scale.X, t.Scale.Y, t.Scale.Z]);
            }
        }
        var scene = model.Scene.Source;
        // A scene that never had a Paint & Check pose is left without the members.
        if (refs.Count == 0 && !scene.Has(MaterialSchema.CheckPoseTargetsMember)) return;
        MaterialSchema.EnsureFields(model.Document.Schema, model.Document.Roots);
        if (!scene.Has(MaterialSchema.CheckPoseTargetsMember)) return;
        scene.Set(MaterialSchema.CheckPoseTargetsMember, refs);
        scene.Set(MaterialSchema.CheckPoseValuesMember, floats.Select(f => (object?)Convert.ToSingle(f)).ToList());
        scene.Set(MaterialSchema.CheckPoseFeetMember, feet);
        scene.Set(MaterialSchema.CheckPoseFootAnglesMember, footAngles.Select(f => (object?)Convert.ToSingle(f)).ToList());
    }

    /// <summary>The character's pose slots, read from the scene the first time.</summary>
    internal static CheckPoseSlots? Read(Character character)
    {
        if (character.CheckPoseRead) return character.CheckPose;
        character.CheckPoseRead = true;
        var model = character.Model;
        var scene = model.Scene.Source;
        if (!scene.Has(MaterialSchema.CheckPoseTargetsMember) || !scene.Has(MaterialSchema.CheckPoseValuesMember))
            return character.CheckPose;
        var targets = References(scene, MaterialSchema.CheckPoseTargetsMember);
        var values = Floats(scene, MaterialSchema.CheckPoseValuesMember);
        var controllers = character.Controllers;
        var index = new Dictionary<AktObject, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < controllers.Count; i++) index.TryAdd(controllers[i].Source, i);
        Transform?[]? slots = null;
        for (int i = 0; i < targets.Count && (i + 1) * FloatsPer <= values.Count; i++)
        {
            if (targets[i] is not AktReference { Target: { } target } || !index.TryGetValue(target, out int at)) continue;
            if (!PluginSceneData.IsAttached(model, target)) continue;
            int o = i * FloatsPer;
            var transform = new Transform(
                new Vector3(values[o], values[o + 1], values[o + 2]),
                Quaternion.Normalize(new Quaternion(values[o + 3], values[o + 4], values[o + 5], values[o + 6])),
                new Vector3(values[o + 7], values[o + 8], values[o + 9]));
            if (!IsFinite(transform)) continue;
            slots ??= new Transform?[controllers.Count];
            slots[at] = transform;
        }
        if (slots != null)
        {
            var read = new CheckPoseSlots(controllers.ToArray(), slots);
            var feet = References(scene, MaterialSchema.CheckPoseFeetMember);
            var angles = Floats(scene, MaterialSchema.CheckPoseFootAnglesMember);
            for (int i = 0; i < feet.Count && (i + 1) * 3 <= angles.Count; i++)
            {
                if (feet[i] is not AktReference { Target: { } foot } || !index.TryGetValue(foot, out int at)) continue;
                var a = new Vector3(angles[i * 3], angles[i * 3 + 1], angles[i * 3 + 2]);
                if (float.IsFinite(a.X) && float.IsFinite(a.Y) && float.IsFinite(a.Z)) read.FootAngles[controllers[at]] = a;
            }
            character.CheckPose = read;
        }
        return character.CheckPose;
    }

    private static List<object?> References(AktObject scene, string member) =>
        !scene.Has(member) ? []
        : scene[member] is IEnumerable<object?> t ? t.ToList()
        : scene[member] is IEnumerable<AktReference> r ? r.Cast<object?>().ToList() : [];

    private static List<float> Floats(AktObject scene, string member) =>
        scene.Has(member) && scene[member] is IEnumerable<object?> v
            ? v.Select(x => x == null ? float.NaN : Convert.ToSingle(x)).ToList() : [];

    private static bool IsFinite(Transform t) =>
        float.IsFinite(t.Translation.X) && float.IsFinite(t.Translation.Y) && float.IsFinite(t.Translation.Z)
        && float.IsFinite(t.Rotation.X) && float.IsFinite(t.Rotation.Y) && float.IsFinite(t.Rotation.Z) && float.IsFinite(t.Rotation.W)
        && float.IsFinite(t.Scale.X) && float.IsFinite(t.Scale.Y) && float.IsFinite(t.Scale.Z);
}

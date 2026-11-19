using System.Numerics;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// Bind and Unbind Skin: attach a character's
/// meshes to its bind pose, or detach every skinned mesh of the scene keeping its world transform.
/// </summary>
/// <remarks>
/// Binding captures the reference pose as the character's bind pose, marks the meshes skinned, keeps
/// newly parented meshes where they are, and writes the saved state the bind writes:
/// <c>SkinnedWithAkeytsu</c>, <c>SmoothSkinningProcessed</c>, <c>CCharacter.RigidModified = false</c>
/// and a one-influence <c>RigidSkinWeightArray</c>
/// The geodesic-voxel rigid solver is not reproduced: the joint choice here is
/// SloppyAnimatsu's nearest-bone rule. A mesh
/// that already has smooth weights keeps them; only an unweighted mesh is filled with the rigid set.
/// </remarks>
public sealed class SkinEditor
{
    public SkinEditor(Character character) => Character = character;

    public Character Character { get; }

    /// <summary>
    /// The meshes Bind Skin takes: the character's own meshes and the scene's free ones. A mesh
    /// under another character is left alone, as the bind does.
    /// </summary>
    public IReadOnlyList<Mesh> Candidates()
    {
        var scene = Character.Model.Scene;
        return scene.Meshes.Where(mesh =>
        {
            var owner = mesh.Ancestor<Character>();
            return owner == null || ReferenceEquals(owner, Character);
        }).ToList();
    }

    /// <summary>Meshes by name, for an explicit selection.</summary>
    public static IReadOnlyList<Mesh> Named(Scene scene, IEnumerable<string> names)
    {
        var meshes = new List<Mesh>();
        foreach (string name in names)
        {
            var matches = scene.Meshes.Where(m => m.Name == name).ToList();
            if (matches.Count == 0) throw new InvalidOperationException($"The scene has no mesh '{name}'.");
            if (matches.Count > 1) throw new InvalidOperationException($"The scene has {matches.Count} meshes named '{name}'.");
            if (!meshes.Contains(matches[0])) meshes.Add(matches[0]);
        }
        return meshes;
    }

    /// <summary>
    /// Binds the meshes (default: <see cref="Candidates"/>): reparents free meshes to the character
    /// keeping their world, sets <c>Skinned</c>/<c>SkinnedWithAkeytsu</c>/<c>SmoothSkinningProcessed</c>,
    /// points them at the character's bind pose (created from the reference pose when the character has
    /// none), writes the rigid one-influence weight array and clears <c>CCharacter.RigidModified</c>.
    /// With <paramref name="rewriteWeights"/> false the weight values are left as they are and, when
    /// <paramref name="previousJoints"/> names the replaced rig, their joint indices are mapped onto
    /// the new rig by name.
    /// The rigging <c>CurrentPose</c> is cleared. Returns the number of meshes visited.
    /// </summary>
    /// <param name="meshes">The meshes to bind; by default the character's candidates.</param>
    /// <param name="rewriteWeights">
    /// Whether the bind solves and writes the rigid array (and fills an empty smooth one). False is
    /// The "Smooth" answer to the "RIGID AND SMOOTH MODIFIED" prompt: the meshes keep the weight
    /// values the user edited and are only attached to the new bind pose
    /// </param>
    /// <param name="previousJoints">
    /// When <paramref name="rewriteWeights"/> is false, the joints each mesh's kept arrays index: the
    /// replaced rig's joint names in its skinning order, per mesh. The bind rewrites every kept
    /// influence's <c>BoneIndex</c> onto the joint of this character with the same name, and leaves
    /// an influence whose old name has no counterpart at its old index. Null skips the mapping, so a
    /// bind on a rig that was not rebuilt never renumbers.
    /// </param>
    public int Bind(IEnumerable<Mesh>? meshes = null, bool rewriteWeights = true,
        IReadOnlyDictionary<Mesh, IReadOnlyList<string>>? previousJoints = null)
    {
        var targets = (meshes ?? Candidates()).Distinct().ToList();
        if (targets.Count == 0) return 0;
        var pose = Character.BindPoses.FirstOrDefault();
        var skeleton = rewriteWeights ? new RigidSkeleton(Character) : null;
        foreach (var mesh in targets)
        {
            var owner = mesh.Ancestor<Character>();
            if (owner != null && !ReferenceEquals(owner, Character))
                throw new InvalidOperationException(
                    $"'{mesh.Name}' belongs to '{owner.Name}', not '{Character.Name}'.");
            bool outsideCharacter = owner == null;
            if (outsideCharacter) MoveKeepWorld(mesh, Character);
            if (!mesh.Skinned)
            {
                pose ??= CreateBindPose();
                if (!mesh.Source.Has("BindPose"))
                    throw new InvalidOperationException(
                        $"'{mesh.Name}' has no BindPose member: the scene schema cannot bind it.");
                mesh.Source.Set("BindPose", new AktReference(pose.Source));
                mesh.Skinned = true;
            }
            if (!mesh.SkinnedWithAkeytsu) mesh.SkinnedWithAkeytsu = true;
            if (rewriteWeights && skeleton!.Weights(mesh, Character.RefPose) is { } rigid)
            {
                WriteWeights(mesh, "RigidSkinWeightArray", rigid);
                // The bind also solves the smooth array from the same joint list, with a
                // threaded pass that is not bit-reproducible; carried weights are kept and only an
                // unweighted mesh gets the rigid solution as its smooth set.
                if (!mesh.SkinWeights.Any(w => w.Length > 0)) WriteWeights(mesh, "SkinWeightArray", rigid);
            }
            else if (!rewriteWeights && previousJoints != null &&
                     previousJoints.TryGetValue(mesh, out var oldJoints))
            {
                // The kept arrays index the rig they were authored on; name the same joints on the
                // new rig so the user's weights still deform the mesh.
                RemapKeptWeights(mesh, oldJoints);
            }
            // The bind worker sets this for both answers of the smooth-again prompt: with
            // "Smooth" the mesh's own smooth solution is the one that stays.
            mesh.SmoothSkinningProcessed = true;
        }
        if (Character.Source.Has("RigidModified")) Character.Source.Set("RigidModified", false);
        if (Character.Source.Has("CurrentPose")) Character.CurrentPose = null;
        return targets.Count;
    }

    /// <summary>
    /// Rewrites a mesh's kept weight arrays' <c>BoneIndex</c> values from the replaced rig's joint
    /// list onto this character's joints by joint name
    /// Only the index changes: an influence whose old
    /// joint name has no counterpart on this character keeps its old index, and one whose index is
    /// outside <paramref name="previousJoints"/> is left alone - a renumbering cannot drop the
    /// user's weights, and a name is the only identity the saved format carries.
    /// </summary>
    /// <param name="mesh">The mesh whose kept arrays are rewritten.</param>
    /// <param name="previousJoints">The replaced rig's joint names, in its skinning order.</param>
    /// <returns>The number of influences whose index changed.</returns>
    public int RemapKeptWeights(Mesh mesh, IReadOnlyList<string> previousJoints)
    {
        if (previousJoints.Count == 0) return 0;
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int joint = 0; joint < Character.Joints.Count; joint++)
            byName.TryAdd(Character.Joints[joint].Name ?? "", joint);
        int changed = RemapWeights(mesh, "SkinWeightArray", previousJoints, byName);
        changed += RemapWeights(mesh, "RigidSkinWeightArray", previousJoints, byName);
        return changed;
    }

    /// <summary>
    /// Unbind Skin: every skinned mesh of the scene that is not locked
    /// is detached to the scene root with its current world transform in <c>LTM</c>, and marked
    /// unskinned. The bind pose and the weight arrays stay.
    /// </summary>
    public static int Unbind(Scene scene)
    {
        int count = 0;
        foreach (var mesh in scene.Meshes.ToList())
        {
            if (!mesh.Skinned || mesh.SkinnedMeshIsLocked) continue;
            var world = mesh.WorldMatrix;
            mesh.Skinned = false;
            mesh.LocalMatrix = world;
            mesh.Source.Parent?.RemoveChild(mesh.Source);
            scene.Source.AddChild(mesh.Source);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Creates the character's bind pose, a <c>CCharacterPose</c> named "Bind Pose" under
    /// <c>__BindPoses</c>, with one matrix per controller copied from the reference pose. This is
    /// how a bind pose is built from the reference pose.
    /// </summary>
    public CharacterPose CreateBindPose()
    {
        if (Character.RefPose is not { } reference)
            throw new InvalidOperationException($"'{Character.Name}' has no reference pose to bind in.");
        var schema = Character.Model.Document.Schema;
        var folder = Character.Child("__BindPoses") as Folder;
        if (folder == null)
        {
            AktObject holder;
            try
            {
                holder = schema.NewObject("CDummy");
            }
            catch (InvalidOperationException)
            {
                throw new InvalidOperationException("The scene schema has no CDummy class; it cannot hold a bind pose.");
            }
            holder.Set("Name", "__BindPoses");
            Character.Source.AddChild(holder);
            folder = (Folder)Character.Model.Wrap(holder);
        }

        AktObject source;
        try
        {
            source = schema.NewObject("CCharacterPose");
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("The scene schema has no CCharacterPose class; it cannot bind a mesh.");
        }
        source.Set("Name", "Bind Pose");
        folder.Source.AddChild(source);
        source.Set("Matrices", reference.Matrices.Select(m => (object?)AktMath.ToArray(m)).ToList());
        return (CharacterPose)Character.Model.Wrap(source);
    }

    /// <summary>Parents <paramref name="o"/> under <paramref name="parent"/> keeping its world matrix.</summary>
    private static void MoveKeepWorld(Object3D o, Object3D parent)
    {
        var world = o.WorldMatrix;
        o.Source.Parent?.RemoveChild(o.Source);
        parent.Source.AddChild(o.Source);
        if (WorldComposition.TryDecompose(world, parent.WorldMatrix,
                Object3D.LocalScale(parent.LocalMatrix), o.CompensatesParentScale, out var local))
            o.LocalMatrix = local;
        else
            o.LocalMatrix = world;
    }

    /// <summary>
    /// The character's bones, positioned in the reference pose, and the one-influence solution a bind
    /// writes: every control point takes the bone nearest to it, weight 1, with the bone's start joint
    /// as its influence (the joint whose transform carries the span, e.g. the upper-arm region between
    /// The shoulder and the elbow belongs to <c>UpperArm</c>). The own rigid array comes from a
    /// geodesic-voxel pass; this is
    /// SloppyAnimatsu's rule for the same shape, not a reproduction of its joint choice.
    /// </summary>
    private sealed class RigidSkeleton
    {
        private readonly RigEditor _rig;
        private readonly Vector3[] _positions;
        private readonly (int Joint, Vector3 Start, Vector3 End)[] _bones;

        public RigidSkeleton(Character character)
        {
            _rig = new RigEditor(character);
            var joints = character.Joints;
            var index = new Dictionary<Joint, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < joints.Count; i++) index[joints[i]] = i;
            _positions = new Vector3[joints.Count];
            for (int i = 0; i < joints.Count; i++)
                _positions[i] = _rig.WorldInPose(character.RefPose, joints[i]).Translation;
            var bones = new List<(int Joint, Vector3 Start, Vector3 End)>(joints.Count);
            for (int i = 0; i < joints.Count; i++)
                if (joints[i].Parent3D is Joint parent && index.TryGetValue(parent, out int p) && p != i)
                    bones.Add((p, _positions[p], _positions[i]));
            _bones = bones.ToArray();
        }

        /// <summary>One influence per control point, or null when the character has no joint to weight against.</summary>
        public VertexWeight[][]? Weights(Mesh mesh, CharacterPose? pose)
        {
            int jointCount = _positions.Length;
            if (jointCount == 0) return null;
            var points = mesh.ControlPoints;
            var meshWorld = _rig.WorldInPose(pose, mesh);
            var result = new VertexWeight[points.Length][];
            for (int p = 0; p < points.Length; p++)
            {
                var world = Vector3.Transform(points[p], meshWorld);
                int nearest = 0;
                float best = float.MaxValue;
                if (_bones.Length > 0)
                {
                    for (int b = 0; b < _bones.Length; b++)
                    {
                        var (joint, start, end) = _bones[b];
                        float distance = DistanceToSegment(world, start, end);
                        if (distance < best) { best = distance; nearest = joint; }
                    }
                }
                else
                {
                    // A rig with one joint has no span: weigh it by the joint position.
                    for (int j = 0; j < jointCount; j++)
                    {
                        float distance = Vector3.Distance(world, _positions[j]);
                        if (distance < best) { best = distance; nearest = j; }
                    }
                }
                result[p] = [new VertexWeight(nearest, 1f)];
            }
            return result;
        }

        private static float DistanceToSegment(Vector3 point, Vector3 start, Vector3 end)
        {
            var delta = end - start;
            float lengthSquared = delta.LengthSquared();
            if (lengthSquared < 1e-12f) return Vector3.Distance(point, start);
            float t = Math.Clamp(Vector3.Dot(point - start, delta) / lengthSquared, 0f, 1f);
            return Vector3.Distance(point, start + delta * t);
        }
    }

    /// <summary>
    /// Rewrites one weight field's <c>BoneIndex</c> values through <paramref name="byName"/>; an
    /// index whose old name is missing from the map stays as it is. Returns how many changed.
    /// </summary>
    private static int RemapWeights(Mesh mesh, string field, IReadOnlyList<string> previousJoints,
        IReadOnlyDictionary<string, int> byName)
    {
        int changed = 0;
        foreach (object? array in SceneValues.Items(mesh.Source, field))
        {
            if (array is not AktStruct weights) continue;
            foreach (object? item in SceneValues.Items(weights, "WeightArray"))
            {
                if (item is not AktStruct influence) continue;
                int old = (int)influence.Get<uint>("BoneIndex");
                if (old < 0 || old >= previousJoints.Count) continue;
                if (!byName.TryGetValue(previousJoints[old], out int next) || next == old) continue;
                influence.Set("BoneIndex", (uint)next);
                changed++;
            }
        }
        return changed;
    }

    /// <summary>Writes one <c>CVertexWeightArray</c> per control point into <paramref name="field"/>.</summary>
    private static void WriteWeights(Mesh mesh, string field, IReadOnlyList<VertexWeight>[] points)
    {
        var schema = mesh.Model.Document.Schema;
        var arrays = new List<object?>(points.Length);
        foreach (var influences in points)
        {
            var array = schema.NewStruct("CVertexWeightArray");
            var weights = (List<object?>)array["WeightArray"]!;
            foreach (var (bone, weight) in influences)
            {
                var entry = schema.NewStruct("CVertexWeight");
                entry.Set("BoneIndex", (uint)bone);
                entry.Set("Weight", weight);
                weights.Add(entry);
            }
            arrays.Add(array);
        }
        mesh.Source.Set(field, arrays);
    }
}
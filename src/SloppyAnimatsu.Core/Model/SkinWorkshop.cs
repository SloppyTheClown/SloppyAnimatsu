using System.Numerics;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>The Skin Workshop's brush modes.</summary>
public enum SkinPaintMode
{
    /// <summary>Add <see cref="SkinPaintBrush.Value"/> (or subtract it with Alt).</summary>
    Add = 0,
    /// <summary>Move a <see cref="SkinPaintBrush.Strength"/> fraction of the way to <see cref="SkinPaintBrush.Value"/>.</summary>
    Aim = 1,
    /// <summary>Move a <see cref="SkinPaintBrush.Strength"/> fraction of the way to the neighbour average.</summary>
    Smooth = 2,
    /// <summary>Paint the vertex selection instead of weights.</summary>
    Select = 3,
}

/// <summary>
/// One brush setting set: the modes, radius, strength and value of
/// The radius is in world units, a
/// SloppyAnimatsu choice (the original works in screen pixels).
/// </summary>
public sealed class SkinPaintBrush
{
    public SkinPaintMode Mode { get; init; } = SkinPaintMode.Add;

    /// <summary>The radius of the brush, in world units.</summary>
    public float Radius { get; init; } = 1f;

    /// <summary>How much of the mode's goal one stroke applies (the shipped strength is 1).</summary>
    public float Strength { get; init; } = 1f;

    /// <summary>The weight Add adds and Aim aims at (the shipped value is 1).</summary>
    public float Value { get; init; } = 1f;

    /// <summary>
    /// <c>BrushType == 1</c> in the preferences: paint the full strength with no falloff. False
    /// applies the 31-entry falloff curve of the soft brush.
    /// </summary>
    public bool Hard { get; init; } = true;

    /// <summary>Alt mode: in Add mode the value is subtracted instead of added.</summary>
    public bool Subtract { get; init; }

    /// <summary>Edit the rigid weight array (the rigging context) instead of the smooth one.</summary>
    public bool Rigid { get; init; }

    /// <summary>
    /// <c>FrontBackPaint</c>: true paints through the mesh, false rejects control points whose
    /// normal faces away from <see cref="SkinPaintStroke.Apply"/>'s eye.
    /// </summary>
    public bool FrontBack { get; init; } = true;

    /// <summary>
    /// Joints that are selected/protected: their weight is not redistributed by the writer
    /// </summary>
    public IReadOnlyCollection<int>? ProtectedJoints { get; init; }

    /// <summary>
    /// When set and not empty, the brush only touches these control points: "the Painter is limited
    /// to the selection".
    /// </summary>
    public SkinSelection? LimitTo { get; init; }
}

/// <summary>
/// The Skin Workshop's weight arithmetic: the per-vertex writer, Sort, Prune, Fix, Flood and the
/// selection. The brush itself is <see cref="SkinPaintStroke"/>.
/// </summary>
public static class SkinWorkshop
{
    /// <summary>
    /// The soft brush's falloff, sampled at <c>distance / radius</c> in 31 steps. See
    /// </summary>
    private static readonly float[] FalloffTable =
    [
        0.990f, 0.980f, 0.9725f, 0.960f, 0.9475f, 0.930f, 0.9075f, 0.885f, 0.8575f, 0.825f,
        0.7875f, 0.740f, 0.6875f, 0.6275f, 0.5625f, 0.500f, 0.430f, 0.365f, 0.305f, 0.250f,
        0.2075f, 0.1975f, 0.135f, 0.1075f, 0.0825f, 0.065f, 0.050f, 0.040f, 0.025f, 0.0175f,
        0.0075f,
    ];

    /// <summary>The soft brush's falloff at a distance ratio in [0, 1] (linear interpolation).</summary>
    public static float Falloff(float ratio)
    {
        if (ratio <= 0f) return FalloffTable[0];
        if (ratio >= 1f) return FalloffTable[^1];
        float x = ratio * (FalloffTable.Length - 1);
        int i = (int)x;
        float u = x - i;
        return FalloffTable[i] + (FalloffTable[i + 1] - FalloffTable[i]) * u;
    }

    /// <summary>The influences of one control point in the chosen weight array.</summary>
    public static IReadOnlyList<(int Bone, float Weight)> Weights(Mesh mesh, int vertex, bool rigid = false)
    {
        var arrays = rigid ? mesh.RigidSkinWeights : mesh.SkinWeights;
        return vertex >= 0 && vertex < arrays.Length
            ? arrays[vertex].Select(w => (w.Bone, w.Weight)).ToArray()
            : [];
    }

    /// <summary>One control point's weight on one joint.</summary>
    public static float Weight(Mesh mesh, int vertex, int joint, bool rigid = false)
    {
        var arrays = rigid ? mesh.RigidSkinWeights : mesh.SkinWeights;
        if (vertex < 0 || vertex >= arrays.Length) return 0f;
        foreach (var w in arrays[vertex])
            if (w.Bone == joint) return w.Weight;
        return 0f;
    }

    /// <summary>
    /// One row of the Skin Workshop's weight list
    /// a joint, the two 0..1 fractions the original's renderer draws (<c>MinWeight</c> for
    /// row +0x8, <c>MaxWeight</c> for +0x10), its weight on the current control point (the editable
    /// field, +0xc) and whether its lock button is on.
    /// </summary>
    /// <remarks>
    /// The two fractions are the smallest and the largest weight the joint carries over the
    /// <b>selected</b> control points (the minimum starts from 1 and the maximum from 0, and a joint that carries none of
    /// The selected points ends with the untouched 1/0 pair, which the app then reports as 0/0).
    /// They are not means and the second is not a mesh-wide share. The list only builds rows for the
    /// joints that are selected or influence the selected points; SloppyAnimatsu lists every joint, so
    /// <see cref="Editable"/> marks the ones the app would have given a row (its own edit field
    /// only refuses the -1 "no character" state of +0x8).
    /// </remarks>
    public readonly record struct Row(int Joint, string Name, float MinWeight, float MaxWeight,
        float Weight = 0f, bool Locked = false)
    {
        /// <summary>Whether the joint influences the selection (the rows the app builds).</summary>
        public bool Editable => MinWeight > 0f;
    }

    /// <summary>
    /// The per-joint weight list of a mesh: for every joint of the
    /// character, the minimum and maximum weight it carries on the selected control points, its
    /// weight on <paramref name="currentVertex"/> (the editable per-joint
    /// column) and its lock, in joint order. With
    /// nothing selected both fractions are 0. The painted joint is the one the panel's list selects.
    /// </summary>
    public static IReadOnlyList<Row> WeightList(Character character, Mesh mesh, bool rigid = false,
        int currentVertex = -1, IReadOnlyCollection<int>? lockedJoints = null, SkinSelection? selection = null)
    {
        var joints = character.Joints;
        var arrays = rigid ? mesh.RigidSkinWeights : mesh.SkinWeights;
        var current = new float[joints.Count];
        var minimum = new float[joints.Count];
        var maximum = new float[joints.Count];
        Array.Fill(minimum, 1f);
        if (selection != null)
            foreach (int vertex in selection.SelectedVertices())
            {
                if (vertex < 0 || vertex >= arrays.Length) continue;
                foreach (var (bone, weight) in arrays[vertex])
                {
                    if (bone < 0 || bone >= minimum.Length || weight < 1e-5f) continue;
                    if (weight < minimum[bone]) minimum[bone] = weight;
                    if (weight > maximum[bone]) maximum[bone] = weight;
                }
            }
        if (currentVertex >= 0 && currentVertex < arrays.Length && arrays[currentVertex] is { } list)
            foreach (var (bone, weight) in list)
                if (bone >= 0 && bone < current.Length) current[bone] = weight;
        var rows = new List<Row>(joints.Count);
        for (int i = 0; i < joints.Count; i++)
        {
            // A joint none of the selected control points carry keeps the 1/0 defaults, and the
            // routine's last pass zeroes the first one (the pair would otherwise read 1..0).
            float min = minimum[i] > maximum[i] ? 0f : minimum[i];
            rows.Add(new Row(i, joints[i].Name ?? "", min, maximum[i], current[i],
                lockedJoints?.Contains(i) == true));
        }
        return rows;
    }

    /// <summary>
    /// Set one control point's weight on one joint through the writer of
    /// (redistribute the other joints,
    /// drop zero weights, normalize). Returns the resulting list.
    /// </summary>
    public static IReadOnlyList<(int Bone, float Weight)> SetWeight(Mesh mesh, int vertex, int joint, float weight,
        bool rigid = false, IReadOnlyCollection<int>? protectedJoints = null,
        IReadOnlyList<Vector3>? jointPositions = null, Vector3? vertexPosition = null, Character? character = null)
    {
        var table = new SkinWeightTable(mesh, rigid, character);
        table.SetWeight(vertex, joint, weight, protectedJoints, jointPositions, vertexPosition);
        table.Write();
        return table.Weights(vertex);
    }

    /// <summary>
    /// The Spinner's weight delta: the target is
    /// <c>clamp(current + delta, 0, 1)</c>.
    /// </summary>
    public static IReadOnlyList<(int Bone, float Weight)> AddWeight(Mesh mesh, int vertex, int joint, float delta,
        bool rigid = false, IReadOnlyCollection<int>? protectedJoints = null,
        IReadOnlyList<Vector3>? jointPositions = null, Vector3? vertexPosition = null, Character? character = null)
    {
        float current = Weight(mesh, vertex, joint, rigid);
        return SetWeight(mesh, vertex, joint, Math.Clamp(current + delta, 0f, 1f), rigid, protectedJoints,
            jointPositions, vertexPosition, character);
    }

    /// <summary>
    /// Prune Weights: remove every influence below <paramref name="limit"/>
    /// and add the removed sum equally to the kept influences. Hidden control points are left alone
    /// (the original walks every non-hidden vertex). Returns the number of influences removed.
    /// </summary>
    public static int Prune(Mesh mesh, bool rigid = false, float limit = 0.001f)
    {
        var table = new SkinWeightTable(mesh, rigid);
        bool hidden = mesh.HasHiddenVertices;
        int removed = 0;
        for (int v = 0; v < table.VertexCount; v++)
        {
            if (hidden && mesh.IsVertexHidden(v)) continue;
            removed += table.PruneVertex(v, limit);
        }
        if (removed > 0) table.Write();
        return removed;
    }

    /// <summary>
    /// Fix Weights: average each selected vertex's influences over its
    /// selected neighbours (at least two) and normalize. Hidden control points take no part.
    /// Returns the number of vertices changed.
    /// </summary>
    public static int Fix(Mesh mesh, SkinSelection selection, bool rigid = false, int passes = 2)
    {
        var table = new SkinWeightTable(mesh, rigid);
        var neighbours = Neighbours(mesh);
        bool hidden = mesh.HasHiddenVertices;
        int changed = 0;
        for (int pass = 0; pass < passes; pass++)
        {
            var averages = new List<(int Bone, float Weight)>?[table.VertexCount];
            for (int v = 0; v < table.VertexCount; v++)
            {
                if (!selection.IsSelected(v)) continue;
                if (hidden && mesh.IsVertexHidden(v)) continue;
                var contributors = neighbours[v]
                    .Where(q => selection.IsSelected(q) && !(hidden && mesh.IsVertexHidden(q))).ToArray();
                if (contributors.Length < 2) continue;
                var sums = new Dictionary<int, float>();
                foreach (int q in contributors)
                    foreach (var (bone, weight) in table.Weights(q))
                        sums[bone] = sums.GetValueOrDefault(bone) + weight;
                averages[v] = sums.Select(kv => (kv.Key, kv.Value / contributors.Length)).ToList();
            }
            for (int v = 0; v < averages.Length; v++)
            {
                if (averages[v] is not { } list) continue;
                table.Replace(v, list);
                table.Normalize(v);
                changed++;
            }
        }
        if (changed > 0) table.Write();
        return changed;
    }

    /// <summary>
    /// The Flooder: grow the selection to the mesh's connected
    /// component(s) containing the selected vertices, restricted to visible vertices (the original
    /// flood stops at hidden ones). Returns the new selection size.
    /// </summary>
    public static int Flood(Mesh mesh, SkinSelection selection)
    {
        var neighbours = Neighbours(mesh);
        bool hidden = mesh.HasHiddenVertices;
        var queue = new Queue<int>();
        for (int v = 0; v < selection.Count; v++)
            if (selection.IsSelected(v)) queue.Enqueue(v);
        while (queue.Count > 0)
        {
            int v = queue.Dequeue();
            foreach (int q in neighbours[v])
            {
                if (selection.IsSelected(q)) continue;
                if (hidden && mesh.IsVertexHidden(q)) continue;
                selection.Set(q, true);
                queue.Enqueue(q);
            }
        }
        return selection.SelectedCount;
    }

    /// <summary>Control-point adjacency from the polygon corners (shared edges).</summary>
    public static int[][] Neighbours(Mesh mesh)
    {
        int count = mesh.ControlPoints.Length;
        var sets = new HashSet<int>[count];
        for (int i = 0; i < count; i++) sets[i] = [];
        var cornerPoints = mesh.CornerControlPoints;
        foreach (var polygon in mesh.Polygons)
        {
            var corners = polygon.Corners;
            for (int i = 0; i < corners.Length; i++)
            {
                int ci = (int)corners[i];
                int cj = (int)corners[(i + 1) % corners.Length];
                if (ci < 0 || cj < 0 || ci >= cornerPoints.Length || cj >= cornerPoints.Length) continue;
                int a = (int)cornerPoints[ci];
                int b = (int)cornerPoints[cj];
                if (a == b || a < 0 || b < 0 || a >= count || b >= count) continue;
                sets[a].Add(b);
                sets[b].Add(a);
            }
        }
        return sets.Select(s => s.ToArray()).ToArray();
    }

    /// <summary>Starts a stroke on one mesh for one joint (see <see cref="SkinPaintStroke"/>).</summary>
    public static SkinPaintStroke BeginStroke(Mesh mesh, int joint, bool rigid = false, Character? character = null) =>
        new(mesh, joint, rigid, character);
}

/// <summary>
/// A control-point selection, the runtime equivalent of the mesh's selection array
/// It is never written to the scene.
/// </summary>
public sealed class SkinSelection
{
    private readonly bool[] _flags;

    public SkinSelection(Mesh mesh) : this(mesh.ControlPoints.Length) { }

    public SkinSelection(int vertexCount) => _flags = new bool[vertexCount];

    public int Count => _flags.Length;

    public int SelectedCount { get; private set; }

    public bool IsSelected(int vertex) => vertex >= 0 && vertex < _flags.Length && _flags[vertex];

    public void Set(int vertex, bool selected)
    {
        if (vertex < 0 || vertex >= _flags.Length || _flags[vertex] == selected) return;
        _flags[vertex] = selected;
        SelectedCount += selected ? 1 : -1;
    }

    public void Clear()
    {
        Array.Clear(_flags);
        SelectedCount = 0;
    }

    public IEnumerable<int> SelectedVertices()
    {
        for (int i = 0; i < _flags.Length; i++)
            if (_flags[i]) yield return i;
    }

    public static SkinSelection From(Mesh mesh, IEnumerable<int> vertices)
    {
        var selection = new SkinSelection(mesh);
        foreach (int v in vertices) selection.Set(v, true);
        return selection;
    }
}

/// <summary>
/// The mutable weight arrays of one mesh, with the writer and Sort of
/// Read once, edit in memory,
/// <see cref="Write"/> once.
/// </summary>
public sealed class SkinWeightTable
{
    private const float WeightEpsilon = 1e-5f;

    private readonly Mesh _mesh;
    private readonly string _field;
    private readonly List<(int Bone, float Weight)>[] _vertices;
    private readonly Character? _character;

    public SkinWeightTable(Mesh mesh, bool rigid = false, Character? character = null)
    {
        _mesh = mesh;
        _field = rigid ? "RigidSkinWeightArray" : "SkinWeightArray";
        _character = character ?? mesh.BindPose?.Character ?? mesh.Ancestor<Character>();
        var arrays = rigid ? mesh.RigidSkinWeights : mesh.SkinWeights;
        _vertices = new List<(int Bone, float Weight)>[mesh.ControlPoints.Length];
        for (int v = 0; v < _vertices.Length; v++)
            _vertices[v] = v < arrays.Length
                ? arrays[v].Select(w => (w.Bone, w.Weight)).ToList()
                : [];
    }

    public Mesh Mesh => _mesh;

    public int VertexCount => _vertices.Length;

    public int JointCount => _character?.Joints.Count ?? 0;

    /// <summary>The character the joint indices index; null for a mesh outside a character.</summary>
    public Character? Character => _character;

    public IReadOnlyList<(int Bone, float Weight)> Weights(int vertex) => _vertices[vertex];

    public float Weight(int vertex, int joint)
    {
        foreach (var (bone, weight) in _vertices[vertex])
            if (bone == joint) return weight;
        return 0f;
    }

    /// <summary>
    /// The weight writer: give <paramref name="joint"/> the
    /// <paramref name="target"/> weight, taking the difference from (or giving it to) the vertex's
    /// other eligible joints proportionally, then Sort. Joints in <paramref name="protectedJoints"/>
    /// are not redistributed.
    /// </summary>
    public void SetWeight(int vertex, int joint, float target, IReadOnlyCollection<int>? protectedJoints = null,
        IReadOnlyList<Vector3>? jointPositions = null, Vector3? vertexPosition = null)
    {
        var list = _vertices[vertex];
        float old = Weight(vertex, joint);
        if (target < 0.001f) target = 0f;

        float others = 0f;
        int eligibleOnVertex = 0;
        foreach (var (bone, weight) in list)
        {
            if (bone == joint || IsProtected(protectedJoints, bone)) continue;
            others += weight;
            eligibleOnVertex++;
        }

        if (old < target && target > old + others) target = old + others;
        float delta = target - old;
        if (delta is > -WeightEpsilon and < WeightEpsilon) return;

        if (delta > 0f)
        {
            if (others == 0f) return;
            for (int i = 0; i < list.Count; i++)
            {
                var (bone, weight) = list[i];
                if (bone == joint || IsProtected(protectedJoints, bone)) continue;
                list[i] = (bone, weight - weight * delta / others);
            }
        }
        else
        {
            int eligibleJoints = CountEligibleJoints(joint, protectedJoints);
            if (eligibleOnVertex == 0)
            {
                if (eligibleJoints == 0) return;
                if (eligibleJoints == 1)
                {
                    int only = FirstEligibleJoint(joint, protectedJoints);
                    Set(list, only, -delta);
                }
                else
                {
                    // Only a fully owned vertex can give weight to a joint it does not have.
                    if (MathF.Abs(old - 1f) > WeightEpsilon) return;
                    int nearest = NearestEligibleJoint(joint, protectedJoints, jointPositions, vertexPosition);
                    if (nearest < 0) return;
                    Set(list, nearest, 1f - target);
                }
            }
            else
            {
                if (others == 0f) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var (bone, weight) = list[i];
                    if (bone == joint || IsProtected(protectedJoints, bone)) continue;
                    list[i] = (bone, weight - weight * delta / others);
                }
            }
        }

        Set(list, joint, target);
        Normalize(vertex);
    }

    /// <summary>Replace a vertex's influence list (prune/fix/mirror).</summary>
    public void Replace(int vertex, IEnumerable<(int Bone, float Weight)> weights)
    {
        var list = _vertices[vertex];
        list.Clear();
        list.AddRange(weights);
    }

    /// <summary>
    /// Sort a vertex's weights: order by weight, drop everything below <c>1e-5</c> and
    /// normalize the rest to a sum of 1.
    /// </summary>
    public void Normalize(int vertex)
    {
        var list = _vertices[vertex];
        if (list.Count == 0) return;
        list.Sort((a, b) => b.Weight.CompareTo(a.Weight));
        int cut = list.FindIndex(w => w.Weight < WeightEpsilon);
        if (cut >= 0) list.RemoveRange(cut, list.Count - cut);
        float sum = 0f;
        foreach (var (_, weight) in list) sum += weight;
        if (list.Count == 0 || sum <= 0f) return;
        if (MathF.Abs(sum - 1f) <= WeightEpsilon) return;
        for (int i = 0; i < list.Count; i++)
        {
            var (bone, weight) = list[i];
            list[i] = (bone, weight / sum);
        }
    }

    /// <summary>Prune one vertex; returns how many influences were removed.</summary>
    public int PruneVertex(int vertex, float limit)
    {
        var list = _vertices[vertex];
        float removedWeight = 0f;
        int removed = 0;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Weight >= limit) continue;
            removedWeight += list[i].Weight;
            list.RemoveAt(i);
            removed++;
        }
        if (removed > 0 && list.Count > 0)
        {
            float add = removedWeight / list.Count;
            for (int i = 0; i < list.Count; i++)
                list[i] = (list[i].Bone, list[i].Weight + add);
        }
        return removed;
    }

    /// <summary>Writes the edited array back into the document.</summary>
    public void Write()
    {
        var schema = _mesh.Model.Document.Schema;
        if (schema.FindStruct("CVertexWeight") == null || schema.FindStruct("CVertexWeightArray") == null)
            throw new InvalidOperationException("The scene schema has no CVertexWeight class; it cannot store skin weights.");
        var array = new List<object?>(_vertices.Length);
        foreach (var weights in _vertices) array.Add(NewWeightArray(schema, weights));
        _mesh.Source.Set(_field, array);
    }

    private static AktStruct NewWeightArray(AktSchema schema, IReadOnlyList<(int Bone, float Weight)> weights)
    {
        var entry = schema.NewStruct("CVertexWeightArray");
        var list = (List<object?>)entry["WeightArray"]!;
        foreach (var (bone, weight) in weights)
        {
            var value = schema.NewStruct("CVertexWeight");
            value.Set("BoneIndex", (uint)bone);
            value.Set("Weight", weight);
            list.Add(value);
        }
        return entry;
    }

    private static bool IsProtected(IReadOnlyCollection<int>? protectedJoints, int joint) =>
        protectedJoints != null && protectedJoints.Contains(joint);

    private int CountEligibleJoints(int joint, IReadOnlyCollection<int>? protectedJoints)
    {
        int count = 0;
        for (int i = 0; i < JointCount; i++)
            if (i != joint && !IsProtected(protectedJoints, i)) count++;
        return count;
    }

    private int FirstEligibleJoint(int joint, IReadOnlyCollection<int>? protectedJoints)
    {
        for (int i = 0; i < JointCount; i++)
            if (i != joint && !IsProtected(protectedJoints, i)) return i;
        return -1;
    }

    private int NearestEligibleJoint(int joint, IReadOnlyCollection<int>? protectedJoints,
        IReadOnlyList<Vector3>? jointPositions, Vector3? vertexPosition)
    {
        if (jointPositions == null || jointPositions.Count == 0 || vertexPosition is not { } point)
            return FirstEligibleJoint(joint, protectedJoints);
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < JointCount && i < jointPositions.Count; i++)
        {
            if (i == joint || IsProtected(protectedJoints, i)) continue;
            float distance = Vector3.DistanceSquared(jointPositions[i], point);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }
        return best;
    }

    private static void Set(List<(int Bone, float Weight)> list, int joint, float weight)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Bone != joint) continue;
            list[i] = (joint, weight);
            return;
        }
        list.Add((joint, weight));
    }
}

/// <summary>
/// The Skin Workshop's copy/paste clipboard:
/// the weights of the copied control points, kept by joint name so they can be pasted
/// onto another character, plus each copied joint's ancestor names for the slough fallback. The
/// caller holds it.
/// </summary>
public sealed class SkinWeightClipboard
{
    /// <summary>One influence of a copied control point: the joint's name and its weight.</summary>
    public readonly record struct Entry(string JointName, float Weight);

    private readonly List<Entry[]> _vertices = [];

    /// <summary>For every copied joint name, its ancestors' names, nearest first (the slough order).</summary>
    private readonly Dictionary<string, string[]> _ancestors = new(StringComparer.Ordinal);

    public int VertexCount => _vertices.Count;

    public bool IsEmpty => _vertices.Count == 0;

    public IReadOnlyList<Entry[]> Vertices => _vertices;

    /// <summary>The ancestor names recorded for one copied joint (empty when unknown).</summary>
    public IReadOnlyList<string> AncestorsOf(string jointName) =>
        _ancestors.TryGetValue(jointName, out var chain) ? chain : [];

    /// <summary>
    /// Copies the selected control points of <paramref name="table"/>. The app refuses fewer than
    /// two control points (it only reports success once the selection array has more than one
    /// non-zero entry), so this returns null and sets <paramref name="refusal"/> then.
    /// </summary>
    public static SkinWeightClipboard? Copy(SkinWeightTable table, SkinSelection selection,
        Character? character, out string? refusal)
    {
        refusal = null;
        if (selection.SelectedCount < 2)
        {
            refusal = $"Copy needs at least two selected control points; {selection.SelectedCount} is not enough.";
            return null;
        }

        var clipboard = new SkinWeightClipboard();
        foreach (int vertex in selection.SelectedVertices())
        {
            if (vertex < 0 || vertex >= table.VertexCount) continue;
            var entries = new List<Entry>();
            foreach (var (bone, weight) in table.Weights(vertex))
            {
                if (character == null || bone < 0 || bone >= character.Joints.Count) continue;
                // The app pastes by joint name, so an unnamed joint could never be matched.
                if (character.Joints[bone].Name is not { } name) continue;
                entries.Add(new Entry(name, weight));
                clipboard.RecordAncestors(character, bone, name);
            }
            clipboard._vertices.Add([.. entries]);
        }

        if (clipboard._vertices.Count == 0)
        {
            refusal = "No control point of the selection carries a weight for a joint of the character.";
            return null;
        }
        return clipboard;
    }

    /// <summary>Records the source joint's ancestors, nearest first, for the slough's fallback.</summary>
    private void RecordAncestors(Character character, int bone, string name)
    {
        if (_ancestors.ContainsKey(name)) return;
        var chain = new List<string>();
        for (var parent = character.Joints[bone].Parent3D; parent is Joint ancestor; parent = parent.Parent3D)
        {
            if (ancestor.Name is not { } ancestorName) continue;
            if (ancestorName == name) break;
            chain.Add(ancestorName);
        }
        _ancestors[name] = [.. chain];
    }

    /// <summary>
    /// Writes the copied weights onto the selected control points of <paramref name="target"/>,
    /// pairing them in ascending order up to the shorter of the two lists. A copied joint with a
    /// name on the target keeps its own joint; one without a counterpart goes through
    /// <see cref="SkinSlough"/> instead of refusing (the app opens its slough dialog for that case).
    /// Returns what was written and the substitutions it made.
    /// </summary>
    public SkinSloughResult Paste(SkinWeightTable target, SkinSelection selection, Character character,
        out string? refusal)
    {
        refusal = null;
        if (IsEmpty)
        {
            refusal = "The clipboard is empty; copy some weights first.";
            return new SkinSloughResult(0, [], false);
        }
        if (character.Joints.Count == 0)
        {
            refusal = $"'{character.Name}' has no joint to paste onto.";
            return new SkinSloughResult(0, [], false);
        }

        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < character.Joints.Count; i++)
            if (character.Joints[i].Name is { } name) indexOf.TryAdd(name, i);

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var sloughed = false;
        foreach (var name in _vertices.SelectMany(entries => entries).Select(entry => entry.JointName).Distinct())
        {
            if (indexOf.TryGetValue(name, out int matched))
            {
                map[name] = matched;
                continue;
            }
            var (joint, via) = SkinSlough.Resolve(name, indexOf, AncestorsOf(name));
            map[name] = joint;
            sloughed = true;
            warnings.Add(via is null
                ? $"'{name}' has no counterpart; pasted onto the root joint '{character.Joints[joint].Name}'."
                : $"'{name}' has no counterpart; pasted onto its ancestor '{via}'.");
        }

        var targets = selection.SelectedVertices().Where(v => v < target.VertexCount).ToList();
        if (targets.Count == 0)
        {
            refusal = "Select the control points to paste onto.";
            return new SkinSloughResult(0, [], false);
        }

        int count = Math.Min(targets.Count, _vertices.Count);
        for (int i = 0; i < count; i++)
        {
            // Several copied joints can map onto the same target joint (the slough's ancestor/root
            // fallback does that), so the mapped list is summed per joint before Sort: an influence
            // array holds one entry per joint.
            var weights = new Dictionary<int, float>();
            foreach (var entry in _vertices[i])
            {
                int joint = map[entry.JointName];
                weights[joint] = weights.GetValueOrDefault(joint) + entry.Weight;
            }
            target.Replace(targets[i], weights.Select(pair => (pair.Key, pair.Value)));
            target.Normalize(targets[i]);
        }
        target.Write();
        return new SkinSloughResult(count, warnings, sloughed);
    }
}

/// <summary>
/// What one copy/paste did: the control points written, the slough substitutions (each a sentence
/// the viewer can put in its status line) and whether any joint had to be substituted at all.
/// </summary>
public readonly record struct SkinSloughResult(int Points, IReadOnlyList<string> Warnings, bool Remapped);

/// <summary>
/// The "slough" joint substitution when copying and pasting weights:
/// The app opens the slough dialog when a copied joint has no
/// same-named counterpart, lets the user pair the slough list with target joints, and pastes the
/// weight arrays through the resulting map. Where the dialog's map has no pair, a joint inherits
/// the target of its nearest already-mapped ancestor joint and otherwise the target character's
/// first joint.
/// </summary>
public static class SkinSlough
{
    /// <summary>
    /// The target joint a copied joint is written onto: its ancestor's target when one of
    /// <paramref name="ancestors"/> (nearest first) has a counterpart on the target, else the target
    /// character's first joint - the last resort. <c>Via</c> names the ancestor used, or null
    /// for the root fallback.
    /// </summary>
    public static (int Joint, string? Via) Resolve(string sourceJoint, IReadOnlyDictionary<string, int> targetByName,
        IReadOnlyList<string> ancestors)
    {
        foreach (string ancestor in ancestors)
            if (ancestor != sourceJoint && targetByName.TryGetValue(ancestor, out int joint)) return (joint, ancestor);
        return (0, null);
    }
}

/// <summary>
/// One painting stroke on one mesh for one joint. The first dab on a control point fixes that
/// point's goal for the whole stroke; later dabs move it toward the goal with the brush's falloff
/// </summary>
public sealed class SkinPaintStroke
{
    private readonly SkinWeightTable _table;
    private readonly int _joint;
    private readonly int[][] _neighbours;
    private readonly float?[] _goals;

    internal SkinPaintStroke(Mesh mesh, int joint, bool rigid, Character? character)
    {
        _table = new SkinWeightTable(mesh, rigid, character);
        _joint = joint;
        _neighbours = SkinWorkshop.Neighbours(mesh);
        _goals = new float?[_table.VertexCount];
    }

    public Mesh Mesh => _table.Mesh;

    public int Joint => _joint;

    public int VertexCount => _table.VertexCount;

    public float Goal(int vertex) => _goals[vertex] ?? float.NaN;

    /// <summary>
    /// One dab at <paramref name="center"/>; <paramref name="positions"/> are the control points in
    /// the pose being painted (world space). Hidden control points are skipped, and when the brush's
    /// <see cref="SkinPaintBrush.FrontBack"/> is off so are the points facing away from
    /// <paramref name="eye"/> (<paramref name="normals"/> in world space). Returns how many control
    /// points were painted.
    /// </summary>
    public int Apply(SkinPaintBrush brush, Vector3 center, IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3>? jointPositions = null, IReadOnlyList<Vector3>? normals = null,
        Vector3? eye = null)
    {
        if (positions.Count != _table.VertexCount)
            throw new ArgumentException("The position list must have one entry per control point.", nameof(positions));
        if (brush.Mode == SkinPaintMode.Select)
            throw new InvalidOperationException("Select mode paints the vertex selection, not the weights.");
        if (brush.Radius <= 0f) throw new InvalidOperationException("The brush radius must be positive.");

        bool hidden = _table.Mesh.HasHiddenVertices;
        bool limited = brush.LimitTo is { SelectedCount: > 0 };
        int painted = 0;
        for (int v = 0; v < positions.Count; v++)
        {
            if (hidden && _table.Mesh.IsVertexHidden(v)) continue;
            if (limited && !brush.LimitTo!.IsSelected(v)) continue;
            float distance = Vector3.Distance(positions[v], center);
            float ratio = distance / brush.Radius;
            if (ratio > 1f) continue;
            if (!brush.FrontBack && normals != null && v < normals.Count &&
                !FrontFacing(positions[v], normals[v], eye)) continue;

            float current = _table.Weight(v, _joint);
            float goal = _goals[v] ??= GoalFor(v, current, brush);
            float factor = brush.Hard ? 1f : SkinWorkshop.Falloff(ratio);
            float next = current + (goal - current) * factor;
            if ((goal > current && next > goal) || (goal < current && next < goal)) next = goal;
            next = Math.Clamp(next, 0f, 1f);
            if (MathF.Abs(next - current) < 1e-6f) continue;

            _table.SetWeight(v, _joint, next, brush.ProtectedJoints, jointPositions, positions[v]);
            painted++;
        }
        return painted;
    }

    /// <summary>
    /// The Skin Workshop's front/back test: a control point is front-facing when its world normal
    /// points toward <paramref name="eye"/>. Points without a usable normal count as front-facing.
    /// </summary>
    public static bool FrontFacing(Vector3 position, Vector3 normal, Vector3? eye)
    {
        if (eye is not { } view) return true;
        if (normal.LengthSquared() <= 1e-12f) return true;
        return Vector3.Dot(normal, view - position) > 0f;
    }

    /// <summary>Writes the stroke's result into the document.</summary>
    public void Commit() => _table.Write();

    private float GoalFor(int vertex, float current, SkinPaintBrush brush) => brush.Mode switch
    {
        SkinPaintMode.Add => current + brush.Strength * (brush.Subtract ? -brush.Value : brush.Value),
        SkinPaintMode.Aim => current + brush.Strength * (brush.Value - current),
        SkinPaintMode.Smooth => current + brush.Strength * (NeighbourAverage(vertex) - current),
        _ => current,
    };

    private float NeighbourAverage(int vertex)
    {
        var neighbours = _neighbours[vertex];
        if (neighbours.Length == 0) return _table.Weight(vertex, _joint);
        float sum = 0f;
        foreach (int q in neighbours) sum += _table.Weight(q, _joint);
        return sum / neighbours.Length;
    }
}

/// <summary>
/// Mirror Skin: copy
/// the selected joints' weights onto the mirrored control points of the character. Each target
/// control point is blended from the three nearest mirrored source control points by inverse
/// distance, with every joint mapped to its symmetric counterpart; a source point at (almost) the
/// mirrored position is copied directly, which is what a symmetric character hits.
/// </summary>
public static class SkinMirror
{
    /// <summary>How many mirrored control points one target point is blended from (the three-entry list).</summary>
    public const int BlendCount = 3;

    /// <summary>
    /// The default search radius, as a fraction of the mesh diagonal. The original shows no explicit
    /// match radius (the <c>1e-4</c> next to the point-list helper is that helper's tolerance), so a
    /// bounded neighbourhood is SloppyAnimatsu's choice; the blend only differs from a single match when
    /// the mirrored point does not land exactly on a control point.
    /// </summary>
    public const float DefaultReachFraction = 0.02f;

    /// <summary>
    /// One candidate of a Mirror Skin match: a source-side control point reflected into the mirrored
    /// half, its distance to the target point and the weights it carries.
    /// </summary>
    public readonly record struct MirrorCandidate(int Mesh, int Vertex, float Distance,
        IReadOnlyList<(int Bone, float Weight)> Weights);

    /// <summary>
    /// The Mirror Skin blend: the up-to-three
    /// nearest candidates (by squared distance, then mesh and vertex so the result is deterministic)
    /// are blended by inverse distance (<c>1 / d</c>), every joint is mapped through
    /// <paramref name="partners"/> before it is accumulated, and the sum is normalised. A nearest
    /// candidate within <paramref name="coincidentDistance"/> (1e-6 when not given) copies its
    /// weights directly instead. Candidates past the third are ignored.
    /// </summary>
    public static IReadOnlyList<(int Bone, float Weight)> BlendCandidates(
        IReadOnlyList<MirrorCandidate> candidates, IReadOnlyDictionary<int, int>? partners = null,
        float coincidentDistance = 0f)
    {
        var nearest = candidates
            .Where(c => c.Distance >= 0f && float.IsFinite(c.Distance))
            .OrderBy(c => c.Distance)
            .ThenBy(c => c.Mesh)
            .ThenBy(c => c.Vertex)
            .Take(BlendCount)
            .ToArray();
        if (nearest.Length == 0) return [];
        float coincident = coincidentDistance > 0f ? coincidentDistance : 1e-6f;
        if (nearest[0].Distance <= coincident) return Mapped(nearest[0].Weights, partners);

        var sums = new Dictionary<int, float>();
        float total = 0f;
        foreach (var candidate in nearest)
        {
            float inverse = 1f / MathF.Max(candidate.Distance, coincident);
            total += inverse;
            foreach (var (bone, weight) in candidate.Weights)
            {
                int joint = partners != null && partners.TryGetValue(bone, out int partner) ? partner : bone;
                sums[joint] = sums.TryGetValue(joint, out float running) ? running + weight * inverse : weight * inverse;
            }
        }
        if (total <= 0f) return [];
        return sums
            .Where(pair => pair.Value >= 1e-5f)
            .Select(pair => (Bone: pair.Key, Weight: pair.Value / total))
            .OrderByDescending(pair => pair.Weight)
            .ThenBy(pair => pair.Bone)
            .ToArray();
    }

    /// <summary>Maps a copied influence list's joints through the partner map and sorts it by weight.</summary>
    private static IReadOnlyList<(int Bone, float Weight)> Mapped(IReadOnlyList<(int Bone, float Weight)> weights,
        IReadOnlyDictionary<int, int>? partners)
    {
        if (partners == null || partners.Count == 0)
            return weights.Select(w => (w.Bone, w.Weight)).OrderByDescending(w => w.Weight).ThenBy(w => w.Bone).ToArray();
        return weights
            .Select(w => (Bone: partners.TryGetValue(w.Bone, out int partner) ? partner : w.Bone, w.Weight))
            .OrderByDescending(w => w.Weight)
            .ThenBy(w => w.Bone)
            .ToArray();
    }

    /// <summary>
    /// Mirrors the <paramref name="sourceJoints"/> weights (default: every joint with a symmetric
    /// counterpart on the left or up side) onto the control points found at the mirrored bind-pose
    /// position, in <paramref name="meshes"/> (default: the character's skinned, unlocked meshes).
    /// The mirror plane is the mean X of the symmetric joint pairs (the original refuses a character
    /// that is not centred; SloppyAnimatsu mirrors about the pair mean instead). Returns how many control
    /// points were written.
    /// </summary>
    public static int Mirror(Character character, IEnumerable<Joint>? sourceJoints = null,
        IEnumerable<Mesh>? meshes = null, float tolerance = 0f, MirrorMatch? match = null)
    {
        match ??= MirrorMatch.Default;
        var joints = character.Joints;
        var jointIndex = new Dictionary<Joint, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < joints.Count; i++) jointIndex.TryAdd(joints[i], i);

        var sources = (sourceJoints ?? joints.Where(j =>
                j.Name is { } name && match.SideOf(name) is MirrorSide.Left or MirrorSide.Up))
            .Distinct()
            .Where(j => jointIndex.ContainsKey(j))
            .ToList();
        if (sources.Count == 0) return 0;

        var partners = new Dictionary<int, int>();
        foreach (var joint in sources)
        {
            if (joint.Name is not { } sourceName) continue;
            if (!match.TryMirror(sourceName, out string mirrored, out _)) continue;
            var other = joints.FirstOrDefault(j => j.Name == mirrored);
            if (other != null) partners[jointIndex[joint]] = jointIndex[other];
        }
        if (partners.Count == 0) return 0;

        var targets = (meshes ?? character.Meshes.Where(m => m.Skinned && !m.SkinnedMeshIsLocked))
            .Distinct()
            .ToList();

        var controllerIndex = new Dictionary<Object3D, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < character.Controllers.Count; i++) controllerIndex.TryAdd(character.Controllers[i], i);

        // Bind-pose positions per mesh, and the mirror plane from the symmetric joint pairs.
        var data = new List<(Mesh Mesh, SkinWeightTable Table, Vector3[] Positions)>();
        foreach (var mesh in targets)
        {
            var skin = new MeshSkin(mesh);
            var worlds = new PoseEvaluator(character, restPose: mesh.BindPose).WorldMatrices(null, 0);
            data.Add((mesh, new SkinWeightTable(mesh, false, character), skin.Positions(worlds)));
        }
        if (data.Count == 0) return 0;

        // Mirrored joint positions in the first mesh's bind pose are close enough for the plane.
        var firstWorlds = new PoseEvaluator(character, restPose: data[0].Mesh.BindPose).WorldMatrices(null, 0);
        float JointX(int joint) =>
            controllerIndex.TryGetValue(joints[joint], out int controller) && controller < firstWorlds.Length
                ? firstWorlds[controller].Translation.X
                : joints[joint].WorldMatrix.Translation.X;
        float planeSum = 0f;
        int planeCount = 0;
        foreach (var (source, partner) in partners)
        {
            planeSum += (JointX(source) + JointX(partner)) * 0.5f;
            planeCount++;
        }
        float plane = planeCount > 0 ? planeSum / planeCount : 0f;

        // The search: the source-side control points reflected into the mirrored half, the three
        // nearest of them for every target point, blended by inverse distance.
        float diagonal = Diagonal(data.SelectMany(d => d.Positions).ToArray());
        float reach = tolerance > 0f ? tolerance : diagonal * DefaultReachFraction;
        float copyThreshold = reach * 1e-3f;
        float cell = MathF.Max(reach * 0.5f, 1e-6f);

        var sourceSet = partners.Keys.ToHashSet();
        var candidates = new List<(int Mesh, int Vertex, Vector3 Point, IReadOnlyList<(int Bone, float Weight)> Weights)>();
        var candidateGrid = new Dictionary<(int X, int Y, int Z), List<int>>();
        for (int m = 0; m < data.Count; m++)
        {
            var (_, table, positions) = data[m];
            for (int v = 0; v < positions.Length; v++)
            {
                var weights = table.Weights(v);
                if (!weights.Any(w => sourceSet.Contains(w.Bone))) continue;
                var reflected = Reflect(positions[v], plane);
                var key = Cell(reflected, cell);
                if (!candidateGrid.TryGetValue(key, out var list)) candidateGrid[key] = list = [];
                list.Add(candidates.Count);
                candidates.Add((m, v, reflected, weights));
            }
        }
        if (candidates.Count == 0) return 0;

        // Every control point on the other side of the plane is blended from the nearest reflected
        // source points. A point that carries a source joint is on the source side: read, not written.
        var writes = new List<(SkinWeightTable Table, int Vertex, List<(int Bone, float Weight)> Weights)>();
        for (int m = 0; m < data.Count; m++)
        {
            var (_, table, positions) = data[m];
            for (int v = 0; v < positions.Length; v++)
            {
                if (table.Weights(v).Any(w => sourceSet.Contains(w.Bone))) continue;
                var found = Nearest(candidateGrid, candidates, positions[v], cell, reach, m, data, match);
                if (found.Count == 0) continue;
                var blended = BlendCandidates(found, partners, copyThreshold);
                if (blended.Count == 0) continue;
                writes.Add((table, v, [.. blended]));
            }
        }

        int written = 0;
        var dirty = new HashSet<SkinWeightTable>(ReferenceEqualityComparer.Instance);
        foreach (var (table, vertex, weights) in writes)
        {
            table.Replace(vertex, weights);
            table.Normalize(vertex);
            dirty.Add(table);
            written++;
        }
        foreach (var table in dirty) table.Write();
        return written;
    }

    /// <summary>
    /// The up-to-three nearest reflected source control points to <paramref name="point"/> within
    /// <paramref name="reach"/>, allowed by <see cref="Receives"/>, sorted by distance then mesh and
    /// vertex (deterministic ties).
    /// </summary>
    private static List<MirrorCandidate> Nearest(
        Dictionary<(int X, int Y, int Z), List<int>> grid,
        List<(int Mesh, int Vertex, Vector3 Point, IReadOnlyList<(int Bone, float Weight)> Weights)> candidates,
        Vector3 point, float cell, float reach, int targetMesh,
        IReadOnlyList<(Mesh Mesh, SkinWeightTable Table, Vector3[] Positions)> data, MirrorMatch match)
    {
        int cx = (int)MathF.Floor(point.X / cell);
        int cy = (int)MathF.Floor(point.Y / cell);
        int cz = (int)MathF.Floor(point.Z / cell);
        int span = (int)MathF.Ceiling(reach / cell);
        float reachSquared = reach * reach;
        var found = new List<MirrorCandidate>();
        var seen = new HashSet<int>();
        for (int dx = -span; dx <= span; dx++)
            for (int dy = -span; dy <= span; dy++)
                for (int dz = -span; dz <= span; dz++)
                {
                    if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var list)) continue;
                    foreach (int index in list)
                    {
                        if (!seen.Add(index)) continue;
                        var (mesh, vertex, candidate, weights) = candidates[index];
                        if (!Receives(targetMesh, mesh, data, match)) continue;
                        float distance = Vector3.DistanceSquared(candidate, point);
                        if (distance > reachSquared) continue;
                        found.Add(new MirrorCandidate(mesh, vertex, MathF.Sqrt(distance), weights));
                    }
                }
        found.Sort((a, b) =>
        {
            int order = a.Distance.CompareTo(b.Distance);
            if (order != 0) return order;
            order = a.Mesh.CompareTo(b.Mesh);
            return order != 0 ? order : a.Vertex.CompareTo(b.Vertex);
        });
        if (found.Count > BlendCount) found.RemoveRange(BlendCount, found.Count - BlendCount);
        return found;
    }

    /// <summary>
    /// Whether a candidate on <paramref name="candidateMesh"/> may write onto
    /// <paramref name="targetMesh"/>: the same mesh, or its symmetric counterpart when their names
    /// mirror (<c>MirrorSkinOnDifferentMeshes</c>).
    /// </summary>
    private static bool Receives(int targetMesh, int candidateMesh,
        IReadOnlyList<(Mesh Mesh, SkinWeightTable Table, Vector3[] Positions)> data, MirrorMatch match)
    {
        if (candidateMesh == targetMesh) return true;
        return data[targetMesh].Mesh.Name is { } name && match.TryMirror(name, out string other, out _) &&
               other == data[candidateMesh].Mesh.Name;
    }

    /// <summary>The point at the other side of the mirror plane.</summary>
    private static Vector3 Reflect(Vector3 point, float plane) => new(2f * plane - point.X, point.Y, point.Z);

    private static (int X, int Y, int Z) Cell(Vector3 p, float size) =>
        ((int)MathF.Floor(p.X / size), (int)MathF.Floor(p.Y / size), (int)MathF.Floor(p.Z / size));

    private static float Diagonal(Vector3[] points)
    {
        if (points.Length == 0) return 1f;
        var min = points[0];
        var max = points[0];
        foreach (var p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        float diagonal = Vector3.Distance(min, max);
        return diagonal > 1e-6f ? diagonal : 1f;
    }
}

/// <summary>
/// The Skin Workshop panel's state and edits
/// the mesh and joint being painted, the brush settings, the control-point selection, the
/// stroke in progress, and the panel's Set, Prune, Fix, Flood, Mirror and Copy/Paste commands. The
/// pointer code only turns viewport positions into dabs, so the panel's whole path can be driven
/// without a window.
/// </summary>
public sealed class SkinPaintSession
{
    private SkinPaintStroke? _stroke;
    private readonly HashSet<int> _locked = [];

    public SkinPaintSession(Character character, Mesh mesh, bool rigid = false)
    {
        Character = character;
        Mesh = mesh;
        Rigid = rigid;
        Selection = new SkinSelection(mesh);
        Joint = character.Joints.Count > 0 ? 0 : -1;
    }

    public Character Character { get; }
    public Mesh Mesh { get; private set; }

    /// <summary>The weight array edited: the rigid one (the rigging context) or the smooth one.</summary>
    public bool Rigid { get; set; }

    /// <summary>The painted joint, as an index into <see cref="Character"/>'s joints; -1 when none.</summary>
    public int Joint { get; set; }

    public Joint? JointObject =>
        Joint >= 0 && Joint < Character.Joints.Count ? Character.Joints[Joint] : null;

    /// <summary>The paint mode.</summary>
    public SkinPaintMode Mode { get; set; } = SkinPaintMode.Add;

    /// <summary>The brush radius in world units (the viewer converts its screen radius).</summary>
    public float Radius { get; set; } = 1f;

    /// <summary>How much of the mode's goal one stroke applies (<c>Strength</c>).</summary>
    public float Strength { get; set; } = 1f;

    /// <summary>The weight Add adds and Aim aims at (<c>Value</c>).</summary>
    public float Value { get; set; } = 1f;

    /// <summary><c>BrushType == 1</c>: no falloff.</summary>
    public bool Hard { get; set; } = true;

    /// <summary>Alt mode: Add subtracts instead of adding.</summary>
    public bool Subtract { get; set; }

    /// <summary>The limit Prune removes below (<c>PruneLimit</c>, 0.001 in the shipped file).</summary>
    public float PruneLimit { get; set; } = 0.001f;

    /// <summary><c>Scanner</c>: hover picks the control point under the brush and reads its weights.</summary>
    public bool Scanner { get; set; }

    /// <summary><c>FrontBackPaint</c>: paint through the mesh when true, front-facing points only when false.</summary>
    public bool FrontBackPaint { get; set; } = true;

    /// <summary><c>FrontBackSelect</c>: select through the mesh when true, front-facing points only when false.</summary>
    public bool FrontBackSelect { get; set; } = true;

    /// <summary>The view point of the front/back test, in world space; null treats every point as front-facing.</summary>
    public Vector3? Eye { get; set; }

    /// <summary>The control point the Scanner/last dab/last selection last touched; -1 when none.</summary>
    public int CurrentVertex { get; set; } = -1;

    /// <summary>The joints whose weight is not redistributed (the weight locks).</summary>
    public IReadOnlyCollection<int> LockedJoints => _locked;

    public bool IsLocked(int joint) => _locked.Contains(joint);

    /// <summary>Turns one joint's lock on or off; returns its new state.</summary>
    public bool ToggleLock(int joint)
    {
        if (!_locked.Add(joint)) _locked.Remove(joint);
        return _locked.Contains(joint);
    }

    /// <summary>Sets several joints' locks and returns how many changed.</summary>
    public int SetLock(IEnumerable<int> joints, bool locked)
    {
        int changed = 0;
        foreach (int joint in joints)
        {
            if (joint < 0 || joint >= Character.Joints.Count) continue;
            if (locked == _locked.Contains(joint)) continue;
            if (locked) _locked.Add(joint); else _locked.Remove(joint);
            changed++;
        }
        return changed;
    }

    /// <summary>Clears every lock; returns how many were on.</summary>
    public int ClearLocks()
    {
        int count = _locked.Count;
        _locked.Clear();
        return count;
    }

    public SkinSelection Selection { get; private set; }

    /// <summary>The stroke in progress, or null.</summary>
    public SkinPaintStroke? Stroke => _stroke;

    /// <summary>How many control points the last dab painted.</summary>
    public int LastPainted { get; private set; }

    /// <summary>The brush the session paints with.</summary>
    public SkinPaintBrush Brush => new()
    {
        Mode = Mode,
        Radius = Radius,
        Strength = Strength,
        Value = Value,
        Hard = Hard,
        Subtract = Subtract,
        Rigid = Rigid,
        FrontBack = FrontBackPaint,
        ProtectedJoints = _locked,
        LimitTo = Selection,
    };

    /// <summary>Switches the painted mesh; a stroke in progress is committed and the selection reset.</summary>
    public void SelectMesh(Mesh mesh)
    {
        if (ReferenceEquals(mesh, Mesh)) return;
        EndStroke();
        Mesh = mesh;
        Selection = new SkinSelection(mesh);
        CurrentVertex = -1;
    }

    /// <summary>Starts a stroke on the painted joint; false when no joint is painted.</summary>
    public bool BeginStroke()
    {
        EndStroke();
        if (JointObject == null) return false;
        _stroke = SkinWorkshop.BeginStroke(Mesh, Joint, Rigid, Character);
        LastPainted = 0;
        return true;
    }

    /// <summary>
    /// One dab at a world point. <paramref name="positions"/> are the mesh's control points in the
    /// pose being painted; <paramref name="jointPositions"/> are the joints' world positions, for
    /// the writer's nearest-joint fallback; <paramref name="normals"/> are the world control-point
    /// normals the front/back test needs.
    /// </summary>
    public int Dab(Vector3 worldPoint, IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3>? jointPositions = null, IReadOnlyList<Vector3>? normals = null)
    {
        if (_stroke == null && !BeginStroke()) return 0;
        LastPainted = _stroke!.Apply(Brush, worldPoint, positions, jointPositions, normals, Eye);
        if (NearestVisible(worldPoint, positions, normals, Radius, FrontBackPaint) is { } nearest)
            CurrentVertex = nearest;
        return LastPainted;
    }

    /// <summary>Ends the stroke and writes its result into the document.</summary>
    public void EndStroke()
    {
        _stroke?.Commit();
        _stroke = null;
    }

    /// <summary>
    /// The Select brush: paints the control-point selection around a world point without touching
    /// the weights. Hidden points are skipped, and
    /// so are back-facing ones when <see cref="FrontBackSelect"/> is off. Returns how many control
    /// points changed state.
    /// </summary>
    public int Select(Vector3 worldPoint, IReadOnlyList<Vector3> positions, bool add = true,
        IReadOnlyList<Vector3>? normals = null)
    {
        int changed = 0;
        bool hidden = Mesh.HasHiddenVertices;
        for (int v = 0; v < positions.Count && v < Selection.Count; v++)
        {
            if (hidden && Mesh.IsVertexHidden(v)) continue;
            if (Vector3.Distance(positions[v], worldPoint) > Radius) continue;
            if (!FrontBackSelect && normals != null && v < normals.Count &&
                !SkinPaintStroke.FrontFacing(positions[v], normals[v], Eye)) continue;
            if (Selection.IsSelected(v) == add) continue;
            Selection.Set(v, add);
            changed++;
        }
        if (NearestVisible(worldPoint, positions, normals, Radius, FrontBackSelect) is { } nearest)
            CurrentVertex = nearest;
        return changed;
    }

    /// <summary>
    /// The Scanner (<c>Scanner</c>): the control point under the brush and the weights it carries,
    /// The readout the app draws next to the cursor. Returns null when no
    /// visible front-facing point is inside the radius.
    /// </summary>
    public ScanHit? Scan(Vector3 worldPoint, IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3>? normals = null,
        float? radius = null)
    {
        float reach = radius ?? Radius;
        int vertex = NearestVisible(worldPoint, positions, normals, reach, FrontBackSelect);
        if (vertex < 0) return null;
        return Inspect(vertex) is { } hit
            ? hit with { Distance = Vector3.Distance(positions[vertex], worldPoint) }
            : null;
    }

    /// <summary>The Scanner's readout for one control point: its weights by joint, sorted by weight.</summary>
    public ScanHit? Inspect(int vertex)
    {
        if (vertex < 0 || vertex >= Mesh.ControlPoints.Length || Mesh.IsVertexHidden(vertex)) return null;
        CurrentVertex = vertex;
        var entries = new List<(int Joint, string Name, float Weight)>();
        foreach (var (bone, weight) in SkinWorkshop.Weights(Mesh, vertex, Rigid))
        {
            if (bone < 0 || bone >= Character.Joints.Count || weight < 1e-5f) continue;
            entries.Add((bone, Character.Joints[bone].Name ?? "", weight));
        }
        entries.Sort((a, b) => b.Weight.CompareTo(a.Weight));
        return new ScanHit(vertex, 0f, entries);
    }

    /// <summary>One Scanner result: the control point, its distance and its weights by joint.</summary>
    public readonly record struct ScanHit(int Vertex, float Distance,
        IReadOnlyList<(int Joint, string Name, float Weight)> Weights);

    /// <summary>
    /// Set Weight (or Spinner Weight when <paramref name="delta"/>): writes the typed value to every
    /// selected control point through the weight writer, in one document write. Hidden and locked
    /// points/joints are left alone.
    /// </summary>
    public int SetWeight(float weight, IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3>? jointPositions = null, bool delta = false)
    {
        if (JointObject == null) return 0;
        if (IsLocked(Joint)) return 0;
        var table = new SkinWeightTable(Mesh, Rigid, Character);
        int written = 0;
        bool hidden = Mesh.HasHiddenVertices;
        foreach (int vertex in Selection.SelectedVertices())
        {
            if (vertex < 0 || vertex >= table.VertexCount) continue;
            if (hidden && Mesh.IsVertexHidden(vertex)) continue;
            float target = delta
                ? Math.Clamp(table.Weight(vertex, Joint) + weight, 0f, 1f)
                : Math.Clamp(weight, 0f, 1f);
            table.SetWeight(vertex, Joint, target, _locked, jointPositions,
                vertex < positions.Count ? positions[vertex] : null);
            written++;
        }
        if (written > 0) table.Write();
        return written;
    }

    /// <summary>
    /// Writes one weight to the selection for one joint (the weight list's
    /// per-joint field); <paramref name="delta"/> is the Spinner's form. Returns how many
    /// control points were written.
    /// </summary>
    public int SetJointWeight(int joint, float weight, IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3>? jointPositions = null, bool delta = false)
    {
        if (joint < 0 || joint >= Character.Joints.Count) return 0;
        if (IsLocked(joint)) return 0;
        var table = new SkinWeightTable(Mesh, Rigid, Character);
        int written = 0;
        bool hidden = Mesh.HasHiddenVertices;
        foreach (int vertex in Selection.SelectedVertices())
        {
            if (vertex < 0 || vertex >= table.VertexCount) continue;
            if (hidden && Mesh.IsVertexHidden(vertex)) continue;
            float target = delta
                ? Math.Clamp(table.Weight(vertex, joint) + weight, 0f, 1f)
                : Math.Clamp(weight, 0f, 1f);
            table.SetWeight(vertex, joint, target, _locked, jointPositions,
                vertex < positions.Count ? positions[vertex] : null);
            written++;
        }
        if (written > 0) table.Write();
        return written;
    }

    // ---- Skinned Mesh Vertices Selection ----

    /// <summary>Whether a control point can be taken by a gesture: not hidden, and facing the eye unless <see cref="FrontBackSelect"/> (Full Paint &amp; Vertex Select Mode) is on.</summary>
    private bool Selectable(int v, IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3>? normals)
    {
        if (Mesh.HasHiddenVertices && Mesh.IsVertexHidden(v)) return false;
        if (!FrontBackSelect && normals != null && v < normals.Count && v < positions.Count &&
            !SkinPaintStroke.FrontFacing(positions[v], normals[v], Eye)) return false;
        return true;
    }

    /// <summary>
    /// The rectangle or lasso: every selectable control point whose projection (<paramref name="project"/>,
    /// null when off-screen or behind the eye) lies inside <paramref name="inside"/> is replaced, added
    /// or removed by <paramref name="op"/>. Returns how many control points changed state.
    /// </summary>
    public int SelectRegion(IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3>? normals,
        Func<Vector3, Vector2?> project, Func<Vector2, bool> inside, VertexSelectOp op)
    {
        var hits = new List<int>();
        for (int v = 0; v < positions.Count && v < Selection.Count; v++)
        {
            if (!Selectable(v, positions, normals)) continue;
            if (project(positions[v]) is { } screen && inside(screen)) hits.Add(v);
        }
        int changed = Apply(hits, op);
        if (hits.Count > 0 && op != VertexSelectOp.Remove) CurrentVertex = hits[^1];
        return changed;
    }

    /// <summary>
    /// The pick: the selectable control point projected nearest to <paramref name="at"/> within
    /// <paramref name="tolerance"/> pixels. Replace with nothing under the pointer clears the selection.
    /// Returns the picked control point, or -1.
    /// </summary>
    public int PickVertex(IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3>? normals,
        Func<Vector3, Vector2?> project, Vector2 at, VertexSelectOp op, float tolerance = VertexSelectRegion.PickTolerance)
    {
        int best = -1;
        float bestDistance = tolerance * tolerance;
        for (int v = 0; v < positions.Count && v < Selection.Count; v++)
        {
            if (!Selectable(v, positions, normals)) continue;
            if (project(positions[v]) is not { } screen) continue;
            float d = Vector2.DistanceSquared(screen, at);
            if (d > bestDistance) continue;
            bestDistance = d;
            best = v;
        }
        Apply(best < 0 ? [] : [best], op);
        if (best >= 0 && op != VertexSelectOp.Remove) CurrentVertex = best;
        return best;
    }

    /// <summary>Applies one gesture's hits, then unlocks the joints that weight the new selection.</summary>
    private int Apply(IReadOnlyList<int> hits, VertexSelectOp op)
    {
        int changed = 0;
        if (op == VertexSelectOp.Replace)
        {
            var keep = new HashSet<int>(hits);
            foreach (int v in Selection.SelectedVertices().ToList())
                if (!keep.Contains(v)) { Selection.Set(v, false); changed++; }
        }
        bool add = op != VertexSelectOp.Remove;
        foreach (int v in hits)
        {
            if (Selection.IsSelected(v) == add) continue;
            Selection.Set(v, add);
            changed++;
        }
        if (Selection.SelectedCount == 0 || !Selection.IsSelected(CurrentVertex)) CurrentVertex = Selection.SelectedVertices().LastOrDefault(-1);
        if (add) UnlockSelectionInfluences();
        return changed;
    }

    /// <summary>
    /// "Selecting vertices automatically unlocks their influences": clears the lock of every joint
    /// that carries weight on a selected control point. Returns how many locks were cleared.
    /// </summary>
    public int UnlockSelectionInfluences()
    {
        if (_locked.Count == 0) return 0;
        var joints = new HashSet<int>();
        foreach (int v in Selection.SelectedVertices())
            foreach (var (bone, weight) in SkinWorkshop.Weights(Mesh, v, Rigid))
                if (weight > 1e-5f) joints.Add(bone);
        return SetLock(joints, false);
    }

    /// <summary>Numpad +: grows the selection by one ring of visible neighbours. Returns how many were added.</summary>
    public int GrowSelection()
    {
        var neighbours = SkinWorkshop.Neighbours(Mesh);
        bool hidden = Mesh.HasHiddenVertices;
        var ring = new HashSet<int>();
        foreach (int v in Selection.SelectedVertices())
            foreach (int q in neighbours[v])
                if (!Selection.IsSelected(q) && !(hidden && Mesh.IsVertexHidden(q))) ring.Add(q);
        foreach (int q in ring) Selection.Set(q, true);
        if (ring.Count > 0) UnlockSelectionInfluences();
        return ring.Count;
    }

    /// <summary>Numpad -: drops the selected control points on the selection's border (those with an unselected neighbour). Returns how many were removed.</summary>
    public int ShrinkSelection()
    {
        var neighbours = SkinWorkshop.Neighbours(Mesh);
        var border = Selection.SelectedVertices()
            .Where(v => neighbours[v].Any(q => !Selection.IsSelected(q))).ToList();
        foreach (int v in border) Selection.Set(v, false);
        if (!Selection.IsSelected(CurrentVertex)) CurrentVertex = Selection.SelectedVertices().LastOrDefault(-1);
        return border.Count;
    }

    /// <summary>
    /// Numpad * (<c>FloodVerticesSelection</c>): floods the selection over its connected
    /// component(s); with nothing selected it selects every visible control point. Returns the new selection size.
    /// </summary>
    public int SelectAllVertices()
    {
        if (Selection.SelectedCount > 0) Flood();
        else
            for (int v = 0; v < Selection.Count; v++)
                if (!Mesh.IsVertexHidden(v)) Selection.Set(v, true);
        UnlockSelectionInfluences();
        return Selection.SelectedCount;
    }

    /// <summary>Numpad / (<c>InvertVerticesSelection</c>): selects the visible unselected control points and drops the selected ones. Returns the new selection size.</summary>
    public int InvertSelection()
    {
        for (int v = 0; v < Selection.Count; v++)
        {
            bool hidden = Mesh.IsVertexHidden(v);
            Selection.Set(v, !hidden && !Selection.IsSelected(v));
        }
        if (!Selection.IsSelected(CurrentVertex)) CurrentVertex = Selection.SelectedVertices().LastOrDefault(-1);
        UnlockSelectionInfluences();
        return Selection.SelectedCount;
    }

    /// <summary>Prune Weights over the whole mesh; returns the influences removed.</summary>
    public int Prune() => SkinWorkshop.Prune(Mesh, Rigid, PruneLimit);

    /// <summary>Fix Weights over the selected control points.</summary>
    public int Fix(int passes = 2) => SkinWorkshop.Fix(Mesh, Selection, Rigid, passes);

    /// <summary>The Flooder: grows the selection to its connected component of visible points.</summary>
    public int Flood() => SkinWorkshop.Flood(Mesh, Selection);

    /// <summary>Hides the selected control points and stamps the character's counter.</summary>
    public int HideSelection()
    {
        int changed = Mesh.SetVerticesHidden(Selection.SelectedVertices(), true);
        if (changed > 0) Character.HiddenVerticesCounter = Character.HiddenVerticesCounter + 1;
        return changed;
    }

    /// <summary>Shows every hidden control point of the painted mesh and stamps the counter.</summary>
    public int ShowAllVertices()
    {
        int changed = Mesh.ShowAllVertices();
        if (changed > 0) Character.HiddenVerticesCounter = Character.HiddenVerticesCounter + 1;
        return changed;
    }

    /// <summary>Mirror Skin of the painted joint onto the character's mirrored control points.</summary>
    public int Mirror() => JointObject is { } joint ? SkinMirror.Mirror(Character, [joint], [Mesh]) : 0;

    /// <summary>Copies the selected control points into a clipboard (null when refused).</summary>
    public SkinWeightClipboard? Copy(out string? refusal) =>
        SkinWeightClipboard.Copy(new SkinWeightTable(Mesh, Rigid, Character), Selection, Character, out refusal);

    /// <summary>Pastes a clipboard onto the selected control points, reporting the slough substitutions.</summary>
    public SkinSloughResult Paste(SkinWeightClipboard clipboard, out string? refusal) =>
        clipboard.Paste(new SkinWeightTable(Mesh, Rigid, Character), Selection, Character, out refusal);

    /// <summary>The per-joint weight list of the painted mesh, with the lock, the two share columns and the per-joint weight column.</summary>
    public IReadOnlyList<SkinWorkshop.Row> WeightList()
    {
        int vertex = CurrentVertex;
        if (vertex < 0) vertex = Selection.SelectedVertices().FirstOrDefault(-1);
        return SkinWorkshop.WeightList(Character, Mesh, Rigid, vertex, _locked, Selection);
    }

    /// <summary>
    /// The nearest visible control point within <paramref name="radius"/> of a world point, skipping
    /// back-facing points when <paramref name="frontBack"/> is false. -1 when none.
    /// </summary>
    private int NearestVisible(Vector3 point, IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3>? normals,
        float radius, bool frontBack)
    {
        int best = -1;
        float bestDistance = radius;
        bool hidden = Mesh.HasHiddenVertices;
        for (int v = 0; v < positions.Count && v < Selection.Count; v++)
        {
            if (hidden && Mesh.IsVertexHidden(v)) continue;
            float distance = Vector3.Distance(positions[v], point);
            if (distance > bestDistance) continue;
            if (!frontBack && normals != null && v < normals.Count &&
                !SkinPaintStroke.FrontFacing(positions[v], normals[v], Eye)) continue;
            bestDistance = distance;
            best = v;
        }
        return best;
    }
}

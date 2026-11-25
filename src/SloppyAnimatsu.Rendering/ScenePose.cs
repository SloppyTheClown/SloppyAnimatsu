using System.Numerics;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Rendering;

/// <summary>A ghost's posed triangles (see <see cref="ScenePose.GhostVertices"/>) and its material ranges.</summary>
public sealed record GhostGeometry(float[] Vertices, IReadOnlyList<MeshRange> Ranges)
{
    public static readonly GhostGeometry Empty = new([], []);
}

/// <summary>
/// A run of triangles drawn with one material slot. <see cref="Texture"/> is the texture shown at the
/// posed time.
/// </summary>
public readonly record struct MeshRange(int First, int Count, Texture? Texture, Material? Material = null)
{
    /// <summary>Whether the texture's alpha is drawn (a material without the setting keeps it).</summary>
    public bool Transparent => Material?.Transparent ?? true;

    /// <summary>Nearest-texel sampling for pixel-art textures.</summary>
    public bool Nearest => Material?.NearestFilter ?? false;
}

/// <summary>
/// One mesh's triangles in <see cref="ScenePose.MeshVertices"/>: where they start, how many vertices
/// (three per triangle) and whether the last <see cref="ScenePose.Update"/> drew them. The renderer
/// and the picker both work from these runs. <see cref="SourceTriangles"/> maps each drawn triangle
/// back to its index in <see cref="Mesh.Triangles"/>'s order, which material grouping reorders
/// (null when the part does not track it).
/// </summary>
public readonly record struct MeshPartRange(
    Mesh Mesh, int First, int Count, bool Visible, IReadOnlyList<int>? SourceTriangles = null);

/// <summary>
/// An animated camera's looker target as the app draws it: a view-facing disc of
/// radius <see cref="Radius"/> at <see cref="Position"/>
/// </summary>
/// <summary>
/// Skeleton decorators that share one size: <see cref="Vertices"/> (<see cref="ScenePose.LineVertexFloats"/>
/// floats per vertex) drawn at <see cref="Scale"/> times the standard joint dot or bone width.
/// </summary>
public sealed record DecoratorBatch(float Scale, float[] Vertices);

public readonly record struct LookerMarker(Object3D Target, Vector3 Position, float Radius);

/// <summary>
/// The drawable state of a scene at one animation time, without any GL: skinned triangle vertices
/// for every mesh and the skeleton of the chosen character as lines and points.
/// </summary>
public sealed class ScenePose
{
    /// <summary>
    /// Floats per mesh vertex: position, normal, colour, texture coordinate. The colour is the
    /// material's flat diffuse colour, drawn where there is no texture. The texture coordinate is
    /// the stored UV, whose v runs down from the image's top row.
    /// </summary>
    public const int MeshVertexFloats = 11;

    /// <summary>Floats per skeleton vertex: position, colour.</summary>
    public const int LineVertexFloats = 6;

    // The user's default skeleton green, #07C76B.
    private static readonly Vector3 BoneColor = new(7 / 255f, 199 / 255f, 107 / 255f);
    private static readonly Vector3 JointColor = BoneColor;
    private static readonly Vector3 ControllerColor = new(0.3f, 0.8f, 1f);

    private readonly List<MeshPart> _parts = [];
    private readonly Dictionary<Character, PoseEvaluator> _evaluators = new(ReferenceEqualityComparer.Instance);
    private readonly List<MeshRange> _visibleRanges = [];
    private readonly IReadOnlyDictionary<Object3D, Character> _controllers;

    public ScenePose(SceneModel model)
    {
        Model = model;
        Characters = model.Scene.Characters.ToArray();
        var controllers = new Dictionary<Object3D, Character>(ReferenceEqualityComparer.Instance);
        foreach (var character in Characters)
            foreach (var controller in character.Controllers)
                controllers.TryAdd(controller, character);

        _controllers = controllers;
        int first = 0;
        foreach (var mesh in model.Scene.Meshes)
        {
            var part = new MeshPart(mesh, first, controllers);
            if (part.VertexCount == 0) continue;
            _parts.Add(part);
            first += part.VertexCount;
        }
        MeshVertices = new float[first * MeshVertexFloats];
        foreach (var character in Characters) _placements[character] = character.LocalMatrix;
    }

    private readonly Dictionary<Character, Matrix4x4> _placements = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Re-reads the mesh worlds and bind worlds that depend on a character whose own transform
    /// (its placement in the scene, <see cref="Object3D.LocalMatrix"/>) changed since the last
    /// call: a placement drag, or its undo. Characters whose placement is unchanged cost a compare.
    /// </summary>
    public void RefreshPlacements()
    {
        foreach (var character in Characters)
        {
            var now = character.LocalMatrix;
            if (_placements.TryGetValue(character, out var was) && was == now) continue;
            _placements[character] = now;
            foreach (var part in _parts)
                if (ReferenceEquals(part.Skin.Character, character) || IsUnder(part.Mesh, character))
                    part.Skin.ReloadPlacement();
        }
        // A mesh, or a group above it, placed on its own (Rigging context) moves the mesh world too.
        foreach (var part in _parts)
            if (part.Mesh.WorldMatrix != part.Skin.MeshWorld)
                part.Skin.ReloadPlacement();

        static bool IsUnder(Object3D o, Character character)
        {
            for (var p = o.Parent3D; p != null; p = p.Parent3D)
                if (ReferenceEquals(p, character)) return true;
            return false;
        }
    }

    public SceneModel Model { get; }

    public IReadOnlyList<Character> Characters { get; }

    /// <summary>Triangle list, <see cref="MeshVertexFloats"/> floats per vertex, world space.</summary>
    public float[] MeshVertices { get; }

    /// <summary>Vertex ranges of <see cref="MeshVertices"/> to draw (meshes of hidden characters are left out).</summary>
    public IReadOnlyList<MeshRange> VisibleRanges => _visibleRanges;

    /// <summary>
    /// Every mesh's triangles in <see cref="MeshVertices"/>, with the visibility of the last
    /// <see cref="Update"/>: the picker tests only the visible ones.
    /// </summary>
    public IReadOnlyList<MeshPartRange> MeshParts => _meshParts;

    private readonly List<MeshPartRange> _meshParts = [];

    /// <summary>Bone lines (parent joint to joint), <see cref="LineVertexFloats"/> floats per vertex.</summary>
    public float[] SkeletonLines { get; private set; } = [];

    /// <summary>One point per controller of the chosen character, <see cref="LineVertexFloats"/> floats per vertex.</summary>
    public float[] SkeletonPoints { get; private set; } = [];

    /// <summary>
    /// IK rings and reverse-foot sole outlines of the chosen character as a triangle list,
    /// <see cref="LineVertexFloats"/> floats per vertex.
    /// </summary>
    public float[] ControllerTriangles { get; private set; } = [];

    /// <summary>
    /// Whether the reverse-foot shoe locators (<c>CReverseFootLocator_VK</c>) are shown: only in
    /// the Rigging context with the bind pose current (the viewer sets it before
    /// <see cref="Update"/>). Hidden, they get no point and no marker.
    /// </summary>
    public bool ShowShoeLocators { get; set; }

    /// <summary>
    /// The joint whose skin weights tint the meshes skinned to its character (View &amp;
    /// Preferences "Display skinning"); null draws the material colours. Set before
    /// <see cref="Update"/>.
    /// </summary>
    public Object3D? SkinDisplayJoint { get; set; }

    /// <summary>
    /// How strongly the skin-weight colours replace the material colour (Decorators "Skinning
    /// decorator alpha"): 0 keeps the material, 1 shows the weights alone. Set before <see cref="Update"/>.
    /// </summary>
    public float SkinTintAlpha { get; set; } = 1f;

    /// <summary>
    /// Light joint display: a joint whose nearest shown neighbour is close (fingers, a face) gets a
    /// smaller dot, sized by the scene's high density factor, so dense areas do not clump into one
    /// blob. False (Classic) draws every joint at the standard size. Set before <see cref="Update"/>.
    /// </summary>
    public bool AdaptiveJointSizes { get; set; } = true;

    /// <summary>
    /// The joint dots of the last <see cref="Update"/> grouped by size: each batch's
    /// <see cref="DecoratorBatch.Scale"/> multiplies the standard dot (<see cref="DecoratorSizes.Joint"/>).
    /// Together they hold the same points as <see cref="SkeletonPoints"/>.
    /// </summary>
    public IReadOnlyList<DecoratorBatch> JointBatches { get; private set; } = [];

    /// <summary>The bones of the last <see cref="Update"/> grouped by width scale, as <see cref="JointBatches"/>.</summary>
    public IReadOnlyList<DecoratorBatch> BoneBatches { get; private set; } = [];

    /// <summary>The dot scale <paramref name="o"/> was drawn with in the last <see cref="Update"/> (1 when it has no dot).</summary>
    public float DotScaleOf(Object3D? o) => o != null && _dotScales.TryGetValue(o, out var s) ? s : 1f;

    private readonly Dictionary<Object3D, float> _dotScales = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The size a joint dot keeps per unit of distance to its nearest neighbour and of the high
    /// density factor: at the default factor 6 a dense joint's dot is 60% of the gap, so the dots
    /// of neighbouring joints never touch.
    /// </summary>
    public const float HighDensityDotPerGap = 0.1f;

    /// <summary>
    /// Joints closer than this fraction of the standard dot are treated as one place (a twist
    /// joint on its parent, an end joint on its tip) and do not shrink each other.
    /// </summary>
    public const float CoincidentFraction = 0.05f;

    /// <summary>The smallest dot scale; the shader's 2 px minimum keeps even these visible.</summary>
    public const float MinDotScale = 1f / 32f;

    /// <summary>
    /// The pivot triad of <paramref name="o"/> as posed, each axis <paramref name="halfLength"/> long,
    /// appended to <paramref name="into"/> as a line list of <see cref="LineVertexFloats"/> floats per vertex.
    /// </summary>
    public void WritePivot(List<float> into, Object3D o, float halfLength) =>
        AddPivot(into, WorldOf(o), halfLength);

    private static void AddPivot(List<float> into, Matrix4x4 world, float half)
    {
        var position = world.Translation;
        var frame = ControllerShapes.Unscaled(world);
        Add(into, position, PivotX); Add(into, position + Vector3.TransformNormal(Vector3.UnitX, frame) * half, PivotX);
        Add(into, position, PivotY); Add(into, position + Vector3.TransformNormal(Vector3.UnitY, frame) * half, PivotY);
        Add(into, position, PivotZ); Add(into, position + Vector3.TransformNormal(Vector3.UnitZ, frame) * half, PivotZ);
    }

    /// <summary>
    /// A small red/green/blue axis triad at each shown controller of the chosen character (View
    /// &amp; Preferences "Display pivots"), as a line list of <see cref="LineVertexFloats"/>
    /// floats per vertex.
    /// </summary>
    public float[] PivotLines { get; private set; } = [];

    /// <summary>
    /// Yellow plus signs for the shown shoe locators, three axis lines each in the locator's
    /// unscaled frame, half-length <c>TargetRenderSize·0.25</c> (controller-display.md "Shoe
    /// locators"), as a line list of <see cref="LineVertexFloats"/> floats per vertex.
    /// </summary>
    public float[] ShoeMarkers { get; private set; } = [];

    /// <summary>The shoe-marker colour (SloppyAnimatsu's choice).</summary>
    public static readonly Vector3 ShoeMarkerColor = new(1f, 0.85f, 0.1f);

    private static readonly Vector3 PivotX = new(0.95f, 0.25f, 0.25f), PivotY = new(0.3f, 0.9f, 0.3f), PivotZ = new(0.3f, 0.5f, 1f);

    /// <summary>
    /// The colour of a skin weight in "Display skinning" (SloppyAnimatsu's own ramp):
    /// dark blue for none, through green and yellow, to red for a full weight.
    /// </summary>
    public static Vector3 WeightColor(float w)
    {
        w = Math.Clamp(w, 0f, 1f);
        if (w <= 0f) return new Vector3(0.12f, 0.14f, 0.3f);
        Vector3 a, b; float t;
        if (w < 0.5f) { a = new(0.1f, 0.3f, 0.9f); b = new(0.2f, 0.9f, 0.3f); t = w / 0.5f; }
        else if (w < 0.75f) { a = new(0.2f, 0.9f, 0.3f); b = new(1f, 0.9f, 0.1f); t = (w - 0.5f) / 0.25f; }
        else { a = new(1f, 0.9f, 0.1f); b = new(1f, 0.15f, 0.1f); t = (w - 0.75f) / 0.25f; }
        return Vector3.Lerp(a, b, t);
    }

    /// <summary>
    /// The shown <c>CCameraTarget</c> rows of the chosen character, as the last
    /// <see cref="Update"/> posed them: the app draws one disc each.
    /// </summary>
    public IReadOnlyList<LookerMarker> Lookers { get; private set; } = [];

    /// <summary>The shown animated cameras of the chosen character as wireframes with target arrows.</summary>
    public IReadOnlyList<CameraGlyph> Cameras { get; private set; } = [];

    /// <summary>Whether a camera rig's target is locked now (drawn blue); set by the viewer.</summary>
    public Func<CameraTarget, bool>? TargetLocked { get; set; }

    /// <summary>Bounds of what <see cref="Update"/> made visible.</summary>
    public (Vector3 Min, Vector3 Max) Bounds { get; private set; }

    /// <summary>The character <see cref="Update"/> posed, or null.</summary>
    public Character? Character { get; private set; }

    /// <summary>The animation <see cref="Character"/> plays in the last <see cref="Update"/>, or null.</summary>
    public Animation? Animation { get; private set; }

    /// <summary>The frame <see cref="Animation"/> was posed at (0 without one).</summary>
    public float Time { get; private set; }

    /// <summary>
    /// World matrices of <see cref="Character"/>'s controllers, in the order of its evaluator's
    /// <see cref="PoseEvaluator.Controllers"/>.
    /// </summary>
    public Matrix4x4[] ControllerWorlds { get; private set; } = [];

    /// <summary>
    /// World matrix of <paramref name="o"/> as posed: evaluated for a controller of
    /// <see cref="Character"/>, the stored world matrix otherwise; identity for null.
    /// </summary>
    public Matrix4x4 WorldOf(Object3D? o)
    {
        if (o == null) return Matrix4x4.Identity;
        if (Character != null)
        {
            int i = Evaluator(Character).IndexOf(o);
            if (i >= 0 && i < ControllerWorlds.Length) return ControllerWorlds[i];
        }
        return o.WorldMatrix;
    }

    /// <summary>
    /// The world-space corners of triangle <paramref name="triangle"/> of <paramref name="mesh"/>
    /// (an index in <see cref="Mesh.Triangles"/>'s order) as the last <see cref="Update"/> drew them;
    /// false when the mesh is not drawn or the triangle was left out. A blendshape zone is drawn
    /// from these.
    /// </summary>
    public bool TryTriangle(Mesh mesh, int triangle, out Vector3 a, out Vector3 b, out Vector3 c)
    {
        foreach (var part in _parts)
            if (ReferenceEquals(part.Mesh, mesh))
                return part.TryTriangle(triangle, MeshVertices, out a, out b, out c);
        a = b = c = Vector3.Zero;
        return false;
    }

    /// <summary>
    /// The meshes <paramref name="character"/> moves, posed by <paramref name="animation"/> at
    /// <paramref name="time"/> (frames), as one packed triangle list (<see cref="MeshVertexFloats"/>
    /// floats per vertex, world space) for a ghost, with one range per material slot (offsets into
    /// that list, carrying the texture the slot shows at that time). Only the meshes shown in the
    /// scene are written. Leaves the pose's own buffers (<see cref="MeshVertices"/>,
    /// <see cref="ControllerWorlds"/>) alone.
    /// </summary>
    public GhostGeometry GhostVertices(Character character, Animation? animation, float time)
    {
        if (!character.IsShownInScene) return GhostGeometry.Empty;
        int count = 0;
        foreach (var part in _parts)
            if (part.Owner == character && part.Mesh.IsShownInScene) count += part.VertexCount;
        if (count == 0) return GhostGeometry.Empty;
        var vertices = new float[count * MeshVertexFloats];
        var ranges = new List<MeshRange>();
        var evaluator = Evaluator(character);
        float atTime = animation != null ? time : 0;
        var worlds = evaluator.DisplayWorldMatrices(animation, atTime);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        int at = 0;
        foreach (var part in _parts)
        {
            if (part.Owner != character || !part.Mesh.IsShownInScene) continue;
            Matrix4x4? rigidWorld = part.Anchor != null ? part.RigidWorld(worlds, evaluator, animation, time) : null;
            part.Write(vertices, worlds, rigidWorld, ref min, ref max, at: at);
            foreach (var range in part.Ranges)
            {
                var rebased = range with { First = range.First - part.First + at };
                ranges.Add(range.Material is { } m ? rebased with { Texture = m.TextureAt(animation, atTime) } : rebased);
            }
            at += part.VertexCount;
        }
        return new GhostGeometry(vertices, ranges);
    }

    /// <summary>Incremented by every <see cref="Update"/>, so a renderer knows when to upload.</summary>
    public int Version { get; private set; }

    public PoseEvaluator Evaluator(Character character)
    {
        if (!_evaluators.TryGetValue(character, out var evaluator))
            _evaluators[character] = evaluator = new PoseEvaluator(character, riggingContext: RiggingContext);
        return evaluator;
    }

    /// <summary>
    /// Whether characters stand in the rigging context's pose (<see cref="PoseEvaluator.RestPose"/>)
    /// instead of their reference pose.
    /// </summary>
    public bool RiggingContext
    {
        get => _riggingContext;
        set
        {
            _riggingContext = value;
            foreach (var evaluator in _evaluators.Values) evaluator.RiggingContext = value;
        }
    }
    private bool _riggingContext;

    /// <summary>
    /// Re-reads <paramref name="character"/>'s rest pose and the bind matrices of the meshes skinned
    /// to it, after one of its poses was edited.
    /// </summary>
    public void ReloadRestPose(Character character)
    {
        Evaluator(character).ReloadRestPose();
        foreach (var part in _parts)
            if (ReferenceEquals(part.Skin.Character, character)) part.Skin.ReloadBindPose();
    }

    /// <summary>
    /// Re-reads every mesh's skin weights after a bind, an unbind or a weight edit changed them
    /// </summary>
    public void ReloadSkins()
    {
        foreach (var part in _parts) part.ReloadSkin();
    }

    /// <summary>
    /// Re-reads the geometry (control points, normals) of every mesh after an edit rewrote it in
    /// place, such as Reset &amp; Freeze; the vertex layout is kept, so a part whose drawn vertex
    /// count changed keeps its old data.
    /// </summary>
    public void ReloadGeometry()
    {
        for (int i = 0; i < _parts.Count; i++)
        {
            var old = _parts[i];
            var part = new MeshPart(old.Mesh, old.First, _controllers);
            if (part.VertexCount == old.VertexCount) _parts[i] = part;
            else old.ReloadSkin();
        }
    }

    /// <summary>
    /// Poses the scene: <paramref name="character"/> plays <paramref name="animation"/> at
    /// <paramref name="time"/> (frames); other characters stay in their rest pose, or are hidden.
    /// </summary>
    /// <param name="otherPlayback">The animation and time each other character shows (its own
    /// remembered playback); null or a null animation is the rest pose.</param>
    public void Update(Character? character, Animation? animation, float time, bool showOtherCharacters = true,
        Func<Character, (Animation? Animation, float Time)>? otherPlayback = null)
    {
        var worlds = new Dictionary<Character, Matrix4x4[]>(ReferenceEqualityComparer.Instance);
        (Animation? Animation, float Time) PlaybackOf(Character c) =>
            c == character ? (animation, time) : otherPlayback?.Invoke(c) ?? (null, 0f);
        Matrix4x4[] WorldsOf(Character c)
        {
            if (!worlds.TryGetValue(c, out var w))
            {
                // The display worlds: IK/RF controllers follow the leaf joint through the other
                // stacks' motion (PoseEvaluator.DisplayWorldMatrices).
                var (a, t) = PlaybackOf(c);
                worlds[c] = w = Evaluator(c).DisplayWorldMatrices(a, a != null ? t : 0);
            }
            return w;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        _visibleRanges.Clear();
        _meshParts.Clear();
        foreach (var part in _parts)
        {
            var owner = part.Owner;
            // Show/Hide: a hidden mesh is not drawn, and
            // neither are the meshes skinned to a hidden character.
            bool visible = !(!showOtherCharacters && owner != null && owner != character)
                           && part.Mesh.IsShownInScene && (owner == null || owner.IsShownInScene);
            _meshParts.Add(new MeshPartRange(part.Mesh, part.First, part.VertexCount, visible, part.SourceTriangles));
            if (!visible) continue;
            var ownerWorlds = owner != null ? WorldsOf(owner) : [];
            var (ownerAnimation, ownerTime) = owner != null ? PlaybackOf(owner) : (null, 0f);
            if (ownerAnimation == null) ownerTime = 0;
            Matrix4x4? rigidWorld = owner != null && part.Anchor != null
                ? part.RigidWorld(ownerWorlds, Evaluator(owner), ownerAnimation, ownerTime)
                : null;
            float[]? tint = null;
            if (SkinDisplayJoint != null && owner != null && ReferenceEquals(part.Skin.Character, owner))
            {
                var ownerJoints = owner.Joints;
                for (int j = 0; j < ownerJoints.Count; j++)
                    if (ReferenceEquals(ownerJoints[j], SkinDisplayJoint)) { tint = part.JointWeights(j); break; }
            }
            part.Write(MeshVertices, ownerWorlds, rigidWorld, ref min, ref max, tint, Math.Clamp(SkinTintAlpha, 0f, 1f));
            // The texture each slot shows now: a texture-swap key of the playing animation, else
            // the material's rest texture.
            var keyed = ownerAnimation;
            foreach (var range in part.Ranges)
                _visibleRanges.Add(range.Material is { } m ? range with { Texture = m.TextureAt(keyed, ownerTime) } : range);
        }

        // Played every frame: the build lists are kept and cleared rather than grown afresh (the
        // controller shapes alone are ~50k floats, a large-object-heap array each time).
        var lines = Cleared(_lines);
        var points = Cleared(_points);
        var shapes = Cleared(_shapes);
        var lookers = new List<LookerMarker>();
        var shoes = Cleared(_shoes);
        var pivots = Cleared(_pivots);
        _dotScales.Clear();
        var dots = new List<(Object3D Controller, Vector3 Position, bool Joint)>();
        var bones = new List<(Object3D Parent, Vector3 From, Object3D Child, Vector3 To)>();
        var pivotAt = new List<(Object3D Controller, int Index)>();
        IReadOnlyList<DecoratorBatch> jointBatchesOut = [], boneBatchesOut = [];
        if (character != null)
        {
            var evaluator = Evaluator(character);
            var world = WorldsOf(character);
            var joints = new HashSet<Object3D>(character.Joints, ReferenceEqualityComparer.Instance);
            float shoeHalf = character.Model.Scene.TargetRenderSize * 0.25f;
            for (int i = 0; i < evaluator.Controllers.Count && i < world.Length; i++)
            {
                var controller = evaluator.Controllers[i];
                if (!controller.IsShownInScene) continue;
                // The camera's hidden joint is the looker chain's effector, not something to show.
                if (Outliner.IsCameraRigJoint(controller)) continue;
                var position = world[i].Translation;
                // An animated camera and its target are drawn as the wireframe glyph (CameraShapes)
                // alone: no pivot axes, no dot.
                if (CameraShapes.IsGlyphPart(controller))
                {
                    min = Vector3.Min(min, position);
                    max = Vector3.Max(max, position);
                    continue;
                }
                pivotAt.Add((controller, i));
                if (controller is ReverseFootLocator locator)
                {
                    if (!ShowShoeLocators || !locator.IsSolePivot) continue;
                    var frame = ControllerShapes.Unscaled(world[i]);
                    foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                    {
                        var d = Vector3.TransformNormal(axis, frame) * shoeHalf;
                        Add(shoes, position - d, ShoeMarkerColor);
                        Add(shoes, position + d, ShoeMarkerColor);
                    }
                    min = Vector3.Min(min, position);
                    max = Vector3.Max(max, position);
                    continue;
                }
                bool isJoint = joints.Contains(controller);
                // A reverse foot is seen as its sole outline only, with no dot at its origin
                if (controller is not ControllerReverseFoot)
                    dots.Add((controller, position, isJoint));
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
                if (!isJoint) continue;

                var parent = controller.Parent3D;
                while (parent != null && !joints.Contains(parent)) parent = parent.Parent3D;
                if (parent != null && !parent.IsShownInScene) continue;
                int p = parent != null ? evaluator.IndexOf(parent) : -1;
                if (p < 0 || p >= world.Length) continue;
                bones.Add((parent!, world[p].Translation, controller, position));
            }

            // Sizes: the standard dot, shrunk in dense areas (Light joint display).
            var scene = character.Model.Scene;
            float standardDot = DecoratorSizes.For(scene).Joint;
            float denseFactor = scene.JointRenderSizeHighDensity;
            float coincident = standardDot * CoincidentFraction;
            for (int a = 0; a < dots.Count; a++)
            {
                float scale = 1f;
                if (AdaptiveJointSizes && dots[a].Joint && denseFactor > 0f)
                {
                    float nearest = float.MaxValue;
                    for (int b = 0; b < dots.Count; b++)
                    {
                        if (b == a || !dots[b].Joint) continue;
                        float d = Vector3.Distance(dots[a].Position, dots[b].Position);
                        if (d >= coincident && d < nearest) nearest = d;
                    }
                    if (nearest < float.MaxValue)
                        scale = Math.Min(1f, nearest * denseFactor * HighDensityDotPerGap / standardDot);
                }
                _dotScales[dots[a].Controller] = Quantized(scale);
            }
            var jointBatches = new SortedDictionary<float, List<float>>();
            foreach (var (controller, position, isJoint) in dots)
            {
                float scale = _dotScales[controller];
                Add(points, position, isJoint ? JointColor : ControllerColor);
                Add(Batch(jointBatches, scale), position, isJoint ? JointColor : ControllerColor);
            }
            var boneBatches = new SortedDictionary<float, List<float>>();
            foreach (var (parentJoint, from, child, to) in bones)
            {
                Add(lines, from, BoneColor);
                Add(lines, to, BoneColor);
                var batch = Batch(boneBatches, Math.Min(DotScaleOf(parentJoint), DotScaleOf(child)));
                Add(batch, from, BoneColor);
                Add(batch, to, BoneColor);
            }
            jointBatchesOut = jointBatches.Select(kv => new DecoratorBatch(kv.Key, kv.Value.ToArray())).ToArray();
            boneBatchesOut = boneBatches.Select(kv => new DecoratorBatch(kv.Key, kv.Value.ToArray())).ToArray();
            // Unselected pivots (Decorators "Pivot size factor (unselected joints)"), each in
            // proportion to its own joint's dot; the renderer draws the selection's larger ones.
            float pivotFactor = scene.UnselectedPivotRenderSize;
            foreach (var (controller, index) in pivotAt)
                AddPivot(pivots, world[index], 0.5f * standardDot * DotScaleOf(controller) * pivotFactor);
            ControllerShapes.Write(shapes, character, evaluator, world, animation, time);
            // The disc's radius is 0.5 * TargetRenderSize * ratio * 0.3.
            float lookerSize = character.Model.Scene.TargetRenderSize * 0.15f;
            for (int i = 0; i < evaluator.Controllers.Count && i < world.Length; i++)
            {
                if (evaluator.Controllers[i] is not CameraTarget target || !target.IsShownInScene || CameraShapes.IsGlyphPart(target)) continue;
                lookers.Add(new LookerMarker(target, world[i].Translation, lookerSize * target.TargetRenderSizeRatio));
            }
        }
        Character = character;
        Animation = character != null ? animation : null;
        Time = animation != null ? time : 0;
        ControllerWorlds = character != null ? WorldsOf(character) : [];
        Lookers = lookers;
        // Every character's animated cameras are drawn (a camera often lives in a character of its
        // own); picking them stays with the current character (ViewportPick).
        var cameras = new List<CameraGlyph>();
        foreach (var c in Characters)
            if (c == character || c.AnimatedCameras.Any())
                cameras.AddRange(CameraShapes.Glyphs(c, Evaluator(c), WorldsOf(c), TargetLocked));
        Cameras = cameras;
        SkeletonLines = Reuse(lines, SkeletonLines);
        SkeletonPoints = Reuse(points, SkeletonPoints);
        ControllerTriangles = Reuse(shapes, ControllerTriangles);
        ShoeMarkers = Reuse(shoes, ShoeMarkers);
        PivotLines = Reuse(pivots, PivotLines);
        JointBatches = jointBatchesOut;
        BoneBatches = boneBatchesOut;
        Bounds = min.X <= max.X ? (min, max) : (Vector3.Zero, Vector3.Zero);
        Version++;
    }

    private readonly List<float> _lines = [], _points = [], _shapes = [], _shoes = [], _pivots = [];

    /// <summary>A dot scale rounded to 1/32 steps, so a skeleton needs only a few draw batches.</summary>
    private static float Quantized(float scale) =>
        Math.Clamp(MathF.Round(scale * 32f) / 32f, MinDotScale, 1f);

    private static List<float> Batch(SortedDictionary<float, List<float>> batches, float scale)
    {
        if (!batches.TryGetValue(scale, out var list)) batches[scale] = list = [];
        return list;
    }

    private static List<float> Cleared(List<float> list)
    {
        list.Clear();
        return list;
    }

    /// <summary>
    /// The list's contents as an array: <paramref name="previous"/> overwritten when it has the
    /// same length (the usual case while playing), else a new one. The arrays are only read for
    /// their values, between updates (SceneRenderer, tests), so overwriting in place is safe.
    /// </summary>
    private static float[] Reuse(List<float> list, float[]? previous)
    {
        if (previous == null || previous.Length != list.Count) return list.ToArray();
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list).CopyTo(previous);
        return previous;
    }

    private static void Add(List<float> list, Vector3 position, Vector3 color)
    {
        list.Add(position.X); list.Add(position.Y); list.Add(position.Z);
        list.Add(color.X); list.Add(color.Y); list.Add(color.Z);
    }

    /// <summary>One mesh, expanded to three vertices per triangle and grouped by material slot.</summary>
    private sealed class MeshPart
    {
        private static readonly Vector3 DefaultColor = new(0.7f);

        private readonly uint[] _points;
        private readonly int[] _sourceTriangles;
        private readonly Vector3[]? _normals;
        private readonly Vector3[] _colors;
        private readonly Vector2[] _uvs;
        private readonly Matrix4x4[] _pointMatrices;
        private readonly Object3D[] _anchorPath = [];

        public MeshPart(Mesh mesh, int first, IReadOnlyDictionary<Object3D, Character> controllers)
        {
            Mesh = mesh;
            Skin = new MeshSkin(mesh);
            First = first;
            Owner = Skin.Character;
            var controlPoints = Skin.ControlPoints;
            var cornerPoints = mesh.CornerControlPoints;
            var normals = mesh.Normals;
            var cornerNormals = mesh.CornerNormals;
            bool hasNormals = normals.Length > 0 && cornerNormals.Length == cornerPoints.Length;
            var uvs = mesh.UVs;
            var cornerUVs = mesh.CornerUVs;
            // With several UV sets, UVIArray holds one corner slice per set; the first set is drawn
            bool hasUVs = uvs.Length > 0 && cornerPoints.Length > 0 && cornerUVs.Length >= cornerPoints.Length
                          && cornerUVs.Length % cornerPoints.Length == 0;
            var materials = mesh.Materials;
            var materialColors = materials.Select(MaterialColor).ToArray();

            var points = new List<uint>();
            var vertexNormals = new List<Vector3>();
            var colors = new List<Vector3>();
            var vertexUVs = new List<Vector2>();
            var ranges = new List<MeshRange>();
            var sources = new List<int>();
            // One range per material slot, so each slot is drawn with its own texture. Every drawn
            // triangle keeps its index in Mesh.Triangles()'s order (the order a blendshape zone's
            // InteractiveFaces uses): grouping by material reorders the triangles, so the index
            // cannot be recomputed from the drawn order.
            var triangles = new List<(uint A, uint B, uint C, uint MaterialIndex, int Source)>();
            int ordinal = 0;
            foreach (var (a, b, c, material) in mesh.Triangles()) triangles.Add((a, b, c, material, ordinal++));
            foreach (var group in triangles.GroupBy(t => t.MaterialIndex).OrderBy(g => g.Key))
            {
                uint material = group.Key;
                var color = material < materialColors.Length ? materialColors[material] : DefaultColor;
                int start = points.Count;
                foreach (var (a, b, c, _, source) in group)
                {
                    if (!Valid(a) || !Valid(b) || !Valid(c)) continue;
                    sources.Add(source);
                    foreach (var corner in (ReadOnlySpan<uint>)[a, b, c])
                    {
                        points.Add(cornerPoints[corner]);
                        colors.Add(color);
                        if (hasNormals)
                            vertexNormals.Add(cornerNormals[corner] < normals.Length ? normals[cornerNormals[corner]] : Vector3.UnitY);
                        var uv = hasUVs && cornerUVs[corner] < uvs.Length ? uvs[cornerUVs[corner]] : Vector2.Zero;
                        vertexUVs.Add(uv);
                    }
                }
                if (points.Count > start)
                    ranges.Add(material < materials.Count
                        ? new MeshRange(first + start, points.Count - start, materials[(int)material].Diffuse.Texture, materials[(int)material])
                        : new MeshRange(first + start, points.Count - start, null));
            }
            _points = points.ToArray();
            _sourceTriangles = sources.ToArray();
            _normals = hasNormals ? vertexNormals.ToArray() : null;
            _colors = colors.ToArray();
            _uvs = vertexUVs.ToArray();
            Ranges = ranges;
            _pointMatrices = new Matrix4x4[controlPoints.Length];

            // An unskinned mesh below a controller of a character moves rigidly with it
            if (!Skin.IsSkinned)
            {
                var path = new List<Object3D>();
                for (Object3D? o = mesh; o != null; o = o.Parent3D)
                {
                    if (o != mesh && controllers.TryGetValue(o, out var character))
                    {
                        Anchor = (character, o);
                        Owner = character;
                        _anchorPath = path.ToArray();
                        break;
                    }
                    path.Add(o);
                }
            }

            bool Valid(uint corner) => corner < cornerPoints.Length && cornerPoints[corner] < controlPoints.Length;
        }

        public MeshSkin Skin { get; private set; }
        public Mesh Mesh { get; }
        public int First { get; }
        public int VertexCount => _points.Length;
        public IReadOnlyList<MeshRange> Ranges { get; }

        /// <summary>Each drawn triangle's index in <see cref="Mesh.Triangles"/>'s order, in drawn order.</summary>
        public IReadOnlyList<int> SourceTriangles => _sourceTriangles;

        /// <summary>
        /// The world-space corners of the drawn triangle whose source index (in
        /// <see cref="Mesh.Triangles"/>'s order) is <paramref name="source"/>; false when the last
        /// write did not draw it.
        /// </summary>
        public bool TryTriangle(int source, ReadOnlySpan<float> vertices, out Vector3 a, out Vector3 b, out Vector3 c)
        {
            a = b = c = Vector3.Zero;
            int drawn = -1;
            for (int i = 0; i < _sourceTriangles.Length; i++)
                if (_sourceTriangles[i] == source) { drawn = i; break; }
            if (drawn < 0) return false;
            int offset = First * MeshVertexFloats + drawn * 3 * MeshVertexFloats;
            if (offset < 0 || offset + 3 * MeshVertexFloats > vertices.Length) return false;
            a = Read(vertices, offset);
            b = Read(vertices, offset + MeshVertexFloats);
            c = Read(vertices, offset + 2 * MeshVertexFloats);
            return true;

            static Vector3 Read(ReadOnlySpan<float> data, int at) => new(data[at], data[at + 1], data[at + 2]);
        }

        /// <summary>Re-reads the weights and the character the mesh is skinned to.</summary>
        public void ReloadSkin()
        {
            Skin = new MeshSkin(Mesh);
            Owner = Skin.Character;
            _jointWeights = null;
        }

        private (int Joint, float[] Weights)? _jointWeights;

        /// <summary>Each control point's weight on joint <paramref name="joint"/> (an index into the character's Joints), cached.</summary>
        public float[] JointWeights(int joint)
        {
            if (_jointWeights is { } cached && cached.Joint == joint) return cached.Weights;
            var smooth = Mesh.SkinWeights;
            var rigid = Mesh.RigidSkinWeights;
            int count = Skin.ControlPoints.Length;
            var result = new float[count];
            for (int p = 0; p < count; p++)
            {
                var influences = p < smooth.Length && smooth[p].Length > 0 ? smooth[p] : p < rigid.Length ? rigid[p] : [];
                foreach (var influence in influences)
                    if (influence.Bone == joint) result[p] += influence.Weight;
            }
            _jointWeights = (joint, result);
            return result;
        }

        /// <summary>The character whose pose moves this mesh: the one it is skinned to, or the one owning <see cref="Anchor"/>.</summary>
        public Character? Owner { get; private set; }

        /// <summary>For an unskinned mesh, its closest ancestor that is a controller of a character.</summary>
        public (Character Character, Object3D Controller)? Anchor { get; }

        /// <summary>
        /// World matrix of a mesh with an <see cref="Anchor"/>: the LTMs from the anchor down to the
        /// mesh, composed onto the anchor's evaluated world matrix.
        /// </summary>
        public Matrix4x4 RigidWorld(IReadOnlyList<Matrix4x4> controllerWorlds, PoseEvaluator evaluator, Animation? animation, float time)
        {
            int index = evaluator.IndexOf(Anchor!.Value.Controller);
            if (index < 0 || index >= controllerWorlds.Count) return Skin.MeshWorld;
            var world = controllerWorlds[index];
            for (int i = _anchorPath.Length - 1; i >= 0; i--)
            {
                var o = _anchorPath[i];
                bool compensate = evaluator.Compensates(o, animation, time);
                var parentScale = !compensate ? Vector3.One
                    : i == _anchorPath.Length - 1 ? evaluator.LocalTransforms(animation, time)[index].Scale
                    : Object3D.LocalScale(_anchorPath[i + 1].LocalMatrix);
                world = WorldComposition.Compose(o.LocalMatrix, world, parentScale, compensate);
            }
            return world;
        }

        /// <param name="at">The first vertex written in <paramref name="destination"/>; <see cref="First"/> when null.</param>
        public void Write(float[] destination, IReadOnlyList<Matrix4x4> controllerWorlds, Matrix4x4? rigidWorld, ref Vector3 min, ref Vector3 max, float[]? tint = null, float tintAlpha = 1f, int? at = null)
        {
            if (rigidWorld is { } rigid) _pointMatrices.AsSpan().Fill(rigid);
            else Skin.PointMatrices(controllerWorlds, _pointMatrices);
            var controlPoints = Skin.ControlPoints;
            var span = destination.AsSpan((at ?? First) * MeshVertexFloats, VertexCount * MeshVertexFloats);
            Span<Vector3> p = stackalloc Vector3[3];
            for (int v = 0; v < VertexCount; v += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    p[k] = Vector3.Transform(controlPoints[_points[v + k]], _pointMatrices[_points[v + k]]);
                    min = Vector3.Min(min, p[k]);
                    max = Vector3.Max(max, p[k]);
                }
                var faceNormal = Vector3.Cross(p[1] - p[0], p[2] - p[0]);
                for (int k = 0; k < 3; k++)
                {
                    var n = _normals != null
                        ? Vector3.TransformNormal(_normals[v + k], _pointMatrices[_points[v + k]])
                        : faceNormal;
                    n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY;
                    uint point = _points[v + k];
                    var c = tint != null
                        ? Vector3.Lerp(_colors[v + k], WeightColor(point < tint.Length ? tint[point] : 0f), tintAlpha)
                        : _colors[v + k];
                    var uv = _uvs[v + k];
                    var o = (v + k) * MeshVertexFloats;
                    span[o] = p[k].X; span[o + 1] = p[k].Y; span[o + 2] = p[k].Z;
                    span[o + 3] = n.X; span[o + 4] = n.Y; span[o + 5] = n.Z;
                    span[o + 6] = c.X; span[o + 7] = c.Y; span[o + 8] = c.Z;
                    span[o + 9] = uv.X; span[o + 10] = uv.Y;
                }
            }
        }

        /// <summary>
        /// The diffuse <c>Value</c>, shown where the diffuse texture is missing or cannot be decoded
        /// </summary>
        private static Vector3 MaterialColor(Material material)
        {
            var diffuse = material.Diffuse;
            var c = new Vector3(diffuse.Value.X, diffuse.Value.Y, diffuse.Value.Z);
            return c.LengthSquared() > 0.0001f ? c : DefaultColor;
        }
    }
}

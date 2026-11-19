using System.Numerics;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// A blendshape target shape (<c>CBlendshape</c>): per-vertex deltas under a mesh
/// The arrays are stored as <c>Vec4f</c>; the xyz part is the
/// position delta, the delta normals are in <see cref="NormalArray"/>.
/// </summary>
public sealed class Blendshape : Object3D
{
    internal Blendshape(SceneModel model, AktObject source) : base(model, source) { }

    public Vector4[] VertexArray
    {
        get => SceneValues.Items(Source, "VertexArray").Select(v => AktMath.ToVector4((float[])v!)).ToArray();
        set => Source.Set("VertexArray", value.Select(AktMath.ToArray).Cast<object?>().ToList());
    }

    public Vector4[] NormalArray
    {
        get => SceneValues.Items(Source, "NormalArray").Select(v => AktMath.ToVector4((float[])v!)).ToArray();
        set => Source.Set("NormalArray", value.Select(AktMath.ToArray).Cast<object?>().ToList());
    }

    /// <summary>The target's index array (SloppyAnimatsu writes the mesh's corner-to-control-point array).</summary>
    public uint[] VertexIArray
    {
        get => UInts("VertexIArray");
        set => Source.Set("VertexIArray", value.Select(v => (object?)v).ToList());
    }

    public float MaxLength
    {
        get => Source.GetOr("MaxLength", 0f);
        set => Source.Set("MaxLength", value);
    }
}

/// <summary>
/// One <c>CBlendshapeInfoBlendshape</c>: a target placed at a position along its channel's value
/// axis.
/// </summary>
public sealed class BlendshapeInfo : SceneObject
{
    internal BlendshapeInfo(SceneModel model, AktObject source) : base(model, source) { }

    public Blendshape? Blendshape => Ref<Blendshape>("Blendshape");

    public float Position
    {
        get => Source.GetOr("Position", 0f);
        set => Source.Set("Position", value);
    }
}

/// <summary>
/// A <c>CBlendshapeChannel</c>: a slider of target shapes driven by one of the controller's two
/// values. <see cref="Direction"/> 2/3 selects value 0 (X), 0/1 selects value 1 (Y).
/// </summary>
public sealed class BlendshapeChannel : SceneObject
{
    internal BlendshapeChannel(SceneModel model, AktObject source) : base(model, source) { }

    /// <summary>
    /// The infos on this channel: the .akt format has no serialized list for them, the children of the
    /// channel are the list (a file written before that was known still carries the field).
    /// </summary>
    public IReadOnlyList<BlendshapeInfo> Infos =>
        Source.Has("BlendshapeInfoArray")
            ? Refs<BlendshapeInfo>("BlendshapeInfoArray")
            : Children.OfType<BlendshapeInfo>().ToList();

    public uint Direction
    {
        get => Source.GetOr("Direction", 0u);
        set => Source.Set("Direction", value);
    }

    public float Attenuation
    {
        get => Source.GetOr("Attenuation", 0f);
        set => Source.Set("Attenuation", value);
    }

    /// <summary>The key value this channel follows: 0 for directions 2 and 3, 1 for 0 and 1.</summary>
    public int Group => Direction is 2 or 3 ? 0 : 1;
}

/// <summary>
/// A <c>CBlendshapeController</c>: drives one mesh's targets from its two animated values and its
/// channels. It is a <c>CController3D</c> (it has a pose slot and a rig track) but not an IK chain.
/// </summary>
public class BlendshapeController : Controller3D
{
    internal BlendshapeController(SceneModel model, AktObject source) : base(model, source) { }

    public Mesh? Mesh => Ref<Mesh>("Mesh");

    public IReadOnlyList<BlendshapeChannel> Channels => Refs<BlendshapeChannel>("BlendshapeChannelArray");

    public bool IsUp
    {
        get => Flag("IsUp");
        set => Source.Set("IsUp", value);
    }

    /// <summary>The values mixed from the animation's stacks at <paramref name="time"/> (two floats).</summary>
    public float[] Values(PoseEvaluator poses, Animation? animation, float time) =>
        poses.BlendshapeValues(animation, this, time);
}

/// <summary><c>CBlendshapeSimpleController</c>: a controller with no extra members.</summary>
public sealed class BlendshapeSimpleController : BlendshapeController
{
    internal BlendshapeSimpleController(SceneModel model, AktObject source) : base(model, source) { }
}

/// <summary><c>CBlendshapeFaceController</c>: a face-pick zone over the mesh.</summary>
public sealed class BlendshapeFaceController : BlendshapeController
{
    internal BlendshapeFaceController(SceneModel model, AktObject source) : base(model, source) { }

    /// <summary>Which mouse button selects the zone (0 left, 1 middle, 2 right).</summary>
    public uint Click
    {
        get => Source.GetOr("Click", 0u);
        set => Source.Set("Click", value);
    }

    /// <summary>Mesh face indices belonging to the pick zone.</summary>
    public uint[] InteractiveFaces
    {
        get => UInts("InteractiveFaces");
        set => Source.Set("InteractiveFaces", value.Select(v => (object?)v).ToList());
    }
}

/// <summary>
/// A <c>CBlendshapeFace</c>: a thumbnail that stores two values and applies them to the face
/// controllers it references.
/// </summary>
public sealed class BlendshapeFace : SceneObject
{
    internal BlendshapeFace(SceneModel model, AktObject source) : base(model, source) { }

    public IReadOnlyList<BlendshapeFaceController> Controllers => Refs<BlendshapeFaceController>("BlendshapeFaceControllerArray");

    /// <summary>The stored two-value keys (inline <c>CKeyBlendshape</c> structs).</summary>
    public IReadOnlyList<float[]> Values => SceneValues.Items(Source, "KeyList")
        .Select(k => SceneValues.Items((AktStruct)k!, "_values").Select(Convert.ToSingle).ToArray())
        .ToArray();

    public int Index => Source.GetOr("Index", 0);
}

/// <summary><c>CBlendShapesSight</c>: the blendshape point-of-view marker.</summary>
public sealed class BlendshapeSight : Object3D
{
    internal BlendshapeSight(SceneModel model, AktObject source) : base(model, source) { }
}

/// <summary>A <c>CKeyBlendshape</c>: the controller's two values at a key (<c>_values</c>).</summary>
public sealed class BlendshapeKey : AnimKey
{
    internal BlendshapeKey(SceneModel model, AktStruct source) : base(model, source) { }

    /// <summary>The stored <c>_values</c> (two floats; short or empty arrays read as padded/truncated).</summary>
    public float[] Values
    {
        get
        {
            var values = SceneValues.Items(Source, "_values").Select(Convert.ToSingle).ToArray();
            return values.Length == 2 ? values : [values.ElementAtOrDefault(0), values.ElementAtOrDefault(1)];
        }
        set
        {
            if (Source["_values"] is not List<object?> list)
                throw new InvalidDataException($"{Source.TypeName} has no _values vector.");
            list.Clear();
            list.Add(value[0]);
            list.Add(value[1]);
        }
    }

    public float X
    {
        get => Values[0];
        set => Values = [value, Values[1]];
    }

    public float Y
    {
        get => Values[1];
        set => Values = [Values[0], value];
    }
}

/// <summary>
/// A <c>CAnimTrackBlendshape</c>: channels 13 (X) and 14 (Y) with two tangent channels per key.
/// </summary>
public sealed class BlendshapeTrack : AnimTrack
{
    internal BlendshapeTrack(SceneModel model, AktObject source) : base(model, source) { }

    public new IReadOnlyList<BlendshapeKey?> Keys =>
        SceneValues.Items(Source, "KeyList").Select(k => k is AktStruct s ? AnimKey.Wrap(Model, s) as BlendshapeKey : null).ToArray();

    public float[] ValuesAt(int key) => Key(key)?.Values ?? [0f, 0f];

    public float TangentIn(int key, int channel) => Tangent("TInList", key, channel);

    public float TangentOut(int key, int channel) => Tangent("TOutList", key, channel);

    private float Tangent(string field, int key, int channel)
    {
        var keys = SceneValues.Items(Source, field);
        if (key < 0 || key >= keys.Count || keys[key] is not AktStruct s) return 0f;
        return SceneValues.Items(s, "_values").Select(Convert.ToSingle).ElementAtOrDefault(channel);
    }

    /// <summary>Whether the segment after <paramref name="key"/> holds it for <paramref name="channel"/>.</summary>
    public bool IsStep(int key, int channel)
    {
        var modes = TangentModes;
        return key >= 0 && key < modes.Count && channel >= 0 && channel < modes[key].Length &&
               modes[key][channel].LowMode == 5;
    }

    public void SetValues(int key, float x, float y)
    {
        if (Key(key) is not { } value) return;
        value.Values = [x, y];
    }

    public void SetTangent(int key, bool inSide, int channel, float value)
    {
        var field = inSide ? "TInList" : "TOutList";
        var keys = SceneValues.Items(Source, field);
        if (key < 0 || key >= keys.Count || keys[key] is not AktStruct s) return;
        var values = SceneValues.Items(s, "_values");
        if (channel >= 0 && channel < values.Count) values[channel] = value;
    }

    private BlendshapeKey? Key(int index) => index >= 0 && index < Keys.Count ? Keys[index] : null;
}

/// <summary>
/// Creates blendshape targets, controllers, channels and info entries in a scene
/// The edits change the document in place; saving writes them.
/// </summary>
public sealed class BlendshapeEditor
{
    /// <summary>Shown and "new object": the flags a created target or controller carries.</summary>
    public const uint NewObjectFlags = 0x401;

    /// <summary>The helper objects' flags (shown plus the hidden-in-tree group), as in the samples.</summary>
    public const uint HelperFlags = 0x4F9;

    public BlendshapeEditor(Character character)
    {
        Character = character;
        BlendshapeSchema.Ensure(character.Model.Document.Schema);
    }

    public Character Character { get; }

    /// <summary>
    /// Adds a target shape under <paramref name="mesh"/>'s <c>Blendshapes</c> helper and appends it
    /// to the mesh's <c>BlendshapeTargetArray</c>. <paramref name="deltas"/> has one position delta
    /// per control point (default: no movement); normals default to zero.
    /// </summary>
    public Blendshape AddTarget(Mesh mesh, string name, IReadOnlyList<Vector4>? deltas = null)
    {
        var schema = Character.Model.Document.Schema;
        var points = mesh.ControlPoints;
        var parent = mesh.Child("Blendshapes") as Object3D;
        if (parent == null)
        {
            var helper = schema.NewObject("CObject3D");
            helper.Set("Name", "Blendshapes");
            helper.Set("Flags", HelperFlags);
            helper.Set("LTM", AktMath.ToArray(Matrix4x4.Identity));
            helper.Set("Constraint", new AktReference(null));
            mesh.Source.AddChild(helper);
            parent = (Object3D)Character.Model.Wrap(helper);
        }

        var source = schema.NewObject(BlendshapeSchema.TargetClass);
        source.Set("Name", UniqueNames.For(parent.Source, name));
        source.Set("Flags", NewObjectFlags);
        source.Set("LTM", AktMath.ToArray(Matrix4x4.Identity));
        source.Set("Constraint", new AktReference(null));
        var array = deltas ?? points.Select(_ => Vector4.Zero).ToArray();
        if (array.Count != points.Length)
            throw new ArgumentException($"A target needs one delta per control point ({points.Length}), not {array.Count}.", nameof(deltas));
        source.Set("VertexArray", array.Select(v => (object?)AktMath.ToArray(v)).ToList());
        source.Set("NormalArray", mesh.Normals.Select(_ => (object?)new float[4]).ToList());
        source.Set("VertexIArray", mesh.CornerControlPoints.Select(v => (object?)v).ToList());
        source.Set("MaxLength", array.Count == 0 ? 0f : array.Max(v => v.Length()));
        parent.Source.AddChild(source);
        ((List<AktReference>)mesh.Source["BlendshapeTargetArray"]!).Add(new AktReference(source));
        return (Blendshape)Character.Model.Wrap(source);
    }

    /// <summary>
    /// Adds a <c>CBlendshapeController</c> under the character's <c>__BlendshapeControllers</c>
    /// dummy, appends it to <c>Controllers</c>, gives it an identity pose slot in every pose and a
    /// two-value <c>__RigAnim</c> track.
    /// </summary>
    public BlendshapeController AddController(Mesh mesh, string? name = null) =>
        AddController(mesh, name, BlendshapeSchema.ControllerClass);

    /// <summary>
    /// Adds a <c>CBlendshapeFaceController</c>, the kind of controller a <see cref="BlendshapeFace"/>
    /// references; it drives the mesh's targets like any other blendshape controller and also marks
    /// a pick zone over the mesh (<c>Click</c>, <c>InteractiveFaces</c>).
    /// </summary>
    public BlendshapeFaceController AddFaceController(Mesh mesh, string? name = null) =>
        (BlendshapeFaceController)AddController(mesh, name, BlendshapeSchema.FaceControllerClass);

    private BlendshapeController AddController(Mesh mesh, string? name, string className)
    {
        var schema = Character.Model.Document.Schema;
        var dummy = Character.Child("__BlendshapeControllers") as Object3D;
        if (dummy == null)
        {
            var helper = schema.NewObject(BlendshapeSchema.HiddenInTreeClass);
            helper.Set("Name", "__BlendshapeControllers");
            helper.Set("Flags", HelperFlags);
            helper.Set("LTM", AktMath.ToArray(Matrix4x4.Identity));
            helper.Set("Constraint", new AktReference(null));
            // The container tells the app that its children changed; the app re-saves the flag as
            // true after a controller is added.
            helper.Set("HierarchyChangedNotifier", true);
            Character.Source.AddChild(helper);
            dummy = (Object3D)Character.Model.Wrap(helper);
        }

        var source = schema.NewObject(className);
        source.Set("Name", UniqueNames.For(dummy.Source, name ?? "Blendshape"));
        source.Set("Flags", NewObjectFlags);
        source.Set("LTM", AktMath.ToArray(Matrix4x4.Identity));
        source.Set("Constraint", new AktReference(null));
        source.Set("TargetRenderSizeRatio", 1f);
        source.Set("IsUp", false);
        source.Set("Mesh", new AktReference(mesh.Source));
        if (className == BlendshapeSchema.FaceControllerClass)
        {
            source.Set("Click", 0u);
            source.Set("InteractiveFaces", new List<object?>());
        }
        dummy.Source.AddChild(source);
        if (dummy.Source.Has("HierarchyChangedNotifier"))
            dummy.Source.Set("HierarchyChangedNotifier", true);

        var controllers = (List<AktReference>)Character.Source["Controllers"]!;
        int slot = controllers.Count;
        controllers.Add(new AktReference(source));
        var identity = AktMath.ToArray(Matrix4x4.Identity);
        foreach (var pose in new RigEditor(Character).Poses())
        {
            var matrices = SceneValues.Items(pose.Source, "Matrices");
            if (slot < matrices.Count) matrices.Insert(slot, (float[])identity.Clone());
            else matrices.Add((float[])identity.Clone());
        }

        var controller = (BlendshapeController)Character.Model.Wrap(source);
        if (Character.RigAnimation?.Stacks.FirstOrDefault() is { } stack && stack.TrackFor(controller) == null)
            TrackFactory.Create(stack, controller);
        return controller;
    }

    /// <summary>Adds a channel to <paramref name="controller"/>; <paramref name="direction"/> is 0-3.</summary>
    public BlendshapeChannel AddChannel(BlendshapeController controller, uint direction, float attenuation = 1f)
    {
        var schema = Character.Model.Document.Schema;
        var source = schema.NewObject(BlendshapeSchema.ChannelClass);
        source.Set("Name", UniqueNames.For(controller.Source, "Channel"));
        source.Set("Direction", direction);
        source.Set("Attenuation", attenuation);
        controller.Source.AddChild(source);
        ((List<AktReference>)controller.Source["BlendshapeChannelArray"]!).Add(new AktReference(source));
        return (BlendshapeChannel)Character.Model.Wrap(source);
    }

    /// <summary>Places <paramref name="target"/> at <paramref name="position"/> on <paramref name="channel"/>'s slider.</summary>
    public BlendshapeInfo AddInfo(BlendshapeChannel channel, Blendshape target, float position)
    {
        var schema = Character.Model.Document.Schema;
        var source = schema.NewObject(BlendshapeSchema.InfoClass);
        source.Set("Name", UniqueNames.For(channel.Source, target.Name ?? "Info"));
        source.Set("Blendshape", new AktReference(target.Source));
        source.Set("Position", position);
        channel.Source.AddChild(source);
        return (BlendshapeInfo)Character.Model.Wrap(source);
    }

    /// <summary>
    /// Adds a <c>CBlendshapeFace</c> under the mesh's <c>__BlendshapeFaces</c> dummy: the stored
    /// two-value pairs (one <c>CKeyBlendshape</c> each) and the face controllers it applies them to.
    /// No scene carries a thumbnail (<see cref="BlendshapeSchema.ThumbnailClass"/>), so a created face
    /// has none: the panel draws a labelled cell instead of a picture.
    /// </summary>
    public BlendshapeFace AddFace(Mesh mesh, string name, IReadOnlyList<BlendshapeFaceController> controllers,
        IReadOnlyList<float[]>? values = null)
    {
        var schema = Character.Model.Document.Schema;
        var parent = mesh.Child("__BlendshapeFaces");
        if (parent == null)
        {
            var helper = schema.NewObject(schema.FindClass("CDummy") != null ? "CDummy" : "CObject3D");
            helper.Set("Name", "__BlendshapeFaces");
            if (helper.Has("Flags")) helper.Set("Flags", HelperFlags);
            if (helper.Has("LTM")) helper.Set("LTM", AktMath.ToArray(Matrix4x4.Identity));
            if (helper.Has("Constraint")) helper.Set("Constraint", new AktReference(null));
            mesh.Source.AddChild(helper);
            parent = Character.Model.Wrap(helper);
        }

        var source = schema.NewObject(BlendshapeSchema.FaceClass);
        source.Set("Name", UniqueNames.For(parent.Source, name));
        // A face may be a plain data object (when the scene has no thumbnail class): write the
        // object members only when its class declares them.
        if (source.Has("Flags")) source.Set("Flags", HelperFlags);
        if (source.Has("LTM")) source.Set("LTM", AktMath.ToArray(Matrix4x4.Identity));
        if (source.Has("Constraint")) source.Set("Constraint", new AktReference(null));
        if (source.Has("Index")) source.Set("Index", mesh.BlendshapeFaces.Count);
        var refs = (List<AktReference>)source["BlendshapeFaceControllerArray"]!;
        foreach (var controller in controllers) refs.Add(new AktReference(controller.Source));
        var keys = (List<object?>)source["KeyList"]!;
        foreach (var pair in values ?? [new[] { 0f, 1f }])
        {
            var key = schema.NewStruct(BlendshapeSchema.KeyStruct);
            var list = (List<object?>)key["_values"]!;
            list.Add(pair[0]);
            list.Add(pair[1]);
            keys.Add(key);
        }
        parent.Source.AddChild(source);
        if (mesh.Source["BlendshapeFaces"] is not List<AktReference> faces)
            throw new InvalidDataException($"{mesh} has no BlendshapeFaces member.");
        faces.Add(new AktReference(source));
        return (BlendshapeFace)Character.Model.Wrap(source);
    }

    /// <summary>
    /// Adds a scene-root <c>CBlendShapesSight</c>: the blendshape point-of-view marker the viewport
    /// draws as a point. Its anchoring is
    /// SloppyAnimatsu's choice, since no sample carries one.
    /// </summary>
    public BlendshapeSight AddSight(string name, Vector3 position)
    {
        var schema = Character.Model.Document.Schema;
        var source = schema.NewObject(BlendshapeSchema.SightClass);
        source.Set("Name", name);
        source.Set("Flags", NewObjectFlags);
        source.Set("LTM", AktMath.ToArray(Matrix4x4.CreateTranslation(position)));
        if (source.Has("Constraint")) source.Set("Constraint", new AktReference(null));
        Character.Model.Scene.Source.AddChild(source);
        return (BlendshapeSight)Character.Model.Wrap(source);
    }

    /// <summary>Writes a track key's two values; with <paramref name="mirrorLoop"/> key 0 also updates the last key.</summary>
    public static void SetKeyValues(BlendshapeTrack track, int key, float x, float y, bool mirrorLoop = true)
    {
        track.SetValues(key, x, y);
        if (mirrorLoop && key == 0 && track.Keys.Count > 1)
            track.SetValues(track.Keys.Count - 1, x, y);
    }
}

/// <summary>
/// Evaluates blendshapes of a character's meshes: the mixed
/// controller values, the per-target influence array, and the deformed control points. The
/// deformation is applied in mesh space before skinning.
/// </summary>
public sealed class BlendshapeEvaluator
{
    private readonly PoseEvaluator _poses;

    public BlendshapeEvaluator(PoseEvaluator poses) => _poses = poses;

    public PoseEvaluator Poses => _poses;

    /// <summary>The controller's two mixed values at <paramref name="time"/>.</summary>
    public float[] Values(BlendshapeController controller, Animation? animation, float time) =>
        _poses.BlendshapeValues(animation, controller, time);

    /// <summary>
    /// One influence per entry of <paramref name="mesh"/>'s <c>BlendshapeTargetArray</c>, accumulated
    /// from every controller of the character that drives this mesh.
    /// </summary>
    public float[] Influences(Mesh mesh, Animation? animation, float time)
    {
        var targets = mesh.BlendshapeTargets;
        var influences = new float[targets.Count];
        if (targets.Count == 0) return influences;
        var character = CharacterOf(mesh);
        if (character == null) return influences;

        foreach (var controller in character.Controllers.OfType<BlendshapeController>())
        {
            if (!ReferenceEquals(controller.Mesh, mesh)) continue;
            var values = Values(controller, animation, time);
            foreach (var channel in controller.Channels)
            {
                float attenuation = channel.Attenuation * values[Math.Clamp(channel.Group, 0, 1)];
                var (lower, lowerPos, upper, upperPos) = Bracket(channel, attenuation);
                var (lowerWeight, upperWeight) = Weights(attenuation, lowerPos, upperPos);
                Add(influences, targets, lower, lowerWeight);
                Add(influences, targets, upper, upperWeight);
            }
        }
        return influences;
    }

    /// <summary>The mesh's control points with the blendshape deltas applied (mesh space, before skinning).</summary>
    public Vector3[] ControlPoints(Mesh mesh, Animation? animation, float time)
    {
        var points = mesh.ControlPoints;
        var influences = Influences(mesh, animation, time);
        var targets = mesh.BlendshapeTargets;
        for (int t = 0; t < targets.Count && t < influences.Length; t++)
        {
            if (influences[t] == 0f) continue;
            var deltas = targets[t].VertexArray;
            int count = Math.Min(points.Length, deltas.Length);
            for (int p = 0; p < count; p++)
                points[p] += new Vector3(deltas[p].X, deltas[p].Y, deltas[p].Z) * influences[t];
        }
        return points;
    }

    /// <summary>The deformed control-point normals (base normals plus the target normal deltas).</summary>
    public Vector3[] Normals(Mesh mesh, Animation? animation, float time)
    {
        var normals = mesh.Normals;
        var influences = Influences(mesh, animation, time);
        var targets = mesh.BlendshapeTargets;
        for (int t = 0; t < targets.Count && t < influences.Length; t++)
        {
            if (influences[t] == 0f) continue;
            var deltas = targets[t].NormalArray;
            int count = Math.Min(normals.Length, deltas.Length);
            for (int p = 0; p < count; p++)
                normals[p] += new Vector3(deltas[p].X, deltas[p].Y, deltas[p].Z) * influences[t];
        }
        return normals;
    }

    /// <summary>The character whose pose drives the mesh, if any.</summary>
    public static Character? CharacterOf(Mesh mesh) => mesh.BindPose?.Character ?? mesh.Ancestor<Character>();

    /// <summary>
    /// The two entries around <paramref name="attenuation"/>: the largest position at or below it and
    /// the smallest at or above it, with a sentinel entry at position 0 and no target at the end
    /// Ties at a position are won by the later entry.
    /// </summary>
    private static (Blendshape? Lower, float LowerPos, Blendshape? Upper, float UpperPos) Bracket(
        BlendshapeChannel channel, float attenuation)
    {
        Blendshape? lower = null, upper = null;
        float lowerPos = float.MinValue, upperPos = float.MaxValue;
        foreach (var info in channel.Infos.Append(null))
        {
            float position = info?.Position ?? 0f;
            var target = info?.Blendshape;
            if (position <= attenuation && position >= lowerPos) { lowerPos = position; lower = target; }
            // The sentinel (and an info whose target is gone) never becomes the upper bracket.
            if (target != null && position >= attenuation && position <= upperPos) { upperPos = position; upper = target; }
        }
        return (lower, lowerPos, upper, upperPos);
    }

    /// <summary>
    /// The two weights of the brackets. Positions beyond
    /// ±1 are the "beyond the end" cases: a single side is used instead of interpolating.
    /// </summary>
    private static (float Lower, float Upper) Weights(float attenuation, float lowerPos, float upperPos)
    {
        if (lowerPos == upperPos) return (1f, 0f);
        if (MathF.Abs(lowerPos) > 1f && MathF.Abs(upperPos) > 1f) return (0f, 0f);
        if (MathF.Abs(lowerPos) > 1f) return (0f, 1f);
        if (MathF.Abs(upperPos) > 1f || upperPos == float.MaxValue) return (1f, 0f);
        float span = upperPos - lowerPos;
        if (MathF.Abs(span) < 1e-12f) return (1f, 0f);
        return ((upperPos - attenuation) / span, (attenuation - lowerPos) / span);
    }

    private static void Add(float[] influences, IReadOnlyList<Blendshape> targets, Blendshape? target, float weight)
    {
        if (target == null || weight == 0f) return;
        for (int i = 0; i < targets.Count; i++)
            if (ReferenceEquals(targets[i], target)) { influences[i] += weight; return; }
    }
}

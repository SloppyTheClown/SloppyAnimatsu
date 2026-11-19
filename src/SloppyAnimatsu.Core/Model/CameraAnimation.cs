using System.Numerics;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// <c>CCameraAnimated</c>: a perspective camera that can be keyed in an animation, with the photo
/// framing fields. Its local transform is a normal pose slot
/// of the owning character, and it is a plain child of that character. The app additionally builds
/// the editor's orbit rig around it - a <c>CHideJointForLooker</c> child, a <c>CCameraTarget</c>
/// constraint child of the character and the looker track - which <see cref="CameraEditor"/> copies;
/// The view reads the camera's own pose and <c>_targetDistance</c> only.
/// </summary>
public sealed class CameraAnimated : PerspectiveCamera
{
    internal CameraAnimated(SceneModel model, AktObject source) : base(model, source) { }

    /// <summary>0 = landscape, 1 = portrait (the <c>Mode</c> member).</summary>
    public int Mode
    {
        get => Source.GetOr("Mode", 0);
        set => Source.Set("Mode", value);
    }

    /// <summary>The aspect preset, 0-5 (the <c>Ratio</c> member; default 3 = 16:9).</summary>
    public int Ratio
    {
        get => Source.GetOr("Ratio", 3);
        set => Source.Set("Ratio", value);
    }

    public float NearClipPlane
    {
        get => Source.GetOr("NearClipPlane", 15f);
        set => Source.Set("NearClipPlane", value);
    }

    public float FarClipPlane
    {
        get => Source.GetOr("FarClipPlane", 30000f);
        set => Source.Set("FarClipPlane", value);
    }

    /// <summary>Width / height from <see cref="Mode"/> and <see cref="Ratio"/>.</summary>
    public new float AspectRatio => Mode switch
    {
        0 => LandscapeRatios[Math.Clamp(Ratio, 0, LandscapeRatios.Length - 1)],
        1 => PortraitRatios[Math.Clamp(Ratio, 0, PortraitRatios.Length - 1)],
        _ => 1f,
    };

    /// <summary>The landscape ratio presets, index 0-5: 32:9, 7:3, 2:1, 16:9, 8:5, 4:3.</summary>
    public static readonly float[] LandscapeRatios = [32f / 9f, 7f / 3f, 2f, 16f / 9f, 8f / 5f, 4f / 3f];

    /// <summary>The portrait presets: the reciprocals of <see cref="LandscapeRatios"/>.</summary>
    public static readonly float[] PortraitRatios = LandscapeRatios.Select(r => 1f / r).ToArray();

    /// <summary>
    /// The orbit rig's target: the <c>CController3D</c> this camera's <c>Constraint</c> names
    /// (the app reads the constraint, it does not search for a child).
    /// </summary>
    public CameraTarget? Target => Constraint as CameraTarget;

    /// <summary>The hidden joint under this camera (a child of the camera, not of the character).</summary>
    public Joint? HideJoint => Target?.HideJoint;

    /// <summary>The owning character (the camera is parented to it).</summary>
    public Character? Character => Parent as Character ?? Ancestor<Character>();
}

/// <summary>
/// <c>CCameraTarget</c>: the <c>CLooker</c> the animated camera constrains, child of the character.
/// Its <c>JointsString</c> is [the camera, the camera's hidden joint] and its decorator is an IK
/// gizmo. It is wrapped as a controller 3D rather than as an IK controller on purpose: a looker
/// has its own solve, which this build does not model, so no chain solve
/// may touch it and its keys (the authored look point) play as they are.
/// </summary>
public sealed class CameraTarget : Controller3D
{
    internal CameraTarget(SceneModel model, AktObject source) : base(model, source) { }

    /// <summary><c>CControllerIK.JointsString</c>: [the camera, its hidden joint].</summary>
    public IReadOnlyList<Object3D> Chain => Refs<Object3D>("JointsString");

    /// <summary><c>CControllerIK.Parents</c>; empty for a camera target.</summary>
    public IReadOnlyList<Object3D> ChainParents => Refs<Object3D>("Parents");

    /// <summary><c>CControllerIK.DecoratorAxis</c>; 2 for the camera target.</summary>
    public byte DecoratorAxis => Source.GetOr("DecoratorAxis", (byte)2);

    /// <summary>The camera this target looks for.</summary>
    public CameraAnimated? Camera => Chain.Count > 0 ? Chain[0] as CameraAnimated : null;

    /// <summary>The camera's hidden joint, the effector of the looker chain.</summary>
    public Joint? HideJoint => Chain.Count > 1 ? Chain[1] as Joint : null;

    /// <summary>
    /// SloppyAnimatsu's runtime lock of the rest-pose target (the Rigging context has no key to carry the
    /// IK lock): while set, moving the camera re-aims it at the target instead of carrying the target
    /// along. Not saved.
    /// </summary>
    public bool RestLocked { get; set; }
}

/// <summary>An animated camera's editor rig: its target and the hidden joint under the camera.</summary>
public readonly record struct CameraRig(CameraTarget Target, Joint HideJoint);

/// <summary>
/// Creates an animated camera: a <c>CCameraAnimated</c>
/// child of the character, appended to its <c>Controllers</c> and to the matching slot of every
/// character pose, together with the editor rig - the hidden joint under the camera, the
/// looker target under the character, and the constraint that ties them together. The scene's
/// schema is extended with the classes when they are missing.
/// </summary>
public static class CameraEditor
{
    /// <summary>The name a new animated camera gets when none is requested.</summary>
    public const string DefaultName = "Camera Animated";

    /// <summary>The name the app gives a new hidden joint before the unique-name rule runs.</summary>
    public const string HideJointName = "Default name";

    /// <summary>The hidden joint's own offset from the camera, on its -Z axis (40 in the sample files).</summary>
    public const float HideJointDistance = 40f;

    /// <summary><c>Flags</c> of a new camera and target: shown plus the "new object" bit (0x400).</summary>
    public const uint CameraFlags = 0x401;

    /// <summary><c>Flags</c> of a new hidden joint: compose mode 1, bit 7 and the "new object" bit.</summary>
    public const uint HideJointFlags = 0x482;

    /// <summary>
    /// Adds an animated camera, and its orbit rig, to <paramref name="character"/>. Its starting
    /// pose is the scene's <c>Perspective</c> camera eye pose when the scene has one, else identity;
    /// its FOV is 30. The three objects are appended to the character's <c>Controllers</c> in
    /// The order (camera, hidden joint, target) and get a matrix in every character pose; the
    /// hidden joint joins the character's <c>JointArray</c>. Returns the new camera.
    /// </summary>
    public static CameraAnimated AddAnimatedCamera(Character character, string? name = null)
    {
        var model = character.Model;
        var schema = model.Document.Schema;
        CameraSchema.EnsureRig(schema);

        var source = schema.NewObject(CameraSchema.AnimatedCameraClass);
        source.Set("Name", UniqueNames.For(character.Source, name ?? DefaultName));
        source.Set("CameraType", -1);
        source.Set("RenderMode", (byte)0);
        source.Set("RenderFlags", 56u);
        source.Set("Flags", CameraFlags);
        // Start from the scene's perspective eye pose, so the new camera shows what the editor showed.
        var world = WorldStartPose(model.Scene);
        var start = Matrix4x4.Invert(character.WorldMatrix, out var inverseParent) ? world * inverseParent : world;
        source.Set("LTM", AktMath.ToArray(start));
        // _matrix is a camera matrix in world space, like the.
        source.Set("_matrix", AktMath.ToArray(world));
        source.Set("_targetDistance", 700f);
        source.Set("_ratio", 1f);
        source.Set("Mode", 0);
        source.Set("Ratio", 3);
        source.Set("NearClipPlane", 15f);
        source.Set("FarClipPlane", 30000f);

        character.Source.AddChild(source);
        var controllers = Controllers(character);
        int slot = controllers.Count;
        controllers.Add(new AktReference(source));
        foreach (var pose in Poses(character)) InsertMatrix(pose, slot, start);

        // The editor's orbit rig: the hidden joint under the camera first (the app creates it before
        // the target), then the target under the character, then the two constraint links.
        var hideLocal = Matrix4x4.CreateTranslation(0f, 0f, -HideJointDistance);
        var hideSource = schema.NewObject(CameraSchema.HideJointClass);
        hideSource.Set("Name", UniqueNames.For(source, HideJointName));
        hideSource.Set("Flags", HideJointFlags);
        hideSource.Set("LTM", AktMath.ToArray(hideLocal));
        hideSource.Set("SmoothMethod", 0);
        source.AddChild(hideSource);

        // The target sits at the camera's own -Z point in the character's space.
        var targetLocal = hideLocal * start;
        var joints = new List<AktReference> { new(source), new(hideSource) };
        var targetSource = schema.NewObject(CameraSchema.CameraTargetClass);
        targetSource.Set("Name", UniqueNames.For(character.Source, source.Name + "_Target"));
        targetSource.Set("Flags", CameraFlags);
        targetSource.Set("LTM", AktMath.ToArray(targetLocal));
        targetSource.Set("TargetRenderSizeRatio", 1f);
        targetSource.Set("JointsString", joints);
        targetSource.Set("Parents", new List<AktReference>());
        targetSource.Set("DecoratorAxis", (byte)2);
        character.Source.AddChild(targetSource);
        source.Set("Constraint", new AktReference(targetSource));
        hideSource.Set("Constraint", new AktReference(targetSource));
        var jointsArray = Joints(character);
        jointsArray.Add(new AktReference(hideSource));

        int hideSlot = controllers.Count;
        controllers.Add(new AktReference(hideSource));
        int targetSlot = controllers.Count;
        controllers.Add(new AktReference(targetSource));
        foreach (var pose in Poses(character))
        {
            InsertMatrix(pose, hideSlot, hideLocal);
            InsertMatrix(pose, targetSlot, targetLocal);
        }
        return (CameraAnimated)model.Wrap(source);
    }

    /// <summary>The camera's orbit rig when it has one, else null.</summary>
    public static CameraRig? Rig(CameraAnimated camera)
    {
        if (camera.Target is { } target && target.HideJoint is { } hide) return new CameraRig(target, hide);
        return null;
    }

    /// <summary>
    /// Whether a camera rig's target is locked: in Animation (<paramref name="stack"/> set) the
    /// IK lock of the target's key at or before <paramref name="frame"/>; in Rigging the runtime
    /// <see cref="CameraTarget.RestLocked"/>. A locked target stays put while the camera moves.
    /// </summary>
    public static bool IsTargetLocked(CameraTarget target, AnimStack? stack, int frame)
    {
        if (stack == null) return target.RestLocked;
        if (stack.TrackFor(target) is not { } track) return false;
        var keys = track.Keys;
        var times = stack.Keys;
        int at = -1;
        for (int i = 0; i < times.Count && i < keys.Count; i++)
            if (times[i].Time <= frame) at = i;
        if (at < 0 && keys.Count > 0) at = 0;
        return at >= 0 && keys[at] is IKKey key && (key.Flags & 1u) != 0;
    }

    /// <summary>
    /// Whether the target's key at <paramref name="keyIndex"/> of <paramref name="stack"/> carries the
    /// IK lock (<c>CKeyIK.Flags</c> bit 0).
    /// </summary>
    public static bool IsTargetKeyLocked(CameraTarget target, AnimStack stack, int keyIndex) =>
        stack.TrackFor(target) is { } track && keyIndex >= 0 && keyIndex < track.Keys.Count
        && track.Keys[keyIndex] is IKKey key && (key.Flags & 1u) != 0;

    /// <summary>
    /// A looker target cannot be
    /// dragged while the view looks through the camera it constrains (<c>_target</c> being the
    /// scene's current animated camera), because the drag would turn the very view being dragged
    /// in. <paramref name="lookingThrough"/> is the animated camera the view shows, if any.
    /// </summary>
    public static bool ForbidsTargetDrag(CameraTarget target, CameraAnimated? lookingThrough) =>
        lookingThrough != null && ReferenceEquals(target.Camera, lookingThrough);

    /// <summary>
    /// The scene's <c>Perspective</c> camera eye pose in world space, or identity. The camera itself
    /// is a child of the character, so its <c>LTM</c> is this pose in the character's space.
    /// </summary>
    private static Matrix4x4 WorldStartPose(Scene scene)
    {
        var perspective = scene.Cameras.FirstOrDefault(c => c.Name == "Perspective") ?? scene.Cameras.FirstOrDefault();
        if (perspective == null) return Matrix4x4.Identity;
        var world = perspective.ViewPose;
        return world == default ? Matrix4x4.Identity : world;
    }

    private static List<AktReference> Controllers(Character character)
    {
        if (character.Source["Controllers"] is List<AktReference> list) return list;
        var created = new List<AktReference>();
        character.Source.Set("Controllers", created);
        return created;
    }

    /// <summary><c>CCharacter.JointArray</c>, created when the scene has none (a brand-new scene).</summary>
    private static List<AktReference> Joints(Character character)
    {
        if (character.Source["JointArray"] is List<AktReference> list) return list;
        var created = new List<AktReference>();
        character.Source.Set("JointArray", created);
        return created;
    }

    /// <summary>
    /// Every pose of the character whose matrix list is parallel to <c>Controllers</c>. A pose may
    /// be referenced twice (e.g. <c>RefPose</c> also listed under the bind poses), so it is appended
    /// to once.
    /// </summary>
    private static IEnumerable<CharacterPose> Poses(Character character) =>
        new[] { character.RefPose, character.CurrentPose, character.RefPoseOld }
            .OfType<CharacterPose>()
            .Concat(character.BindPoses)
            .Distinct();

    /// <summary>
    /// Puts the camera's matrix at its controller slot. A pose list can be longer than the
    /// controller list in some files (stale slots), so it is inserted at the slot rather than
    /// appended when there is room; the pose matrices are indexed by <c>Controllers</c>.
    /// </summary>
    private static void InsertMatrix(CharacterPose pose, int slot, Matrix4x4 matrix)
    {
        var matrices = SceneValues.Items(pose.Source, "Matrices");
        if (slot < matrices.Count) matrices.Insert(slot, AktMath.ToArray(matrix));
        else matrices.Add(AktMath.ToArray(matrix));
    }
}

/// <summary>
/// The animated camera's hidden-joint and looker keys
/// The app authors the three tracks by hand and its keyed camera axis
/// misses the keyed target by 0.3-5 degrees; SloppyAnimatsu derives them exactly from the camera's key,
/// which is the documented choice this class pins down:
/// <list type="bullet">
/// <item>the hidden joint's key keeps the track's orbit distance (its keyed local -Z length, 40 by
/// default) and is written on every camera key;</item>
/// <item>the looker's key is the hidden joint's posed position in the character's space, i.e. the
/// camera key's translation plus its forward axis times the orbit distance, stored as the target's
/// key delta; its <c>RefArray</c> is [the camera's key pose, the hidden joint's key pose], with
/// <c>PoleAngle</c> and <c>RefPoleAngle</c> 0 and <c>Flags</c> cleared except the IK lock (bit 0).</item>
/// </list>
/// </summary>
public static class CameraRigKeys
{
    /// <summary>
    /// Derives the rig keys at <paramref name="keyIndex"/> of <paramref name="stack"/> from the
    /// camera's key there, creating the hidden-joint and looker tracks when the stack lacks them.
    /// Returns the tracks it wrote (to recompute their tangents).
    /// <para><paramref name="keepLookerChain"/> is the target-key write's rule: the looker key keeps
    /// its own rotation at the target's rest orientation and its <c>RefArray</c> as it was (the
    /// pre-edit chain pose stays there). <paramref name="mirrorEndKey"/>
    /// is false there too, because that edit leaves the loop end key alone while an ordinary key-0
    /// write mirrors into it.</para>
    /// </summary>
    public static AnimTrack[] Derive(AnimStack stack, int keyIndex, PoseEvaluator evaluator, CameraAnimated? camera,
        bool keepLookerChain = false, bool mirrorEndKey = true)
    {
        if (camera == null || CameraEditor.Rig(camera) is not { } rig) return [];
        if (stack.TrackFor(camera) is not { } cameraTrack || keyIndex < 0 || keyIndex >= cameraTrack.Keys.Count) return [];
        if (cameraTrack.Keys[keyIndex] is not TransformKey cameraKey) return [];
        int ci = evaluator.IndexOf(camera), hi = evaluator.IndexOf(rig.HideJoint), ti = evaluator.IndexOf(rig.Target);
        if (ci < 0 || hi < 0 || ti < 0) return [];

        var cameraLocal = PoseEvaluator.Compose(evaluator.RestTransforms[ci], cameraKey.Value);
        var hideTrack = stack.TrackFor(rig.HideJoint) ?? TrackFactory.Create(stack, rig.HideJoint);
        var lookerTrack = stack.TrackFor(rig.Target) ?? TrackFactory.Create(stack, rig.Target);
        if (keyIndex >= hideTrack.Keys.Count || keyIndex >= lookerTrack.Keys.Count) return [];
        if (hideTrack.Keys[keyIndex] is not TransformKey hideKey) return [];

        // The orbit distance: the hidden joint's own keyed distance from the camera, or the rest one.
        var hideLocal = PoseEvaluator.Compose(evaluator.RestTransforms[hi], hideKey.Value);
        float distance = -hideLocal.Translation.Z;
        if (distance <= 1e-4f) distance = CameraEditor.HideJointDistance;
        var hidePose = new Transform(new Vector3(0f, 0f, -distance), Quaternion.Identity, Vector3.One);

        // The look point: the camera's -Z axis at the orbit distance, in the character's space. The
        // target is a point, so its own rotation stays at its rest orientation (the app writes an
        // identity offset there, not a world-identity rotation).
        var position = (Matrix4x4.CreateTranslation(0f, 0f, -distance) * cameraLocal.ToMatrix()).Translation;
        var delta = PoseEvaluator.Decompose(evaluator.RestTransforms[ti],
            new Transform(position, Quaternion.Identity, Vector3.One));
        var value = new Transform(delta.Translation, Quaternion.Identity, delta.Scale);
        var chain = keepLookerChain && LookerChainLength(lookerTrack, keyIndex) > 0
            ? null
            : (IReadOnlyList<Transform>)[cameraLocal, hidePose];
        WriteLooker(lookerTrack, keyIndex, value, chain);
        if (mirrorEndKey && keyIndex == 0 && lookerTrack.Keys.Count > 1 && lookerTrack.Keys[^1] is IKKey loop)
            WriteLooker(lookerTrack, lookerTrack.Keys.Count - 1, value, chain);
        return [hideTrack, lookerTrack];
    }

    private static int LookerChainLength(AnimTrack track, int index) =>
        index >= 0 && index < track.Keys.Count && track.Keys[index] is IKKey key ? key.ChainTransforms.Count : 0;

    /// <summary>
    /// Turns <paramref name="cameraLocal"/> (the camera's own pose in its parent's space) so its -Z
    /// axis points from the eye at <paramref name="lookPoint"/> (the same space), keeping the eye,
    /// the scale and the roll: dragging the looker target moves the chain's IK goal, so the camera
    /// turns about its own eye to keep looking at it (the looker solve plus the chain solve,
    /// the editor's orbit rig of the target and the looker).
    /// The turn is the **short way about the axis through both forward directions** - a pure swing,
    /// which is what the app writes: the live target edit's key is a rotation about the camera's
    /// local Y, and this reproduces its quaternion to the last digit.
    /// Returns false, with <paramref name="aimed"/> unused, when the point is on the eye.
    /// <paramref name="distance"/> is how far the point lies along the camera's -Z in camera-space
    /// units - the value the hidden joint's key carries so its effector lands on the point.
    /// </summary>
    public static bool Aim(Matrix4x4 cameraLocal, Vector3 lookPoint, out Transform aimed, out float distance)
    {
        aimed = Transform.Identity;
        distance = 0f;
        var eye = cameraLocal.Translation;
        var delta = lookPoint - eye;
        float length = delta.Length();
        if (!float.IsFinite(length) || length <= 1e-4f) return false;
        var current = Transform.FromMatrixLoose(cameraLocal);
        var forward = -Vector3.Normalize(new Vector3(cameraLocal.M31, cameraLocal.M32, cameraLocal.M33));
        var target = delta / length;
        float cos = Math.Clamp(Vector3.Dot(forward, target), -1f, 1f);
        var axis = Vector3.Cross(forward, target);
        float sin = axis.Length();
        var rotation = current.Rotation;
        if (sin > 1e-6f)
        {
            // The swing's axis lies in the plane of the two forward directions, so no roll is added
            // about the old one: the key is the same swing expressed in the camera's frame.
            rotation = Quaternion.Multiply(
                Quaternion.CreateFromAxisAngle(axis / sin, MathF.Atan2(sin, cos)), rotation);
        }
        else if (cos < 0f)
        {
            // Turned 180 degrees: any perpendicular axis, chosen from the camera's own up.
            axis = Vector3.Cross(forward, Vector3.UnitY);
            if (axis.LengthSquared() < 1e-8f) axis = Vector3.Cross(forward, Vector3.UnitX);
            rotation = Quaternion.Multiply(
                Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI), rotation);
        }
        aimed = new Transform(eye, Quaternion.Normalize(rotation), current.Scale);
        // The point now lies on the aimed camera's -Z axis: its depth there is the orbit distance.
        if (!Matrix4x4.Invert(aimed.ToMatrix(), out var inverse))
        {
            distance = length;
            return true;
        }
        distance = -Vector3.Transform(lookPoint, inverse).Z;
        return distance > 1e-4f;
    }

    /// <summary>
    /// Fills every looker key's <c>RefArray</c> of <paramref name="stack"/> from the evaluated
    /// locals at the key's time, for a track that was just created.
    /// </summary>
    public static void FillLookerChains(AnimStack stack, PoseEvaluator evaluator)
    {
        if (stack.Animation is not { } animation) return;
        foreach (var track in stack.Tracks)
        {
            if (track.Target is not CameraTarget target || target.Chain is not { Count: >= 2 } chain) continue;
            int first = evaluator.IndexOf(chain[0]), second = evaluator.IndexOf(chain[1]);
            if (first < 0 || second < 0) continue;
            var times = stack.Keys;
            for (int i = 0; i < Math.Min(times.Count, track.Keys.Count); i++)
            {
                if (track.Keys[i] is not IKKey key) continue;
                var local = evaluator.LocalTransforms(animation, times[i].Time);
                key.InitChainTransforms([local[first], local[second]]);
            }
        }
    }

    private static void WriteLooker(AnimTrack track, int index, Transform value, IReadOnlyList<Transform>? chain)
    {
        if (index < 0 || index >= track.Keys.Count || track.Keys[index] is not IKKey key) return;
        key.Value = value;
        if (chain != null) key.InitChainTransforms(chain);
        // Only the IK lock (bit 0) survives: it is the user's right-click lock, and clearing it here
        // unlocked the target on the first step of a camera drag, so the rest of the drag carried
        // the target along.
        key.SetFlags(key.Flags & 1u);
        key.PoleAngle = 0f;
        key.Source.Set("RefPoleAngle", 0f);
    }
}

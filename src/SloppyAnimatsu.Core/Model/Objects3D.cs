using System.Numerics;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary><c>CObject3D</c>: an object with a transform.</summary>
public class Object3D : SceneObject
{
    protected internal Object3D(SceneModel model, AktObject source) : base(model, source) { }

    /// <summary>Bit field.</summary>
    public uint Flags
    {
        get => Source.Get<uint>("Flags");
        set => Source.Set("Flags", value);
    }

    /// <summary>Flags bit 0: shown in the viewport (cleared by Hide).</summary>
    public bool IsShown
    {
        get => (Flags & 1) != 0;
        set => Flags = value ? Flags | 1 : Flags & ~1u;
    }

    /// <summary>
    /// Whether the viewport draws the object: it and every 3D ancestor are shown. Hiding a parent
    /// hides what is under it without touching the children's own bits, so showing the parent
    /// again brings back exactly the children that were shown before.
    /// </summary>
    public bool IsShownInScene
    {
        get
        {
            for (Object3D? o = this; o != null; o = o.Parent3D)
                if (!o.IsShown) return false;
            return true;
        }
    }

    /// <summary>Flags bit 8: offered as a space by Change Space.</summary>
    public bool IsSpaceable
    {
        get => (Flags & 0x100) != 0;
        set => Flags = value ? Flags | 0x100 : Flags & ~0x100u;
    }

    /// <summary>Flags bit 9: cannot be picked in the viewport (on a character: none of its descendants can).</summary>
    public bool IsUnpickable
    {
        get => (Flags & 0x200) != 0;
        set => Flags = value ? Flags | 0x200 : Flags & ~0x200u;
    }

    /// <summary>
    /// <c>Flags &amp; 6</c>: the parent's own scale moves this object but does not scale or shear it
    /// </summary>
    public bool CompensatesParentScale
    {
        get => (Flags & 6) != 0;
        // Turning it on writes mode 1 (0x2), the mode the app gives the joints it creates; turning
        // it off clears both mode bits (data-model.md flag table).
        set
        {
            if (value == CompensatesParentScale) return;
            Flags = value ? (Flags & ~6u) | 2u : Flags & ~6u;
        }
    }

    /// <summary><c>LTM</c>: transform relative to the closest 3D ancestor.</summary>
    public Matrix4x4 LocalMatrix
    {
        get => AktMath.ToMatrix(Source.Get<float[]>("LTM"));
        set => Source.Set("LTM", AktMath.ToArray(value));
    }

    /// <summary>The controller that drives this object, if any.</summary>
    public Controller3D? Constraint => Ref<Controller3D>("Constraint");

    /// <summary>
    /// The closest ancestor with a transform. Folders (<c>CDummy</c>) and the scene have none and
    /// count as identity.
    /// </summary>
    public Object3D? Parent3D => Ancestor<Object3D>();

    /// <summary>World transform from the stored local matrices (the saved, unanimated state).</summary>
    public Matrix4x4 WorldMatrix => Parent3D is { } p
        ? WorldComposition.Compose(LocalMatrix, p.WorldMatrix, LocalScale(p.LocalMatrix), CompensatesParentScale)
        : LocalMatrix;

    /// <summary>The scale part of a local matrix (the lengths of its basis rows).</summary>
    public static Vector3 LocalScale(Matrix4x4 m) =>
        new(new Vector3(m.M11, m.M12, m.M13).Length(), new Vector3(m.M21, m.M22, m.M23).Length(), new Vector3(m.M31, m.M32, m.M33).Length());
}

/// <summary><c>CJoint</c>: a skeleton bone. Its transform is <see cref="Object3D.LocalMatrix"/>.</summary>
public sealed class Joint : Object3D
{
    internal Joint(SceneModel model, AktObject source) : base(model, source) { }

    public int SmoothMethod => Source.GetOr("SmoothMethod", 0);
}

/// <summary><c>CCameraBase</c>.</summary>
public class Camera : Object3D
{
    protected internal Camera(SceneModel model, AktObject source) : base(model, source) { }

    /// <summary>May hold garbage in 20.3 files.</summary>
    public int CameraType => Source.GetOr("CameraType", 0);
    /// <summary>Runtime camera number.</summary>
    public int CameraId => Source.GetOr("CameraId", 0);
    public byte RenderMode => Source.GetOr<byte>("RenderMode", 0);
    public uint RenderFlags => Source.GetOr("RenderFlags", 0u);
    /// <summary><c>_matrix</c>; its relation to <see cref="Object3D.LocalMatrix"/> is not documented yet.</summary>
    public Matrix4x4 CameraMatrix => AktMath.ToMatrix(Source.Get<float[]>("_matrix"));

    /// <summary>
    /// The camera pose the view is built from
    /// The app reads the global transform, but a fixed camera can store a zeroed
    /// <c>LTM</c> and its pose in <c>_matrix</c> instead (Resource3D's Photo camera), so a zero
    /// translation falls back to <c>_matrix</c>.
    /// </summary>
    public Matrix4x4 ViewPose
    {
        get
        {
            var ltm = WorldMatrix;
            if (ltm.Translation.LengthSquared() > 1e-8f) return ltm;
            var matrix = CameraMatrix;
            return matrix == default ? Matrix4x4.Identity : matrix;
        }
    }
    /// <summary>Orbit distance to the point the camera looks at.</summary>
    public float TargetDistance => Source.GetOr("_targetDistance", 0f);
}

/// <summary><c>CCamera</c>: perspective camera.</summary>
public class PerspectiveCamera : Camera
{
    internal PerspectiveCamera(SceneModel model, AktObject source) : base(model, source) { }

    public float AspectRatio => Source.GetOr("_ratio", 1f);
}

/// <summary><c>CCameraOrtho</c>: front/back/right/left/top/bottom views.</summary>
public sealed class OrthoCamera : Camera
{
    internal OrthoCamera(SceneModel model, AktObject source) : base(model, source) { }

    /// <summary>Zoom: world height shown, in centimetres.</summary>
    public float CentimetersInHeight
    {
        get => Source.GetOr("CentimetersInHeight", 0f);
        set => Source.Set("CentimetersInHeight", value);
    }

    /// <summary>Pan: the view-space point at the centre of the view.</summary>
    public Vector2 WorldOffset
    {
        get => new(Source.GetOr("WorldOffsetX", 0f), Source.GetOr("WorldOffsetY", 0f));
        set
        {
            Source.Set("WorldOffsetX", value.X);
            Source.Set("WorldOffsetY", value.Y);
        }
    }
}

/// <summary><c>CCharacter</c>: a skeleton with its rig, poses, animations and skinned meshes.</summary>
public sealed class Character : Object3D
{
    internal Character(SceneModel model, AktObject source) : base(model, source) { }

    /// <summary>Joints in skinning order; <see cref="VertexWeight.Bone"/> indexes this list.</summary>
    public IReadOnlyList<Joint> Joints => Refs<Joint>("JointArray");

    /// <summary>
    /// Everything that can be animated: the joints (same order as <see cref="Joints"/>), then
    /// IK and reverse-foot controllers and shoe locators. Pose matrices are indexed by this list.
    /// </summary>
    public IReadOnlyList<Object3D> Controllers => Refs<Object3D>("Controllers");

    /// <summary>Whether <see cref="Controllers"/> would return exactly <paramref name="list"/>, without building it.</summary>
    public bool ControllersMatch(IReadOnlyList<Object3D> list) => RefsMatch("Controllers", list);

    /// <summary>
    /// Paint &amp; Check's runtime pose, kept on the character (not in an evaluator) so a rebuilt
    /// scene pose, a context switch or a CHAR BANK row change keeps it; saved by
    /// <see cref="CheckPoseStore"/>. Read and written through <see cref="PoseEvaluator"/>.
    /// </summary>
    internal CheckPoseSlots? CheckPose { get; set; }

    /// <summary>Whether <see cref="CheckPose"/> was read from the scene file yet.</summary>
    internal bool CheckPoseRead { get; set; }

    public CharacterPose? RefPose
    {
        get => Ref<CharacterPose>("RefPose");
        set => SetRef("RefPose", value);
    }

    /// <summary>
    /// <c>CCharacter.AudioObject</c>: the audio anchor the character's audio stacks play through
    /// null when it has none.
    /// </summary>
    public AudioObject? AudioObject
    {
        get => Ref<AudioObject>("AudioObject");
        set => SetRef("AudioObject", value);
    }

    /// <summary>The rigging context's working pose; Bind Skin clears it.</summary>
    public CharacterPose? CurrentPose
    {
        get => Ref<CharacterPose>("CurrentPose");
        set => SetRef("CurrentPose", value);
    }
    public CharacterPose? RefPoseOld
    {
        get => Ref<CharacterPose>("RefPoseOld");
        set => SetRef("RefPoseOld", value);
    }

    public Animation? CurrentAnimation
    {
        get => Ref<Animation>("CurrentAnim");
        set => SetRef("CurrentAnim", value);
    }

    /// <summary>The Anim Bank root folder, <c>__Anims</c>.</summary>
    public Folder? AnimBank => Child("__Anims") as Folder;

    /// <summary>The user's animations (everything in the Anim Bank, in tree order).</summary>
    public IEnumerable<Animation> Animations => AnimBank?.Descendants<Animation>() ?? [];

    /// <summary>The hidden animation holding the rig/reference pose, <c>__RigAnim</c>.</summary>
    public Animation? RigAnimation => Child("__RigAnim") as Animation;

    public IEnumerable<CharacterPose> BindPoses => (Child("__BindPoses") as Folder)?.Descendants<CharacterPose>() ?? [];

    /// <summary>Meshes skinned to this character (<see cref="Mesh.BoundCharacter"/>; they may live
    /// outside the character). An unbound mesh that still remembers this character's bind pose is not one.</summary>
    public IEnumerable<Mesh> Meshes => Model.Scene.Meshes.Where(m => m.BoundCharacter == this);

    public IEnumerable<ControllerIK> IKControllers => Controllers.OfType<ControllerIK>();

    /// <summary>
    /// <c>RigidModified</c>: the user edited the rigid skin weights. Bind Skin clears it; the
    /// AutoRigger asks which version to keep when it and <see cref="SmoothModified"/> are both set
    /// </summary>
    public bool RigidModified
    {
        get => Flag("RigidModified");
        set { if (Source.Has("RigidModified")) Source.Set("RigidModified", value); }
    }

    /// <summary><c>SmoothModified</c>: the user edited the smooth skin weights.</summary>
    public bool SmoothModified
    {
        get => Flag("SmoothModified");
        set { if (Source.Has("SmoothModified")) Source.Set("SmoothModified", value); }
    }

    /// <summary>The animated cameras parented to this character.</summary>
    public IEnumerable<CameraAnimated> AnimatedCameras => Controllers.OfType<CameraAnimated>();

    /// <summary>
    /// <c>HiddenVerticesCounter</c>: incremented once for every hide/show edit of the character's
    /// meshes, as hiding vertices does (a change stamp, not a vertex count).
    /// </summary>
    public uint HiddenVerticesCounter
    {
        get => Source.GetOr("HiddenVerticesCounter", 0u);
        set => Source.Set("HiddenVerticesCounter", value);
    }
}

/// <summary><c>CController3D</c>: an animatable handle that is not a joint.</summary>
public class Controller3D : Object3D
{
    protected internal Controller3D(SceneModel model, AktObject source) : base(model, source) { }

    /// <summary>
    /// <c>TargetRenderSizeRatio</c>: the controller graphic's size as a multiple of
    /// <c>CScene.TargetRenderSize</c> (the Properties window's "Target Size Multiplier").
    /// Setting it needs the member in the object (<see cref="HasTargetRenderSizeRatio"/>).
    /// </summary>
    public float TargetRenderSizeRatio
    {
        get => Source.GetOr("TargetRenderSizeRatio", 1f);
        set => Source.Set("TargetRenderSizeRatio", value);
    }

    public bool HasTargetRenderSizeRatio => Source.Has("TargetRenderSizeRatio");
}

/// <summary><c>CControllerIK</c>: an IK effector for a joint chain.</summary>
public class ControllerIK : Controller3D
{
    protected internal ControllerIK(SceneModel model, AktObject source) : base(model, source) { }

    /// <summary><c>JointsString</c>: the chain, root first (e.g. shoulder, elbow, wrist).</summary>
    public IReadOnlyList<Object3D> Chain => Refs<Object3D>("JointsString");

    /// <summary>Ancestors above the chain.</summary>
    public IReadOnlyList<Object3D> ChainParents => Refs<Object3D>("Parents");

    /// <summary>0/1/2 = X/Y/Z: the normal of the drawn ring, or the plane of a reverse foot's sole outline.</summary>
    public byte DecoratorAxis => Source.GetOr<byte>("DecoratorAxis", 0);
}

/// <summary><c>CControllerReverseFoot</c>: an IK leg with a 7-pivot shoe.</summary>
public sealed class ControllerReverseFoot : ControllerIK
{
    internal ControllerReverseFoot(SceneModel model, AktObject source) : base(model, source) { }

    public IReadOnlyList<ReverseFootLocator> Shoe => Refs<ReverseFootLocator>("Shoe");
    public Vector4 RollAxisToes => AktMath.ToVector4(Source.Get<float[]>("RollAxisToes"));
    public Vector4 RollAxisPlant => AktMath.ToVector4(Source.Get<float[]>("RollAxisPlant"));
}

/// <summary><c>CReverseFootLocator_VK</c>: one pivot of a reverse-foot shoe.</summary>
public sealed class ReverseFootLocator : Object3D
{
    internal ReverseFootLocator(SceneModel model, AktObject source) : base(model, source) { }

    public uint Type => Source.Get<uint>("Type");
    public uint Category => Source.Get<uint>("Category");
    /// <summary>Position in the shoe, 0..6.</summary>
    public uint Index => Source.Get<uint>("Index");
    public ControllerReverseFoot? Controller => Ref<ControllerReverseFoot>("Controller");

    /// <summary>
    /// One of the four sole pivots (index 0..3: SoleHeel, SoleLeft, SoleRight, SoleToes), the only
    /// locators the app shows and lets the user move; the three skeleton pivots (index 4..6) are
    /// hidden.
    /// </summary>
    public bool IsSolePivot => Index < 4;
}

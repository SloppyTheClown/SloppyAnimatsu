using System.Numerics;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary><c>CScene</c>: the root of a scene file.</summary>
public sealed class Scene : SceneObject
{
    internal Scene(SceneModel model, AktObject source) : base(model, source) { }

    /// <summary>Frames per second; key times are integer frames at this rate.</summary>
    public uint FrameRate
    {
        get => Source.Get<uint>("FrameRate");
        set => Source.Set("FrameRate", value);
    }

    public int FrameRateIndex => Source.GetOr("FrameRateIndex", 0);

    /// <summary>Current frame. Null in files older than 20.3, which do not store it.</summary>
    public int? Time
    {
        get => Source["Time"] is int t ? t : null;
        set { if (value is int t) Source.Set("Time", t); }
    }

    /// <summary>
    /// Base display size of IK and reverse-foot controllers, scaled by each controller's
    /// <c>TargetRenderSizeRatio</c>. 8 when absent, as in the samples.
    /// </summary>
    public float TargetRenderSize => Source.GetOr("TargetRenderSize", 8f);

    /// <summary>
    /// The Decorators page's joint size factor (standard density area). A missing or ~0 value falls
    /// back to 5, the value FBX-imported scenes carry; the
    /// own new scene writes 2.
    /// </summary>
    public float JointRenderSize => Factor("JointRenderSize", DefaultJointRenderSize);

    /// <summary>The joint size factor a scene without one is drawn with.</summary>
    public const float DefaultJointRenderSize = 5f;

    /// <summary>The joint size factor of the high density area (Light mode only); not drawn yet.</summary>
    public float JointRenderSizeHighDensity => Factor("JointRenderSizeHighDensity", 6f);

    /// <summary>The pivot size factor; not drawn yet.</summary>
    public float PivotRenderSize => Factor("PivotRenderSize", 3f);

    /// <summary>The pivot size factor of unselected joints; not drawn yet.</summary>
    public float UnselectedPivotRenderSize => Factor("UnselectedPivotRenderSize", 0.4f);

    private float Factor(string member, float fallback)
    {
        float value = Source.GetOr(member, fallback);
        return float.IsFinite(value) && MathF.Abs(value) >= 1e-6f ? value : fallback;
    }

    /// <summary>
    /// The scene's size unit: 0 mm, 1 cm, 2 dm, 3 m,
    /// 4 inch, 5 foot, 6 yard; -1 when the scene does not store one.
    /// </summary>
    public int SizeUnit => Source.Has("SizeUnit") ? Source.GetOr("SizeUnit", -1) : -1;

    /// <summary>The Unit &amp; grid page's grid size (0 when the scene does not store one).</summary>
    public uint GridSize => Source.GetOr("GridSize", 0u);

    /// <summary>The Unit &amp; grid page's subdivisions per unit (0 when the scene does not store one).</summary>
    public uint UnitSubdivision => Source.GetOr("UnitSubdivision", 0u);

    /// <summary>One <see cref="SizeUnit"/> in scene units (centimetres), or 0 for an unknown unit.</summary>
    public static float UnitLength(int sizeUnit) => sizeUnit switch
    {
        0 => 0.1f, 1 => 1f, 2 => 10f, 3 => 100f, 4 => 2.54f, 5 => 30.48f, 6 => 91.44f, _ => 0f,
    };

    /// <summary>0 = rigging, 1 = animation.</summary>
    public byte InterfaceContextIndex => Source.GetOr<byte>("InterfaceContextIndex", 0);

    /// <summary>
    /// The saved interface context
    /// A file without the member (before 20.1) keeps the value it has.
    /// </summary>
    public InterfaceContext Context
    {
        get => InterfaceContextIndex == 1 ? InterfaceContext.Animation : InterfaceContext.Rigging;
        set
        {
            if (Source.Has("InterfaceContextIndex")) Source.Set("InterfaceContextIndex", (byte)value);
        }
    }

    public Character? CurrentCharacter
    {
        get => Ref<Character>("CurrentCharacter");
        set => SetRef("CurrentCharacter", value);
    }

    public IReadOnlyList<Camera> Cameras => Refs<Camera>("CameraList");

    /// <summary>
    /// The position of <paramref name="camera"/> in <c>CameraList</c>, or -1. A view's camera slot
    /// (<c>ViewCamIndices</c>) is such a position, and the app also stores an animated camera there
    /// once a view looks through it.
    /// </summary>
    public int IndexOfCamera(Camera camera)
    {
        if (Source["CameraList"] is not List<AktReference> list) return -1;
        for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i].Target, camera.Source)) return i;
        return -1;
    }

    /// <summary>
    /// The position of <paramref name="camera"/> in <c>CameraList</c>, appending it when absent so a
    /// view's camera slot can name it. The app does the same when a view is set to an animated
    /// camera: the camera is not in the list at creation, only once it is looked through.
    /// </summary>
    public int EnsureCameraInList(Camera camera)
    {
        int index = IndexOfCamera(camera);
        if (index >= 0) return index;
        if (Source["CameraList"] is not List<AktReference> list)
            throw new InvalidOperationException("This scene has no CScene.CameraList; a view cannot store this camera.");
        list.Add(new AktReference(camera.Source));
        return list.Count - 1;
    }

    /// <summary>
    /// Drops <paramref name="camera"/> from <c>CameraList</c> and turns every stored view slot that
    /// looked through it back to Perspective (index 0), which is what the
    /// scene does when a camera object is deleted: it resets the view's camera index to 0 for the
    /// layout and drops the reference. Returns whether
    /// the camera was listed (an animated camera only joins the list once a view looks through it).
    /// </summary>
    public bool RemoveCamera(Camera camera)
    {
        int index = IndexOfCamera(camera);
        if (index < 0 || Source["CameraList"] is not List<AktReference> list) return false;
        list.RemoveAt(index);
        if (Source["ViewCamIndices"] is List<object?> slots)
            for (int i = 0; i < slots.Count; i++)
                if (slots[i] is int slot && slot == index) slots[i] = 0;
        return true;
    }

    public IEnumerable<Character> Characters => Descendants<Character>();
    public IEnumerable<Mesh> Meshes => Descendants<Mesh>();
    public IEnumerable<Material> Materials => Descendants<Material>();
    public IEnumerable<Texture> Textures => Descendants<Texture>();

    /// <summary>The characters' audio anchors.</summary>
    public IEnumerable<AudioObject> AudioObjects => Descendants<AudioObject>();

    /// <summary>The scene's audio files, wherever they sit in the tree.</summary>
    public IEnumerable<AudioEntry> AudioEntries => Descendants<AudioEntry>();

    /// <summary>Reverse-foot angle limits in degrees (roll back/pre/toes/full, twist, bank).</summary>
    public float GetAngleLimit(string member) => Source.Get<float>(member);
}

/// <summary><c>CDummy</c>: a plain folder (the Anim Bank folders, <c>__Materials</c>, ...).</summary>
public sealed class Folder : SceneObject
{
    internal Folder(SceneModel model, AktObject source) : base(model, source) { }
}

/// <summary>A material channel: a colour or factor, optionally replaced by a texture.</summary>
public readonly record struct MaterialColor(Vector4 Value, Texture? Texture);
public readonly record struct MaterialScalar(float Value, Texture? Texture);

/// <summary><c>CMaterial</c>: the FBX material model.</summary>
public sealed partial class Material : SceneObject
{
    internal Material(SceneModel model, AktObject source) : base(model, source) { }

    public static readonly string[] ColorChannels =
        ["Ambient", "Diffuse", "Specular", "Emissive", "Transparency", "NormalMap", "Bump", "Displacement", "VectorDisplacement", "Reflection"];

    public static readonly string[] ScalarChannels =
        ["AmbientFactor", "DiffuseFactor", "SpecularFactor", "EmissiveFactor", "TransparencyFactor", "BumpFactor",
         "DisplacementFactor", "VectorDisplacementFactor", "ReflectionFactor", "Shininess"];

    public MaterialColor GetColor(string channel)
    {
        var c = Source.Get<AktStruct>(channel);
        return new(AktMath.ToVector4(c.Get<float[]>("Value")), Model.Wrap(c["Texture"] as AktReference) as Texture);
    }

    public MaterialScalar GetScalar(string channel)
    {
        var c = Source.Get<AktStruct>(channel);
        return new(c.Get<float>("Value"), Model.Wrap(c["Texture"] as AktReference) as Texture);
    }

    public MaterialColor Diffuse => GetColor("Diffuse");
}

/// <summary><c>CTexture</c>: an embedded image file. <see cref="SceneObject.Name"/> is its file name.</summary>
public sealed class Texture : SceneObject
{
    internal Texture(SceneModel model, AktObject source) : base(model, source) { }

    /// <summary>The complete image file (e.g. a PNG).</summary>
    public byte[] Data => Source.GetOr<byte[]>("Data", []);
}

/// <summary>
/// <c>CCharacterPose</c>: one matrix per entry of the owning character's
/// <see cref="Character.Controllers"/>.
/// </summary>
public sealed class CharacterPose : SceneObject
{
    internal CharacterPose(SceneModel model, AktObject source) : base(model, source) { }

    public Matrix4x4[] Matrices =>
        SceneValues.Items(Source, "Matrices").Select(m => AktMath.ToMatrix((float[])m!)).ToArray();

    /// <summary>Number of matrix slots.</summary>
    public int Count => SceneValues.Items(Source, "Matrices").Count;

    public Matrix4x4 Matrix(int index) => AktMath.ToMatrix((float[])SceneValues.Items(Source, "Matrices")[index]!);

    /// <summary>Overwrites slot <paramref name="index"/>.</summary>
    public void SetMatrix(int index, Matrix4x4 matrix) => SceneValues.Items(Source, "Matrices")[index] = AktMath.ToArray(matrix);

    /// <summary>Copies every slot of <paramref name="other"/> that this pose also has.</summary>
    public void CopyMatricesFrom(CharacterPose other)
    {
        var from = SceneValues.Items(other.Source, "Matrices");
        var to = SceneValues.Items(Source, "Matrices");
        for (int i = 0; i < Math.Min(from.Count, to.Count); i++) to[i] = ((float[])from[i]!).Clone();
    }

    public Character? Character => Ancestor<Character>();
}

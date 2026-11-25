namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// What kind of row a 3D object takes in the hierarchy panel
/// The app draws one of the pictures shipped in
/// <c>Resource/Tree_*.png</c> beside each row, so the class - and, for a mesh, its skinned and
/// locked state - decides what the row looks like: <c>Tree_Character</c>,
/// <c>Tree_Unpickable_Character</c>, <c>Tree_Joint</c>, <c>Tree_Mesh</c>,
/// <c>Tree_Unpickable_Mesh</c>, <c>Tree_Skinned_Mesh</c>, <c>Tree_L_Skinned_Mesh</c>,
/// <c>Tree_IK</c>, <c>Tree_RF</c>, <c>Tree_RFLocator</c>, <c>Tree_Camera</c> and
/// <c>Tree_BShape</c>/<c>Tree_BShape_Connected</c>, plus <c>Tree_Camera_Target</c>,
/// <c>Tree_Looker</c>, <c>Tree_Hotpoint</c>, <c>Tree_Dummy</c> and <c>Tree_Unk_Node</c> for the
/// classes SloppyAnimatsu has no model class for. SloppyAnimatsu draws its own glyph for each
/// kind; the names above only say which classes get a row of their own.
/// </summary>
public enum OutlinerKind
{
    /// <summary>No picture of its own: <c>Tree_Unk_Node</c> (a looker, a hot point, a plain controller, ...).</summary>
    Object,
    Character,
    /// <summary>A character that is unpickable (<c>Flags</c> bit 9): <c>Tree_Unpickable_Character</c>.</summary>
    UnpickableCharacter,
    Joint,
    Mesh,
    /// <summary>A mesh that is unpickable (<c>Flags</c> bit 9): <c>Tree_Unpickable_Mesh</c>.</summary>
    UnpickableMesh,
    SkinnedMesh,
    /// <summary>A skinned mesh the panel's lock holds: <c>Tree_L_Skinned_Mesh</c>.</summary>
    LockedSkinnedMesh,
    IkController,
    ReverseFootController,
    /// <summary>One of a reverse foot's seven shoe locators.</summary>
    ShoeLocator,
    /// <summary>A perspective, animated or orthographic camera.</summary>
    Camera,
    Blendshape,
    /// <summary>A blendshape's simple or face controller.</summary>
    BlendshapeController,
    /// <summary>An animated camera's look-at target: <c>Tree_Camera_Target</c>.</summary>
    CameraTarget,
    /// <summary>A sound attached to the character (<c>CAudioObject</c>).</summary>
    Audio,
}

/// <summary>
/// One row's decoration: the kind it is drawn as, the glyph to draw (path data in a 12 x 12 box),
/// the colour to draw it in, and whether the row is dimmed because the object is hidden.
/// </summary>
public readonly record struct OutlinerRowStyle(OutlinerKind Kind, string Glyph, uint Color, bool Dimmed)
{
    /// <summary>The colour's red, green, blue and alpha bytes, in that order.</summary>
    public (byte R, byte G, byte B, byte A) Channels =>
        ((byte)(Color >> 16), (byte)(Color >> 8), (byte)Color, (byte)(Color >> 24));
}

/// <summary>
/// The hierarchy panel's pictures and colours, and the rule that dims a hidden row
/// The rule is the
/// as follows: a row whose object has <c>Flags</c>
/// bit 0 clear and that is not selected is drawn in the base colour mixed half-and-half with the
/// theme's disabled colour, its alpha the sum of the two alphas (clamped). A selected row keeps the
/// plain colour, so a hidden object that is selected does not look hidden.
/// </summary>
public static class OutlinerStyle
{
    /// <summary>The theme's disabled colour, which a hidden row's colour is mixed with.</summary>
    public const uint DisabledColor = 0xFF7F7F7F;

    /// <summary>The glyphs are drawn in a 12 x 12 box.</summary>
    public const float Box = 12f;

    /// <summary>The kind of row <paramref name="o"/> takes. Unpickable wins over the shape it covers.</summary>
    public static OutlinerKind KindOf(Object3D o) => o switch
    {
        Character { IsUnpickable: true } => OutlinerKind.UnpickableCharacter,
        Character => OutlinerKind.Character,
        Joint => OutlinerKind.Joint,
        // The kind stays the class's: a mesh keeps its picture when it is hidden, dimmed only.
        Mesh { IsUnpickable: true } => OutlinerKind.UnpickableMesh,
        Mesh { Skinned: true, SkinnedMeshIsLocked: true } => OutlinerKind.LockedSkinnedMesh,
        Mesh { Skinned: true } => OutlinerKind.SkinnedMesh,
        Mesh => OutlinerKind.Mesh,
        ControllerReverseFoot => OutlinerKind.ReverseFootController,
        ControllerIK => OutlinerKind.IkController,
        ReverseFootLocator => OutlinerKind.ShoeLocator,
        Camera => OutlinerKind.Camera,
        CameraTarget => OutlinerKind.CameraTarget,
        AudioObject => OutlinerKind.Audio,
        Blendshape => OutlinerKind.Blendshape,
        BlendshapeController => OutlinerKind.BlendshapeController,
        _ => OutlinerKind.Object,
    };

    /// <summary>
    /// A bound mesh: a triangle (5,3.5)-(9.5,11.2)-(0.5,11.2), a joint ball of radius 1.9 at
    /// (9.6,2.6) and a bone 0.9 wide from the ball's edge to the triangle's right side. The parts
    /// barely touch, so the default even-odd fill leaves no seams and a cut-out still works.
    /// </summary>
    private const string SkinnedTriangle =
        "M5,3.5 L9.5,11.2 L0.5,11.2 Z " +
        "M7.7,2.6 A1.9,1.9 0 1 1 11.5,2.6 A1.9,1.9 0 1 1 7.7,2.6 Z " +
        "M8.66,4.31 L8.0,3.71 L6.27,5.6 L6.93,6.2 Z";

    /// <summary>The glyph a kind is drawn with: path data in a <see cref="Box"/>-square box.</summary>
    public static string GlyphOf(OutlinerKind kind) => kind switch
    {
        // A frame: an object with no picture of its own.
        OutlinerKind.Object => "M2,2 H10 V10 H2 Z M4,4 V8 H8 V4 Z",
        // A person: a head over a body.
        OutlinerKind.Character => "M1.5,5 A3.2,3.2 0 0 1 7.9,5 A3.2,3.2 0 0 1 1.5,5 Z M3.2,6.2 H6.2 L7.4,11.5 H2 Z",
        OutlinerKind.UnpickableCharacter =>
            "M1.5,5 A3.2,3.2 0 0 1 7.9,5 A3.2,3.2 0 0 1 1.5,5 Z M3.2,6.2 H6.2 L7.4,11.5 H2 Z M0.8,11.2 L11.2,0.8 L12,1.6 L1.6,12 Z",
        // A bone: the joint's ball with the bone tapering away from it (non-zero fill, one outline).
        OutlinerKind.Joint => "F1 M1,3.5 A2.5,2.5 0 1 1 6,3.5 A2.5,2.5 0 1 1 1,3.5 Z M5.3,1.7 L11.2,11.2 L1.7,5.3 Z",
        // A triangle: a mesh.
        OutlinerKind.Mesh => "M6,1.5 L11.2,10.5 L0.8,10.5 Z",
        OutlinerKind.UnpickableMesh => "M6,1.5 L11.2,10.5 L0.8,10.5 Z M0.8,11.2 L11.2,0.8 L12,1.6 L1.6,12 Z",
        // Bound (skinned): the mesh triangle, moved down-left, tied by a short bone to a joint ball
        // at the top right - the same shape family as an unbound mesh, told apart at a glance.
        OutlinerKind.SkinnedMesh => SkinnedTriangle,
        // The bound glyph with a padlock body cut out of the triangle: locked.
        OutlinerKind.LockedSkinnedMesh => SkinnedTriangle + " M3.7,8.1 H6.1 V10.3 H3.7 Z",
        // A hand (four fingers, a thumb and the palm): an IK controller, the hand of an arm chain.
        OutlinerKind.IkController =>
            "M3,6 H9.5 V9 Q9.5,11.5 7,11.5 H5 Q3,11.5 3,9.5 Z M3,2.5 H4.3 V6 H3 Z M4.8,1.5 H6.1 V6 H4.8 Z " +
            "M6.6,1.8 H7.9 V6 H6.6 Z M8.4,3 H9.5 V6 H8.4 Z M9.5,7.4 L11.2,5.6 L12,6.4 L9.5,9.2 Z",
        // A boot: a reverse foot, the IK of a leg.
        OutlinerKind.ReverseFootController => "M2,1 H5.6 V6.8 L10.2,8.2 Q11.6,8.7 11.6,10 V11.4 H2 Z",
        // A small block: one pivot of the shoe.
        OutlinerKind.ShoeLocator => "M3,3 H9 V9 H3 Z M4.4,4.4 V7.6 H7.6 V4.4 Z",
        // A film camera: a body, a lens hood on its right and two reels on top (the earlier body
        // and cone read as a loudspeaker).
        OutlinerKind.Camera =>
            "M0.6,5 H7.6 V11 H0.6 Z M7.6,7 L11.6,5.2 V10.8 L7.6,9 Z " +
            "M0.8,2.6 A1.7,1.7 0 1 1 4.2,2.6 A1.7,1.7 0 1 1 0.8,2.6 Z M4.4,2.6 A1.7,1.7 0 1 1 7.8,2.6 A1.7,1.7 0 1 1 4.4,2.6 Z",
        // An arrow: the point an animated camera aims at.
        OutlinerKind.CameraTarget =>
            "M1,4.6 H6.4 V1.8 L11.2,6 L6.4,10.2 V7.4 H1 Z",
        // A loudspeaker: a sound.
        OutlinerKind.Audio => "M1,4 H3.8 L7.6,1 V11 L3.8,8 H1 Z M9,4 H10.3 V8 H9 Z",
        // A lens: a blendshape target.
        OutlinerKind.Blendshape => "M0.8,6 Q6,10.6 11.2,6 Q6,1.4 0.8,6 Z",
        // The lens with a ring: a blendshape controller.
        OutlinerKind.BlendshapeController =>
            "M0.8,6 Q6,10.6 11.2,6 Q6,1.4 0.8,6 Z M4.9,5 A1.7,1.7 0 0 1 8.3,5 A1.7,1.7 0 0 1 4.9,5 Z",
        _ => "M2,2 H10 V10 H2 Z M4,4 V8 H8 V4 Z",
    };

    /// <summary>
    /// The colour a kind is drawn in, from SloppyAnimatsu's own palette.
    /// The colours only have to tell the kinds apart on the dark panel.
    /// </summary>
    public static uint ColorOf(OutlinerKind kind) => kind switch
    {
        OutlinerKind.Character => 0xFFE6C05C,
        OutlinerKind.UnpickableCharacter => 0xFF8A7440,
        OutlinerKind.Joint => 0xFF7FB2E5,
        OutlinerKind.Mesh => 0xFF8FA3B8,
        OutlinerKind.UnpickableMesh => 0xFF5A6674,
        OutlinerKind.SkinnedMesh => 0xFF64C79A,
        OutlinerKind.LockedSkinnedMesh => 0xFF3F8A6B,
        OutlinerKind.IkController => 0xFFE58FBF,
        OutlinerKind.ReverseFootController => 0xFFE59A5C,
        OutlinerKind.ShoeLocator => 0xFFC9A97A,
        OutlinerKind.Camera => 0xFF4DA6FF,
        OutlinerKind.Blendshape => 0xFFB9A3E3,
        OutlinerKind.BlendshapeController => 0xFF8E7BC0,
        OutlinerKind.CameraTarget => 0xFF3D8BF0,
        OutlinerKind.Audio => 0xFFB5D27A,
        _ => 0xFF9E9E9E,
    };

    /// <summary>The kind's name as the panel's tooltip says it.</summary>
    public static string Describe(OutlinerKind kind) => kind switch
    {
        OutlinerKind.Character => "Character",
        OutlinerKind.UnpickableCharacter => "Character (unpickable)",
        OutlinerKind.Joint => "Bone",
        OutlinerKind.Mesh => "Mesh",
        OutlinerKind.UnpickableMesh => "Mesh (unpickable)",
        OutlinerKind.SkinnedMesh => "Skin (skinned mesh)",
        OutlinerKind.LockedSkinnedMesh => "Skin (skinned mesh, locked)",
        OutlinerKind.IkController => "IK hand (IK controller)",
        OutlinerKind.ReverseFootController => "IK foot (reverse foot)",
        OutlinerKind.ShoeLocator => "Foot pivot (shoe locator)",
        OutlinerKind.Camera => "Camera",
        OutlinerKind.CameraTarget => "Camera target",
        OutlinerKind.Blendshape => "Blendshape",
        OutlinerKind.BlendshapeController => "Blendshape controller",
        OutlinerKind.Audio => "Sound",
        _ => "Object",
    };

    /// <summary>A row's decoration; <paramref name="selected"/> keeps a hidden row from being dimmed.</summary>
    public static OutlinerRowStyle For(Object3D o, bool selected = false)
    {
        var kind = KindOf(o);
        uint color = ColorOf(kind);
        // Dimmed while the viewport does not draw it: its own bit or a hidden ancestor.
        bool dimmed = !o.IsShownInScene && !selected;
        return new OutlinerRowStyle(kind, GlyphOf(kind), dimmed ? Dim(color) : color, dimmed);
    }

    /// <summary>A colour mixed half-and-half with <see cref="DisabledColor"/>, as the dimmed row is.</summary>
    public static uint Dim(uint color) => Dim(color, DisabledColor);

    /// <summary>
    /// <paramref name="color"/> mixed half-and-half with <paramref name="disabled"/>: each channel is
    /// <c>disabled * 0x80 / 0xFF + color * 0x7F / 0xFF</c>, clamped to 0xFF, and the alpha is the sum
    /// of the two alphas, clamped (the integer arithmetic, not a float blend).
    /// </summary>
    public static uint Dim(uint color, uint disabled)
    {
        static uint Mix(uint a, uint b) => Math.Min(0xFFu, a * 0x80 / 0xFF + b * 0x7F / 0xFF);
        uint r = Mix((disabled >> 16) & 0xFF, (color >> 16) & 0xFF);
        uint g = Mix((disabled >> 8) & 0xFF, (color >> 8) & 0xFF);
        uint b = Mix(disabled & 0xFF, color & 0xFF);
        uint a = Math.Min(0xFFu, ((disabled >> 24) & 0xFF) + ((color >> 24) & 0xFF));
        return (a << 24) | (r << 16) | (g << 8) | b;
    }
}

using System.Numerics;

namespace SloppyAnimatsu.Rendering;

/// <summary>
/// The parts of the Spinner window: <see cref="Frame"/> is its round body outside the disc (dragging
/// it moves the window), <see cref="Space"/> the transform-space button beside it.
/// </summary>
public enum SpinnerPart { None, Axis, Uniform, Mode, Frame, Space, Snap }

/// <summary>The frame a Spinner edit and the gizmo handles work in: the controller's own (Local), the world's, or the view's (Screen).</summary>
public enum TransformSpace { Local, World, Screen }

/// <summary>What lies under a point of the Spinner dial: an axis sector, the centre circle, a mode button, the frame or the space button.</summary>
public readonly record struct SpinnerHit(SpinnerPart Part, int Axis = -1, GizmoMode Mode = GizmoMode.Rotate);

/// <summary>
/// The Spinner window's layout and drag rules, apart from any toolkit: a round floating window
/// holding a disc of three coloured sectors (X at the top, Y bottom-left, Z bottom-right) that fills
/// most of it, three mode buttons (translate, rotate, scale) leaning round the top just outside the
/// disc's edge, and a small round transform-space button in the same ring on the right. Everything
/// stays inside the round rim. The disc has a centre circle in scale mode (uniform scale) and, for
/// an IK selection, in rotate mode (the chain's swivel). Dragging in a sector does what dragging the
/// same-coloured gizmo handle does; the amount is the angle swept around the dial's centre, clockwise
/// positive. The window's rim moves it. The arrangement follows the Spinner; the drag amounts
/// are SloppyAnimatsu's own.
/// </summary>
public static class SpinnerDial
{
    /// <summary>The round frame's radius: the window is the frame plus a small margin for its rim.</summary>
    public const float FrameRadius = 100;

    /// <summary>The sector disc's radius: most of the frame, stopping short of the button ring.</summary>
    public const float DiscRadius = 70;

    /// <summary>The radius the mode buttons and the space button sit on, just outside the disc's edge.</summary>
    public const float ButtonRing = 85;

    /// <summary>The window's size in DIPs: the frame and its rim (the buttons are inside it).</summary>
    public const float Width = 2 * FrameRadius + 4, Height = 2 * FrameRadius + 4;

    /// <summary>How far the buttons lean round the ring between one another.</summary>
    public const float ButtonStep = 23 * MathF.PI / 180;

    public const float ButtonWidth = 30, ButtonHeight = 22;

    /// <summary>The space button's radius; it sits in the button ring on the right.</summary>
    public const float SpaceRadius = 12;

    /// <summary>The centre of the round frame, which is the disc's centre too: the disc is centred in it.</summary>
    public static Vector2 FrameCenter(Vector2 size) => new(FrameRadius + 2, size.Y / 2);

    /// <summary>The centre of the transform-space button: in the button ring, on the right.</summary>
    public static Vector2 SpaceCenter(Vector2 size) => FrameCenter(size) + new Vector2(ButtonRing, 0);

    /// <summary>
    /// The centre of the snap button: in the button ring on the
    /// left, opposite the space button, the same size. It is there only while an IK or reverse-foot
    /// handle is selected in an animation, and puts that handle back on the end of its chain
    /// </summary>
    public static Vector2 SnapCenter(Vector2 size) => FrameCenter(size) - new Vector2(ButtonRing, 0);

    /// <summary>The sector ring and the uniform circle, as fractions of <see cref="Radius"/>.</summary>
    public const float OuterRadius = 0.95f, InnerRadius = 0.40f, UniformRadius = 0.32f;

    /// <summary>Half the angle a sector spans; the three sectors leave a small gap between them.</summary>
    public const float SectorHalfAngle = MathF.PI / 3 - 0.06f;

    /// <summary>The mode buttons, left to right.</summary>
    public static readonly GizmoMode[] Modes = [GizmoMode.Translate, GizmoMode.Rotate, GizmoMode.Scale];

    /// <summary>The centre of the sector disc, the frame's centre: the disc takes up most of the window.</summary>
    public static Vector2 Center(Vector2 size) => FrameCenter(size);

    /// <summary>The sector disc's radius.</summary>
    public static float Radius(Vector2 size) => DiscRadius;

    /// <summary>
    /// The screen angle of mode button <paramref name="index"/>'s centre (y down): the middle one at
    /// the top, the other two leaning round the ring either side of it.
    /// </summary>
    public static float ModeButtonAngle(int index) => -MathF.PI / 2 + (index - 1) * ButtonStep;

    /// <summary>The centre of mode button <paramref name="index"/>, on the button ring.</summary>
    public static Vector2 ModeButtonCenter(Vector2 size, int index)
    {
        float angle = ModeButtonAngle(index);
        return FrameCenter(size) + ButtonRing * new Vector2(MathF.Cos(angle), MathF.Sin(angle));
    }

    /// <summary>
    /// How far mode button <paramref name="index"/> is turned: its long side follows the ring, so the
    /// three of them curve round the disc's edge instead of sitting in a straight row.
    /// </summary>
    public static float ModeButtonRotation(int index) => ModeButtonAngle(index) + MathF.PI / 2;

    /// <summary>The axis-aligned box the (turned) mode button <paramref name="index"/> covers.</summary>
    public static (Vector2 Min, Vector2 Max) ModeButton(Vector2 size, int index)
    {
        var c = ModeButtonCenter(size, index);
        float cos = MathF.Abs(MathF.Cos(ModeButtonRotation(index)));
        float sin = MathF.Abs(MathF.Sin(ModeButtonRotation(index)));
        var half = new Vector2(cos * ButtonWidth / 2 + sin * ButtonHeight / 2, sin * ButtonWidth / 2 + cos * ButtonHeight / 2);
        return (c - half, c + half);
    }

    /// <summary>Whether <paramref name="point"/> is inside mode button <paramref name="index"/> (its turned rectangle).</summary>
    public static bool InModeButton(Vector2 size, int index, Vector2 point)
    {
        var d = point - ModeButtonCenter(size, index);
        float a = ModeButtonRotation(index);
        float cos = MathF.Cos(a), sin = MathF.Sin(a);
        // Undo the button's turn: its long side then runs along local x and its short side along local y.
        float x = cos * d.X + sin * d.Y;
        float y = -sin * d.X + cos * d.Y;
        return MathF.Abs(x) <= ButtonWidth / 2 && MathF.Abs(y) <= ButtonHeight / 2;
    }

    /// <summary>Swivel radians per DIP of vertical drag (half a degree: 90 degrees over 180 DIPs).</summary>
    public const float SwivelRate = MathF.PI / 360;

    /// <summary>
    /// The swivel a centre-circle drag asks for, in the convention of a left-side chain: only the
    /// vertical part of <paramref name="moved"/> (screen y down) counts, a drag down turns a left arm
    /// that faces the camera clockwise (a negative angle about the root-to-end line, which then points
    /// at the camera) and a drag up counter-clockwise.
    /// <see cref="SwivelSide"/> flips it for a right-side chain.
    /// </summary>
    public static float SwivelAngle(Vector2 moved) => -moved.Y * SwivelRate;

    /// <summary>
    /// +1 for a left-side (or unsided) chain, -1 for a right-side one, read from the controller's name
    /// and then its chain's joint names with the default mirror naming rules.
    /// </summary>
    public static float SwivelSide(SloppyAnimatsu.Core.Model.ControllerIK controller)
    {
        var match = SloppyAnimatsu.Core.Model.MirrorMatch.Default;
        foreach (var name in new[] { controller.Name }.Concat(controller.Chain.Select(j => j.Name).Reverse()))
        {
            if (string.IsNullOrEmpty(name)) continue;
            var side = match.SideOf(name);
            if (side == SloppyAnimatsu.Core.Model.MirrorSide.Left) return 1;
            if (side == SloppyAnimatsu.Core.Model.MirrorSide.Right) return -1;
        }
        return 1;
    }

    /// <summary>
    /// The space a click on the space button turns to: Local, Screen, World, round again - the
    /// order of the 1 (Local), 2 (Screen) and 3 (World) frame shortcuts
    /// </summary>
    public static TransformSpace NextSpace(TransformSpace space) => space switch
    {
        TransformSpace.Local => TransformSpace.Screen,
        TransformSpace.Screen => TransformSpace.World,
        _ => TransformSpace.Local,
    };

    /// <summary>The screen angle (y down, so clockwise from +x) of the middle of an axis's sector.</summary>
    public static float SectorAngle(int axis) => axis switch
    {
        0 => -MathF.PI / 2,       // X, red: top
        1 => 5 * MathF.PI / 6,    // Y, green: bottom-left
        _ => MathF.PI / 6,        // Z, blue: bottom-right
    };

    /// <summary>
    /// Whether the disc has its centre circle: in scale mode (uniform scale), and in rotate mode
    /// when <paramref name="swivel"/> says the selection holds an IK chain to swivel.
    /// </summary>
    public static bool HasCentre(GizmoMode mode, bool swivel) => mode == GizmoMode.Scale || (mode == GizmoMode.Rotate && swivel);

    /// <summary>
    /// The part under <paramref name="point"/>. The centre circle is there only when
    /// <see cref="HasCentre"/>; the rest of the round frame (the hub, the gaps, the rim) is
    /// <see cref="SpinnerPart.Frame"/>, and anything outside the frame is
    /// <see cref="SpinnerPart.None"/>. The mode and space buttons sit inside the rim, on the space
    /// between the disc's edge and it.
    /// </summary>
    public static SpinnerHit Hit(Vector2 size, Vector2 point, GizmoMode mode, bool swivel = false, bool snap = false)
    {
        if (Vector2.Distance(point, SpaceCenter(size)) <= SpaceRadius) return new SpinnerHit(SpinnerPart.Space);
        if (snap && Vector2.Distance(point, SnapCenter(size)) <= SpaceRadius) return new SpinnerHit(SpinnerPart.Snap);
        if (Vector2.Distance(point, FrameCenter(size)) > FrameRadius) return default;
        for (int i = 0; i < Modes.Length; i++)
            if (InModeButton(size, i, point))
                return new SpinnerHit(SpinnerPart.Mode, -1, Modes[i]);
        var d = point - Center(size);
        float r = d.Length() / Radius(size);
        if (HasCentre(mode, swivel) && r <= UniformRadius) return new SpinnerHit(SpinnerPart.Uniform, TransformGizmo.Uniform);
        if (r < InnerRadius || r > OuterRadius) return new SpinnerHit(SpinnerPart.Frame);
        float angle = MathF.Atan2(d.Y, d.X);
        for (int axis = 0; axis < 3; axis++)
            if (MathF.Abs(Wrap(angle - SectorAngle(axis))) <= SectorHalfAngle)
                return new SpinnerHit(SpinnerPart.Axis, axis);
        return new SpinnerHit(SpinnerPart.Frame);
    }

    /// <summary>Whether <paramref name="point"/> is on the window at all (anywhere inside the round rim).</summary>
    public static bool Contains(Vector2 size, Vector2 point) => Hit(size, point, GizmoMode.Rotate).Part != SpinnerPart.None;

    /// <summary>The default gap between the dial and the view area's right and bottom edges.</summary>
    public const float DefaultRight = 480, DefaultBottom = 180;

    /// <summary>
    /// The dial's top-left corner in a view area of <paramref name="host"/> size: where the user
    /// dragged it (<paramref name="dragged"/>), or the default place clear of the default layout's
    /// right-hand panels and Timeline, kept inside the area either way.
    /// </summary>
    public static Vector2 Place(Vector2 host, Vector2? dragged)
    {
        var at = dragged ?? new Vector2(host.X - DefaultRight - Width, host.Y - DefaultBottom - Height);
        return new Vector2(
            MathF.Max(MathF.Min(at.X, host.X - Width), 0),
            MathF.Max(MathF.Min(at.Y, host.Y - Height), 0));
    }

    /// <summary>
    /// The angle in radians swept around <paramref name="center"/> by a pointer step from
    /// <paramref name="from"/> to <paramref name="to"/>, clockwise (as seen) positive. Steps are
    /// summed by the caller, so a drag can wind more than a full turn.
    /// </summary>
    public static float Swept(Vector2 center, Vector2 from, Vector2 to)
    {
        var a = from - center;
        var b = to - center;
        if (a.LengthSquared() < 1e-6f || b.LengthSquared() < 1e-6f) return 0;
        return MathF.Atan2(a.X * b.Y - a.Y * b.X, Vector2.Dot(a, b));
    }

    /// <summary>
    /// The dial-turn equivalent of a sector drag, in radians: only the vertical part of
    /// <paramref name="moved"/> counts (screen y down), up positive, at <see cref="SwivelRate"/> per
    /// DIP; the horizontal part is ignored. Fed to the same rotate, move and
    /// scale amounts as before, so 180 DIPs up is a quarter turn.
    /// </summary>
    public static float VerticalTurn(Vector2 moved) => -moved.Y * SwivelRate;

    /// <summary>Reverse-foot roll/twist/bank degrees per DIP of vertical right-button drag (up positive).</summary>
    public const float FootDegreesPerDip = 0.5f;

    /// <summary>The roll, twist or bank change in degrees a right-button sector drag asks for.</summary>
    public static float FootDegrees(Vector2 moved) => -moved.Y * FootDegreesPerDip;

    /// <summary>Translate: a quarter turn moves by one gizmo handle length. Returns handle lengths.</summary>
    public static float MoveHandles(float swept) => swept / (MathF.PI / 2);

    /// <summary>Scale: half a turn clockwise doubles, half a turn back reaches zero.</summary>
    public static float ScaleFactor(float swept) => MathF.Max(1 + swept / MathF.PI, 0);

    /// <summary>Uniform scale from the centre circle: a drag up by one dial radius doubles; horizontal movement is ignored.</summary>
    public static float UniformFactor(Vector2 moved, float radius) =>
        radius < 1e-6f ? 1 : MathF.Max(1 - moved.Y / radius, 0);

    private static float Wrap(float angle)
    {
        while (angle > MathF.PI) angle -= 2 * MathF.PI;
        while (angle < -MathF.PI) angle += 2 * MathF.PI;
        return angle;
    }
}

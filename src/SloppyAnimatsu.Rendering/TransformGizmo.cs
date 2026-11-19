using System.Numerics;

namespace SloppyAnimatsu.Rendering;

public enum GizmoMode { Rotate, Translate, Scale }

/// <summary>
/// The rotate, translate and scale handles drawn on the selected controller. In
/// <see cref="TransformSpace.Local"/> (the default) the rotate rings turn about the controller's own
/// axes, the translate arrows follow its parent's axes (the frame its local translation is expressed
/// in), and the scale handles follow the controller's own axes (scale applies before its rotation).
/// <see cref="TransformSpace.World"/> uses the scene's axes for all three, and
/// <see cref="TransformSpace.Screen"/> the active view's right, up and backward axes. Scale has a
/// triangle at the centre (handle <see cref="Uniform"/>) scaling all three; translate has a plane
/// handle between each pair of arrows (<see cref="PlaneX"/>). <see cref="Segments"/> is the pick
/// skeleton; the handles are drawn thick by <see cref="WriteTriangles"/>. A viewer design, not
/// The.
/// </summary>
public sealed class TransformGizmo
{
    private const int RingSegments = 64;

    /// <summary>Where the uniform scale handle meets each axis, as a fraction of the handle length.</summary>
    private const float UniformBox = 0.3f;

    /// <summary>Handle size as a fraction of the view height at the gizmo's depth.</summary>
    public const float SizeOfView = 0.12f;

    /// <summary>The scale mode's handle that scales all three axes together.</summary>
    public const int Uniform = 3;

    /// <summary>The first translate plane handle; <c>PlaneX + n</c> is the plane across axis <c>n</c>.</summary>
    public const int PlaneX = 4;

    /// <summary>Where a plane handle starts and ends along its two axes, as fractions of the handle length.</summary>
    public const float PlaneNear = 0.2f, PlaneFar = 0.55f;

    /// <summary>
    /// A plane handle is hidden when its normal's angle to the view direction leaves it within this
    /// cosine of a right angle: the plane is then seen edge-on and its two axes fall on one line.
    /// </summary>
    private const float PlaneEdgeOn = 0.15f;

    // Drawn thicknesses, as fractions of the handle length.
    public const float ShaftHalfWidth = 0.03f, RingHalfWidth = 0.02f, HeadLength = 0.24f, HeadRadius = 0.085f,
        BoxHalf = 0.07f, CenterRadius = 0.045f;

    public static bool IsPlane(int handle) => handle is >= PlaneX and < PlaneX + 3;

    /// <summary>The axis a plane handle lies across (the one it does not move along).</summary>
    public static int PlaneNormal(int handle) => handle - PlaneX;

    /// <summary>The two axes a plane handle moves along.</summary>
    public static (int A, int B) PlaneAxes(int handle) => ((PlaneNormal(handle) + 1) % 3, (PlaneNormal(handle) + 2) % 3);

    private static readonly Vector3[] AxisColors = [new(0.95f, 0.25f, 0.25f), new(0.3f, 0.9f, 0.3f), new(0.3f, 0.5f, 1f), new(0.85f, 0.85f, 0.85f)];
    private static readonly Vector3 ActiveColor = new(1f, 0.9f, 0.2f);

    public bool Visible { get; set; }
    public GizmoMode Mode { get; set; } = GizmoMode.Rotate;

    /// <summary>The space the handles are drawn in (the Spinner's Local / World / Screen button).</summary>
    public TransformSpace Space { get; set; } = TransformSpace.Local;

    /// <summary>World matrix of the controller (centre, and the rotate axes as its rows).</summary>
    public Matrix4x4 Frame { get; set; } = Matrix4x4.Identity;

    /// <summary>World matrix of the frame the local translation is expressed in (the translate axes as its rows).</summary>
    public Matrix4x4 ParentFrame { get; set; } = Matrix4x4.Identity;

    /// <summary>Axis under the pointer or being dragged (0 = X, 1 = Y, 2 = Z, <see cref="Uniform"/>), or -1.</summary>
    public int ActiveAxis { get; set; } = -1;

    /// <summary>Whether translation can be keyed on the selected controller (otherwise translate shows nothing).</summary>
    public bool CanTranslate { get; set; }

    /// <summary>Whether scale can be keyed on the selected controller (otherwise scale shows nothing).</summary>
    public bool CanScale { get; set; }

    public Vector3 Center => Frame.Translation;

    /// <summary>
    /// Unit world direction of handle <paramref name="axis"/> in the current mode and space. Without
    /// a camera, Screen falls back to the world axes; the viewer passes the active view.
    /// </summary>
    public Vector3 Axis(int axis) => Axis(axis, null);

    /// <summary>Unit world direction of handle <paramref name="axis"/> in the current mode and space.</summary>
    public Vector3 Axis(int axis, IViewCamera? camera) => Space switch
    {
        TransformSpace.World => Unit(axis),
        TransformSpace.Screen => camera != null ? CameraAxis(camera, axis) : Unit(axis),
        _ => Direction(Mode == GizmoMode.Translate ? ParentFrame : Frame, axis),
    };

    /// <summary>
    /// The rows of the current space's axes as a world matrix, captured by a drag at its start (the
    /// Local space's own frame is not used: a local drag reads its axes from the controller).
    /// </summary>
    public Matrix4x4 SpaceFrame(IViewCamera? camera = null)
    {
        var x = Axis(0, camera);
        var y = Axis(1, camera);
        var z = Axis(2, camera);
        return new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
    }

    /// <summary>World length of the handles at the gizmo's position.</summary>
    public float Size(IViewCamera camera) => SizeOfView * camera.ViewHeightAt(Center);

    /// <summary>
    /// The length of a translate handle in the drag's units along <paramref name="axis"/>: local
    /// translation units in the Local space, world units in World and Screen (0 when the parent
    /// frame is degenerate there); the Spinner dial moves by multiples of it.
    /// </summary>
    public float HandleUnits(IViewCamera camera, int axis)
    {
        float unit = Space == TransformSpace.Local ? Row(ParentFrame, axis).Length() : 1f;
        return unit < 1e-6f ? 0 : Size(camera) / unit;
    }

    /// <summary>The handles as world-space line segments, each tagged with its axis.</summary>
    public IEnumerable<(Vector3 A, Vector3 B, int Axis)> Segments(IViewCamera camera)
    {
        if (!Visible) yield break;
        float size = Size(camera);
        var center = Center;
        if (Mode == GizmoMode.Translate)
        {
            if (!CanTranslate) yield break;
            for (int axis = 0; axis < 3; axis++)
            {
                var d = Axis(axis, camera);
                var tip = center + d * size;
                yield return (center, tip, axis);
            }
            yield break;
        }
        if (Mode == GizmoMode.Scale)
        {
            if (!CanScale) yield break;
            var d = new[] { Axis(0, camera), Axis(1, camera), Axis(2, camera) };
            for (int axis = 0; axis < 3; axis++)
            {
                var tip = center + d[axis] * size;
                yield return (center + d[axis] * size * UniformBox, tip, axis);
                // A small square across the tip.
                var u = d[(axis + 1) % 3] * size * 0.05f;
                var v = d[(axis + 2) % 3] * size * 0.05f;
                yield return (tip - u - v, tip + u - v, axis);
                yield return (tip + u - v, tip + u + v, axis);
                yield return (tip + u + v, tip - u + v, axis);
                yield return (tip - u + v, tip - u - v, axis);
            }
            // The uniform handle: a triangle joining the three axes near the centre.
            for (int axis = 0; axis < 3; axis++)
                yield return (center + d[axis] * size * UniformBox, center + d[(axis + 1) % 3] * size * UniformBox, Uniform);
            yield break;
        }
        for (int axis = 0; axis < 3; axis++)
        {
            var (u, v, from, to) = RingArc(axis, camera);
            int steps = Math.Max(1, (int)MathF.Round(RingSegments * (to - from) / (2 * MathF.PI)));
            var previous = center + (MathF.Cos(from) * u + MathF.Sin(from) * v) * size;
            for (int i = 1; i <= steps; i++)
            {
                float a = from + i * (to - from) / steps;
                var next = center + (MathF.Cos(a) * u + MathF.Sin(a) * v) * size;
                yield return (previous, next, axis);
                previous = next;
            }
        }
    }

    /// <summary>
    /// How far a ring may lean away from facing the viewer before only its nearest quadrant is
    /// drawn: the sine of the angle between the ring's axis and the view direction (see
    /// <see cref="IViewCamera.Forward"/>). Below this the ring's plane is nearly parallel to the
    /// image plane, so no part of it is nearer than another and the whole ring is drawn.
    /// </summary>
    public const float RingFaceOn = 0.15f;

    /// <summary>
    /// The drawn part of rotation ring <paramref name="axis"/>: points
    /// <c>Center + (cos a * U + sin a * V) * size</c> for <c>a</c> in <c>[From, To]</c>. It is the
    /// quadrant of the ring, bounded by the gizmo frame's other two axes, on the camera's side, so it
    /// stays put while the camera moves and jumps a whole quadrant when the nearest one changes; or
    /// the whole ring when its axis is within <see cref="RingFaceOn"/> of the view direction. Screen
    /// space is fixed instead: its depth ring always draws whole (its axis is the view direction
    /// itself) and its two other rings keep the same quarter, up and left of the centre on screen,
    /// wherever the gizmo or the eye is.
    /// </summary>
    public (Vector3 U, Vector3 V, float From, float To) RingArc(int axis, IViewCamera camera)
    {
        var n = Axis(axis, camera);
        var forward = camera.Forward;
        var inPlane = forward - n * Vector3.Dot(n, forward);
        if (inPlane.Length() >= RingFaceOn)
        {
            // Snap to a whole quadrant of the gizmo's frame: the one between the two other axes'
            // half-lines on the eye's side. The quadrants follow the frame's axes (the transform
            // space), and the arc jumps to the next one only when the eye crosses an axis plane.
            var a = Axis((axis + 1) % 3, camera);
            var b = Axis((axis + 2) % 3, camera);
            float sa, sb;
            if (Space == TransformSpace.Screen)
            {
                // The screen frame is the camera's, so there is an upper left on the image plane to
                // hold the quarters still: the rings of the right and up axes are seen edge-on and
                // their arcs stay one above the centre and one to its left, whatever the gizmo's
                // place on screen or the eye's position. (A keyframed frame would have no such
                // fixed corner, and the app keeps its quadrants turning with the camera there.)
                var upLeft = Axis(1, camera) - Axis(0, camera);
                sa = Vector3.Dot(upLeft, a) < 0 ? -1 : 1;
                sb = Vector3.Dot(upLeft, b) < 0 ? -1 : 1;
            }
            else
            {
                // The eye may sit anywhere in front of the ring (a perspective view), so the side is
                // taken from the direction to the eye rather than from the view direction above: the
                // quadrant follows where the eye is, which is what "facing the camera" means there.
                var toEye = camera.Eye - Center;
                sa = Vector3.Dot(toEye, a) < 0 ? -1 : 1;
                sb = Vector3.Dot(toEye, b) < 0 ? -1 : 1;
            }
            var mid = sa * a + sb * b;
            mid -= n * Vector3.Dot(n, mid);
            if (mid.LengthSquared() > 1e-12f)
            {
                var u = Vector3.Normalize(mid);
                return (u, Vector3.Cross(n, u), -MathF.PI / 4, MathF.PI / 4);
            }
        }
        var p = Perpendicular(n);
        return (p, Vector3.Cross(n, p), 0, 2 * MathF.PI);
    }

    /// <summary>
    /// The translate mode's plane handles: a triangle between each pair of arrows that moves the
    /// controller in the plane of those two axes. Handle <c>PlaneX + n</c> lies across axis
    /// <c>n</c> (its normal) and takes that axis's colour.
    /// </summary>
    public IEnumerable<(Vector3 A, Vector3 B, Vector3 C, int Handle)> PlaneTriangles(IViewCamera camera)
    {
        if (!Visible || Mode != GizmoMode.Translate || !CanTranslate) yield break;
        float size = Size(camera);
        var center = Center;
        var forward = camera.Forward;
        for (int n = 0; n < 3; n++)
        {
            var (a, b) = PlaneAxes(PlaneX + n);
            var u = Axis(a, camera);
            var v = Axis(b, camera);
            var normal = Vector3.Cross(u, v);
            if (normal.LengthSquared() < 1e-8f) continue;
            // A plane seen edge-on cannot be dragged in: its two axes fall on one screen line.
            if (MathF.Abs(Vector3.Dot(Vector3.Normalize(normal), forward)) < PlaneEdgeOn) continue;
            u *= size;
            v *= size;
            yield return (center + (u + v) * PlaneNear, center + u * PlaneFar + v * PlaneNear, center + u * PlaneNear + v * PlaneFar, PlaneX + n);
        }
    }

    /// <summary>
    /// Appends the handles as coloured triangles (<see cref="ScenePose.LineVertexFloats"/> floats per
    /// vertex): thick view-facing shafts and rings, cone arrow heads, boxes on the scale handles.
    /// </summary>
    public void WriteTriangles(List<float> o, IViewCamera camera)
    {
        if (!Visible) return;
        float size = Size(camera);
        var center = Center;
        var eye = camera.Eye;
        Vector3 Color(int handle) => handle == ActiveAxis ? ActiveColor : AxisColors[IsPlane(handle) ? PlaneNormal(handle) : handle];

        if (Mode == GizmoMode.Translate)
        {
            if (!CanTranslate) return;
            foreach (var (a, b, c, handle) in PlaneTriangles(camera)) Tri(o, a, b, c, Color(handle));
            for (int axis = 0; axis < 3; axis++)
            {
                var d = Axis(axis, camera);
                var neck = center + d * size * (1 - HeadLength);
                Ribbon(o, center, neck, ShaftHalfWidth * size, eye, Color(axis));
                Cone(o, neck, center + d * size, HeadRadius * size, eye, Color(axis));
            }
            Disc(o, center, CenterRadius * size, eye, AxisColors[Uniform]);
            return;
        }
        if (Mode == GizmoMode.Scale)
        {
            if (!CanScale) return;
            var d = new[] { Axis(0, camera), Axis(1, camera), Axis(2, camera) };
            Tri(o, center + d[0] * size * UniformBox, center + d[1] * size * UniformBox, center + d[2] * size * UniformBox, Color(Uniform));
            for (int axis = 0; axis < 3; axis++)
            {
                var tip = center + d[axis] * size;
                Ribbon(o, center + d[axis] * size * UniformBox, tip, ShaftHalfWidth * size, eye, Color(axis));
                Box(o, tip, d, BoxHalf * size, eye, Color(axis));
            }
            return;
        }
        foreach (var (a, b, axis) in Segments(camera)) Ribbon(o, a, b, RingHalfWidth * size, eye, Color(axis));
        // Arrow heads pointing the way a positive turn goes: one in the middle of a quarter ring,
        // two opposite each other on a whole one.
        for (int axis = 0; axis < 3; axis++)
        {
            var n = Axis(axis, camera);
            var (u, v, from, to) = RingArc(axis, camera);
            bool whole = to - from > MathF.PI;
            for (int k = 0; k < (whole ? 2 : 1); k++)
            {
                float angle = whole ? MathF.PI / 4 + k * MathF.PI : (from + to) / 2;
                var radial = MathF.Cos(angle) * u + MathF.Sin(angle) * v;
                var tangent = Vector3.Cross(n, radial);
                var at = center + radial * size;
                Cone(o, at - tangent * size * HeadLength / 2, at + tangent * size * HeadLength / 2, HeadRadius * size, eye, Color(axis));
            }
        }
    }

    /// <summary>
    /// The handle at <paramref name="point"/> (viewport units), or -1: the nearest shaft, ring or
    /// box edge within <paramref name="tolerance"/> of the handle's drawn thickness, else the plane
    /// triangle (translate) or the uniform triangle (scale) the point lies inside.
    /// </summary>
    public int HitTest(IViewCamera camera, Vector2 viewport, Vector2 point, float tolerance)
    {
        int best = -1;
        // The handles are SizeOfView of the view height long on screen, and drawn this thick.
        float bestDistance = tolerance + (Mode == GizmoMode.Rotate ? RingHalfWidth : ShaftHalfWidth) * SizeOfView * viewport.Y;
        foreach (var (a, b, axis) in Segments(camera))
        {
            if (camera.Project(a, viewport) is not { } pa || camera.Project(b, viewport) is not { } pb) continue;
            float d = DistanceToSegment(point, pa, pb);
            if (d <= bestDistance)
            {
                bestDistance = d;
                best = axis;
            }
        }
        if (best >= 0) return best;
        foreach (var (a, b, c, handle) in PlaneTriangles(camera))
            if (Inside(camera, viewport, point, a, b, c)) return handle;
        if (Visible && Mode == GizmoMode.Scale && CanScale)
        {
            float reach = Size(camera) * UniformBox;
            if (Inside(camera, viewport, point, Center + Axis(0, camera) * reach, Center + Axis(1, camera) * reach, Center + Axis(2, camera) * reach)) return Uniform;
        }
        return -1;
    }

    private static bool Inside(IViewCamera camera, Vector2 viewport, Vector2 p, Vector3 a, Vector3 b, Vector3 c)
    {
        if (camera.Project(a, viewport) is not { } pa || camera.Project(b, viewport) is not { } pb || camera.Project(c, viewport) is not { } pc) return false;
        float d1 = Cross(pb - pa, p - pa), d2 = Cross(pc - pb, p - pb), d3 = Cross(pa - pc, p - pc);
        bool negative = d1 < 0 || d2 < 0 || d3 < 0;
        bool positive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(negative && positive) && MathF.Abs(Cross(pb - pa, pc - pa)) > 1e-3f;

        static float Cross(Vector2 x, Vector2 y) => x.X * y.Y - x.Y * y.X;
    }

    /// <summary>
    /// How far along the two axes of plane handle <paramref name="handle"/>
    /// (<see cref="PlaneAxes"/>), in the drag's units (local translation units in the Local space,
    /// world units in World and Screen), a drag of <paramref name="delta"/> (viewport units) moves:
    /// the drag resolved into the screen images of one unit along each of the plane's axes. Zero
    /// when the plane is seen edge-on.
    /// </summary>
    public Vector2 DragPlane(IViewCamera camera, Vector2 viewport, int handle, Vector2 delta)
    {
        var (a, b) = PlaneAxes(handle);
        if (ScreenUnit(camera, viewport, a) is not { } sa || ScreenUnit(camera, viewport, b) is not { } sb) return Vector2.Zero;
        float det = sa.X * sb.Y - sa.Y * sb.X;
        if (MathF.Abs(det) < 1e-6f * sa.Length() * sb.Length() || det == 0) return Vector2.Zero;
        return new Vector2((delta.X * sb.Y - delta.Y * sb.X) / det, (sa.X * delta.Y - sa.Y * delta.X) / det);
    }

    /// <summary>The screen image (viewport units) of one drag unit along <paramref name="axis"/>.</summary>
    private Vector2? ScreenUnit(IViewCamera camera, Vector2 viewport, int axis)
    {
        var center = Center;
        var unit = Space == TransformSpace.Local ? Row(ParentFrame, axis) : Axis(axis, camera);
        if (unit.LengthSquared() < 1e-12f) return null;
        // Probe a short step so that the projection stays near-linear, then scale back up.
        float step = Size(camera) / unit.Length();
        if (camera.Project(center, viewport) is not { } a || camera.Project(center + unit * step, viewport) is not { } b) return null;
        return (b - a) / step;
    }

    private static void Tri(List<float> o, Vector3 a, Vector3 b, Vector3 c, Vector3 color)
    {
        o.AddRange([a.X, a.Y, a.Z, color.X, color.Y, color.Z, b.X, b.Y, b.Z, color.X, color.Y, color.Z, c.X, c.Y, c.Z, color.X, color.Y, color.Z]);
    }

    /// <summary>A flat strip from <paramref name="a"/> to <paramref name="b"/>, turned to face the viewer.</summary>
    private static void Ribbon(List<float> o, Vector3 a, Vector3 b, float half, Vector3 eye, Vector3 color)
    {
        var along = b - a;
        if (along.LengthSquared() < 1e-14f) return;
        var side = Vector3.Cross(along, eye - (a + b) / 2);
        side = side.LengthSquared() < 1e-10f * along.LengthSquared() ? Perpendicular(Vector3.Normalize(along)) : Vector3.Normalize(side);
        side *= half;
        Tri(o, a - side, a + side, b + side, color);
        Tri(o, a - side, b + side, b - side, color);
    }

    /// <summary>Darkens a face that is turned away from the viewer, so that solid heads read as solids.</summary>
    private static Vector3 Shade(Vector3 color, Vector3 normal, Vector3 toEye) =>
        color * (0.6f + 0.4f * MathF.Abs(Vector3.Dot(normal, toEye)));

    private static void Cone(List<float> o, Vector3 from, Vector3 tip, float radius, Vector3 eye, Vector3 color)
    {
        const int sides = 12;
        var axis = tip - from;
        if (axis.LengthSquared() < 1e-14f) return;
        axis = Vector3.Normalize(axis);
        var u = Perpendicular(axis);
        var v = Vector3.Cross(axis, u);
        var toEye = eye - from;
        toEye = toEye.LengthSquared() > 1e-12f ? Vector3.Normalize(toEye) : axis;
        var cap = Shade(color, axis, toEye) * 0.85f;
        float length = Vector3.Distance(from, tip);
        // The overlay is drawn without a depth test, so only the faces turned to the viewer are
        // emitted; for a convex solid that is exactly the visible surface.
        bool capFaces = Vector3.Dot(axis, toEye) < 0;
        for (int i = 0; i < sides; i++)
        {
            float a0 = i * 2 * MathF.PI / sides, a1 = (i + 1) * 2 * MathF.PI / sides, mid = (a0 + a1) / 2;
            var p0 = from + (MathF.Cos(a0) * u + MathF.Sin(a0) * v) * radius;
            var p1 = from + (MathF.Cos(a1) * u + MathF.Sin(a1) * v) * radius;
            var normal = Vector3.Normalize((MathF.Cos(mid) * u + MathF.Sin(mid) * v) * length + axis * radius);
            if (Vector3.Dot(normal, toEye) > 0) Tri(o, p0, p1, tip, Shade(color, normal, toEye));
            if (capFaces) Tri(o, from, p1, p0, cap);
        }
    }

    private static void Box(List<float> o, Vector3 center, Vector3[] axes, float half, Vector3 eye, Vector3 color)
    {
        var toEye = eye - center;
        toEye = toEye.LengthSquared() > 1e-12f ? Vector3.Normalize(toEye) : Vector3.UnitZ;
        for (int i = 0; i < 3; i++)
        {
            var n = axes[i];
            var u = axes[(i + 1) % 3] * half;
            var v = axes[(i + 2) % 3] * half;
            var shade = Shade(color, n, toEye);
            // Only the face of each pair that is turned to the viewer (no depth test in the overlay).
            var c = center + n * half * (Vector3.Dot(n, toEye) >= 0 ? 1f : -1f);
            Tri(o, c - u - v, c + u - v, c + u + v, shade);
            Tri(o, c - u - v, c + u + v, c - u + v, shade);
        }
    }

    private static void Disc(List<float> o, Vector3 center, float radius, Vector3 eye, Vector3 color)
    {
        const int sides = 12;
        var n = eye - center;
        n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
        var u = Perpendicular(n);
        var v = Vector3.Cross(n, u);
        for (int i = 0; i < sides; i++)
        {
            float a0 = i * 2 * MathF.PI / sides, a1 = (i + 1) * 2 * MathF.PI / sides;
            Tri(o, center, center + (MathF.Cos(a0) * u + MathF.Sin(a0) * v) * radius, center + (MathF.Cos(a1) * u + MathF.Sin(a1) * v) * radius, color);
        }
    }

    /// <summary>
    /// The rotation in radians about <paramref name="axis"/> (right-handed) for a drag from
    /// <paramref name="from"/> to <paramref name="to"/>: the angle swept around the gizmo's centre
    /// on screen, reversed when the axis points away from the viewer.
    /// </summary>
    public float DragAngle(IViewCamera camera, Vector2 viewport, int axis, Vector2 from, Vector2 to)
    {
        if (camera.Project(Center, viewport) is not { } c) return 0;
        var a = from - c;
        var b = to - c;
        if (a.LengthSquared() < 1e-6f || b.LengthSquared() < 1e-6f) return 0;
        // Screen y points down, so a positive screen angle turns clockwise as seen.
        float screen = MathF.Atan2(a.X * b.Y - a.Y * b.X, Vector2.Dot(a, b));
        bool towardViewer = Vector3.Dot(Axis(axis, camera), camera.Eye - Center) > 0;
        return towardViewer ? -screen : screen;
    }

    /// <summary>
    /// How far along translate handle <paramref name="axis"/>, in the drag's units (local
    /// translation units in the Local space, world units in World and Screen), a drag of
    /// <paramref name="delta"/> (viewport units) moves: the drag projected onto the screen image of
    /// one unit along that axis.
    /// </summary>
    public float DragDistance(IViewCamera camera, Vector2 viewport, int axis, Vector2 delta)
    {
        if (ScreenUnit(camera, viewport, axis) is not { } screen) return 0;
        float length2 = screen.LengthSquared();
        return length2 < 1e-9f ? 0 : Vector2.Dot(delta, screen) / length2;
    }

    /// <summary>
    /// The scale factor for a drag of <paramref name="delta"/> (viewport units) on scale handle
    /// <paramref name="axis"/>: 1 plus the drag along the handle's screen image in handle lengths
    /// (dragging by one handle length doubles), or for <see cref="Uniform"/> the drag right and up
    /// in handle lengths. Never negative.
    /// </summary>
    public float DragScale(IViewCamera camera, Vector2 viewport, int axis, Vector2 delta)
    {
        float t;
        if (axis is >= 0 and < 3)
        {
            var center = Center;
            if (camera.Project(center, viewport) is not { } a || camera.Project(center + Axis(axis, camera) * Size(camera), viewport) is not { } b) return 1;
            var screen = b - a;
            float length2 = screen.LengthSquared();
            if (length2 < 1e-6f) return 1;
            t = Vector2.Dot(delta, screen) / length2;
        }
        else
        {
            // The handles are SizeOfView of the view height long on screen.
            float handle = SizeOfView * viewport.Y;
            if (handle < 1e-6f) return 1;
            t = (delta.X - delta.Y) / handle;
        }
        return MathF.Max(1 + t, 0);
    }

    public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float length2 = ab.LengthSquared();
        float t = length2 < 1e-9f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / length2, 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }

    private static Vector3 Row(Matrix4x4 m, int axis) => axis switch
    {
        0 => new Vector3(m.M11, m.M12, m.M13),
        1 => new Vector3(m.M21, m.M22, m.M23),
        _ => new Vector3(m.M31, m.M32, m.M33),
    };

    private static Vector3 Direction(Matrix4x4 m, int axis)
    {
        var r = Row(m, axis);
        return r.LengthSquared() > 1e-12f ? Vector3.Normalize(r) : Unit(axis);
    }

    private static Vector3 Unit(int axis) => axis switch
    {
        0 => Vector3.UnitX,
        1 => Vector3.UnitY,
        _ => Vector3.UnitZ,
    };

    /// <summary>
    /// Screen space's right, up and backward axes: the columns of the view matrix (a view matrix in
    /// this row-vector convention holds the camera basis in its columns).
    /// </summary>
    private static Vector3 CameraAxis(IViewCamera camera, int axis)
    {
        var m = camera.View;
        var row = axis switch
        {
            0 => new Vector3(m.M11, m.M21, m.M31),
            1 => new Vector3(m.M12, m.M22, m.M32),
            _ => new Vector3(m.M13, m.M23, m.M33),
        };
        return row.LengthSquared() > 1e-12f ? Vector3.Normalize(row) : Unit(axis);
    }

    private static Vector3 Perpendicular(Vector3 n)
    {
        var other = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        return Vector3.Normalize(Vector3.Cross(n, other));
    }
}


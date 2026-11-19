using System.Numerics;

namespace SloppyAnimatsu.Rendering;

/// <summary>
/// The view cube drawn in the outer corner of every orbit view: a
/// small cube that turns with the camera, with a handle on each of its six sides - a cone pointing
/// at the face centre and a labelled ball at the cone's base, coloured like the axis. This class
/// is the geometry only: it lays the parts out in screen space for a camera rotation, in draw
/// order, and hit-tests a point against them; the viewer paints the parts.
/// </summary>
/// <remarks>
/// Axes are numbered 0 +X, 1 +Y, 2 +Z, 3 -X, 4 -Y, 5 -Z (the numbering
/// <see cref="OrbitCamera.AlignTo"/> takes). Sizes are in cube units (the cube is one unit wide)
/// and scaled to pixels by the caller's <c>scale</c>; the projection is parallel, so the cube
/// reads the same in perspective and flat views.
/// </remarks>
public static class AxisCube
{
    public const int AxisCount = 6;

    /// <summary>Half the cube's edge.</summary>
    public const float HalfSize = 0.5f;

    /// <summary>The distance from the cube centre to the centre of a handle's ball.</summary>
    public const float BallDistance = 1.35f;

    public const float BallRadius = 0.22f;

    /// <summary>The cone's base sits just inside the ball; its tip stops short of the face.</summary>
    public const float ConeBase = 1.13f;

    public const float ConeTip = 0.62f;

    public const float ConeRadius = 0.12f;

    /// <summary>The farthest any part reaches from the centre, in cube units.</summary>
    public const float Extent = BallDistance + BallRadius;

    private const int ConeSegments = 16;

    public enum PartKind { Face, Cone, Ball }

    /// <summary>
    /// One drawn part in screen space (y down). <see cref="Outline"/> is the polygon of a face or
    /// a cone; a ball is the circle <see cref="Center"/>/<see cref="Radius"/>. <see cref="Depth"/>
    /// grows towards the viewer; <see cref="Facing"/> is how squarely the part's axis points at
    /// the viewer (-1..1), for shading.
    /// </summary>
    public sealed record Part(PartKind Kind, int Axis, Vector2[] Outline, Vector2 Center, float Radius, float Depth, float Facing);

    /// <summary>The unit direction of <paramref name="axis"/>.</summary>
    public static Vector3 Direction(int axis) => (axis % 3) switch
    {
        0 => Vector3.UnitX,
        1 => Vector3.UnitY,
        _ => Vector3.UnitZ,
    } * (axis < 3 ? 1 : -1);

    public static bool IsPositive(int axis) => axis < 3;

    /// <summary>The handle's label: "X", "Y", "Z", or "-X", "-Y", "-Z" on the negative side.</summary>
    public static string Label(int axis) => (axis < 3 ? "" : "-") + "XYZ"[axis % 3];

    /// <summary>The axis colour (red X, green Y, blue Z: the transform gizmo's colours), RGB 0-1.</summary>
    public static Vector3 Color(int axis) => (axis % 3) switch
    {
        0 => new Vector3(0.95f, 0.25f, 0.25f),
        1 => new Vector3(0.3f, 0.9f, 0.3f),
        _ => new Vector3(0.3f, 0.5f, 1f),
    };

    /// <summary>
    /// The parts seen through a camera whose view matrix is <paramref name="view"/> (only its
    /// rotation is used), centred on <paramref name="center"/> with <paramref name="scale"/>
    /// pixels per cube unit, sorted back to front. Cube faces turned away are left out.
    /// </summary>
    public static IReadOnlyList<Part> Layout(Matrix4x4 view, Vector2 center, float scale)
    {
        var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
        var parts = new List<Part>(18);
        Vector2 Screen(Vector3 v) => new(center.X + v.X * scale, center.Y - v.Y * scale);
        Vector3 Rotate(Vector3 v) => Vector3.TransformNormal(v, rotation);

        for (int axis = 0; axis < AxisCount; axis++)
        {
            var dir = Direction(axis);
            var (t1, t2) = Tangents(axis);
            var viewDir = Rotate(dir);
            float facing = viewDir.Z;

            if (facing > 1e-4f)
            {
                var face = HalfSize * dir;
                var corners = new[]
                {
                    Screen(Rotate(face + HalfSize * (t1 + t2))),
                    Screen(Rotate(face + HalfSize * (t1 - t2))),
                    Screen(Rotate(face - HalfSize * (t1 + t2))),
                    Screen(Rotate(face - HalfSize * (t1 - t2))),
                };
                parts.Add(new Part(PartKind.Face, axis, corners, Screen(Rotate(face)), 0, Rotate(face).Z, facing));
            }

            var base_ = ConeBase * dir;
            var tip = ConeTip * dir;
            var cone = new List<Vector2>(ConeSegments + 1) { Screen(Rotate(tip)) };
            for (int i = 0; i < ConeSegments; i++)
            {
                float a = i * 2 * MathF.PI / ConeSegments;
                cone.Add(Screen(Rotate(base_ + ConeRadius * (MathF.Cos(a) * t1 + MathF.Sin(a) * t2))));
            }
            var middle = Rotate((base_ + tip) / 2);
            parts.Add(new Part(PartKind.Cone, axis, ConvexHull(cone), Screen(middle), 0, middle.Z, facing));

            var ball = Rotate(BallDistance * dir);
            parts.Add(new Part(PartKind.Ball, axis, [], Screen(ball), BallRadius * scale, ball.Z, facing));
        }
        // Back to front. A ball and its own cone touch; the ball wins a tie towards the viewer side.
        parts.Sort((a, b) => a.Depth.CompareTo(b.Depth));
        return parts;
    }

    /// <summary>The front-most part under <paramref name="point"/>, or null.</summary>
    public static Part? HitTest(IReadOnlyList<Part> parts, Vector2 point)
    {
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            var part = parts[i];
            bool hit = part.Kind == PartKind.Ball
                ? Vector2.DistanceSquared(point, part.Center) <= part.Radius * part.Radius
                : Contains(part.Outline, point);
            if (hit) return part;
        }
        return null;
    }

    /// <summary>
    /// Where a cube of <paramref name="radius"/> (its footprint, pixels) goes in a view at
    /// <paramref name="viewOrigin"/>/<paramref name="viewSize"/> inside an area of
    /// <paramref name="areaSize"/> (y down): in the view's corner nearest the area's edges
    /// (bottom-left on a tie, so a single view keeps it there), <paramref name="inset"/> in from
    /// both sides. When an obstacle (min, max rectangle; a panel over the view) covers that spot,
    /// the cube moves inward, in <paramref name="step"/>s, to the nearest free spot that stays
    /// inside the view; with none free it stays in the corner. Null when the view is too small.
    /// </summary>
    public static Vector2? Place(Vector2 viewOrigin, Vector2 viewSize, Vector2 areaSize,
        IReadOnlyList<(Vector2 Min, Vector2 Max)> obstacles, float radius, float inset, float step = 6f)
    {
        if (viewSize.X < inset * 2.2f || viewSize.Y < inset * 2.2f) return null;
        bool left = viewOrigin.X <= areaSize.X - (viewOrigin.X + viewSize.X) + 0.5f;
        bool bottom = areaSize.Y - (viewOrigin.Y + viewSize.Y) <= viewOrigin.Y + 0.5f;
        var corner = new Vector2(
            left ? viewOrigin.X + inset : viewOrigin.X + viewSize.X - inset,
            bottom ? viewOrigin.Y + viewSize.Y - inset : viewOrigin.Y + inset);
        var inward = new Vector2(left ? 1 : -1, bottom ? -1 : 1);
        int nx = (int)((viewSize.X - 2 * inset) / step), ny = (int)((viewSize.Y - 2 * inset) / step);

        bool Free(Vector2 c)
        {
            foreach (var (min, max) in obstacles)
            {
                var nearest = Vector2.Clamp(c, min, max);
                if (Vector2.DistanceSquared(nearest, c) < radius * radius) return false;
            }
            return true;
        }

        if (Free(corner)) return corner;
        // The nearest free spot first: grow a radius and take every grid step inside it.
        var steps = new List<(int I, int J)>((nx + 1) * (ny + 1));
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= ny; j++)
                steps.Add((i, j));
        steps.Sort((a, b) => (a.I * a.I + a.J * a.J).CompareTo(b.I * b.I + b.J * b.J));
        foreach (var (i, j) in steps)
        {
            var c = corner + inward * new Vector2(i * step, j * step);
            if (Free(c)) return c;
        }
        return corner;
    }

    /// <summary>Two unit axes spanning the face of <paramref name="axis"/>.</summary>
    private static (Vector3, Vector3) Tangents(int axis) => (axis % 3) switch
    {
        0 => (Vector3.UnitY, Vector3.UnitZ),
        1 => (Vector3.UnitZ, Vector3.UnitX),
        _ => (Vector3.UnitX, Vector3.UnitY),
    };

    /// <summary>Whether a convex or simple polygon contains the point (even-odd rule).</summary>
    public static bool Contains(IReadOnlyList<Vector2> polygon, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>The convex hull of the points (Andrew's monotone chain), counter-clockwise.</summary>
    public static Vector2[] ConvexHull(IEnumerable<Vector2> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        if (sorted.Length < 3) return sorted;
        var hull = new Vector2[sorted.Length * 2];
        int k = 0;
        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        foreach (var p in sorted)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], p) <= 0) k--;
            hull[k++] = p;
        }
        for (int i = sorted.Length - 2, lower = k + 1; i >= 0; i--)
        {
            var p = sorted[i];
            while (k >= lower && Cross(hull[k - 2], hull[k - 1], p) <= 0) k--;
            hull[k++] = p;
        }
        return hull[..(k - 1)];
    }
}

using System.Numerics;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Rendering;

/// <summary>
/// One animated camera as SloppyAnimatsu draws it: a small wireframe camera at the camera's eye, looking
/// down its local -Z, and an arrow from its lens to the looker target's point.
/// </summary>
public readonly record struct CameraGlyph(
    CameraAnimated Camera, CameraTarget? Target, Matrix4x4 Frame, Vector3? TargetPoint, float Size, bool Locked);

/// <summary>
/// The animated camera's viewport shape (SloppyAnimatsu's own): a wireframe body with
/// a lens and an "up" fin, plus an arrow from the lens to the target. The target point is where the
/// arrow ends; clicking the body picks the camera, clicking the arrow picks the target. A locked
/// rig (<see cref="CameraEditor.IsTargetLocked"/>) is drawn blue.
/// </summary>
public static class CameraShapes
{
    public static readonly Vector3 DefaultColor = new(0xB8 / 255f);
    public static readonly Vector3 SelectedColor = Vector3.One;
    public static readonly Vector3 LockedColor = new(0x4D / 255f, 0xA6 / 255f, 1f);
    public static readonly Vector3 LockedSelectedColor = new(0.65f, 0.85f, 1f);

    /// <summary>The camera's size relative to the scene's <c>TargetRenderSize</c>.</summary>
    public const float SizeRatio = 5f;

    /// <summary>The shown animated cameras of <paramref name="character"/>, posed by <paramref name="worlds"/>.</summary>
    public static List<CameraGlyph> Glyphs(Character character, PoseEvaluator evaluator, IReadOnlyList<Matrix4x4> worlds,
        Func<CameraTarget, bool>? locked = null)
    {
        var glyphs = new List<CameraGlyph>();
        float size = character.Model.Scene.TargetRenderSize * SizeRatio;
        for (int i = 0; i < evaluator.Controllers.Count && i < worlds.Count; i++)
        {
            if (evaluator.Controllers[i] is not CameraAnimated camera || !camera.IsShownInScene) continue;
            var target = camera.Target;
            Vector3? point = null;
            if (target != null && target.IsShownInScene && evaluator.IndexOf(target) is int t && t >= 0 && t < worlds.Count)
                point = worlds[t].Translation;
            glyphs.Add(new CameraGlyph(camera, target, ControllerShapes.Unscaled(worlds[i]), point, size,
                target != null && locked != null && locked(target)));
        }
        return glyphs;
    }

    /// <summary>
    /// Whether <paramref name="o"/> is drawn by this glyph (an animated camera, or the target of one):
    /// such an object gets no handle dot, pivot axes or looker disc, and selecting it lights the glyph.
    /// </summary>
    public static bool IsGlyphPart(Object3D? o) => o is CameraAnimated || o is CameraTarget { Camera: not null };

    /// <summary>The lens point: the front of the lens hood, where the arrow starts.</summary>
    public static Vector3 Lens(CameraGlyph g) => Vector3.Transform(new Vector3(0, 0, -0.6f * g.Size), g.Frame);

    /// <summary>
    /// The glyph's segments as pairs of world points; <c>Arrow</c> marks the target arrow's.
    /// </summary>
    public static IEnumerable<(Vector3 A, Vector3 B, bool Arrow)> Segments(CameraGlyph g)
    {
        float s = g.Size;
        Vector3 P(float x, float y, float z) => Vector3.Transform(new Vector3(x * s, y * s, z * s), g.Frame);
        // The body: a box behind the eye.
        float bx = 0.35f, by = 0.25f, bz0 = 0f, bz1 = 0.8f;
        var b = new[]
        {
            P(-bx, -by, bz0), P(bx, -by, bz0), P(bx, by, bz0), P(-bx, by, bz0),
            P(-bx, -by, bz1), P(bx, -by, bz1), P(bx, by, bz1), P(-bx, by, bz1),
        };
        for (int k = 0; k < 4; k++)
        {
            yield return (b[k], b[(k + 1) % 4], false);
            yield return (b[k + 4], b[(k + 1) % 4 + 4], false);
            yield return (b[k], b[k + 4], false);
        }
        // The lens hood: from a small square on the body's front to a wider one ahead.
        float l0 = 0.12f, l1 = 0.22f, lz = -0.6f;
        var l = new[]
        {
            P(-l0, -l0, 0), P(l0, -l0, 0), P(l0, l0, 0), P(-l0, l0, 0),
            P(-l1, -l1, lz), P(l1, -l1, lz), P(l1, l1, lz), P(-l1, l1, lz),
        };
        for (int k = 0; k < 4; k++)
        {
            yield return (l[k + 4], l[(k + 1) % 4 + 4], false);
            yield return (l[k], l[k + 4], false);
        }
        // The up fin: a triangle on top, so roll reads at a glance.
        var f0 = P(-0.15f, by, 0.25f);
        var f1 = P(0.15f, by, 0.25f);
        var f2 = P(0f, by + 0.22f, 0.25f);
        yield return (f0, f2, false);
        yield return (f2, f1, false);

        if (g.TargetPoint is not { } end) yield break;
        var start = Lens(g);
        var along = end - start;
        float length = along.Length();
        if (length < 1e-3f) yield break;
        var dir = along / length;
        var up = Vector3.TransformNormal(Vector3.UnitY, g.Frame);
        var right = Vector3.TransformNormal(Vector3.UnitX, g.Frame);
        // Keep the arrow's cross-section perpendicular to it even when the camera is not aimed at it.
        right -= dir * Vector3.Dot(right, dir);
        if (right.LengthSquared() < 1e-12f) right = Vector3.Cross(dir, up);
        right = Vector3.Normalize(right);
        up = Vector3.Normalize(Vector3.Cross(dir, right));
        // A square tube a quarter of the lens wide, then a pyramid head wider than the tube.
        float shaft = ShaftWidth * s * 0.5f;
        float head = MathF.Min(HeadLength * s, length * 0.4f), spread = MathF.Max(HeadWidth * s * 0.5f, shaft * 1.5f);
        var back = end - dir * head;
        Vector3 Corner(Vector3 c, float r, int k) => c + right * (k is 0 or 3 ? -r : r) + up * (k < 2 ? -r : r);
        for (int k = 0; k < 4; k++)
        {
            yield return (Corner(start, shaft, k), Corner(back, shaft, k), true);
            yield return (Corner(start, shaft, k), Corner(start, shaft, (k + 1) % 4), true);
            yield return (Corner(back, spread, k), Corner(back, spread, (k + 1) % 4), true);
            yield return (Corner(back, spread, k), end, true);
        }
    }

    /// <summary>The lens hood's front width (0.44 x size): the arrow's shaft is a quarter of it.</summary>
    public const float LensWidth = 0.44f;
    public const float ShaftWidth = LensWidth / 4f;
    public const float HeadWidth = 0.32f;
    public const float HeadLength = 0.45f;

    /// <summary>
    /// Line vertices (<see cref="ScenePose.LineVertexFloats"/> floats each) for every glyph. A camera
    /// whose eye is at <paramref name="eye"/> (the view looks through it) is left out.
    /// </summary>
    public static float[] Lines(IReadOnlyList<CameraGlyph> glyphs, Vector3 eye, Object3D? selected, IEnumerable<Object3D> alsoSelected)
    {
        var also = alsoSelected as ICollection<Object3D> ?? [.. alsoSelected];
        var vertices = new List<float>();
        foreach (var g in glyphs)
        {
            if (Vector3.DistanceSquared(g.Frame.Translation, eye) < g.Size * g.Size * 0.01f) continue;
            // Selecting the camera lights its body, selecting the target lights the arrow; no handle
            // dot is drawn for either (ScenePose leaves them out).
            bool bodyLit = IsSelected(g.Camera);
            bool arrowLit = g.Target != null && IsSelected(g.Target);
            foreach (var (a, b, arrow) in Segments(g))
            {
                bool lit = arrow ? arrowLit : bodyLit;
                var color = g.Locked ? (lit ? LockedSelectedColor : LockedColor) : (lit ? SelectedColor : DefaultColor);
                Add(vertices, a, color);
                Add(vertices, b, color);
            }
        }
        return vertices.ToArray();

        bool IsSelected(Object3D o) => ReferenceEquals(o, selected) || also.Contains(o);
    }

    private static void Add(List<float> vertices, Vector3 position, Vector3 color)
    {
        vertices.Add(position.X); vertices.Add(position.Y); vertices.Add(position.Z);
        vertices.Add(color.X); vertices.Add(color.Y); vertices.Add(color.Z);
    }
}

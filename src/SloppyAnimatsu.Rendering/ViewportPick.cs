using System.Numerics;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Rendering;

/// <summary>
/// What a click in the viewport selects
/// the controller drawn nearest to the pointer within a screen tolerance, hidden and
/// unpickable ones left out. The viewer's click handler and the command line share this rule so the
/// selection it makes can be tested and printed without a window.
/// </summary>
public static class ViewportPick
{
    /// <summary>How far (DIPs) the pointer may be from a controller and still pick it.</summary>
    public const float Tolerance = 10;

    /// <summary>
    /// The controller of <paramref name="controllers"/> drawn nearest to <paramref name="at"/>
    /// (viewport DIPs), or null when none is within <paramref name="tolerance"/>. A controller whose
    /// own or ancestor's flags hide it, or mark it unpickable, is skipped unless
    /// <paramref name="pickable"/> says otherwise; on a tie the later controller wins, as the one
    /// drawn over the other.
    /// </summary>
    public static Object3D? Nearest(
        IViewCamera camera,
        Vector2 viewport,
        IReadOnlyList<Object3D> controllers,
        IReadOnlyList<Matrix4x4> worlds,
        Vector2 at,
        float tolerance = Tolerance,
        Func<Object3D, bool>? pickable = null)
    {
        Object3D? best = null;
        float bestDistance = tolerance;
        for (int i = 0; i < controllers.Count && i < worlds.Count; i++)
        {
            var controller = controllers[i];
            if (!(pickable ?? Outliner.IsPickable)(controller)) continue;
            if (camera.Project(worlds[i].Translation, viewport) is not { } projected) continue;
            float d = Vector2.Distance(projected, at);
            if (d <= bestDistance)
            {
                bestDistance = d;
                best = controller;
            }
        }
        return best;
    }

    /// <summary>
    /// The click rule with the IK handles in it: an IK controller is drawn as a ring around its
    /// chain's leaf joint (a reverse foot as a sole outline), so a click on that outline picks the
    /// controller, unless a controller's own origin is nearer to the pointer. Everything else is
    /// <see cref="Nearest"/>.
    /// </summary>
    public static Object3D? At(
        IViewCamera camera,
        Vector2 viewport,
        Character character,
        PoseEvaluator evaluator,
        IReadOnlyList<Matrix4x4> worlds,
        Vector2 at,
        float tolerance = Tolerance,
        Func<Object3D, bool>? pickable = null)
    {
        Object3D? best = null;
        float bestDistance = tolerance;
        foreach (var (controller, points) in ControllerShapes.Outlines(character, evaluator, worlds))
        {
            if (!(pickable ?? Outliner.IsPickable)(controller)) continue;
            Vector2? previous = null;
            foreach (var point in points)
            {
                var projected = camera.Project(point, viewport);
                if (projected is { } b)
                {
                    float d = previous is { } a ? SegmentDistance(at, a, b) : Vector2.Distance(at, b);
                    if (d <= bestDistance)
                    {
                        bestDistance = d;
                        best = controller;
                    }
                }
                previous = projected;
            }
        }
        // An animated camera's wireframe picks the camera, its arrow the target.
        foreach (var glyph in CameraShapes.Glyphs(character, evaluator, worlds))
        {
            foreach (var (a, b, arrow) in CameraShapes.Segments(glyph))
            {
                Object3D owner = arrow && glyph.Target != null ? glyph.Target : glyph.Camera;
                if (!(pickable ?? Outliner.IsPickable)(owner)) continue;
                if (camera.Project(a, viewport) is not { } pa || camera.Project(b, viewport) is not { } pb) continue;
                float d = SegmentDistance(at, pa, pb);
                if (d <= bestDistance)
                {
                    bestDistance = d;
                    best = owner;
                }
            }
        }
        // An origin has to be strictly nearer than the outline to win: a ring shrunk to a few
        // pixels still belongs to its joint, but a click on a visible ring is the ring's.
        var origin = Nearest(camera, viewport, evaluator.Controllers, worlds, at, tolerance, pickable);
        if (best == null) return origin;
        if (origin != null && origin != best)
        {
            int index = evaluator.IndexOf(origin);
            if (index >= 0 && index < worlds.Count && camera.Project(worlds[index].Translation, viewport) is { } p &&
                Vector2.Distance(p, at) < bestDistance)
                return origin;
        }
        return best;
    }

    private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float length = ab.LengthSquared();
        float t = length < 1e-9f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / length, 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }
}

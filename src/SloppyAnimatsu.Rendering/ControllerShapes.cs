using System.Numerics;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Rendering;

/// <summary>
/// Triangles for the IK rings and reverse-foot sole outlines of one character
/// </summary>
public static class ControllerShapes
{
    private const int Steps = 120;

    private static readonly Vector3 Color = new(0xB8 / 255f);
    /// <summary>
    /// Viewer choice. An active IK is one
    /// whose lock is set (pinned in place), and it is drawn red.
    /// </summary>
    private static readonly Vector3 ActiveColor = new(0.95f, 0.15f, 0.12f);

    /// <summary>
    /// Points along the middle of each shown IK ring or sole band, in world space: what a click
    /// has to land near to pick the controller itself and not the joint at its centre.
    /// </summary>
    public static IEnumerable<(ControllerIK Controller, List<Vector3> Points)> Outlines(
        Character character, PoseEvaluator evaluator, IReadOnlyList<Matrix4x4> world)
    {
        float baseSize = character.Model.Scene.TargetRenderSize;
        foreach (var controller in character.IKControllers)
        {
            if (!controller.IsShownInScene) continue;
            int index = evaluator.IndexOf(controller);
            if (index < 0 || index >= world.Count) continue;
            List<Vector3>? flat;
            Matrix4x4 frame;
            if (controller is ControllerReverseFoot foot)
            {
                frame = Unscaled(world[index]);
                if (!SoleStrips(foot, evaluator, world, frame, baseSize, controller.TargetRenderSizeRatio, out flat, out _)) continue;
            }
            else
            {
                frame = RingFrame(controller, evaluator, world, index);
                RingStrips(baseSize * controller.TargetRenderSizeRatio, controller.DecoratorAxis, out flat, out _);
            }
            var points = new List<Vector3>(flat!.Count / 2);
            for (int i = 0; i + 1 < flat.Count; i += 2)
                points.Add(Vector3.Transform((flat[i] + flat[i + 1]) / 2, frame));
            yield return (controller, points);
        }
    }

    /// <summary>Appends triangles (<see cref="ScenePose.LineVertexFloats"/> floats per vertex) to <paramref name="triangles"/>.</summary>
    public static void Write(List<float> triangles, Character character, PoseEvaluator evaluator, Matrix4x4[] world,
                             Animation? animation, float time)
        => Write(triangles, character, evaluator, world, animation, time, null, null);

    /// <summary>
    /// The selection highlight: the shapes of the IK controllers in <paramref name="selected"/> only,
    /// all in <paramref name="color"/>, drawn over the normal pass. A selected IK lights up like a
    /// selected joint; the joints of its chain do not.
    /// </summary>
    public static void WriteSelected(List<float> triangles, Character character, PoseEvaluator evaluator, Matrix4x4[] world,
                                     IEnumerable<Object3D> selected, Vector3 color)
        => WriteSelected(triangles, character, evaluator, world, selected, color, null, 0, color);

    /// <summary>
    /// The selection highlight with the pinned state kept: a selected controller whose IK is active
    /// (<see cref="PoseEvaluator.IsIKActive"/>) at <paramref name="time"/> of
    /// <paramref name="animation"/> is drawn in <paramref name="activeColor"/>, a lit red, instead
    /// of <paramref name="color"/>.
    /// </summary>
    public static void WriteSelected(List<float> triangles, Character character, PoseEvaluator evaluator, Matrix4x4[] world,
                                     IEnumerable<Object3D> selected, Vector3 color, Animation? animation, float time, Vector3 activeColor)
    {
        var set = new HashSet<Object3D>(selected.OfType<ControllerIK>(), ReferenceEqualityComparer.Instance);
        if (set.Count > 0) Write(triangles, character, evaluator, world, animation, time, set, color, activeColor);
    }

    /// <summary>
    /// The ring's frame: the leaf joint's orientation at the controller's own position. A handle on
    /// its chain sits on the leaf joint ("Where it is drawn"); one dragged out of reach at an IK key
    /// is drawn where it was left.
    /// </summary>
    private static Matrix4x4 RingFrame(ControllerIK controller, PoseEvaluator evaluator, IReadOnlyList<Matrix4x4> world, int index)
    {
        var chain = controller.Chain;
        int leaf = chain.Count > 0 ? evaluator.IndexOf(chain[^1]) : -1;
        var frame = Unscaled(leaf >= 0 && leaf < world.Count ? world[leaf] : world[index]);
        frame.Translation = world[index].Translation;
        return frame;
    }

    private static void Write(List<float> triangles, Character character, PoseEvaluator evaluator, Matrix4x4[] world,
                              Animation? animation, float time, HashSet<Object3D>? only, Vector3? fixedColor,
                              Vector3? fixedActiveColor = null)
    {
        float baseSize = character.Model.Scene.TargetRenderSize;
        foreach (var controller in character.IKControllers)
        {
            if (!controller.IsShownInScene) continue;
            if (only != null && !only.Contains(controller)) continue;
            int index = evaluator.IndexOf(controller);
            if (index < 0 || index >= world.Length) continue;
            float size = baseSize * controller.TargetRenderSizeRatio;
            bool active = (fixedColor == null || fixedActiveColor != null) && animation != null
                && evaluator.IsIKActive(animation, controller, time);
            var color = active ? fixedActiveColor ?? ActiveColor : fixedColor ?? Color;

            if (controller is ControllerReverseFoot foot)
                Sole(triangles, foot, evaluator, world, Unscaled(world[index]), baseSize, controller.TargetRenderSizeRatio, color);
            else
                Ring(triangles, RingFrame(controller, evaluator, world, index), size, controller.DecoratorAxis, color);
        }
    }

    /// <summary>Annulus of radii 1..1.3 plus a rim wall ±0.1 high, normal to <paramref name="axis"/>.</summary>
    private static void Ring(List<float> triangles, Matrix4x4 frame, float size, byte axis, Vector3 color)
    {
        RingStrips(size, axis, out var flat, out var wall);
        Strip(triangles, flat, frame, color);
        Strip(triangles, wall, frame, color);
    }

    private static void RingStrips(float size, byte axis, out List<Vector3> flat, out List<Vector3> wall)
    {
        // Axis 0 = ring in YZ, 1 = in XZ, 2 = in XY.
        var (u, v, n) = axis switch
        {
            0 => (Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX),
            1 => (Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
            _ => (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ),
        };
        flat = new List<Vector3>();
        wall = new List<Vector3>();
        for (int k = 0; k <= Steps; k++)
        {
            float a = k * 2 * MathF.PI / Steps;
            var dir = MathF.Cos(a) * u + MathF.Sin(a) * v;
            flat.Add(dir * size);
            flat.Add(dir * 1.3f * size);
            wall.Add((dir * 1.3f - n * 0.1f) * size);
            wall.Add((dir * 1.3f + n * 0.1f) * size);
        }
    }

    /// <summary>The rounded sole band and its outer wall, in the controller's unscaled frame.</summary>
    private static void Sole(List<float> triangles, ControllerReverseFoot foot, PoseEvaluator evaluator, Matrix4x4[] world,
                             Matrix4x4 frame, float baseSize, float ratio, Vector3 color)
    {
        if (!SoleStrips(foot, evaluator, world, frame, baseSize, ratio, out var flat, out var wall)) return;
        Strip(triangles, flat!, frame, color);
        Strip(triangles, wall!, frame, color);
    }

    private static bool SoleStrips(ControllerReverseFoot foot, PoseEvaluator evaluator, IReadOnlyList<Matrix4x4> world,
                                   Matrix4x4 frame, float baseSize, float ratio, out List<Vector3>? flatOut, out List<Vector3>? wallOut)
    {
        flatOut = wallOut = null;
        var shoe = foot.Shoe;
        if (shoe.Count < 4 || !Matrix4x4.Invert(frame, out var toFrame)) return false;
        Span<Vector3> p = stackalloc Vector3[4];
        for (int i = 0; i < 4; i++)
        {
            int s = evaluator.IndexOf(shoe[i]);
            if (s < 0 || s >= world.Count) return false;
            p[i] = Vector3.Transform(world[s].Translation, toFrame);
            p[i].Y = 0;
        }
        var (heel, left, right, toes) = (p[0], p[1], p[2], p[3]);

        var c = (heel + toes) / 2;
        float length = Vector3.Distance(toes, heel);
        if (length < 1e-6f) return false;
        var d = (toes - heel) / length;
        var across = left - right;
        across -= d * Vector3.Dot(across, d);
        if (across.LengthSquared() < 1e-12f) return false;
        var n = Vector3.Normalize(across);
        c += n * Vector3.Dot((left + right) / 2 - c, n);
        float width = Vector3.Dot(left - right, n);
        float rho = width / 2;
        float f = length < 1.5f * width ? length / (1.5f * width) : 1f;
        float half = (length - 2 * f * rho) / 2;
        var toeCentre = c + d * half;
        var heelCentre = c - d * half;
        float radius = baseSize * 0.25f * ratio + rho;
        float band = baseSize * 0.325f * ratio;
        float height = baseSize * 0.2f * ratio;

        // (cos term, sin term, raised) axes per DecoratorAxis ("Reverse-foot controller: sole outline").
        var (u, v, up) = foot.DecoratorAxis switch
        {
            0 => (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ),
            1 => (Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX),
            _ => (Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
        };
        var flat = new List<Vector3>();
        var wall = new List<Vector3>();
        void Step(Vector3 centre, float a)
        {
            float cos = MathF.Cos(a), sin = MathF.Sin(a);
            var inner = centre + u * (cos * radius) + v * (sin * radius * f);
            var outer = centre + u * (cos * (radius + band)) + v * (sin * (radius * f + band));
            flat.Add(inner);
            flat.Add(outer);
            wall.Add(outer);
            wall.Add(outer + up * height);
        }
        for (int k = 0; k <= Steps / 2; k++) Step(toeCentre, k * 2 * MathF.PI / Steps);
        for (int k = 0; k <= Steps / 2; k++) Step(heelCentre, k * 2 * MathF.PI / Steps + MathF.PI);
        Step(toeCentre, 0);
        flatOut = flat;
        wallOut = wall;
        return true;
    }

    /// <summary>A triangle strip, written as separate triangles in world space.</summary>
    private static void Strip(List<float> triangles, List<Vector3> strip, Matrix4x4 frame, Vector3 color)
    {
        for (int i = 2; i < strip.Count; i++)
            foreach (var q in (ReadOnlySpan<Vector3>)[strip[i - 2], strip[i - 1], strip[i]])
            {
                var w = Vector3.Transform(q, frame);
                triangles.Add(w.X); triangles.Add(w.Y); triangles.Add(w.Z);
                triangles.Add(color.X); triangles.Add(color.Y); triangles.Add(color.Z);
            }
    }

    /// <summary><paramref name="m"/> with the length of each axis row divided out ("Size").</summary>
    internal static Matrix4x4 Unscaled(Matrix4x4 m)
    {
        static Vector3 Unit(Vector3 r) => r.LengthSquared() > 1e-12f ? Vector3.Normalize(r) : r;
        var x = Unit(new Vector3(m.M11, m.M12, m.M13));
        var y = Unit(new Vector3(m.M21, m.M22, m.M23));
        var z = Unit(new Vector3(m.M31, m.M32, m.M33));
        return new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, m.M41, m.M42, m.M43, 1);
    }
}

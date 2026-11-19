using System.Numerics;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The FABRIK chain solver, following.
/// Matrices are row-vector (<see cref="AktMath"/>); quaternion products are Hamilton products.
/// </summary>
public static class IKSolver
{
    private const int MaxIterations = 300;
    private const float Epsilon = 1e-5f;

    /// <summary>
    /// Re-solves a chain so that its last joint reaches <paramref name="target"/>.
    /// </summary>
    /// <param name="base">World transform of the parent of the chain's first joint.</param>
    /// <param name="chain">Local transform of each chain joint, root first.</param>
    /// <param name="target">World transform of the IK controller.</param>
    /// <param name="accumulatesScale">
    /// Per joint except the last: true when the joint does not compensate its parent's scale
    /// (<c>Flags &amp; 6</c> = 0), so its own scale adds to the scale passed down the chain.
    /// </param>
    /// <param name="twist">Rotation in radians about the root-to-target axis applied to the first joint.</param>
    /// <returns>The joints' local transforms: translations and scales kept, rotations solved.</returns>
    public static Transform[] Solve(Transform @base, IReadOnlyList<Transform> chain, Transform target,
        IReadOnlyList<bool> accumulatesScale, float twist = 0f)
    {
        int n = chain.Count;
        var result = chain.ToArray();
        if (n < 2) return result;

        // Initial chain in chain space: the base frame without its scale.
        var local = new Matrix4x4[n];
        var p = new Vector3[n];
        var scale = @base.Scale;
        var world = Matrix4x4.Identity;
        for (int i = 0; i < n; i++)
        {
            var m = Matrix4x4.CreateFromQuaternion(chain[i].Rotation);
            m.Translation = chain[i].Translation * scale;
            local[i] = m;
            world = i == 0 ? m : m * world;
            p[i] = world.Translation;
            if (i < n - 1)
                scale = i < accumulatesScale.Count && accumulatesScale[i] ? scale * chain[i].Scale : chain[i].Scale;
        }
        var length = new float[n - 1];
        for (int i = 0; i < n - 1; i++) length[i] = Vector3.Distance(p[i + 1], p[i]);

        var t = Vector3.Transform(target.Translation - @base.Translation, Quaternion.Inverse(@base.Rotation));
        var root = p[0];

        var pre = FromTo(Normalize(p[n - 1] - root), Normalize(t - root));
        for (int i = 0; i < n; i++) p[i] = root + Vector3.Transform(p[i] - root, pre);

        for (int iteration = 0; ; )
        {
            var previousEnd = p[n - 1];
            Pass(p, length, t, n - 1, 0, -1, -1);
            Pass(p, length, root, 0, n - 1, 1, 0);
            if (++iteration >= MaxIterations) break;
            if (Vector3.Distance(p[n - 1], t) < Epsilon || Vector3.Distance(p[n - 1], previousEnd) < Epsilon) break;
        }

        // Rotations from the solved positions.
        var parent = Matrix4x4.Identity;
        var rotation = new Quaternion[n];
        for (int i = 0; i < n - 1; i++)
        {
            var direction = Normalize(p[i + 1] - p[i]);
            Matrix4x4.Invert(local[i] * parent, out var inverse);
            var directionLocal = Vector3.TransformNormal(direction, inverse);
            var swing = FromTo(Normalize(local[i + 1].Translation), directionLocal);
            rotation[i] = NormalizeQ(chain[i].Rotation * swing);
            var solved = Matrix4x4.CreateFromQuaternion(rotation[i]);
            solved.Translation = local[i].Translation;
            parent = solved * parent;
        }
        var twistQ = Quaternion.Identity;
        if (twist != 0f)
        {
            var axis = Normalize(t - root);
            twistQ = new Quaternion(axis * MathF.Sin(twist / 2), MathF.Cos(twist / 2));
            rotation[0] = twistQ * rotation[0];
        }

        // The last joint takes the target's world rotation.
        var parentWorld = @base.Rotation * twistQ * Quaternion.CreateFromRotationMatrix(parent);
        rotation[n - 1] = Quaternion.Inverse(parentWorld) * target.Rotation;

        for (int i = 0; i < n; i++) result[i] = chain[i] with { Rotation = rotation[i] };
        return result;
    }

    /// <summary>
    /// Re-solves a chain from its base's full world matrix, staying exact when the chain or its
    /// ancestors carry non-uniform scale.
    /// </summary>
    /// <remarks>
    /// The FABRIK solve above models the chain as rotations applied after the accumulated scale,
    /// which is what world composition does only for uniform scale. A scale between two rotations
    /// (a non-uniform scale on the base or on a chain joint whose child does not compensate it)
    /// makes the real chain end miss the target. With <paramref name="exactScale"/> such a solve is
    /// corrected: the target handed to FABRIK is moved by Newton steps on the chain's real forward
    /// kinematics (<see cref="WorldComposition"/>) until the real end lands on the target, and the
    /// last joint's rotation is taken against its real parent frame. A chain without such scale is
    /// left exactly as the plain solve returns it.
    /// </remarks>
    /// <param name="baseWorld">World matrix of the parent of the chain's first joint.</param>
    /// <param name="baseScale">Local scale of that parent.</param>
    /// <param name="chain">Local transform of each chain joint, root first.</param>
    /// <param name="target">World matrix of the IK controller (or the reverse foot's ankle locator).</param>
    /// <param name="compensates">Per chain joint: whether it compensates its parent's scale.</param>
    /// <param name="exactScale">False reproduces the solve, which misses under such scale.</param>
    public static Transform[] Solve(Matrix4x4 baseWorld, Vector3 baseScale, IReadOnlyList<Transform> chain, Matrix4x4 target,
        IReadOnlyList<bool> compensates, bool exactScale = true, float twist = 0f)
    {
        int n = chain.Count;
        var @base = Transform.FromMatrixLoose(baseWorld);
        var goal = Transform.FromMatrixLoose(target);
        var accumulates = Enumerable.Range(0, Math.Max(0, n - 1))
            .Select(i => !(i < compensates.Count && compensates[i])).ToArray();
        var result = Solve(@base, chain, goal, accumulates, twist);
        if (!exactScale || n < 2 || !Distorted(baseWorld, baseScale, chain, compensates)) return result;

        bool Compensates(int i) => i < compensates.Count && compensates[i];
        Matrix4x4 Parent(Transform[] solved, out Vector3 parentScale)
        {
            var world = baseWorld;
            parentScale = baseScale;
            for (int i = 0; i < n - 1; i++)
            {
                world = WorldComposition.Compose(solved[i].ToMatrix(), world, parentScale, Compensates(i));
                parentScale = solved[i].Scale;
            }
            return world;
        }
        Vector3 End(Transform[] solved) =>
            Vector3.Transform(chain[n - 1].Translation, Parent(solved, out _));

        float length = 0;
        {
            var world = baseWorld;
            var parentScale = baseScale;
            var previous = Vector3.Zero;
            for (int i = 0; i < n; i++)
            {
                world = WorldComposition.Compose(chain[i].ToMatrix(), world, parentScale, Compensates(i));
                parentScale = chain[i].Scale;
                if (i > 0) length += Vector3.Distance(world.Translation, previous);
                previous = world.Translation;
            }
        }
        float tolerance = MathF.Max(1e-4f * length, 1e-5f);
        float h = MathF.Max(1e-3f * length, 1e-4f);

        Transform[] SolveFor(Vector3 aim) => Solve(@base, chain, goal with { Translation = aim }, accumulates, twist);
        // Start from where the plain solve's model puts the chain's own end, offset by its real
        // miss: a chain that already reaches the target is then returned unchanged, so solving a
        // solved pose again does not drift.
        var x = goal.Translation + ModelEnd(@base, chain, accumulates) - End(chain.ToArray());
        result = SolveFor(x);
        var residual = goal.Translation - End(result);
        for (int iteration = 0; iteration < 8 && residual.Length() > tolerance; iteration++)
        {
            var f = goal.Translation - residual;
            var columns = new Vector3[3];
            for (int a = 0; a < 3; a++)
            {
                var step = a == 0 ? Vector3.UnitX * h : a == 1 ? Vector3.UnitY * h : Vector3.UnitZ * h;
                columns[a] = (End(SolveFor(x + step)) - f) / h;
            }
            var jacobian = new Matrix4x4(
                columns[0].X, columns[0].Y, columns[0].Z, 0,
                columns[1].X, columns[1].Y, columns[1].Z, 0,
                columns[2].X, columns[2].Y, columns[2].Z, 0,
                0, 0, 0, 1);
            if (!Matrix4x4.Invert(jacobian, out var inverse)) break;
            var dx = Vector3.TransformNormal(residual, inverse);
            bool improved = false;
            for (int halving = 0; halving < 5 && !improved; halving++, dx *= 0.5f)
            {
                var trial = SolveFor(x + dx);
                var trialResidual = goal.Translation - End(trial);
                if (trialResidual.Length() < residual.Length())
                {
                    (x, result, residual, improved) = (x + dx, trial, trialResidual, true);
                }
            }
            if (!improved) break;
        }

        // The last joint takes the target's world rotation against its real parent frame.
        var parent = Parent(result, out var lastParentScale);
        var frame = Compensates(n - 1) ? WorldComposition.Compensated(parent, lastParentScale) : parent;
        var frameRotation = Transform.FromMatrixLoose(frame).Rotation;
        result[n - 1] = result[n - 1] with { Rotation = NormalizeQ(Quaternion.Inverse(frameRotation) * goal.Rotation) };
        return result;
    }

    /// <summary>World position of the chain's last joint as the plain solve models it (scale before rotation).</summary>
    private static Vector3 ModelEnd(Transform @base, IReadOnlyList<Transform> chain, IReadOnlyList<bool> accumulates)
    {
        int n = chain.Count;
        var scale = @base.Scale;
        var world = Matrix4x4.Identity;
        for (int i = 0; i < n; i++)
        {
            var m = Matrix4x4.CreateFromQuaternion(chain[i].Rotation);
            m.Translation = chain[i].Translation * scale;
            world = i == 0 ? m : m * world;
            if (i < n - 1)
                scale = i < accumulates.Count && accumulates[i] ? scale * chain[i].Scale : chain[i].Scale;
        }
        return @base.Translation + Vector3.Transform(world.Translation, @base.Rotation);
    }

    /// <summary>
    /// Whether a scale sits between two rotations of the chain: a non-uniform base frame, or a
    /// non-uniform scale on a chain joint that its child does not compensate.
    /// </summary>
    private static bool Distorted(Matrix4x4 baseWorld, Vector3 baseScale, IReadOnlyList<Transform> chain, IReadOnlyList<bool> compensates)
    {
        static bool NonUniform(Vector3 s) =>
            MathF.Abs(s.X - s.Y) > 1e-4f * MathF.Max(MathF.Abs(s.X), MathF.Abs(s.Y))
            || MathF.Abs(s.X - s.Z) > 1e-4f * MathF.Max(MathF.Abs(s.X), MathF.Abs(s.Z));
        var rows = new Vector3(
            new Vector3(baseWorld.M11, baseWorld.M12, baseWorld.M13).Length(),
            new Vector3(baseWorld.M21, baseWorld.M22, baseWorld.M23).Length(),
            new Vector3(baseWorld.M31, baseWorld.M32, baseWorld.M33).Length());
        bool firstCompensates = compensates.Count > 0 && compensates[0];
        if (NonUniform(rows)) return true;
        if (!firstCompensates && NonUniform(baseScale)) return true;
        for (int i = 0; i < chain.Count - 1; i++)
            if (NonUniform(chain[i].Scale)) return true;
        return false;
    }

    /// <summary>
    /// One pass: moves joint <paramref name="start"/> to <paramref name="goal"/> and pulls the
    /// joints after it (by <paramref name="step"/>) to their segment lengths. Each direction mixes
    /// 1/n toward the moved joint's new position and 1 − 1/n toward its old one.
    /// </summary>
    private static void Pass(Vector3[] p, float[] length, Vector3 goal, int start, int end, int step, int lengthOffset)
    {
        float toNew = 1f / p.Length, toOld = 1f - toNew;
        var old = p[start];
        p[start] = goal;
        for (int i = start; i != end; i += step)
        {
            var next = p[i + step];
            var d = Normalize(toNew * (next - p[i]) + toOld * (next - old));
            old = next;
            p[i + step] = p[i] + d * length[lengthOffset + i];
        }
    }

    /// <summary>Rotation taking unit vector <paramref name="a"/> to <paramref name="b"/>: (a×b, 1 + a·b), normalized.</summary>
    private static Quaternion FromTo(Vector3 a, Vector3 b) =>
        NormalizeQ(new Quaternion(Vector3.Cross(a, b), 1f + Vector3.Dot(a, b)));

    private static Quaternion NormalizeQ(Quaternion q)
    {
        float l = q.Length();
        return l > 0 ? q * (1f / l) : Quaternion.Identity;
    }

    private static Vector3 Normalize(Vector3 v)
    {
        float l = v.Length();
        return l > 0 ? v / l : v;
    }
}

using System.Numerics;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Tests;

/// <summary>The IK override pieces on synthetic chains.</summary>
public class IKTests
{
    private const float Tolerance = 1e-3f;

    /// <summary>
    /// Two segments of length 10: shoulder at the origin, elbow bent about Z, wrist. (A straight
    /// chain stays straight under FABRIK, so it could not fold towards a nearer target.)
    /// </summary>
    private static readonly Transform[] Arm =
    {
        Transform.Identity,
        new(new Vector3(10, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f), Vector3.One),
        new(new Vector3(10, 0, 0), Quaternion.Identity, Vector3.One),
    };

    private static readonly bool[] Accumulates = { true, true };

    private static Vector3 EndPosition(Transform @base, IReadOnlyList<Transform> chain)
    {
        var world = @base.ToMatrix();
        for (int i = 0; i < chain.Count; i++) world = chain[i].ToMatrix() * world;
        return world.Translation;
    }

    [Fact]
    public void ReachableTargetIsReached()
    {
        var @base = new Transform(new Vector3(5, 2, -3), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f), Vector3.One);
        var target = new Transform(new Vector3(17, 9, 1), Quaternion.Identity, Vector3.One);

        var result = IKSolver.Solve(@base, Arm, target, Accumulates);

        Assert.True(Vector3.Distance(EndPosition(@base, result), target.Translation) < Tolerance);
        // Segment lengths are kept: only rotations change.
        Assert.All(result.Zip(Arm), p => Assert.Equal(p.Second.Translation, p.First.Translation));
    }

    [Fact]
    public void UnreachableTargetStretchesTheChainTowardsIt()
    {
        var target = new Transform(new Vector3(0, 50, 0), Quaternion.Identity, Vector3.One);

        var result = IKSolver.Solve(Transform.Identity, Arm, target, Accumulates);

        Assert.True(Vector3.Distance(EndPosition(Transform.Identity, result), new Vector3(0, 20, 0)) < Tolerance);
    }

    [Fact]
    public void DecomposeInvertsCompose()
    {
        var @base = new Transform(new Vector3(1, 2, 3), Quaternion.CreateFromYawPitchRoll(0.3f, -1.1f, 2.0f), new Vector3(1, 2, 0.5f));
        var local = new Transform(new Vector3(-4, 0.5f, 7), Quaternion.CreateFromYawPitchRoll(-2.2f, 0.4f, 0.9f), new Vector3(3, 1, 0.25f));

        var round = PoseEvaluator.Compose(@base, PoseEvaluator.Decompose(@base, local));

        Assert.True(Vector3.Distance(round.Translation, local.Translation) < Tolerance);
        Assert.True(MathF.Abs(Quaternion.Dot(round.Rotation, local.Rotation)) > 1 - Tolerance);
        Assert.True(Vector3.Distance(round.Scale, local.Scale) < Tolerance);
    }

    [Fact]
    public void LooseDecompositionHandlesShear()
    {
        // A rotated child under a non-uniformly scaled parent: the world carries shear.
        var child = Matrix4x4.CreateFromYawPitchRoll(0.6f, 0.2f, -0.4f) * Matrix4x4.CreateTranslation(1, 2, 3);
        var parent = Matrix4x4.CreateScale(1, 0.5f, 2) * Matrix4x4.CreateTranslation(4, 5, 6);
        var world = child * parent;
        Assert.False(Transform.TryFromMatrix(world, out _));

        var t = Transform.FromMatrixLoose(world);

        Assert.Equal(world.Translation, t.Translation);
        Assert.Equal(new Vector3(world.M11, world.M12, world.M13).Length(), t.Scale.X, 4);
        Assert.True(MathF.Abs(t.Rotation.Length() - 1) < Tolerance);
        Assert.True(float.IsFinite(t.Rotation.W));
    }

    [Fact]
    public void LooseDecompositionMatchesExactWithoutShear()
    {
        var m = new Transform(new Vector3(1, 2, 3), Quaternion.CreateFromYawPitchRoll(0.3f, 0.2f, 0.1f), new Vector3(2, 3, 4)).ToMatrix();
        Assert.True(Transform.TryFromMatrix(m, out var exact));
        Assert.Equal(exact, Transform.FromMatrixLoose(m));
    }

    // ---- Scaled chains ----

    /// <summary>World position of the chain's last joint under the real world composition.</summary>
    private static Vector3 ComposedEnd(Matrix4x4 @base, Vector3 baseScale, IReadOnlyList<Transform> chain, IReadOnlyList<bool> compensates)
    {
        var world = @base;
        var scale = baseScale;
        for (int i = 0; i < chain.Count; i++)
        {
            world = WorldComposition.Compose(chain[i].ToMatrix(), world, scale, compensates[i]);
            scale = chain[i].Scale;
        }
        return world.Translation;
    }

    /// <summary>Three joints: shoulder scaled non-uniformly, elbow bent about Z and Y, wrist.</summary>
    private static readonly Transform[] ScaledArm =
    {
        new(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f), new Vector3(1, 2, 0.6f)),
        new(new Vector3(10, 0, 0), Quaternion.CreateFromYawPitchRoll(0.4f, 0f, 0.5f), Vector3.One),
        new(new Vector3(10, 0, 0), Quaternion.Identity, Vector3.One),
    };

    private static readonly bool[] NoCompensation = { false, false, false };

    [Fact]
    public void ScaledChain_ExactSolveReachesTheTarget()
    {
        var @base = Matrix4x4.CreateFromYawPitchRoll(0.2f, 0.1f, 0f) * Matrix4x4.CreateTranslation(3, 1, 2);
        var target = Matrix4x4.CreateTranslation(14, 12, 5);

        var exact = IKSolver.Solve(@base, Vector3.One, ScaledArm, target, NoCompensation);
        var plain = IKSolver.Solve(@base, Vector3.One, ScaledArm, target, NoCompensation, exactScale: false);

        Assert.True(Vector3.Distance(ComposedEnd(@base, Vector3.One, exact, NoCompensation), target.Translation) < Tolerance);
        // The solve (scale applied after the rotations) misses here: the case the fix is for.
        Assert.True(Vector3.Distance(ComposedEnd(@base, Vector3.One, plain, NoCompensation), target.Translation) > 0.1f);
        Assert.All(exact.Zip(ScaledArm), p => Assert.Equal(p.Second.Scale, p.First.Scale));
    }

    [Fact]
    public void ScaledChain_SolvingAReachedTargetChangesNothing()
    {
        var @base = Matrix4x4.CreateTranslation(1, 2, 3);
        var target = Matrix4x4.CreateTranslation(ComposedEnd(@base, Vector3.One, ScaledArm, NoCompensation));

        var result = IKSolver.Solve(@base, Vector3.One, ScaledArm, target, NoCompensation);

        for (int i = 0; i < ScaledArm.Length - 1; i++)
            Assert.True(MathF.Abs(Quaternion.Dot(result[i].Rotation, ScaledArm[i].Rotation)) > 1 - 1e-5f, $"joint {i}");
    }

    [Fact]
    public void UnscaledChain_ExactSolveIsThePlainSolve()
    {
        var @base = Matrix4x4.CreateFromYawPitchRoll(0.7f, 0f, 0f) * Matrix4x4.CreateTranslation(5, 2, -3);
        var target = Matrix4x4.CreateTranslation(17, 9, 1);
        var compensates = new[] { true, true, true };
        var chain = Arm.Select(t => t).ToArray();

        var exact = IKSolver.Solve(@base, Vector3.One, chain, target, compensates);
        var plain = IKSolver.Solve(@base, Vector3.One, chain, target, compensates, exactScale: false);

        Assert.Equal(plain, exact);
    }
}

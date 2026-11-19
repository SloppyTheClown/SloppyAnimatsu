using System.Numerics;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Tests;

/// <summary>The reverse-foot pieces on synthetic data.</summary>
public class ReverseFootTests
{
    private const float Tolerance = 1e-3f;
    private const float Deg = MathF.PI / 180f;

    private static readonly ReverseFootLimits Limits = new(60 * Deg, 60 * Deg, 75 * Deg, 85 * Deg, 60 * Deg, 60 * Deg);

    private static readonly Transform RestPlant = new(new Vector3(0, 0, -4), Quaternion.Identity, Vector3.One);
    private static readonly Transform RestAnkle = new(new Vector3(0, 5, -2), Quaternion.Identity, Vector3.One);

    [Fact]
    public void SmallRollGoesToThePlantAndLargeRollToTheToes()
    {
        Assert.Equal((0.5f, 0f), ReverseFootSolver.SolveRoll(0.5f, Limits, RestPlant, RestAnkle));
        Assert.Equal((0f, 80 * Deg), ReverseFootSolver.SolveRoll(80 * Deg, Limits, RestPlant, RestAnkle));
    }

    [Fact]
    public void RollSplitIsContinuousAtBothLimits()
    {
        var atPre = ReverseFootSolver.SolveRoll(60 * Deg, Limits, RestPlant, RestAnkle);
        Assert.Equal(60 * Deg, atPre.Plant, Tolerance);
        Assert.Equal(0f, atPre.Toes, Tolerance);

        var nearToes = ReverseFootSolver.SolveRoll(75 * Deg - 1e-5f, Limits, RestPlant, RestAnkle);
        Assert.Equal(0f, nearToes.Plant, Tolerance);
        Assert.Equal(75 * Deg, nearToes.Toes, Tolerance);
    }

    private static readonly Transform[] ShoeRest = Enumerable.Range(0, 7)
        .Select(i => new Transform(new Vector3(i, 1, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.1f * i), Vector3.One))
        .ToArray();

    [Fact]
    public void ZeroAnglesLeaveTheShoeAtRest()
    {
        var locals = ReverseFootSolver.ShoeLocals(ShoeRest, 0, 0, 0, Limits, Vector3.UnitX);
        Assert.All(locals.Zip(ShoeRest), p => Assert.True(Quaternion.Dot(p.First.Rotation, p.Second.Rotation) > 1 - 1e-6f));
    }

    [Fact]
    public void EachAngleTurnsItsOwnPivot()
    {
        static int[] Turned(Transform[] locals) =>
            Enumerable.Range(0, 7).Where(i => Quaternion.Dot(locals[i].Rotation, ShoeRest[i].Rotation) < 1 - 1e-6f).ToArray();

        Assert.Equal(new[] { 0 }, Turned(ReverseFootSolver.ShoeLocals(ShoeRest, -0.3f, 0, 0, Limits, Vector3.UnitX)));
        Assert.Equal(new[] { 5 }, Turned(ReverseFootSolver.ShoeLocals(ShoeRest, 0.3f, 0, 0, Limits, Vector3.UnitX)));
        Assert.Equal(new[] { 3 }, Turned(ReverseFootSolver.ShoeLocals(ShoeRest, 80 * Deg, 0, 0, Limits, Vector3.UnitX)));
        Assert.Equal(new[] { 3 }, Turned(ReverseFootSolver.ShoeLocals(ShoeRest, 0, 0.3f, 0, Limits, Vector3.UnitX)));
        Assert.Equal(new[] { 1 }, Turned(ReverseFootSolver.ShoeLocals(ShoeRest, 0, 0, -0.3f, Limits, Vector3.UnitX)));
        Assert.Equal(new[] { 2 }, Turned(ReverseFootSolver.ShoeLocals(ShoeRest, 0, 0, 0.3f, Limits, Vector3.UnitX)));
    }

    [Fact]
    public void AnglesAreClampedToTheLimits()
    {
        var clamped = ReverseFootSolver.ShoeLocals(ShoeRest, 0, 0, 2f, Limits, Vector3.UnitX);
        var atLimit = ReverseFootSolver.ShoeLocals(ShoeRest, 0, 0, 60 * Deg, Limits, Vector3.UnitX);
        Assert.True(Quaternion.Dot(clamped[2].Rotation, atLimit[2].Rotation) > 1 - 1e-6f);
    }

    [Fact]
    public void PureTwistIsRemoved()
    {
        var axis = Vector3.Normalize(new Vector3(1, 2, -0.5f));
        var swing = ReverseFootSolver.RemoveTwist(Quaternion.CreateFromAxisAngle(axis, 0.8f), axis);
        Assert.Equal(1f, MathF.Abs(swing.W), Tolerance);
    }

    [Fact]
    public void RotationWithoutTwistIsKept()
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.8f);
        var swing = ReverseFootSolver.RemoveTwist(q, Vector3.UnitZ);
        Assert.True(MathF.Abs(Quaternion.Dot(q, swing)) > 1 - 1e-5f);
    }

    [Fact]
    public void LegReachesTheAnkleAndAimsAtPlantAndToes()
    {
        // Thigh at the origin, calf and ankle 10 below each, ball 3 forward and 2 down from the ankle.
        Transform[] chain =
        {
            Transform.Identity,
            new(new Vector3(0, -10, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f), Vector3.One),
            new(new Vector3(0, -10, 0), Quaternion.Identity, Vector3.One),
            new(new Vector3(0, -2, 3), Quaternion.Identity, Vector3.One),
        };
        var ankle = new Vector3(1, -17, 2);
        var plant = ankle + new Vector3(0.5f, -2.5f, 2.5f);
        var toes = plant + new Vector3(0.3f, -0.5f, 3);
        static Matrix4x4 At(Vector3 p) => Matrix4x4.CreateTranslation(p);

        var result = ReverseFootSolver.Solve(Matrix4x4.Identity, Vector3.One, chain, new bool[4],
            chain[3], RestPlant, At(ankle), At(plant), At(toes));

        Assert.NotNull(result);
        var worlds = new Matrix4x4[4];
        var parent = Matrix4x4.Identity;
        for (int i = 0; i < 4; i++) parent = worlds[i] = result![i].ToMatrix() * parent;
        Assert.True(Vector3.Distance(worlds[2].Translation, ankle) < Tolerance);
        var toPlant = Vector3.Normalize(plant - worlds[2].Translation);
        Assert.True(Vector3.Dot(Vector3.Normalize(worlds[3].Translation - worlds[2].Translation), toPlant) > 1 - 1e-4f);
        // The ball's +Z (toward SkeletonToes from SkeletonPlant at rest) points at the toes.
        var toToes = Vector3.Normalize(toes - worlds[3].Translation);
        Assert.True(Vector3.Dot(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, worlds[3])), toToes) > 1 - 1e-4f);
    }
}

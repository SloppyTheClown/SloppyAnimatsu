using System.Numerics;

namespace SloppyAnimatsu.Core.Model;

/// <summary>Reverse-foot angle limits in radians.</summary>
public readonly record struct ReverseFootLimits(
    float RollBack, float RollPre, float RollToes, float RollFull, float Twist, float Bank)
{
    /// <summary>The scene's limits, stored in degrees.</summary>
    public static ReverseFootLimits FromScene(Scene scene)
    {
        float R(string member) => scene.GetAngleLimit(member) * (MathF.PI / 180f);
        return new ReverseFootLimits(R("RollAngleBackMax"), R("RollAnglePreMax"), R("RollAngleToesMax"),
            R("RollAngleFullMax"), R("TwistAngleMax"), R("BankAngleMax"));
    }
}

/// <summary>
/// The reverse-foot controller, following "Reverse-foot override
/// segments": the shoe pivots posed from roll, twist and bank, and the leg solved toward them.
/// Matrices are row-vector (<see cref="AktMath"/>); quaternion products are Hamilton products.
/// </summary>
public static class ReverseFootSolver
{
    public const int SkeletonToes = 4;
    public const int SkeletonPlant = 5;
    public const int SkeletonAnkle = 6;

    /// <summary>
    /// Local transforms of the seven shoe locators for the given angles (radians).
    /// </summary>
    /// <param name="rest">Rest transform of each locator, index 0..6.</param>
    /// <param name="rollAxisPlant">The controller's <c>RollAxisPlant</c>, used as stored.</param>
    public static Transform[] ShoeLocals(IReadOnlyList<Transform> rest, float roll, float twist, float bank,
        ReverseFootLimits limits, Vector3 rollAxisPlant)
    {
        roll = Math.Clamp(roll, -limits.RollBack, limits.RollFull);
        twist = Math.Clamp(twist, -limits.Twist, limits.Twist);
        bank = Math.Clamp(bank, -limits.Bank, limits.Bank);
        var (plant, toes) = SolveRoll(roll, limits, rest[SkeletonPlant], rest[SkeletonAnkle]);

        var result = rest.Take(7).ToArray();
        void Turn(int i, Vector3 axis, float angle) =>
            result[i] = result[i] with { Rotation = result[i].Rotation * Quaternion.CreateFromAxisAngle(axis, angle) };

        if (roll < 0) Turn(0, Vector3.UnitX, roll);
        if (bank < 0) Turn(1, Vector3.UnitZ, bank);
        if (bank > 0) Turn(2, Vector3.UnitZ, bank);
        Turn(3, Vector3.UnitY, twist);
        if (roll > 0)
        {
            Turn(3, Vector3.UnitX, toes);
            Turn(SkeletonPlant, rollAxisPlant, plant);
        }
        return result;
    }

    /// <summary>Splits a forward roll into the plant pivot's angle and the toes pivot's angle.</summary>
    public static (float Plant, float Toes) SolveRoll(float roll, ReverseFootLimits limits, Transform restPlant, Transform restAnkle)
    {
        float pre = limits.RollPre, toesMax = limits.RollToes;
        if (roll < pre) return (roll, 0f);
        if (roll >= toesMax) return (0f, roll);
        float l1 = Vector3.TransformNormal(restAnkle.Translation, restPlant.ToMatrix()).Length();
        float l2 = restPlant.Translation.Length();
        float Phi(float x) => MathF.Atan(MathF.Sin(x) * l1 / (MathF.Cos(x) * l1 + l2));
        float s = (roll - pre) / (toesMax - pre);
        float plant = (1 - s) * pre;
        return (plant, Phi(pre) - Phi(plant) + (toesMax - Phi(pre)) * s);
    }

    /// <summary>
    /// Solves a thigh, calf, ankle, ball chain: FABRIK to the SkeletonAnkle pivot, then the ankle
    /// aimed at SkeletonPlant and the ball aimed at SkeletonToes without twist.
    /// </summary>
    /// <param name="base">World matrix of the parent of the chain's first joint.</param>
    /// <param name="baseScale">Local scale of that parent.</param>
    /// <param name="chain">Interpolated local transform of each chain joint, root first.</param>
    /// <param name="compensates">Per chain joint: whether it compensates its parent's scale.</param>
    /// <param name="restBall">Rest transform of the ball joint.</param>
    /// <param name="restPlant">Rest transform of the SkeletonPlant locator.</param>
    /// <param name="ankle">World matrix of the SkeletonAnkle locator.</param>
    /// <param name="plant">World matrix of the SkeletonPlant locator.</param>
    /// <param name="toes">World matrix of the SkeletonToes locator.</param>
    /// <returns>The chain joints' local transforms, or null if a matrix is singular.</returns>
    public static Transform[]? Solve(Matrix4x4 @base, Vector3 baseScale, IReadOnlyList<Transform> chain,
        IReadOnlyList<bool> compensates, Transform restBall, Transform restPlant,
        Matrix4x4 ankle, Matrix4x4 plant, Matrix4x4 toes, bool exactScale = true)
    {
        if (chain.Count != 4) return null;
        const int calf = 1, ankleJoint = 2, ball = 3;

        // 1. Hip to ankle.
        var leg = chain.Take(3).ToArray();
        var solved = IKSolver.Solve(@base, baseScale, leg, ankle, compensates.Take(3).ToArray(), exactScale);
        var result = solved.Append(chain[ball]).ToArray();

        // 2. Worlds.
        var thighWorld = WorldComposition.Compose(result[0].ToMatrix(), @base, baseScale, compensates[0]);
        var calfWorld = WorldComposition.Compose(result[calf].ToMatrix(), thighWorld, result[0].Scale, compensates[calf]);
        var ankleWorld = WorldComposition.Compose(result[ankleJoint].ToMatrix(), calfWorld, result[calf].Scale, compensates[ankleJoint]);

        // 3. Aim the ankle at the plant.
        var newAnkle = Aim(ankleWorld, Normalize(restBall.Translation), plant);
        if (newAnkle is not { } newAnkleWorld
            || !WorldComposition.TryDecompose(newAnkleWorld, calfWorld, result[calf].Scale, compensates[ankleJoint], out var ankleLocal))
            return null;
        result[ankleJoint] = Transform.FromMatrixLoose(ankleLocal);

        // 4. Aim the ball at the toes.
        var ballWorld = WorldComposition.Compose(chain[ball].ToMatrix(), newAnkleWorld, result[ankleJoint].Scale, compensates[ball]);
        if (!Matrix4x4.Invert(restPlant.ToMatrix(), out var plantInverse)) return null;
        var newBall = Aim(ballWorld, Normalize(plantInverse.Translation), toes);
        if (newBall is not { } newBallWorld
            || !WorldComposition.TryDecompose(newBallWorld, newAnkleWorld, result[ankleJoint].Scale, compensates[ball], out var ballLocal)
            || !Matrix4x4.Invert(newBallWorld, out var ballInverse))
            return null;
        var newLocal = Transform.FromMatrixLoose(ballLocal);

        // 5. Remove the ball's twist about the direction to the toes.
        var v = Normalize((toes * ballInverse).Translation);
        var qd = Quaternion.Inverse(restBall.Rotation) * newLocal.Rotation;
        result[ball] = newLocal with { Rotation = restBall.Rotation * RemoveTwist(qd, v) };
        return result;
    }

    /// <summary>
    /// <paramref name="world"/> turned in its own frame so that <paramref name="restDirection"/>
    /// points at <paramref name="goal"/>'s position; translation and scale kept.
    /// </summary>
    private static Matrix4x4? Aim(Matrix4x4 world, Vector3 restDirection, Matrix4x4 goal)
    {
        var t = Transform.FromMatrixLoose(world);
        var unscaled = (t with { Scale = Vector3.One }).ToMatrix();
        if (!Matrix4x4.Invert(unscaled, out var inverse)) return null;
        var direction = Normalize((goal * inverse).Translation);
        return (t with { Rotation = t.Rotation * FromTo(restDirection, direction) }).ToMatrix();
    }

    /// <summary>
    /// q with its twist about unit axis <paramref name="v"/> removed: q ⊗ k for the candidate
    /// k = (±v·s, c) whose product has the larger |w|.
    /// </summary>
    public static Quaternion RemoveTwist(Quaternion q, Vector3 v)
    {
        float p = q.X * v.X + q.Y * v.Y + q.Z * v.Z;
        float w2 = q.W * q.W;
        float c = -MathF.Sqrt(w2 / (p * p + w2));
        float s = MathF.Sqrt(1 - c * c);
        var plus = q * new Quaternion(v * s, c);
        var minus = q * new Quaternion(-v * s, c);
        return MathF.Abs(minus.W) <= MathF.Abs(plus.W) ? plus : minus;
    }

    /// <summary>Rotation taking unit vector <paramref name="a"/> to <paramref name="b"/>: (a×b, 1 + a·b), normalized.</summary>
    private static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        var q = new Quaternion(Vector3.Cross(a, b), 1f + Vector3.Dot(a, b));
        float l = q.Length();
        return l > 0 ? q * (1f / l) : Quaternion.Identity;
    }

    private static Vector3 Normalize(Vector3 v)
    {
        float l = v.Length();
        return l > 0 ? v / l : v;
    }
}

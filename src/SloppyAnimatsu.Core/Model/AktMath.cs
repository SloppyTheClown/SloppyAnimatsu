using System.Numerics;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// Conversions between the file's float groups and System.Numerics types.
/// </summary>
/// <remarks>
/// A <c>Mat4f</c> is stored column-major with the translation in elements 12-14
/// Read in order into <see cref="Matrix4x4"/>'s row-major fields,
/// this gives System.Numerics' row-vector convention directly: translation lands in M41..M43, and
/// "parent world x LTM" (column vectors) becomes <c>LTM * parentWorld</c>.
/// </remarks>
public static class AktMath
{
    public static Matrix4x4 ToMatrix(float[] a) => new(
        a[0], a[1], a[2], a[3],
        a[4], a[5], a[6], a[7],
        a[8], a[9], a[10], a[11],
        a[12], a[13], a[14], a[15]);

    public static float[] ToArray(Matrix4x4 m) =>
    [
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44,
    ];

    public static Vector2 ToVector2(float[] a) => new(a[0], a[1]);
    public static Vector3 ToVector3(float[] a) => new(a[0], a[1], a[2]);
    public static Vector4 ToVector4(float[] a) => new(a[0], a[1], a[2], a[3]);
    /// <summary><c>Quatf</c> is stored (x, y, z, w).</summary>
    public static Quaternion ToQuaternion(float[] a) => new(a[0], a[1], a[2], a[3]);

    public static float[] ToArray(Vector3 v) => [v.X, v.Y, v.Z];
    public static float[] ToArray(Vector4 v) => [v.X, v.Y, v.Z, v.W];
    public static float[] ToArray(Quaternion q) => [q.X, q.Y, q.Z, q.W];

    /// <summary>Largest absolute element difference, for comparing matrices read from different places.</summary>
    public static float MaxDifference(Matrix4x4 a, Matrix4x4 b)
    {
        float[] x = ToArray(a), y = ToArray(b);
        float max = 0;
        for (int i = 0; i < 16; i++) max = Math.Max(max, Math.Abs(x[i] - y[i]));
        return max;
    }
}

/// <summary>Translation, rotation and scale, as stored in <c>CMRSKey</c> and <c>IKBaseRefInfo</c>.</summary>
public readonly record struct Transform(Vector3 Translation, Quaternion Rotation, Vector3 Scale)
{
    public static readonly Transform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>Scale, then rotate, then translate (row-vector matrix, see <see cref="AktMath"/>).</summary>
    public Matrix4x4 ToMatrix() =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Translation);

    /// <summary>
    /// How far a decomposed scale axis may sit from ±1 and still be float round-off of a unit
    /// row: the length of a rotation matrix's row computed in floats lands a few ulps off 1
    /// (1.0000001, 0.99999994), and a local re-derived from two world matrices a few more
    /// (7.7e-7 seen on an evaluated IK chain). About sixteen ulps above 1; drift in the data
    /// itself is larger and is removed by Normalize Scale instead.
    /// </summary>
    public const float ScaleRoundOff = 2e-6f;

    /// <summary>
    /// Each axis within <see cref="ScaleRoundOff"/> of ±1 becomes exactly ±1. Applied only to
    /// scale values written out (FBX); the scene's own matrices and decompositions stay untouched,
    /// so world positions computed from them do not move.
    /// </summary>
    public static Vector3 SnapUnitScale(Vector3 s) => new(SnapUnit(s.X), SnapUnit(s.Y), SnapUnit(s.Z));

    private static float SnapUnit(float v) => MathF.Abs(MathF.Abs(v) - 1f) <= ScaleRoundOff ? MathF.CopySign(1f, v) : v;

    /// <summary>Splits a matrix into TRS; returns false when it has shear or a negative determinant.</summary>
    public static bool TryFromMatrix(Matrix4x4 m, out Transform transform)
    {
        bool ok = Matrix4x4.Decompose(m, out var s, out var r, out var t);
        transform = new Transform(t, r, s);
        return ok;
    }

    /// <summary>
    /// Splits a matrix into TRS, also when it has shear: the translation row, the row lengths as
    /// scale and the rotation of the normalized rows
    /// </summary>
    public static Transform FromMatrixLoose(Matrix4x4 m)
    {
        if (TryFromMatrix(m, out var exact)) return exact;
        var x = new Vector3(m.M11, m.M12, m.M13);
        var y = new Vector3(m.M21, m.M22, m.M23);
        var z = new Vector3(m.M31, m.M32, m.M33);
        var s = new Vector3(x.Length(), y.Length(), z.Length());
        x /= s.X; y /= s.Y; z /= s.Z;
        var rot = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        return new Transform(m.Translation, Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rot)), s);
    }
}

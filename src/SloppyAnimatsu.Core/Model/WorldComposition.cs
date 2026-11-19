using System.Numerics;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// Chains a local transform onto its parent's world transform
/// and back.
/// </summary>
/// <remarks>
/// The default is the plain matrix chain, <c>local * parentWorld</c>. An object whose
/// <see cref="Object3D.CompensatesParentScale"/> is set takes its orientation and scale from the
/// parent's world matrix with the parent's own local scale divided out of its basis rows, so the
/// parent's scale does not stretch or shear it, while its position still comes from the full
/// parent world.
/// </remarks>
public static class WorldComposition
{
    /// <summary>
    /// World matrix of a child with local matrix <paramref name="local"/> under a parent with world
    /// matrix <paramref name="parentWorld"/> and local scale <paramref name="parentScale"/>.
    /// </summary>
    public static Matrix4x4 Compose(Matrix4x4 local, Matrix4x4 parentWorld, Vector3 parentScale, bool compensate)
    {
        if (!compensate) return local * parentWorld;
        var world = local * Compensated(parentWorld, parentScale);
        var position = Vector3.Transform(local.Translation, parentWorld);
        world.M41 = position.X;
        world.M42 = position.Y;
        world.M43 = position.Z;
        return world;
    }

    /// <summary>The local matrix that <see cref="Compose"/> turns into <paramref name="world"/>; false if the parent is singular.</summary>
    public static bool TryDecompose(Matrix4x4 world, Matrix4x4 parentWorld, Vector3 parentScale, bool compensate, out Matrix4x4 local)
    {
        local = default;
        if (!Matrix4x4.Invert(parentWorld, out var fullInverse)) return false;
        if (!compensate)
        {
            local = world * fullInverse;
            return true;
        }
        if (!Matrix4x4.Invert(Compensated(parentWorld, parentScale), out var basisInverse)) return false;
        local = world * basisInverse;
        var position = Vector3.Transform(world.Translation, fullInverse);
        local.M41 = position.X;
        local.M42 = position.Y;
        local.M43 = position.Z;
        return true;
    }

    /// <summary>
    /// The parent's world matrix with each basis row divided by the matching parent scale
    /// component; where that is not finite, the parent's world rotation alone.
    /// </summary>
    public static Matrix4x4 Compensated(Matrix4x4 parentWorld, Vector3 parentScale)
    {
        var m = parentWorld;
        m.M11 /= parentScale.X; m.M12 /= parentScale.X; m.M13 /= parentScale.X;
        m.M21 /= parentScale.Y; m.M22 /= parentScale.Y; m.M23 /= parentScale.Y;
        m.M31 /= parentScale.Z; m.M32 /= parentScale.Z; m.M33 /= parentScale.Z;
        if (IsFinite(m)) return m;
        Matrix4x4.Decompose(parentWorld, out _, out var rotation, out var translation);
        return Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
    }

    private static bool IsFinite(Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33);
}

using System.Numerics;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The point of view a mesh stores for the blendshape view
/// the viewport camera as a transform relative to the mesh's
/// <c>BlendshapeCameraAnchor</c> - or, when the scene has no anchor for it, relative to the mesh's
/// own world matrix - plus the camera's target distance.
/// </summary>
/// <remarks>
/// Setting the point of view copies <c>inverse(anchorWorld) *
/// cameraWorld</c> into <c>BlendshapeCameraPositionFromAnchor</c>, and the action stores the camera's target distance in
/// <c>BlendshapeCameraDistanceFromAnchor</c> right after it
/// A matrix here is a System.Numerics row-vector
/// matrix with its translation in row 4 (<see cref="AktMath"/>), so the original's column-vector
/// "anchorWorld * local" is <c>relative * baseWorld</c> and its inverse is
/// <c>cameraWorld * inverse(baseWorld)</c>. The anchor fallback is **inferred**: no sample scene
/// declares <c>BlendshapeCameraAnchor</c> (<c>10_portedOldAnims.akt</c>'s <c>CMesh</c> has the other
/// two members only), so the base an anchor-less mesh would use cannot be read from the original;
/// the mesh's own world matrix is the choice.
/// </remarks>
public static class BlendshapePointOfView
{
    /// <summary>Whether a stored distance means "a point of view is set": the distance is the camera's target distance, so it is positive.</summary>
    public static bool IsSet(float distance) => distance > 0f;

    /// <summary>
    /// The stored transform of <paramref name="cameraWorld"/> relative to <paramref name="baseWorld"/>
    /// (the <c>inverse(anchorWorld) * cameraWorld</c>).
    /// </summary>
    public static Matrix4x4 RelativeTo(Matrix4x4 baseWorld, Matrix4x4 cameraWorld) =>
        cameraWorld * Inverse(baseWorld);

    /// <summary>The world camera a stored relative transform describes.</summary>
    public static Matrix4x4 WorldOf(Matrix4x4 baseWorld, Matrix4x4 relative) => relative * baseWorld;

    /// <summary>The inverse of <paramref name="m"/>, or the identity when it is singular (a degenerate base or camera).</summary>
    public static Matrix4x4 Inverse(Matrix4x4 m) => Matrix4x4.Invert(m, out Matrix4x4 inverse) ? inverse : Matrix4x4.Identity;

    /// <summary>
    /// The world camera that looks from <paramref name="eye"/> at <paramref name="target"/>: the
    /// inverse of <see cref="Matrix4x4.CreateLookAt(Vector3, Vector3, Vector3)"/>, so the eye is its
    /// translation, its local -Z the view direction and <see cref="TargetOf"/> recovers the target.
    /// <paramref name="up"/> falls back to +Z when the view is vertical (a look-at with up parallel to
    /// the direction has no rotation to take).
    /// </summary>
    public static Matrix4x4 FromLookAt(Vector3 eye, Vector3 target, Vector3 up)
    {
        var forward = target - eye;
        if (forward.LengthSquared() < 1e-12f) return Matrix4x4.Identity;
        forward = Vector3.Normalize(forward);
        if (MathF.Abs(Vector3.Dot(forward, Vector3.Normalize(up))) > 0.9999f) up = Vector3.UnitZ;
        return Matrix4x4.Invert(Matrix4x4.CreateLookAt(eye, target, up), out Matrix4x4 world)
            ? world
            : Matrix4x4.Identity;
    }

    /// <summary>The camera's eye (the matrix's translation).</summary>
    public static Vector3 EyeOf(Matrix4x4 world) => world.Translation;

    /// <summary>The direction the camera looks in: the negative of the matrix's third row (local -Z).</summary>
    public static Vector3 ForwardOf(Matrix4x4 world)
    {
        var forward = -new Vector3(world.M31, world.M32, world.M33);
        return forward.LengthSquared() > 1e-12f ? Vector3.Normalize(forward) : -Vector3.UnitZ;
    }

    /// <summary>The camera's target: its eye plus its forward direction times the stored distance.</summary>
    public static Vector3 TargetOf(Matrix4x4 world, float distance) => EyeOf(world) + ForwardOf(world) * distance;

    // ---- the members of a mesh -----------------------------------------------------------------
    // The stored values live in Mesh.BlendshapeCameraPositionFromAnchor / ...DistanceFromAnchor and
    // BlendshapeCameraAnchor; these readers apply the rule to them.

    /// <summary>The world a mesh's stored transform is relative to: its anchor's world matrix, else its own.</summary>
    public static Matrix4x4 BaseOf(Mesh mesh) => mesh.BlendshapeCameraAnchor?.WorldMatrix ?? mesh.WorldMatrix;

    /// <summary>The mesh's world camera, or null when it stores no point of view (no positive distance).</summary>
    public static Matrix4x4? Of(Mesh mesh) =>
        mesh.HasBlendshapePointOfView ? WorldOf(BaseOf(mesh), mesh.BlendshapeCameraPositionFromAnchor) : null;

    /// <summary>The eye of the mesh's stored point of view, or null when it has none.</summary>
    public static Vector3? EyeOf(Mesh mesh) => Of(mesh)?.Translation;

    /// <summary>Stores <paramref name="cameraWorld"/> and <paramref name="distance"/> on <paramref name="mesh"/>.</summary>
    public static void Set(Mesh mesh, Matrix4x4 cameraWorld, float distance)
    {
        mesh.BlendshapeCameraPositionFromAnchor = RelativeTo(BaseOf(mesh), cameraWorld);
        mesh.BlendshapeCameraDistanceFromAnchor = Math.Max(distance, 0f);
    }
}
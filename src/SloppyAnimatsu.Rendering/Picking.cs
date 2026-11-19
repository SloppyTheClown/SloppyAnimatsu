using System.Numerics;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Rendering;

/// <summary>
/// What a point of a view hits on the scene's drawn geometry. <see cref="Triangle"/> is the hit
/// triangle's index in <see cref="Mesh.Triangles"/>'s order - the order a blendshape face
/// controller's <c>InteractiveFaces</c> uses - or -1 when the caller did not ask for it
/// </summary>
public readonly record struct SceneHit(Mesh Mesh, Vector3 Point, Vector3 Normal, float Distance, int Triangle = -1);

/// <summary>
/// Turning a point of a view into world space (a ray through the drawn triangles, or a point on a
/// plane). The viewport's controller picking uses <see cref="IViewCamera.Project"/>; this is the
/// inverse, for the commands that need a world point under the pointer: the interactive joint
/// placement and the Skin Workshop's brush
/// </summary>
public static class ScenePicking
{
    /// <summary>A world-space ray from a point of a view (origin on the near plane).</summary>
    public readonly record struct Ray(Vector3 Origin, Vector3 Direction);

    /// <summary>The ray through <paramref name="at"/> (view coordinates), or null when the view is degenerate.</summary>
    public static Ray? Through(IViewCamera camera, Vector2 viewport, Vector2 at)
    {
        float width = MathF.Max(viewport.X, 1e-3f);
        float height = MathF.Max(viewport.Y, 1e-3f);
        var projection = camera.Projection(width / height);
        if (!Matrix4x4.Invert(camera.View * projection, out var inverse)) return null;
        float x = at.X / width * 2f - 1f;
        float y = 1f - at.Y / height * 2f;
        var near = Unproject(inverse, new Vector3(x, y, -1f));
        var far = Unproject(inverse, new Vector3(x, y, 1f));
        var direction = far - near;
        return direction.LengthSquared() < 1e-12f ? null : new Ray(near, Vector3.Normalize(direction));
    }

    /// <summary>
    /// The nearest triangle of the scene's drawn meshes under the pointer. With
    /// <paramref name="only"/> set, only that mesh is tested (the Skin Workshop paints its own mesh
    /// even when another one is in front).
    /// </summary>
    public static SceneHit? Pick(ScenePose pose, IViewCamera camera, Vector2 viewport, Vector2 at, Mesh? only = null)
    {
        if (Through(camera, viewport, at) is not { } ray) return null;
        SceneHit? best = null;
        foreach (var part in pose.MeshParts)
        {
            if (!part.Visible) continue;
            if (only != null && !ReferenceEquals(only, part.Mesh)) continue;
            var span = pose.MeshVertices.AsSpan(part.First * ScenePose.MeshVertexFloats,
                part.Count * ScenePose.MeshVertexFloats);
            for (int v = 0; v + 3 <= part.Count; v += 3)
            {
                var a = Position(span, v);
                var b = Position(span, v + 1);
                var c = Position(span, v + 2);
                if (!IntersectTriangle(ray.Origin, ray.Direction, a, b, c, out float distance)) continue;
                if (best is { } current && distance >= current.Distance) continue;
                var normal = Vector3.Cross(b - a, c - a);
                // The drawn triangles are grouped by material, which reorders them: the part keeps
                // each one's source index in the mesh's Triangles() order.
                int drawn = v / 3;
                int triangle = part.SourceTriangles is { } sources && drawn < sources.Count ? sources[drawn] : drawn;
                best = new SceneHit(part.Mesh, ray.Origin + ray.Direction * distance,
                    normal.LengthSquared() > 1e-20f ? Vector3.Normalize(normal) : Vector3.UnitY, distance, triangle);
            }
        }
        return best;
    }

    /// <summary>
    /// Where the ray meets the plane through <paramref name="point"/> facing <paramref name="normal"/>;
    /// null when the ray is parallel to it or would have to run backwards.
    /// </summary>
    public static Vector3? OnPlane(Ray ray, Vector3 point, Vector3 normal)
    {
        float denominator = Vector3.Dot(normal, ray.Direction);
        if (MathF.Abs(denominator) < 1e-6f) return null;
        float t = Vector3.Dot(point - ray.Origin, normal) / denominator;
        return t >= 0f ? ray.Origin + ray.Direction * t : null;
    }

    /// <summary>Möller–Trumbore: whether the ray meets the triangle ahead of its origin.</summary>
    public static bool IntersectTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c,
        out float distance)
    {
        distance = 0f;
        var edge1 = b - a;
        var edge2 = c - a;
        var p = Vector3.Cross(direction, edge2);
        float determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < 1e-12f) return false;
        float inverse = 1f / determinant;
        var t = origin - a;
        float u = Vector3.Dot(t, p) * inverse;
        if (u < -1e-6f || u > 1f + 1e-6f) return false;
        var q = Vector3.Cross(t, edge1);
        float v = Vector3.Dot(direction, q) * inverse;
        if (v < -1e-6f || u + v > 1f + 1e-6f) return false;
        float d = Vector3.Dot(edge2, q) * inverse;
        if (d <= 1e-6f) return false;
        distance = d;
        return true;
    }

    private static Vector3 Position(ReadOnlySpan<float> vertices, int vertex)
    {
        int o = vertex * ScenePose.MeshVertexFloats;
        return new Vector3(vertices[o], vertices[o + 1], vertices[o + 2]);
    }

    private static Vector3 Unproject(Matrix4x4 inverse, Vector3 ndc)
    {
        var v = Vector4.Transform(new Vector4(ndc, 1f), inverse);
        return v.W != 0f && float.IsFinite(v.W) ? new Vector3(v.X / v.W, v.Y / v.W, v.Z / v.W) : new Vector3(v.X, v.Y, v.Z);
    }
}

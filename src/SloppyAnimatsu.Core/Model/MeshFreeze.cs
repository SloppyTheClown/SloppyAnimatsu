using System.Numerics;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The TRANSFORM window's <b>Reset &amp; Freeze</b>: bakes an unskinned mesh's local transform into
/// its geometry and resets the transform to the identity
/// </summary>
public static class MeshFreeze
{
    /// <summary>Whether Reset &amp; Freeze can act on <paramref name="o"/>: an unskinned mesh.</summary>
    public static bool CanFreeze(Object3D? o) => o is Mesh { Skinned: false };

    /// <summary>
    /// Whether the action is enabled for <paramref name="selection"/>: not empty, and every object
    /// an unskinned mesh. The Rigging-context condition is the caller's.
    /// </summary>
    public static bool CanFreeze(IReadOnlyCollection<Object3D> selection) =>
        selection.Count > 0 && selection.All(CanFreeze);

    /// <summary>The objects whose fields <see cref="Freeze"/> changes: the mesh and its blendshape targets.</summary>
    public static IEnumerable<SceneObject> TouchedObjects(Mesh mesh) =>
        new SceneObject[] { mesh }.Concat(mesh.BlendshapeTargets);

    /// <summary>
    /// Freezes <paramref name="mesh"/>: vertices become <c>v·M</c>, normals <c>(n, 0)·M</c>
    /// (not renormalised), blendshape deltas are re-expressed so each target keeps its shape, and
    /// the <c>LTM</c> becomes the identity. Returns false (and changes nothing) for a skinned mesh.
    /// </summary>
    public static bool Freeze(Mesh mesh)
    {
        if (!CanFreeze(mesh)) return false;
        var m = mesh.LocalMatrix;
        var oldVertices = Read(mesh.Source, "VertexArray");
        var oldNormals = Read(mesh.Source, "NormalArray");
        var newVertices = oldVertices.Select(v => Vector4.Transform(v, m)).ToArray();
        var newNormals = oldNormals.Select(n => Vector4.Transform(Direction(n), m)).ToArray();
        var corners = mesh.CornerNormals;

        foreach (var target in mesh.BlendshapeTargets)
        {
            var deltas = target.VertexArray;
            int count = Math.Min(deltas.Length, oldVertices.Length);
            for (int i = 0; i < count; i++)
                deltas[i] = Vector4.Transform(oldVertices[i] + deltas[i], m) - newVertices[i];
            target.VertexArray = deltas;

            var normalDeltas = target.NormalArray;
            for (int j = 0; j < normalDeltas.Length && j < corners.Length; j++)
            {
                uint at = corners[j];
                if (at >= oldNormals.Length) continue;
                normalDeltas[j] = Vector4.Transform(Direction(oldNormals[at] + normalDeltas[j]), m) - newNormals[at];
            }
            target.NormalArray = normalDeltas;
        }

        Write(mesh.Source, "VertexArray", newVertices);
        Write(mesh.Source, "NormalArray", newNormals);
        mesh.LocalMatrix = Matrix4x4.Identity;
        return true;
    }

    private static Vector4 Direction(Vector4 v) => new(v.X, v.Y, v.Z, 0f);

    private static Vector4[] Read(AktObject source, string field) =>
        SceneValues.Items(source, field).Select(v => AktMath.ToVector4((float[])v!)).ToArray();

    private static void Write(AktObject source, string field, Vector4[] values) =>
        source.Set(field, values.Select(v => (object?)AktMath.ToArray(v)).ToList());
}

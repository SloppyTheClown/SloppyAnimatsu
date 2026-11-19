using System.Numerics;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// Linear blend skinning of one mesh on the CPU. Reads the mesh
/// once; afterwards only the character's world matrices are needed per pose.
/// </summary>
public sealed class MeshSkin
{
    private readonly int[] _boneController;
    private readonly VertexWeight[][] _weights;
    private readonly Matrix4x4[] _inverseBindWorlds;

    public MeshSkin(Mesh mesh)
    {
        Mesh = mesh;
        ControlPoints = mesh.ControlPoints;
        MeshWorld = mesh.WorldMatrix;
        Character = mesh.BindPose?.Character;

        var joints = Character?.Joints ?? [];
        var controllers = Character?.Controllers ?? [];
        _boneController = joints.Select(j => IndexOf(controllers, j)).ToArray();

        _weights = Character != null && mesh.Skinned ? Weights(mesh, ControlPoints.Length) : [];
        IsSkinned = _weights.Length > 0;

        BindWorlds = new Matrix4x4[_boneController.Length];
        _inverseBindWorlds = new Matrix4x4[_boneController.Length];
        ReloadBindPose();
    }

    /// <summary>
    /// Reads the joints' bind-time world matrices again, after the bind pose was edited
    /// </summary>
    public void ReloadBindPose()
    {
        var bindPose = Character != null ? new PoseEvaluator(Character, restPose: Mesh.BindPose).WorldMatrices(null, 0) : [];
        for (int b = 0; b < _boneController.Length; b++)
        {
            int c = _boneController[b];
            BindWorlds[b] = c >= 0 && c < bindPose.Length ? bindPose[c] : Matrix4x4.Identity;
            _inverseBindWorlds[b] = Matrix4x4.Invert(BindWorlds[b], out var inv) ? inv : Matrix4x4.Identity;
        }
    }

    /// <summary>
    /// Reads the mesh's world and the joints' bind worlds again after a character was placed
    /// elsewhere (its own LTM changed): both compose under the character, so the mesh then shows
    /// exactly as it would after a save and reload.
    /// </summary>
    public void ReloadPlacement()
    {
        MeshWorld = Mesh.WorldMatrix;
        ReloadBindPose();
    }

    public Mesh Mesh { get; }

    /// <summary>The character whose bind pose the mesh uses; null for a static mesh.</summary>
    public Character? Character { get; }

    /// <summary>Whether any control point has weights; otherwise the mesh follows <see cref="MeshWorld"/> only.</summary>
    public bool IsSkinned { get; }

    /// <summary>Control points in mesh space.</summary>
    public Vector3[] ControlPoints { get; }

    /// <summary>The mesh's world matrix from the stored <c>LTM</c> chain, at bind time and now.</summary>
    public Matrix4x4 MeshWorld { get; private set; }

    /// <summary>
    /// Bind-time world matrix of every joint, indexed like <see cref="Character.Joints"/> (the
    /// skinning order, which can differ from <see cref="Character.Controllers"/>).
    /// </summary>
    public Matrix4x4[] BindWorlds { get; }

    /// <summary>
    /// For each control point, the matrix taking it from mesh space to its skinned world position:
    /// the mesh world times the weighted sum of the joints' skin matrices.
    /// </summary>
    /// <param name="controllerWorlds">World matrices indexed like <see cref="Character.Controllers"/> (<see cref="PoseEvaluator.WorldMatrices"/>).</param>
    public void PointMatrices(IReadOnlyList<Matrix4x4> controllerWorlds, Span<Matrix4x4> result)
    {
        if (!IsSkinned)
        {
            result[..ControlPoints.Length].Fill(MeshWorld);
            return;
        }

        var bones = new Matrix4x4[_boneController.Length];
        for (int b = 0; b < bones.Length; b++)
        {
            int c = _boneController[b];
            bones[b] = c >= 0 && c < controllerWorlds.Count
                ? MeshWorld * _inverseBindWorlds[b] * controllerWorlds[c]
                : MeshWorld;
        }

        for (int p = 0; p < ControlPoints.Length; p++)
        {
            var w = _weights[p];
            if (w.Length == 0) { result[p] = MeshWorld; continue; }
            var m = default(Matrix4x4);
            foreach (var (bone, weight) in w)
                m += (bone >= 0 && bone < bones.Length ? bones[bone] : MeshWorld) * weight;
            result[p] = m;
        }
    }

    /// <summary>Skinned world positions of the control points.</summary>
    public Vector3[] Positions(IReadOnlyList<Matrix4x4> controllerWorlds)
    {
        var matrices = new Matrix4x4[ControlPoints.Length];
        PointMatrices(controllerWorlds, matrices);
        var result = new Vector3[ControlPoints.Length];
        for (int p = 0; p < result.Length; p++)
            result[p] = Vector3.Transform(ControlPoints[p], matrices[p]);
        return result;
    }

    /// <summary>
    /// Per control point world normals: the average of the corner normals of the mesh's first
    /// normal layer, taken through each point's skin matrix. Empty when the mesh stores no
    /// normals or no corners. Used by the Skin Workshop's front/back test
    /// </summary>
    public Vector3[] Normals(IReadOnlyList<Matrix4x4> controllerWorlds)
    {
        var cornerNormals = Mesh.Normals;
        var cornerIndices = Mesh.CornerNormalsOf(0);
        var cornerPoints = Mesh.CornerControlPoints;
        if (cornerNormals.Length == 0 || cornerIndices.Length == 0 || cornerPoints.Length == 0)
            return [];
        int corners = Math.Min(cornerIndices.Length, cornerPoints.Length);
        var sums = new Vector3[ControlPoints.Length];
        var counts = new int[ControlPoints.Length];
        for (int c = 0; c < corners; c++)
        {
            int normal = (int)cornerIndices[c];
            int point = (int)cornerPoints[c];
            if (normal < 0 || normal >= cornerNormals.Length || point < 0 || point >= sums.Length) continue;
            sums[point] += cornerNormals[normal];
            counts[point]++;
        }
        var matrices = new Matrix4x4[ControlPoints.Length];
        PointMatrices(controllerWorlds, matrices);
        var result = new Vector3[ControlPoints.Length];
        for (int p = 0; p < result.Length; p++)
        {
            if (counts[p] == 0) continue;
            var normal = Vector3.TransformNormal(sums[p], matrices[p]);
            result[p] = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.Zero;
        }
        return result;
    }

    /// <summary>Smooth weights where a point has them, else rigid ones.</summary>
    private static VertexWeight[][] Weights(Mesh mesh, int count)
    {
        var smooth = mesh.SkinWeights;
        var rigid = mesh.RigidSkinWeights;
        if (smooth.Length == 0 && rigid.Length == 0) return [];
        var result = new VertexWeight[count][];
        for (int p = 0; p < count; p++)
            result[p] = p < smooth.Length && smooth[p].Length > 0 ? smooth[p]
                : p < rigid.Length ? rigid[p]
                : [];
        return result;
    }

    private static int IndexOf(IReadOnlyList<Object3D> list, Object3D o)
    {
        for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], o)) return i;
        return -1;
    }
}

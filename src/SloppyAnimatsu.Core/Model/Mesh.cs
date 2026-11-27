using System.Numerics;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>A polygon: indices of its corners (into the per-corner index arrays) and its material slot.</summary>
public readonly record struct MeshPolygon(uint[] Corners, uint MaterialIndex);

/// <summary>One skinning influence. <see cref="Bone"/> indexes <see cref="Character.Joints"/>.</summary>
public readonly record struct VertexWeight(int Bone, float Weight);

/// <summary>
/// <c>CMesh</c>: indexed polygon-corner geometry. The arrays are decoded
/// from the document on every access; cache them if you read them repeatedly.
/// </summary>
public sealed class Mesh : Object3D
{
    internal Mesh(SceneModel model, AktObject source) : base(model, source) { }

    /// <summary>Control point positions (stored as Vec4f with w = 1).</summary>
    public Vector3[] ControlPoints => Vectors3("VertexArray");

    public IReadOnlyList<MeshPolygon> Polygons => PackedPolygons() ??
        SceneValues.Items(Source, "PolygonArray")
            .Select(p => (AktStruct)p!)
            .Select(p => new MeshPolygon(SceneValues.UInts(p, "IndexArray"), p.Get<uint>("MatIndex")))
            .ToArray();

    /// <summary>Corner to control point (<c>VertexIArray</c>).</summary>
    public uint[] CornerControlPoints => UInts("VertexIArray");

    public Vector4[] Colors =>
        SceneValues.Items(Source, "ColorArray").Select(v => AktMath.ToVector4((float[])v!)).ToArray();
    /// <summary>Corner to colour (<c>ColorIArray</c>).</summary>
    public uint[] CornerColors => UInts("ColorIArray");

    public Vector3[] Normals => Vectors3("NormalArray");
    /// <summary>Corner to normal (<c>NormalIArray</c>, every layer: see <see cref="CornerNormalsOf"/>).</summary>
    public uint[] CornerNormals => UInts("NormalIArray");

    public Vector2[] UVs =>
        SceneValues.Items(Source, "UVArray").Select(v => AktMath.ToVector2((float[])v!)).ToArray();
    /// <summary>Corner to UV (<c>UVIArray</c>, every layer: see <see cref="CornerUVsOf"/>).</summary>
    public uint[] CornerUVs => UInts("UVIArray");
    /// <summary>UV set name per layer (<c>UVLayersNameArray</c>).</summary>
    public IReadOnlyList<string> UVSetNames => SceneValues.Items(Source, "UVLayersNameArray").Cast<string>().ToArray();

    // ---- attribute layers --------------------------------
    // A layer's per-corner indices are one slice of its index array: layer i occupies
    // [i * corners, (i + 1) * corners). The value arrays are shared by all layers.

    /// <summary>Number of normal layers (<c>NormalLayersCount</c>; one in every sample).</summary>
    public int NormalLayerCount => LayerCount("NormalLayersCount", "NormalIArray");
    /// <summary>Number of colour layers (<c>ColorLayersCount</c>; one in every sample).</summary>
    public int ColorLayerCount => LayerCount("ColorLayersCount", "ColorIArray");
    /// <summary>Number of UV layers (<c>UVLayersCount</c>; samples have 1, 2 or 4).</summary>
    public int UVLayerCount => LayerCount("UVLayersCount", "UVIArray");

    /// <summary>Corner to normal for one layer (<c>NormalIArray</c>'s slice <paramref name="layer"/>).</summary>
    public uint[] CornerNormalsOf(int layer) => LayerSlice(CornerNormals, layer);
    /// <summary>Corner to colour for one layer (<c>ColorIArray</c>'s slice <paramref name="layer"/>).</summary>
    public uint[] CornerColorsOf(int layer) => LayerSlice(CornerColors, layer);
    /// <summary>Corner to UV for one layer (<c>UVIArray</c>'s slice <paramref name="layer"/>).</summary>
    public uint[] CornerUVsOf(int layer) => LayerSlice(CornerUVs, layer);

    /// <summary>The name of a UV layer, or "" when the file names fewer layers than it has.</summary>
    public string UVLayerName(int layer) =>
        layer >= 0 && layer < UVSetNames.Count ? UVSetNames[layer] : "";

    private int LayerCount(string countField, string indicesField)
    {
        if (Source[countField] is uint count) return (int)count;
        int corners = CornerControlPoints.Length;
        int total = UInts(indicesField).Length;
        if (total == 0 || corners == 0) return 0;
        return total % corners == 0 ? total / corners : 1;
    }

    private uint[] LayerSlice(uint[] indices, int layer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(layer);
        int corners = CornerControlPoints.Length;
        if (corners == 0 || indices.Length < corners * (layer + 1)) return [];
        return indices[(layer * corners)..((layer + 1) * corners)];
    }

    public IReadOnlyList<Material> Materials => Refs<Material>("MaterialArray");

    /// <summary>Whether the mesh follows the character's joints.</summary>
    public bool Skinned
    {
        get => Flag("Skinned");
        set => Source.Set("Skinned", value);
    }

    /// <summary>Whether the skinning data has been written for the mesh.</summary>
    public bool SkinnedWithAkeytsu
    {
        get => Flag("SkinnedWithAkeytsu");
        set => Source.Set("SkinnedWithAkeytsu", value);
    }

    /// <summary>
    /// <c>SmoothSkinningProcessed</c>: whether the smooth weight array has been solved. The bind
    /// sets it after writing the arrays.
    /// </summary>
    public bool SmoothSkinningProcessed
    {
        get => Flag("SmoothSkinningProcessed");
        set => Source.Set("SmoothSkinningProcessed", value);
    }

    /// <summary>Whether Unbind Skin refuses to detach the mesh.</summary>
    public bool SkinnedMeshIsLocked
    {
        get => Flag("SkinnedMeshIsLocked");
        set => Source.Set("SkinnedMeshIsLocked", value);
    }

    /// <summary>The pose the mesh was bound in.</summary>
    public CharacterPose? BindPose => Ref<CharacterPose>("BindPose");

    /// <summary>
    /// The character the mesh is skinned to: its bind pose's character while <see cref="Skinned"/> is
    /// set, else null. Unbinding (Unbind Skin, or moving the mesh out of its character) clears
    /// <c>Skinned</c> but keeps the bind pose reference and the weights so a later Bind Skin can carry
    /// them across, so the reference alone does not make the mesh part of that character.
    /// </summary>
    public Character? BoundCharacter => Skinned ? BindPose?.Character : null;

    /// <summary>Target shapes of the mesh, in influence-array order.</summary>
    public IReadOnlyList<Blendshape> BlendshapeTargets => Refs<Blendshape>("BlendshapeTargetArray");

    /// <summary>Face thumbnails of the mesh.</summary>
    public IReadOnlyList<BlendshapeFace> BlendshapeFaces => Refs<BlendshapeFace>("BlendshapeFaces");

    // ---- the blendshape point of view -----
    // CMesh stores the blendshape camera as a transform relative to an anchor plus the camera's
    // target distance (the "set blendshapes point of view" action). The rule
    // that turns them into a world camera is BlendshapePointOfView; these are the members.

    /// <summary>
    /// <c>BlendshapeCameraPositionFromAnchor</c>: the blendshape camera relative to
    /// <see cref="BlendshapeCameraAnchor"/> (or to the mesh's world matrix when it has no anchor).
    /// The identity when the scene does not declare the member, which also means "no point of view".
    /// </summary>
    public Matrix4x4 BlendshapeCameraPositionFromAnchor
    {
        get => Source[BlendshapeSchema.CameraPositionMember] is float[] a ? AktMath.ToMatrix(a) : Matrix4x4.Identity;
        set => SetCameraMember(BlendshapeSchema.CameraPositionMember, "Mat4f", AktMath.ToArray(value));
    }

    /// <summary><c>BlendshapeCameraDistanceFromAnchor</c>: the camera's target distance; 0 = no point of view.</summary>
    public float BlendshapeCameraDistanceFromAnchor
    {
        get => Source.GetOr(BlendshapeSchema.CameraDistanceMember, 0f);
        set => SetCameraMember(BlendshapeSchema.CameraDistanceMember, "float", value);
    }

    /// <summary>
    /// <c>BlendshapeCameraAnchor</c>: the object the camera transform is relative to, or null when
    /// the member or its reference is absent. No sample scene declares it, so setting it adds the
    /// member to the scene's <c>CMesh</c> class (see <see cref="BlendshapeSchema.EnsureMeshMembers"/>).
    /// </summary>
    public Object3D? BlendshapeCameraAnchor
    {
        get => Ref<Object3D>(BlendshapeSchema.CameraAnchorMember);
        set => SetCameraMember(BlendshapeSchema.CameraAnchorMember, "Reference<class CObject3D>",
            new AktReference(value?.Source), withAnchor: true);
    }

    /// <summary>Whether the mesh stores a blendshape point of view (a positive camera distance).</summary>
    public bool HasBlendshapePointOfView => BlendshapePointOfView.IsSet(BlendshapeCameraDistanceFromAnchor);

    /// <summary>
    /// Writes one point-of-view member, adding the members to the scene's <c>CMesh</c> class - and a
    /// value to every mesh of the scene - when a scene written before blendshapes lacks them.
    /// </summary>
    private void SetCameraMember(string name, string typeName, object? value, bool withAnchor = false)
    {
        if (BlendshapeSchema.EnsureMeshMembers(Model.Document.Schema, withAnchor)) EnsureCameraFields();
        if (Source.Has(name)) Source.Set(name, value);
        else Source.Fields.Add(new AktField(Source.TypeName, name, typeName, value));
    }

    /// <summary>
    /// Gives every mesh of the scene a value for the point-of-view members its <c>CMesh</c> class
    /// declares but its fields lack, after <see cref="BlendshapeSchema.EnsureMeshMembers"/> extended
    /// the class: the writer needs one value per declared member, and the defaults mean "no point of
    /// view" (identity, distance 0, no anchor).
    /// </summary>
    private void EnsureCameraFields()
    {
        var declared = Model.Document.Schema.FindClass(BlendshapeSchema.MeshClass)!.Members.Select(m => m.Name).ToHashSet();
        foreach (var source in Model.Document.AllObjects().Where(o => o.ClassKey == BlendshapeSchema.MeshClass))
        {
            if (!source.Has(BlendshapeSchema.CameraPositionMember))
                source.Fields.Add(new AktField(source.TypeName, BlendshapeSchema.CameraPositionMember,
                    "Mat4f", AktMath.ToArray(Matrix4x4.Identity)));
            if (!source.Has(BlendshapeSchema.CameraDistanceMember))
                source.Fields.Add(new AktField(source.TypeName, BlendshapeSchema.CameraDistanceMember, "float", 0f));
            if (declared.Contains(BlendshapeSchema.CameraAnchorMember) && !source.Has(BlendshapeSchema.CameraAnchorMember))
                source.Fields.Add(new AktField(source.TypeName, BlendshapeSchema.CameraAnchorMember,
                    "Reference<class CObject3D>", new AktReference(null)));
        }
    }

    // ---- hidden control points ---------
    // The app keeps one uint per control point in SavedSelectedVerticesToHide; a non-zero entry is
    // hidden. The array is saved (an array of unsigned ints on CMesh) and the
    // runtime visible-index map is derived from it.

    /// <summary>Per control point visibility from <c>SavedSelectedVerticesToHide</c>: 0 = shown, non-zero = hidden.</summary>
    public uint[] HiddenVertexFlags => UInts("SavedSelectedVerticesToHide");

    public bool HasHiddenVertices => HiddenVertexFlags.Any(f => f != 0);

    public int HiddenVertexCount => HiddenVertexFlags.Count(f => f != 0);

    /// <summary>Whether a control point is hidden (missing array entries are shown).</summary>
    public bool IsVertexHidden(int vertex)
    {
        var flags = HiddenVertexFlags;
        return vertex >= 0 && vertex < flags.Length && flags[vertex] != 0;
    }

    /// <summary>
    /// Hides or shows control points, growing the array to one entry per control point as
    /// the hide-vertices command does. Returns how many control points changed state.
    /// </summary>
    public int SetVerticesHidden(IEnumerable<int> vertices, bool hidden)
    {
        int count = ControlPoints.Length;
        var flags = HiddenVertexFlags;
        var array = new uint[Math.Max(count, flags.Length)];
        Array.Copy(flags, array, flags.Length);
        int changed = 0;
        foreach (int vertex in vertices)
        {
            if (vertex < 0 || vertex >= count) continue;
            uint value = hidden ? 1u : 0u;
            if (array[vertex] == value) continue;
            array[vertex] = value;
            changed++;
        }
        if (changed == 0) return 0;
        Source.Set("SavedSelectedVerticesToHide", array.Select(v => (object?)v).ToList());
        return changed;
    }

    /// <summary>Shows every hidden control point; returns how many were hidden.</summary>
    public int ShowAllVertices() => SetVerticesHidden(Enumerable.Range(0, ControlPoints.Length), false);

    /// <summary>Smooth skinning: the influences of each control point (weights sum to 1).</summary>
    public VertexWeight[][] SkinWeights => Weights("SkinWeightArray");

    /// <summary>Rigid skinning: one influence of weight 1 per control point.</summary>
    public VertexWeight[][] RigidSkinWeights => Weights("RigidSkinWeightArray");

    /// <summary>
    /// Fan-triangulates every polygon. Each triangle is three corner indices; resolve them with
    /// <see cref="CornerControlPoints"/>, <see cref="CornerNormals"/> and <see cref="CornerUVs"/>.
    /// </summary>
    public IEnumerable<(uint A, uint B, uint C, uint MaterialIndex)> Triangles()
    {
        foreach (var p in Polygons)
            for (int i = 1; i + 1 < p.Corners.Length; i++)
                yield return (p.Corners[0], p.Corners[i], p.Corners[i + 1], p.MaterialIndex);
    }

    private Vector3[] Vectors3(string field) =>
        SceneValues.Items(Source, field).Select(v => AktMath.ToVector3((float[])v!)).ToArray();

    // Packed polygon and weight lists are read straight from their words: walking them as structs
    // would build (and keep) a view per element.
    private MeshPolygon[]? PackedPolygons()
    {
        if (Source["PolygonArray"] is not AktArrayColumn { IsPacked: true } column || !column.Shape.ScalarItems
            || column.Shape.ItemKinds[0] != AktTypeKind.UInt || column.Shape.HeadSlotOf("MatIndex") is not (>= 0 and var material)
            || column.Shape.HeadKinds[material] != AktTypeKind.UInt)
            return null;
        var polygons = new MeshPolygon[column.Count];
        for (int i = 0; i < polygons.Length; i++)
            polygons[i] = new MeshPolygon(column.ItemsAt(i).ToArray(), column.WordsAt(i)[material]);
        return polygons;
    }

    private VertexWeight[][]? PackedWeights(string field)
    {
        if (Source[field] is not AktArrayColumn { IsPacked: true } column) return null;
        var shape = column.Shape;
        int bone = shape.ItemSlotOf("BoneIndex"), weight = shape.ItemSlotOf("Weight");
        if (bone < 0 || weight < 0 || shape.ItemKinds[bone] != AktTypeKind.UInt || shape.ItemKinds[weight] != AktTypeKind.Float)
            return null;
        int stride = shape.Stride;
        var result = new VertexWeight[column.Count][];
        for (int i = 0; i < result.Length; i++)
        {
            var words = column.ItemsAt(i);
            var influences = new VertexWeight[words.Length / stride];
            for (int k = 0; k < influences.Length; k++)
                influences[k] = new VertexWeight((int)words[k * stride + bone], BitConverter.UInt32BitsToSingle(words[k * stride + weight]));
            result[i] = influences;
        }
        return result;
    }

    private VertexWeight[][] Weights(string field) => PackedWeights(field) ??
        SceneValues.Items(Source, field)
            .Select(a => SceneValues.Items((AktStruct)a!, "WeightArray")
                .Select(w => (AktStruct)w!)
                .Select(w => new VertexWeight((int)w.Get<uint>("BoneIndex"), w.Get<float>("Weight")))
                .ToArray())
            .ToArray();
}


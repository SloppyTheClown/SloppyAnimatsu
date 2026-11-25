using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Mesh polygons and skin weights held as packed word arrays: the mesh's fast readers agree with
/// the struct views, edits through the views write through, a snapshot restores them, a copy
/// shares nothing it can change, and a foreign value turns a list back into a plain one with the
/// same bytes on save.
/// </summary>
public class PackedMeshArraysTests
{
    private static readonly string[] Fields = ["PolygonArray", "SkinWeightArray", "RigidSkinWeightArray"];

    private static List<Mesh> Meshes(SceneModel model) =>
        model.Document.AllObjects().Select(o => model.Wrap(o)).OfType<Mesh>().ToList();

    private static IEnumerable<(Mesh Mesh, string Field, AktArrayColumn Column)> Columns(SceneModel model)
    {
        foreach (var mesh in Meshes(model))
            foreach (var field in Fields)
                if (mesh.Source[field] is AktArrayColumn { IsPacked: true, Count: > 0 } column)
                    yield return (mesh, field, column);
    }

    private static string Hash(SceneModel model) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(AktWriter.Write(model.Document)));

    /// <summary>The weights of <paramref name="field"/> read through the element and item views.</summary>
    private static VertexWeight[][] WeightsThroughViews(Mesh mesh, string field) =>
        SceneValues.Items(mesh.Source, field).Select(a => SceneValues.Items((AktStruct)a!, "WeightArray")
            .Select(w => (AktStruct)w!)
            .Select(w => new VertexWeight((int)w.Get<uint>("BoneIndex"), w.Get<float>("Weight"))).ToArray()).ToArray();

    private static MeshPolygon[] PolygonsThroughViews(Mesh mesh) =>
        SceneValues.Items(mesh.Source, "PolygonArray").Select(p => (AktStruct)p!)
            .Select(p => new MeshPolygon(SceneValues.Items(p, "IndexArray").Select(v => (uint)v!).ToArray(), p.Get<uint>("MatIndex")))
            .ToArray();

    private static void AssertSamePolygons(IReadOnlyList<MeshPolygon> expected, IReadOnlyList<MeshPolygon> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].MaterialIndex, actual[i].MaterialIndex);
            Assert.Equal(expected[i].Corners, actual[i].Corners);
        }
    }

    private static void AssertSameWeights(VertexWeight[][] expected, VertexWeight[][] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i]);
    }

    /// <summary>Turns every packed mesh array of <paramref name="model"/> into a plain list.</summary>
    private static int DemoteAll(SceneModel model)
    {
        int n = 0;
        foreach (var (_, _, column) in Columns(model).ToList())
        {
            column.Add("not an element");
            column.RemoveAt(column.Count - 1);
            Assert.False(column.IsPacked);
            n++;
        }
        return n;
    }

    [SceneTheory, SceneSamples]
    public void MeshArrays_ArePacked_AndTheFastReadersAgreeWithTheViews(string path)
    {
        if (!AktPackedKeys.Enabled) return;
        var model = TestFiles.LoadScene(path);
        var meshes = Meshes(model);
        Assert.True(meshes.Count == 0 || Columns(model).Any(c => c.Field == "PolygonArray"), "no polygon list is packed");
        foreach (var mesh in meshes)
        {
            var fast = mesh.Polygons;
            var smooth = mesh.SkinWeights;
            var rigid = mesh.RigidSkinWeights;
            AssertSamePolygons(PolygonsThroughViews(mesh), fast);
            AssertSameWeights(WeightsThroughViews(mesh, "SkinWeightArray"), smooth);
            AssertSameWeights(WeightsThroughViews(mesh, "RigidSkinWeightArray"), rigid);
        }
    }

    [SceneTheory, SceneSamples]
    public void Edits_WriteThrough_AndASnapshotRestoresThem(string path)
    {
        if (!AktPackedKeys.Enabled) return;
        var model = TestFiles.LoadScene(path);
        if (!Columns(model).Any(c => c.Field == "PolygonArray")) return;
        string before = Hash(model);

        foreach (bool share in new[] { false, true })
        {
            // A restore puts new columns into the fields, so look them up again on every pass.
            var weighted = Columns(model).FirstOrDefault(c => c.Field != "PolygonArray" && c.Column.ItemsAt(0).Length > 0);
            var polygons = Columns(model).First(c => c.Field == "PolygonArray");
            var polygonSnapshot = AktFieldSnapshot.Capture(polygons.Mesh.Source, share);
            var weightSnapshot = weighted.Mesh != null && !ReferenceEquals(weighted.Mesh, polygons.Mesh)
                ? AktFieldSnapshot.Capture(weighted.Mesh.Source, share) : null;

            var polygon = (AktStruct)polygons.Column[0]!;
            polygon.Set("MatIndex", polygon.Get<uint>("MatIndex") + 3u);
            var corners = SceneValues.Items(polygon, "IndexArray");
            corners[0] = (uint)corners[1]! + 0u;
            corners.Add(corners[0]);
            AssertSamePolygons(PolygonsThroughViews(polygons.Mesh), polygons.Mesh.Polygons);

            if (weighted.Mesh != null)
            {
                var influence = (AktStruct)SceneValues.Items((AktStruct)weighted.Column[0]!, "WeightArray")[0]!;
                influence.Set("Weight", influence.Get<float>("Weight") * 0.5f);
                influence.Set("BoneIndex", influence.Get<uint>("BoneIndex") + 1u);
                var field = weighted.Field;
                AssertSameWeights(WeightsThroughViews(weighted.Mesh, field),
                    field == "SkinWeightArray" ? weighted.Mesh.SkinWeights : weighted.Mesh.RigidSkinWeights);
                Assert.True(weighted.Column.IsPacked);
            }
            Assert.True(polygons.Column.IsPacked);
            Assert.NotEqual(before, Hash(model));

            polygonSnapshot.RestoreInto(polygons.Mesh.Source);
            weightSnapshot?.RestoreInto(weighted.Mesh!.Source);
            Assert.Equal(before, Hash(model));
            Assert.True(polygons.Mesh.Source["PolygonArray"] is AktArrayColumn { IsPacked: true });
        }
    }

    [SceneTheory, SceneSamples]
    public void ACopiedElement_SharesNothingItCanChange(string path)
    {
        if (!AktPackedKeys.Enabled) return;
        var model = TestFiles.LoadScene(path);
        string before = Hash(model);
        foreach (var (mesh, field, column) in Columns(model).ToList())
        {
            var original = (AktStruct)column[0]!;
            var copy = original.Clone();
            if (field == "PolygonArray")
            {
                var corners = SceneValues.Items(copy, "IndexArray");
                corners[0] = (uint)corners[0]! + 7u;
                corners.Add(1u);
                copy.Set("MatIndex", 99u);
            }
            else
            {
                var weights = SceneValues.Items(copy, "WeightArray");
                if (weights.Count == 0) continue;
                ((AktStruct)weights[0]!).Set("Weight", 0.125f);
                weights.Add(((AktStruct)weights[0]!).Clone());
            }
            // The whole column copied the same way.
            var columnCopy = (IList<object?>)AktStruct.CopyValue(column, shareLargeArrays: false)!;
            columnCopy.RemoveAt(0);
        }
        Assert.Equal(before, Hash(model));
    }

    [SceneTheory, SceneSamples]
    public void AForeignValue_DemotesTheList_AndTheBytesStayTheSame(string path)
    {
        if (!AktPackedKeys.Enabled) return;
        var model = TestFiles.LoadScene(path);
        if (!Columns(model).Any()) return;
        string before = Hash(model);
        var meshes = Meshes(model);
        var polygons = meshes.Select(m => m.Polygons).ToList();
        var weights = meshes.Select(m => m.SkinWeights).ToList();

        // A foreign value inside one element's vector demotes that vector and its column.
        var (mesh, _, column) = Columns(model).First();
        var inner = SceneValues.Items((AktStruct)column[0]!, AktNames.Key(column.Shape.TypeName) == "CPolygon" ? "IndexArray" : "WeightArray");
        inner.Add("not an item");
        Assert.False(column.IsPacked);
        inner.RemoveAt(inner.Count - 1);
        Assert.Equal(before, Hash(model));

        Assert.True(DemoteAll(model) > 0);
        Assert.Equal(before, Hash(model));
        for (int m = 0; m < meshes.Count; m++)
        {
            AssertSamePolygons(polygons[m], meshes[m].Polygons);
            AssertSameWeights(weights[m], meshes[m].SkinWeights);
        }
    }

    [SceneTheory, SceneSamples]
    public void RebuildingWeightLists_LikeAJointDelete_MatchesThePlainLists(string path)
    {
        if (!AktPackedKeys.Enabled) return;
        string? expected = null;
        foreach (bool demote in new[] { true, false })
        {
            var model = TestFiles.LoadScene(path);
            if (!Columns(model).Any(c => c.Field != "PolygonArray")) return;
            if (demote) DemoteAll(model);
            foreach (var mesh in Meshes(model))
                foreach (var field in new[] { "SkinWeightArray", "RigidSkinWeightArray" })
                    foreach (var entry in SceneValues.Items(mesh.Source, field).Take(200))
                    {
                        // The pattern RigEditing uses: edit the items, clear the list, add them back.
                        var list = SceneValues.Items((AktStruct)entry!, "WeightArray");
                        var kept = list.Cast<AktStruct>().Reverse().ToList();
                        foreach (var w in kept) w.Set("BoneIndex", w.Get<uint>("BoneIndex") + 1u);
                        list.Clear();
                        foreach (var w in kept) list.Add(w);
                    }
            string hash = Hash(model);
            if (demote) expected = hash;
            else
            {
                Assert.Equal(expected, hash);
                Assert.Contains(Columns(model), c => c.Field != "PolygonArray");
            }
        }
    }

    /// <summary>
    /// A whole-field rewrite (Bind Skin, the Skinning Workshop, the FBX import, the AutoRigger):
    /// freshly built struct lists go through <see cref="AktArrayColumn.Pack"/>, come out packed, and
    /// save to the same bytes as the plain lists.
    /// </summary>
    [SceneTheory, SceneSamples]
    public void AWholeFieldRewrite_IsPacked_AndSavesLikeThePlainList(string path)
    {
        if (!AktPackedKeys.Enabled) return;
        string? expected = null;
        foreach (bool pack in new[] { false, true })
        {
            var model = TestFiles.LoadScene(path);
            var schema = model.Document.Schema;
            if (!Columns(model).Any()) return;
            int packed = 0;
            foreach (var mesh in Meshes(model))
            {
                var polygons = new List<object?>();
                foreach (var p in mesh.Polygons)
                {
                    var polygon = schema.NewStruct("CPolygon");
                    var corners = (List<object?>)polygon["IndexArray"]!;
                    foreach (uint c in p.Corners) corners.Add(c);
                    polygon.Set("MatIndex", p.MaterialIndex);
                    polygons.Add(polygon);
                }
                var weights = new List<object?>();
                foreach (var vertex in mesh.SkinWeights)
                {
                    var array = schema.NewStruct("CVertexWeightArray");
                    var entries = (List<object?>)array["WeightArray"]!;
                    foreach (var (bone, weight) in vertex)
                    {
                        var entry = schema.NewStruct("CVertexWeight");
                        entry.Set("BoneIndex", (uint)bone);
                        entry.Set("Weight", weight);
                        entries.Add(entry);
                    }
                    weights.Add(array);
                }
                var newPolygons = pack ? AktArrayColumn.Pack(schema, "CPolygon", polygons) : polygons;
                var newWeights = pack ? AktArrayColumn.Pack(schema, "CVertexWeightArray", weights) : weights;
                if (pack)
                {
                    Assert.True(polygons.Count == 0 || newPolygons is AktArrayColumn { IsPacked: true }, "polygons not packed");
                    Assert.True(weights.Count == 0 || newWeights is AktArrayColumn { IsPacked: true }, "weights not packed");
                    packed += newPolygons is AktArrayColumn ? 1 : 0;
                }
                if (mesh.Source["PolygonArray"] is IList<object?> { Count: > 0 }) mesh.Source.Set("PolygonArray", newPolygons);
                if (mesh.Source["SkinWeightArray"] is IList<object?> { Count: > 0 }) mesh.Source.Set("SkinWeightArray", newWeights);
            }
            string hash = Hash(model);
            if (!pack) expected = hash;
            else
            {
                Assert.True(packed > 0);
                Assert.Equal(expected, hash);
            }
        }
    }
}

using System.Numerics;
using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Attribute layers: the layer-major index slices, the FBX
/// export of one LayerElement per set (and one Layer per extra set) and the import that reads
/// every set back. No shipped Resource3D mesh has a second UV set, so most tests copy a mesh's
/// first set into a second one in memory; a sample scene with real multi-set meshes
/// (larger character autosaves) is used when SLOPPYANIMATSU_SAMPLES has one.
/// </summary>
public sealed class MeshLayerTests
{
    private static readonly FbxExportOptions Fixed = new() { Timestamp = new DateTime(2026, 1, 1, 12, 0, 0) };

    // ---- the typed model -----------------------------------------------------------------------

    [Fact]
    public void One_layer_means_the_whole_index_array()
    {
        if (TestFiles.Resource3D is not { } path) return;
        var mesh = TestFiles.LoadScene(path).Scene.Meshes.First(m => m.UVLayerCount == 1 && m.CornerUVs.Length > 0);

        Assert.Equal(1, mesh.NormalLayerCount);
        Assert.Equal(mesh.CornerControlPoints.Length, mesh.CornerNormalsOf(0).Length);
        Assert.Equal(mesh.CornerNormals, mesh.CornerNormalsOf(0));
        Assert.Equal(mesh.CornerUVs, mesh.CornerUVsOf(0));
        // Resource3D (20.1.4) has no UVLayersNameArray member; newer scenes do.
        Assert.Equal(mesh.UVSetNames.Count > 0 ? mesh.UVSetNames[0] : "", mesh.UVLayerName(0));
        Assert.Empty(mesh.CornerUVsOf(1));
    }

    [Fact]
    public void Two_layers_are_two_consecutive_slices_of_one_array()
    {
        if (TestFiles.Resource3D is not { } path) return;
        var mesh = TestFiles.LoadScene(path).Scene.Meshes
            .First(m => m.UVLayerCount == 1 && m.UVs.Length > 3 && m.CornerUVs.Length > 0);
        var first = mesh.CornerUVsOf(0);
        AddSecondUvLayer(mesh);

        int corners = mesh.CornerControlPoints.Length;
        Assert.Equal(2, mesh.UVLayerCount);
        Assert.Equal(mesh.Source.Has("UVLayersNameArray") ? "Eyes" : "", mesh.UVLayerName(1));
        if (mesh.Source.Has("UVLayersNameArray"))
            Assert.Equal(new[] { "Base", "Eyes" }, mesh.UVSetNames);
        Assert.Equal(corners * 2, mesh.CornerUVs.Length);
        Assert.Equal(first, mesh.CornerUVsOf(0));
        Assert.Equal(mesh.CornerUVs.Skip(corners), mesh.CornerUVsOf(1));
        // The second set points at other values than the first.
        Assert.NotEqual(mesh.CornerUVsOf(0), mesh.CornerUVsOf(1));
    }

    [SceneTheory, SceneSamples]
    public void A_sample_with_real_uv_sets_has_layer_major_slices(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var mesh in model.Scene.Meshes.Where(m => m.UVLayerCount > 1))
        {
            int corners = mesh.CornerControlPoints.Length;
            Assert.Equal(mesh.UVLayerCount * corners, mesh.CornerUVs.Length);
            if (mesh.Source.Has("UVLayersNameArray"))
                Assert.Equal(mesh.UVLayerCount, mesh.UVSetNames.Count);
            for (int layer = 0; layer < mesh.UVLayerCount; layer++)
            {
                var slice = mesh.CornerUVsOf(layer);
                Assert.Equal(corners, slice.Length);
                Assert.All(slice, index => Assert.InRange(index, 0u, (uint)(mesh.UVs.Length - 1)));
            }
        }
    }

    // ---- FBX round trip ------------------------------------------------------------------------

    [Fact]
    public void The_export_writes_a_layer_element_and_a_layer_per_uv_set()
    {
        if (TestFiles.Resource3D is not { } path) return;
        var model = TestFiles.LoadScene(path);
        var mesh = model.Scene.Meshes
            .First(m => m.UVLayerCount == 1 && m.UVs.Length > 3 && m.CornerUVs.Length > 0);
        AddSecondUvLayer(mesh);

        var document = FbxDocument.Read(FbxExporter.Export(model, Fixed));
        var geometry = document.Node("Objects")!.Children
            .First(o => o.Name == "Geometry" && FbxScene.ShortName(o) == mesh.Name);

        var uvSets = geometry.ChildrenNamed("LayerElementUV").ToList();
        Assert.Equal(2, uvSets.Count);
        Assert.Equal(ExportedUvName(mesh, 0), uvSets[0].Value<string>("Name"));
        Assert.Equal(ExportedUvName(mesh, 1), uvSets[1].Value<string>("Name"));
        Assert.Equal(mesh.CornerControlPoints.Length, ((int[])uvSets[0].Child("UVIndex")!.Properties[0]).Length);
        Assert.Equal(mesh.CornerControlPoints.Length, ((int[])uvSets[1].Child("UVIndex")!.Properties[0]).Length);
        Assert.Equal(mesh.CornerUVsOf(1), ((int[])uvSets[1].Child("UVIndex")!.Properties[0]).Select(i => (uint)i));

        // The first Layer holds the first set, the second Layer repeats with TypedIndex 1, as
        // Blender's FBX writer packs several UV sets.
        var layers = geometry.ChildrenNamed("Layer").ToList();
        Assert.Equal(2, layers.Count);
        var typedIndexes = layers
            .Select(l => l.ChildrenNamed("LayerElement").Single(e => e.Value<string>("Type") == "LayerElementUV"))
            .Select(e => e.Value<int>("TypedIndex"))
            .ToArray();
        Assert.Equal(new[] { 0, 1 }, typedIndexes);
        // The material belongs to the first layer only.
        Assert.Single(layers[0].ChildrenNamed("LayerElement").Where(e => e.Value<string>("Type") == "LayerElementMaterial"));
        Assert.Empty(layers[1].ChildrenNamed("LayerElement").Where(e => e.Value<string>("Type") == "LayerElementMaterial"));
    }

    [Fact]
    public void The_import_reads_every_uv_set_back()
    {
        if (TestFiles.Resource3D is not { } path) return;
        var model = TestFiles.LoadScene(path);
        var mesh = model.Scene.Meshes
            .First(m => m.UVLayerCount == 1 && m.UVs.Length > 3 && m.CornerUVs.Length > 0);
        AddSecondUvLayer(mesh);

        var imported = FbxImporter.Import(FbxDocument.Read(FbxExporter.Export(model, Fixed)), null);
        var round = imported.Scene.Meshes.First(m => m.Name == mesh.Name);

        Assert.Equal(2, round.UVLayerCount);
        Assert.Equal(new[] { ImportedUvName(mesh, 0), ImportedUvName(mesh, 1) }, round.UVSetNames);
        Assert.Equal(mesh.CornerControlPoints.Length, round.CornerUVsOf(0).Length);
        Assert.Equal(mesh.CornerControlPoints.Length, round.CornerUVsOf(1).Length);
        AssertResolvedUVsEqual(mesh, 0, round, 0);
        AssertResolvedUVsEqual(mesh, 1, round, 1);
        // The import appends set values instead of deduplicating (fbx-import.md "Geometry").
        Assert.Equal(mesh.UVs.Length * 2, round.UVs.Length);
    }

    [Fact]
    public void A_second_normal_layer_round_trips()
    {
        if (TestFiles.Resource3D is not { } path) return;
        var model = TestFiles.LoadScene(path);
        var mesh = model.Scene.Meshes
            .First(m => m.NormalLayerCount == 1 && m.CornerNormals.Length > 0 && m.Normals.Length > 1);
        AddSecondNormalLayer(mesh);

        var document = FbxDocument.Read(FbxExporter.Export(model, Fixed));
        var geometry = document.Node("Objects")!.Children
            .First(o => o.Name == "Geometry" && FbxScene.ShortName(o) == mesh.Name);
        var sets = geometry.ChildrenNamed("LayerElementNormal").ToList();
        Assert.Equal(2, sets.Count);
        Assert.Equal(mesh.CornerControlPoints.Length, ((int[])sets[1].Child("NormalsIndex")!.Properties[0]).Length);
        var normalLayerIndex = geometry.ChildrenNamed("Layer")
            .Select(l => l.ChildrenNamed("LayerElement").SingleOrDefault(e => e.Value<string>("Type") == "LayerElementNormal"))
            .Where(e => e != null)
            .Select(e => e!.Value<int>("TypedIndex"))
            .ToArray();
        Assert.Equal(new[] { 0, 1 }, normalLayerIndex);

        var round = FbxImporter.Import(document, null).Scene.Meshes.First(m => m.Name == mesh.Name);
        Assert.Equal(2, round.NormalLayerCount);
        AssertResolvedNormalsEqual(mesh, 0, round, 0);
        AssertResolvedNormalsEqual(mesh, 1, round, 1);
    }

    [Fact]
    public void An_imported_multi_set_sample_keeps_every_set()
    {
        var path = TestFiles.AllSceneSamples.FirstOrDefault(f => Path.GetFileName(f).StartsWith("DragonPrince"));
        if (path == null) return;
        var model = TestFiles.LoadScene(path);
        var mesh = model.Scene.Meshes.FirstOrDefault(m => m.UVLayerCount > 1);
        if (mesh == null) return;

        var imported = FbxImporter.Import(FbxDocument.Read(FbxExporter.Export(model, Fixed)), null);
        var round = imported.Scene.Meshes.First(m => m.Name == mesh.Name);

        Assert.Equal(mesh.UVLayerCount, round.UVLayerCount);
        Assert.Equal(mesh.UVSetNames, round.UVSetNames);
        for (int layer = 0; layer < mesh.UVLayerCount; layer++)
            AssertResolvedUVsEqual(mesh, layer, round, layer);
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>Copies a mesh's UV indices into a second layer that points at other values.</summary>
    private static void AddSecondUvLayer(Mesh mesh)
    {
        var indices = mesh.CornerUVsOf(0);
        int values = mesh.UVs.Length;
        var all = new List<object?>(indices.Length * 2);
        all.AddRange(indices.Select(i => (object?)i));
        all.AddRange(indices.Select(i => (object?)(uint)(((long)i + 1) % values)));
        mesh.Source.Set("UVIArray", all);
        mesh.Source.Set("UVLayersCount", 2u);
        // Resource3D (20.1.4) has no UVLayersNameArray member; the exporter then falls back to
        // the set's number for the extra layer.
        if (mesh.Source.Has("UVLayersNameArray"))
            mesh.Source.Set("UVLayersNameArray", new List<object?> { "Base", "Eyes" });
    }

    /// <summary>The name the export writes for a UV set.</summary>
    private static string ExportedUvName(Mesh mesh, int layer)
    {
        string name = mesh.UVLayerName(layer);
        return name.Length > 0 || layer == 0 ? name : $"UVSet{layer}";
    }

    /// <summary>The name the import gives back to a UV set.</summary>
    private static string ImportedUvName(Mesh mesh, int layer)
    {
        string name = ExportedUvName(mesh, layer);
        return name.Length > 0 ? name : layer == 0 ? "UVMap" : $"UVSet{layer}";
    }

    /// <summary>Copies a mesh's normal indices into a second layer that points at other values.</summary>
    private static void AddSecondNormalLayer(Mesh mesh)
    {
        var indices = mesh.CornerNormalsOf(0);
        int values = mesh.Normals.Length;
        var all = new List<object?>(indices.Length * 2);
        all.AddRange(indices.Select(i => (object?)i));
        all.AddRange(indices.Select(i => (object?)(uint)(((long)i + 1) % values)));
        mesh.Source.Set("NormalIArray", all);
        mesh.Source.Set("NormalLayersCount", 2u);
    }

    private static void AssertResolvedNormalsEqual(Mesh expected, int expectedLayer, Mesh actual, int actualLayer)
    {
        var expectedIndices = expected.CornerNormalsOf(expectedLayer);
        var actualIndices = actual.CornerNormalsOf(actualLayer);
        Assert.Equal(expectedIndices.Length, actualIndices.Length);
        for (int corner = 0; corner < expectedIndices.Length; corner++)
        {
            var e = expected.Normals[expectedIndices[corner]];
            var a = actual.Normals[actualIndices[corner]];
            Assert.True(Vector3.Distance(e, a) < 1e-4f, $"corner {corner}: {a} != {e}");
        }
    }

    private static void AssertResolvedUVsEqual(Mesh expected, int expectedLayer, Mesh actual, int actualLayer)
    {
        var expectedUVs = expected.UVs;
        var actualUVs = actual.UVs;
        var expectedIndices = expected.CornerUVsOf(expectedLayer);
        var actualIndices = actual.CornerUVsOf(actualLayer);
        Assert.Equal(expectedIndices.Length, actualIndices.Length);
        for (int corner = 0; corner < expectedIndices.Length; corner++)
        {
            var e = expectedUVs[expectedIndices[corner]];
            var a = actualUVs[actualIndices[corner]];
            Assert.True(Vector2.Distance(e, a) < 1e-4f,
                $"corner {corner}: ({a.X}, {a.Y}) != ({e.X}, {e.Y})");
        }
    }
}

using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Aknm;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Importing another `.akt` scene: the merge of the
/// File &gt; Import of an `.akt`, checked on the sample scenes.
/// </summary>
public sealed class AktImportTests : IDisposable
{
    private readonly List<string> _temporaryFiles = [];

    public void Dispose()
    {
        foreach (string path in _temporaryFiles)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Importing_a_scene_adds_its_characters_meshes_and_animations_and_leaves_the_scene_alone()
    {
        if (Load("7_AddingIK.akt") is not { } target || Load("19_run.akt") is not { } source) return;
        var existingCharacter = target.Scene.Characters.Single();
        var currentCharacter = target.Scene.CurrentCharacter;
        int frameRate = (int)target.Scene.FrameRate;
        int cameras = target.Scene.Cameras.Count;
        int targetMaterials = target.Scene.Materials.Count();
        int targetTextures = target.Scene.Textures.Count();

        var result = AktImport.Import(target, source.Document);

        // 19_run has two characters: Dup (105 animations) and Dup_BCRef (none). The target already
        // has a character named Dup, so the import renames the duplicate the way the app does
        Assert.Equal(2, result.Characters.Count);
        Assert.Equal(new[] { "Dup_02", "Dup_BCRef" }, result.Characters.Select(c => c.Name));
        Assert.Equal(105, result.Animations.Count);
        Assert.All(result.Animations, a => Assert.Equal("Dup_02", a.Character?.Name));
        Assert.Contains(result.Characters[0].Animations, a => a.Name == "Run_3");
        Assert.Equal("Run_3", result.Characters[0].CurrentAnimation?.Name);
        Assert.Equal(4, result.Characters[0].Meshes.Count());

        // The open scene's own objects and settings are untouched; imported names are made unique
        // against it, as the import does (Dup -> Dup_02, Dup_b -> Dup_b_02).
        Assert.Equal(3, target.Scene.Characters.Count());
        Assert.Same(existingCharacter, target.Scene.Characters.First());
        Assert.Same(currentCharacter, target.Scene.CurrentCharacter);
        Assert.Empty(existingCharacter.Animations);
        Assert.Equal(cameras, target.Scene.Cameras.Count);
        Assert.Equal(frameRate, (int)target.Scene.FrameRate);
        Assert.Equal(8, result.SkippedCameras);
        Assert.Equal(0, result.SkippedBlendShapeSights);

        // The source's material and texture pools were appended to the target's own dummies.
        Assert.Equal(13, result.Materials.Count);
        Assert.Equal(10, result.Textures.Count);
        Assert.Equal(targetMaterials + 13, target.Scene.Materials.Count());
        Assert.Equal(targetTextures + 10, target.Scene.Textures.Count());
        var materialDummy = target.Document.Root.Children.Single(c => c.Name == AktImport.MaterialDummyName);
        var textureDummy = target.Document.Root.Children.Single(c => c.Name == AktImport.TextureDummyName);
        Assert.All(result.Materials, m => Assert.Same(materialDummy, m.Source.Parent));
        Assert.All(result.Textures, t => Assert.Same(textureDummy, t.Source.Parent));

        // Imported meshes point at the copies, not at materials that stayed in the source document.
        var sourceObjects = new HashSet<AktObject>(source.Document.AllObjects(), ReferenceEqualityComparer.Instance);
        foreach (var mesh in result.Characters.SelectMany(c => c.Meshes))
            foreach (var material in mesh.Materials)
                Assert.DoesNotContain(material.Source, sourceObjects);
    }

    [Fact]
    public void The_imported_character_evaluates_to_the_source_poses()
    {
        if (Load("7_AddingIK.akt") is not { } target || Load("19_run.akt") is not { } source) return;
        var sourceCharacter = source.Scene.Characters.First(c => c.Name == "Dup");
        var sourceRun = sourceCharacter.Animations.First(a => a.Name == "Run_3");
        var result = AktImport.Import(target, source.Document);
        var imported = result.Characters.First(c => c.Name == "Dup_02");
        var importedRun = imported.Animations.First(a => a.Name == "Run_3");

        // Same stacks, key times and tracks.
        Assert.Equal(sourceRun.Stacks.Count, importedRun.Stacks.Count);
        for (int s = 0; s < sourceRun.Stacks.Count; s++)
        {
            Assert.Equal(sourceRun.Stacks[s].Keys.Select(k => k.Time), importedRun.Stacks[s].Keys.Select(k => k.Time));
            Assert.Equal(sourceRun.Stacks[s].Tracks.Select(t => t.Target?.Name),
                importedRun.Stacks[s].Tracks.Select(t => t.Target?.Name));
        }

        var from = new PoseEvaluator(sourceCharacter);
        var to = new PoseEvaluator(imported);
        foreach (int frame in new[] { 0, 1, 3, 5, 9, 15, 30 })
        {
            var expected = from.LocalTransforms(sourceRun, frame);
            var actual = to.LocalTransforms(importedRun, frame);
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                float difference = AktMath.MaxDifference(expected[i].ToMatrix(), actual[i].ToMatrix());
                Assert.True(difference < 1e-6f,
                    $"controller {imported.Controllers[i].Name} differs by {difference} at frame {frame}");
            }
        }
    }

    [Fact]
    public void The_merged_document_saves_reloads_and_saves_byte_exact()
    {
        if (Load("7_AddingIK.akt") is not { } target || Load("19_run.akt") is not { } source) return;
        var result = AktImport.Import(target, source.Document);
        string first = TempName();
        target.Save(first);

        var reloaded = TestFiles.LoadScene(first);
        Assert.Equal(3, reloaded.Scene.Characters.Count());
        Assert.Equal(1, reloaded.Scene.Characters.Count(c => c.Name == "Dup"));
        Assert.Equal(1, reloaded.Scene.Characters.Count(c => c.Name == "Dup_02"));
        var imported = reloaded.Scene.Characters.First(c => c.Name == "Dup_02" && c.Animations.Any());
        var run = imported.Animations.First(a => a.Name == "Run_3");
        Assert.Equal(105, imported.Animations.Count());
        Assert.Equal(result.Animations.Count, reloaded.Scene.Characters.Sum(c => c.Animations.Count()));
        Assert.NotEmpty(new PoseEvaluator(imported).WorldMatrices(run, 9));

        string second = TempName();
        reloaded.Save(second);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Fact]
    public void A_file_with_a_different_version_is_refused_unless_forced()
    {
        if (Load("7_AddingIK.akt") is not { } target || TestFiles.Resource3D is not { } resource) return;
        var source = AktDocument.Load(resource);
        Assert.NotEqual(AktVersion.Current, source.Version);

        var error = Assert.Throws<InvalidOperationException>(() => AktImport.Import(target, source));
        Assert.Equal(AktImport.VersionMismatchMessage, error.Message);
        Assert.Contains("format version", error.Message);

        var result = AktImport.Import(target, source, new AktImportOptions { AllowDifferentVersion = true });
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("version"));
        // Resource3D's 31 top-level meshes and 16 materials came across.
        Assert.Equal(31, result.Objects.Count(o => o is Mesh));
        Assert.Equal(16, result.Materials.Count);
    }

    [Fact]
    public void Missing_material_and_texture_dummies_are_created()
    {
        if (Load("7_AddingIK.akt") is not { } baseModel || Load("19_run.akt") is not { } source) return;
        var model = EmptyScene(baseModel);
        Assert.Empty(model.Scene.Materials);
        Assert.Empty(model.Scene.Textures);

        var result = AktImport.Import(model, source.Document);

        Assert.Equal(13, result.Materials.Count);
        Assert.Equal(10, result.Textures.Count);
        Assert.Equal(AktImport.MaterialDummyName,
            model.Document.Root.Children.Single(c => c.Name == AktImport.MaterialDummyName).Name);
        Assert.Equal(AktImport.TextureDummyName,
            model.Document.Root.Children.Single(c => c.Name == AktImport.TextureDummyName).Name);
        Assert.Equal(13, model.Scene.Materials.Count());
        Assert.Equal(10, model.Scene.Textures.Count());
    }

    [Fact]
    public void A_source_without_pools_creates_no_empty_dummies()
    {
        if (Load("7_AddingIK.akt") is not { } baseModel || TestFiles.Resource3D is not { } resource) return;
        var model = EmptyScene(baseModel);

        var result = AktImport.Import(model, AktDocument.Load(resource),
            new AktImportOptions { AllowDifferentVersion = true });

        Assert.NotEmpty(result.Materials);
        Assert.Empty(result.Textures);
        Assert.Contains(model.Document.Root.Children, c => c.Name == AktImport.MaterialDummyName);
        Assert.DoesNotContain(model.Document.Root.Children, c => c.Name == AktImport.TextureDummyName);
    }

    [Fact]
    public void Blendshape_sights_and_top_level_cameras_are_skipped()
    {
        if (Load("7_AddingIK.akt") is not { } target || Load("19_run.akt") is not { } source) return;
        // No sample uses a CBlendShapesSight, so give the source's schema the class the app has.
        var schema = source.Document.Schema;
        if (schema.FindClass("class CBlendShapesSight") == null)
            schema.AddClass(new AktClassDef("class CBlendShapesSight"), schema.FindClass("class CDataObject"));
        var sight = schema.NewObject("class CBlendShapesSight");
        sight.Set("Name", "Sight");
        source.Document.Root.AddChild(sight);

        var result = AktImport.Import(target, source.Document);

        Assert.Equal(1, result.SkippedBlendShapeSights);
        Assert.Equal(8, result.SkippedCameras);
        Assert.DoesNotContain(result.Objects, o => o.ClassName == "CBlendShapesSight");
        Assert.Equal(8, target.Scene.Cameras.Count);
    }

    [Fact]
    public void Importing_the_same_scene_twice_makes_the_names_unique_and_the_copies_distinct()
    {
        if (Load("7_AddingIK.akt") is not { } target || Load("7_AddingIK.akt") is not { } source) return;
        var existing = target.Scene.Characters.Single();
        var existingMeshes = existing.Meshes.ToList();
        var existingMaterials = target.Scene.Materials.ToList();

        var result = AktImport.Import(target, source.Document);

        // The app makes an imported name unique against the target, so a
        // second copy of the scene's own character is Dup_02, not a second Dup.
        Assert.Equal(new[] { "Dup", "Dup_02" }, target.Scene.Characters.Select(c => c.Name));
        Assert.Equal(2, target.Scene.Characters.Count());
        Assert.Equal(existingMaterials.Count + 8, target.Scene.Materials.Count());
        Assert.Contains(target.Scene.Materials, m => m.Name == "Dup_b_02");
        Assert.Equal(12, target.Scene.Textures.Count());
        Assert.Same(existing, target.Scene.Characters.First());
        Assert.All(result.Characters.Single().Meshes, m => Assert.DoesNotContain(m, existingMeshes));
        Assert.All(result.Materials, m => Assert.DoesNotContain(m, existingMaterials));
    }

    [Fact]
    public void Importing_from_a_file_points_the_scenes_fbx_folder_at_that_files_folder()
    {
        if (Load("7_AddingIK.akt") is not { } target || Load("19_run.akt") is not { } source) return;
        string imported = TestFiles.AllSceneSamples.First(p => Path.GetFileName(p) == "19_run.akt");
        string before = ScenePaths.Get(target, ScenePaths.FbxPath)!;

        AktImport.Import(target, source.Document, null, imported);

        // The.akt Import writes the imported file's folder into CScene.FBXPath
        // A scene in A importing B/other.akt got B/, not its own folder and not the
        // imported scene's FBXPath); Save and Open leave FBXPath alone. The folder is spelled like
        // The: forward slashes, one trailing slash.
        Assert.Equal(ScenePaths.Folder(imported), ScenePaths.Get(target, ScenePaths.FbxPath));
        Assert.NotEqual(before, ScenePaths.Get(target, ScenePaths.FbxPath));
        Assert.NotEqual(ScenePaths.Get(source, ScenePaths.FbxPath), ScenePaths.Get(target, ScenePaths.FbxPath));
    }

    [Fact]
    public void Importing_a_document_without_a_file_leaves_the_fbx_folder_alone()
    {
        if (Load("7_AddingIK.akt") is not { } target || Load("19_run.akt") is not { } source) return;
        string before = ScenePaths.Get(target, ScenePaths.FbxPath)!;

        AktImport.Import(target, source.Document);

        Assert.Equal(before, ScenePaths.Get(target, ScenePaths.FbxPath));
    }

    [Fact]
    public void A_file_that_is_not_a_scene_is_refused()
    {
        if (Load("7_AddingIK.akt") is not { } target) return;
        var schema = AknmSchema.Clone(target.Document.Schema);
        var document = new AktDocument(schema, new[] { schema.NewObject("class CDummy") });
        Assert.Throws<InvalidDataException>(() => AktImport.Import(target, document));
    }

    // ---- helpers --------------------------------------------------------------------------

    private static SceneModel? Load(string fileName) =>
        TestFiles.AllSceneSamples.FirstOrDefault(p => Path.GetFileName(p) == fileName) is { } path
            ? TestFiles.LoadScene(path)
            : null;

    private static SceneModel EmptyScene(SceneModel schemaSource)
    {
        var schema = AknmSchema.Clone(schemaSource.Document.Schema);
        var document = new AktDocument(schema, new[] { schema.NewObject("class CScene") });
        return new SceneModel(document);
    }

    private string TempName(string extension = ".akt")
    {
        string path = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-aktimport-{Guid.NewGuid():N}{extension}");
        _temporaryFiles.Add(path);
        return path;
    }
}

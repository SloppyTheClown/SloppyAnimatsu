using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Aknm;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// `.aknm` export and import. No reference file exists, so
/// the tests export from a real scene, read the result back and import it into a fresh copy of the
/// same scene; they return early when the samples are absent.
/// </summary>
public sealed class AknmTests : IDisposable
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
    public void The_export_is_one_character_without_meshes_and_only_the_selected_animations()
    {
        if (Load("19_run.akt") is not { } model) return;
        var character = Dup(model);
        var run = character.Animations.First(a => a.Name == "Run_3");

        var document = AknmExport.Build(model, character, [run]);

        Assert.Equal("class CCharacterAKNM", document.Root.TypeName);
        Assert.Equal("Dup", document.Root.Name);
        Assert.DoesNotContain(document.AllObjects(), o => AknmFormat.IsA(document, o, "CMesh"));
        Assert.Equal(new[] { "Run_3", "__RigAnim" }, AnimationNames(document));
        Assert.True(document.AllObjects().Count() < character.Source.PreOrder().Count(),
            "the export must not carry the scene or the unselected animations");

        // No reference points outside the file; every reference either resolves or is null.
        var inside = new HashSet<AktObject>(document.AllObjects(), ReferenceEqualityComparer.Instance);
        foreach (var obj in document.AllObjects())
            foreach (var field in obj.Fields)
                AssertResolved(field.Value, inside);
        Assert.Null(model.Document.Schema.FindClass("class CCharacterAKNM")); // the scene's schema is untouched
    }

    [Fact]
    public void The_exported_animation_is_structurally_the_source_animation()
    {
        if (Load("19_run.akt") is not { } model) return;
        var character = Dup(model);
        var run = character.Animations.First(a => a.Name == "Run_3");

        var document = AknmExport.Build(model, character, [run]);
        var exported = document.AllObjects().First(o => o.Name == "Run_3" && AknmFormat.IsA(document, o, "CAnimation"));

        AssertSame(run.Source, exported);

        // A written-and-reloaded .aknm is byte-exact.
        string first = TempName(".aknm");
        document.Save(first);
        var reloaded = AktDocument.Load(first);
        string second = TempName(".aknm");
        reloaded.Save(second);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        Assert.Equal(new[] { "Run_3", "__RigAnim" }, AnimationNames(reloaded));
    }

    [Fact]
    public void A_file_from_an_older_schema_gets_the_missing_member_defaulted()
    {
        if (Load("19_run.akt") is not { } model) return;
        var character = Dup(model);
        var run = character.Animations.First(a => a.Name == "Run_3");
        var document = AknmExport.Build(model, character, [run]);

        // Simulate an older .aknm by removing a member from both the schema and the object.
        var definition = document.Schema.FindClass("class CAnimation")!;
        definition.Members.RemoveAll(m => m.Name == "CurrentTime");
        var animation = document.AllObjects().First(o => o.Name == "Run_3" && AknmFormat.IsA(document, o, "CAnimation"));
        animation.Fields.RemoveAll(f => f.Name == "CurrentTime");

        string file = TempName(".aknm");
        document.Save(file);
        var aknm = AknmFile.Read(file);
        Assert.DoesNotContain("CurrentTime", aknm.Animations[0].Fields.Select(f => f.Name));

        var target = Load("19_run.akt")!;
        var result = AknmImport.Import(target, aknm, new AknmImportOptions { Character = Dup(target) });

        // The target schema knows the member; the import adds its default and the scene saves.
        Assert.Equal(0, result.Animations[0].Source["CurrentTime"]);
        string saved = TempName(".akt");
        target.Save(saved);
        var reloaded = TestFiles.LoadScene(saved).Scene.Characters.First(c => c.Name == "Dup").Animations
            .First(a => a.Name == "Run_3" && a.FolderPath.Contains("Retargeted From Dup"));
        Assert.Equal(0, reloaded.Source["CurrentTime"]);
    }

    [Fact]
    public void Exporting_every_animation_keeps_the_whole_bank()
    {
        if (Load("19_run.akt") is not { } model) return;
        var character = Dup(model);
        var all = character.Animations.ToList();

        var document = AknmExport.Build(model, character, all);

        Assert.Equal(all.Count + 1, document.AllObjects().Count(o => AknmFormat.IsA(document, o, "CAnimation")));
        Assert.Contains(document.AllObjects(), o => o.Name == "__RigAnim");
    }

    [Fact]
    public void An_exported_file_imports_back_onto_the_same_character_and_evaluates_the_same()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var run = source.Animations.First(a => a.Name == "Run_3");
        string file = TempName(".aknm");
        AknmExport.Write(model, source, [run], file);

        var aknm = AknmFile.Read(file);
        Assert.Equal("Dup", aknm.Name);
        Assert.Single(aknm.Animations);
        Assert.NotNull(aknm.RigAnimation);

        var target = Load("19_run.akt")!;
        var character = Dup(target);
        var result = AknmImport.Import(target, aknm, new AknmImportOptions { Character = character });

        Assert.Single(result.Animations);
        Assert.Equal(0, result.UnmatchedControllers);
        Assert.Equal(0, result.MismatchedControllers);
        Assert.Equal(0, result.DroppedTracks);
        Assert.Same(result.Animations[^1], character.CurrentAnimation);
        Assert.Equal(new[] { "Retargeted From Dup", "Retargeted From DupTheClown", "Common" },
            result.Animations[0].FolderPath);
        AssertSame(aknm.Animations[0], result.Animations[0].Source);

        var before = new PoseEvaluator(source);
        var after = new PoseEvaluator(character);
        foreach (int frame in new[] { 0, 3, 12, 25, 40 })
        {
            var a = before.LocalTransforms(run, frame, out var worldA);
            var b = after.LocalTransforms(result.Animations[0], frame, out var worldB);
            Assert.Equal(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.True(Vector3.Distance(a[i].Translation, b[i].Translation) < 1e-4f,
                    $"frame {frame}, controller {before.Controllers[i].Name}: translation differs");
                Assert.True(MathF.Abs(MathF.Abs(Quaternion.Dot(a[i].Rotation, b[i].Rotation)) - 1f) < 1e-5f,
                    $"frame {frame}, controller {before.Controllers[i].Name}: rotation differs");
                Assert.True(Vector3.Distance(worldA[i].Translation, worldB[i].Translation) < 1e-3f,
                    $"frame {frame}, controller {before.Controllers[i].Name}: world position differs");
            }
        }

        string saved = TempName(".akt");
        target.Save(saved);
        var reloaded = TestFiles.LoadScene(saved);
        string again = TempName(".akt");
        reloaded.Save(again);
        Assert.Equal(File.ReadAllBytes(saved), File.ReadAllBytes(again));
    }

    [Fact]
    public void A_target_controller_that_is_gone_loses_its_track()
    {
        if (Load("19_run.akt") is not { } model) return;
        var run = Dup(model).Animations.First(a => a.Name == "Run_3");
        string file = TempName(".aknm");
        AknmExport.Write(model, Dup(model), [run], file);
        var aknm = AknmFile.Read(file);

        var target = Load("19_run.akt")!;
        var character = Dup(target);
        var controllers = (List<AktReference>)character.Source["Controllers"]!;
        int index = controllers.FindIndex(r => r.Target?.Name == "IK_Neck");
        Assert.True(index >= 0, "19_run has an IK_Neck controller");
        controllers.RemoveAt(index);

        var result = AknmImport.Import(target, aknm, new AknmImportOptions { Character = character });

        Assert.Equal(1, result.UnmatchedControllers);
        Assert.True(result.DroppedTracks >= 1, "the IK_Neck track must be dropped");
        Assert.DoesNotContain(result.Animations[0].Stacks.SelectMany(s => s.Tracks),
            t => t.Target?.Name == "IK_Neck");
        Assert.Contains(result.Warnings, w => w.Contains("no counterpart"));
    }

    [Fact]
    public void Importing_at_the_bank_root_reuses_folders_and_uniquifies_the_name()
    {
        if (Load("19_run.akt") is not { } model) return;
        var run = Dup(model).Animations.First(a => a.Name == "Run_3");
        string file = TempName(".aknm");
        AknmExport.Write(model, Dup(model), [run], file);
        var aknm = AknmFile.Read(file);

        var target = Load("19_run.akt")!;
        var result = AknmImport.Import(target, aknm,
            new AknmImportOptions { Character = Dup(target), RetargetFolder = false });

        // 19_run already has __Anims/Retargeted From DupTheClown/Common/Run_3, so the folder is
        // reused and the copy must not repeat the name. The bank's rule strips the trailing _3
        // and takes the next free "Run_NN", which is Run_04.
        Assert.Equal("Run_04", result.Animations[0].Name);
        Assert.Equal(new[] { "Retargeted From DupTheClown", "Common" }, result.Animations[0].FolderPath);
        Assert.Same(result.Animations[0], Dup(target).CurrentAnimation);
    }

    [Fact]
    public void A_different_reference_pose_is_refused_unless_forced()
    {
        if (Load("19_run.akt") is not { } model) return;
        var run = Dup(model).Animations.First(a => a.Name == "Run_3");
        string file = TempName(".aknm");
        AknmExport.Write(model, Dup(model), [run], file);
        var aknm = AknmFile.Read(file);

        var target = Load("19_run.akt")!;
        var character = Dup(target);
        var pose = character.RefPose!;
        int index = new PoseEvaluator(character).IndexOf(character.Controllers.First(c => c.Name == "Pelvis"));
        var matrix = pose.Matrix(index);
        matrix.M41 += 5f;
        pose.SetMatrix(index, matrix);

        var error = Assert.Throws<InvalidOperationException>(
            () => AknmImport.Import(target, aknm, new AknmImportOptions { Character = character }));
        Assert.Contains("reference pose", error.Message);

        var result = AknmImport.Import(target, aknm,
            new AknmImportOptions { Character = character, AllowDifferentPose = true });
        Assert.True(result.MismatchedControllers > 0);
        Assert.Single(result.Animations);
    }

    [Fact]
    public void A_scene_is_not_an_aknm()
    {
        if (Load("19_run.akt") is not { } model) return;
        Assert.Throws<InvalidDataException>(() => AknmFile.Read(model.Document));
    }

    // ---- helpers --------------------------------------------------------------------------

    private static SceneModel? Load(string fileName) =>
        TestFiles.AllSceneSamples.FirstOrDefault(p => Path.GetFileName(p) == fileName) is { } path
            ? TestFiles.LoadScene(path)
            : null;

    private static Character Dup(SceneModel model) => model.Scene.Characters.First(c => c.Name == "Dup");

    private static string[] AnimationNames(AktDocument document) =>
        document.AllObjects()
            .Where(o => AknmFormat.IsA(document, o, "CAnimation"))
            .Select(o => o.Name ?? "?")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    private string TempName(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-aknm-{Guid.NewGuid():N}{extension}");
        _temporaryFiles.Add(path);
        return path;
    }

    private static void AssertResolved(object? value, HashSet<AktObject> inside)
    {
        switch (value)
        {
            case AktReference r:
                Assert.True(r.Target != null || r.IsNull, $"unresolved reference {r}");
                if (r.Target != null) Assert.Contains(r.Target, inside);
                break;
            case List<AktReference> refs:
                foreach (var r in refs) AssertResolved(r, inside);
                break;
            case AktStruct s:
                foreach (var f in s.Fields) AssertResolved(f.Value, inside);
                break;
            case List<object?> list:
                foreach (var v in list) AssertResolved(v, inside);
                break;
            case object?[] tuple:
                foreach (var v in tuple) AssertResolved(v, inside);
                break;
        }
    }

    /// <summary>Two object trees with the same classes, names and values; references compare by their target's class and name.</summary>
    private static void AssertSame(AktObject expected, AktObject actual)
    {
        Assert.Equal(expected.TypeName, actual.TypeName);
        Assert.Equal(expected.Name, actual.Name);
        Assert.True(expected.Fields.Count == actual.Fields.Count,
            $"{expected}: {expected.Fields.Count} fields, but the copy has {actual.Fields.Count}");
        for (int i = 0; i < expected.Fields.Count; i++)
        {
            Assert.Equal(expected.Fields[i].Name, actual.Fields[i].Name);
            Assert.Equal(expected.Fields[i].TypeName, actual.Fields[i].TypeName);
            AssertSameValue(expected.Fields[i].Value, actual.Fields[i].Value);
        }
        Assert.Equal(expected.Children.Count, actual.Children.Count);
        for (int i = 0; i < expected.Children.Count; i++) AssertSame(expected.Children[i], actual.Children[i]);
    }

    private static void AssertSameValue(object? expected, object? actual)
    {
        switch (expected)
        {
            case null:
                Assert.Null(actual);
                break;
            case AktReference r:
            {
                var other = Assert.IsType<AktReference>(actual);
                Assert.Equal(r.Target?.ClassKey, other.Target?.ClassKey);
                Assert.Equal(r.Target?.Name, other.Target?.Name);
                break;
            }
            case List<AktReference> refs:
            {
                var other = Assert.IsType<List<AktReference>>(actual);
                Assert.Equal(refs.Count, other.Count);
                for (int i = 0; i < refs.Count; i++) AssertSameValue(refs[i], other[i]);
                break;
            }
            case AktStruct s:
            {
                var other = Assert.IsType<AktStruct>(actual);
                Assert.Equal(s.TypeName, other.TypeName); // the polymorphic concrete type too
                Assert.Equal(s.Fields.Count, other.Fields.Count);
                for (int i = 0; i < s.Fields.Count; i++)
                {
                    Assert.Equal(s.Fields[i].Name, other.Fields[i].Name);
                    AssertSameValue(s.Fields[i].Value, other.Fields[i].Value);
                }
                break;
            }
            case List<object?> list:
            {
                var other = Assert.IsType<List<object?>>(actual);
                Assert.Equal(list.Count, other.Count);
                for (int i = 0; i < list.Count; i++) AssertSameValue(list[i], other[i]);
                break;
            }
            case object?[] tuple:
            {
                var other = Assert.IsType<object?[]>(actual);
                Assert.Equal(tuple.Length, other.Length);
                for (int i = 0; i < tuple.Length; i++) AssertSameValue(tuple[i], other[i]);
                break;
            }
            case float[] floats:
            {
                var other = Assert.IsType<float[]>(actual);
                Assert.Equal(floats.Length, other.Length);
                for (int i = 0; i < floats.Length; i++) Assert.Equal(floats[i], other[i]);
                break;
            }
            case byte[] bytes:
                Assert.Equal(bytes, Assert.IsType<byte[]>(actual));
                break;
            default:
                Assert.Equal(expected, actual);
                break;
        }
    }
}

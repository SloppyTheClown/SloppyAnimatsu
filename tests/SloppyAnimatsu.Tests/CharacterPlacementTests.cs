using System.Numerics;
using SloppyAnimatsu.Core;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Placing a whole character in the scene by dragging the character object itself
/// the rig and every mesh it carries follow, the edit
/// undoes, and a saved and reloaded scene shows the character where the drag left it.
/// </summary>
public class CharacterPlacementTests
{
    private static Character? FirstCharacter(SceneModel model) =>
        model.Scene.Characters.FirstOrDefault(c => c.Controllers.Count > 0);

    private static Vector3[] Positions(ScenePose pose)
    {
        var v = pose.MeshVertices;
        var result = new Vector3[v.Length / ScenePose.MeshVertexFloats];
        for (int i = 0; i < result.Length; i++)
        {
            int o = i * ScenePose.MeshVertexFloats;
            result[i] = new Vector3(v[o], v[o + 1], v[o + 2]);
        }
        return result;
    }

    private static void AssertMoved(Vector3[] before, Vector3[] after, Vector3 by, bool expectSome)
    {
        Assert.Equal(before.Length, after.Length);
        int moved = 0;
        for (int i = 0; i < before.Length; i++)
        {
            var d = after[i] - before[i];
            float tolerance = 1e-3f * MathF.Max(1, before[i].Length());
            if ((d - by).Length() <= tolerance) { if (by != Vector3.Zero) moved++; continue; }
            // A vertex of another character (or a static prop) stays where it was.
            Assert.True(d.Length() <= tolerance, $"vertex {i} moved by {d}, expected 0 or {by}");
        }
        if (expectSome) Assert.True(moved > 0, "no vertex followed the character");
    }

    [SceneTheory, SceneSamples]
    public void Translate_MovesTheRigAndItsMeshes_UndoPutsThemBack(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        var pose = new ScenePose(model);
        var session = new PoseEditSession(pose, character);
        pose.Update(character, null, 0);
        var worlds = pose.ControllerWorlds.ToArray();
        var vertices = Positions(pose);
        var by = new Vector3(100, 0, 0);

        var drag = session.BeginPlacement(character);
        drag.Translate(0, 100);
        pose.Update(character, null, 0);

        for (int i = 0; i < worlds.Length; i++)
            Assert.True((pose.ControllerWorlds[i].Translation - worlds[i].Translation - by).Length() < 1e-2f, $"controller {i}");
        AssertMoved(vertices, Positions(pose), by, expectSome: character.Meshes.Any());

        var record = drag.Commit();
        Assert.Contains(character.Name, record.Description);
        Assert.True(session.Undo());
        pose.Update(character, null, 0);
        AssertMoved(vertices, Positions(pose), Vector3.Zero, expectSome: false);
        for (int i = 0; i < worlds.Length; i++)
            Assert.True((pose.ControllerWorlds[i].Translation - worlds[i].Translation).Length() < 1e-2f, $"controller {i} after undo");

        Assert.True(session.Redo());
        pose.Update(character, null, 0);
        AssertMoved(vertices, Positions(pose), by, expectSome: character.Meshes.Any());
    }

    [SceneTheory, SceneSamples]
    public void Rotate_ThenSaveAndReload_ShowsTheSameScene(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        var pose = new ScenePose(model);
        var session = new PoseEditSession(pose, character);
        pose.Update(character, null, 0);

        var drag = session.BeginPlacement(character);
        drag.Rotate(1, MathF.PI / 3);
        drag.Translate(2, -50);
        drag.Commit();
        pose.Update(character, null, 0);
        var live = Positions(pose);
        var liveWorlds = pose.ControllerWorlds.ToArray();

        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-placement-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(temp, SceneModel.SaveFormat);
            var reloaded = SceneModel.Load(temp);
            var again = reloaded.Scene.Characters.First(c => c.Name == character.Name && c.Controllers.Count == character.Controllers.Count);
            var pose2 = new ScenePose(reloaded);
            pose2.Update(again, null, 0);
            AssertMoved(live, Positions(pose2), Vector3.Zero, expectSome: false);
            for (int i = 0; i < liveWorlds.Length; i++)
                Assert.True((pose2.ControllerWorlds[i].Translation - liveWorlds[i].Translation).Length() < 1e-2f, $"controller {i}");
        }
        finally
        {
            File.Delete(temp);
        }
    }
}

/// <summary>The progress a save or export reports (the viewer's progress bar) and that it changes no byte.</summary>
public class WriteProgressTests
{
    private sealed class Recorder : IProgress<double>
    {
        public readonly List<double> Values = [];
        public void Report(double value) => Values.Add(value);
    }

    private static void AssertRunsToOne(Recorder r)
    {
        Assert.NotEmpty(r.Values);
        for (int i = 1; i < r.Values.Count; i++) Assert.True(r.Values[i] >= r.Values[i - 1] - 1e-9, $"progress went back at {i}");
        Assert.All(r.Values, v => Assert.InRange(v, 0, 1));
        Assert.Equal(1, r.Values[^1], 6);
    }

    [SceneTheory, SceneSamples]
    public void SaveWithProgress_WritesTheSameBytes(string path)
    {
        var model = TestFiles.LoadScene(path);
        string a = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-progress-{Guid.NewGuid():N}.slopa");
        string b = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-progress-{Guid.NewGuid():N}.slopa");
        try
        {
            model.Save(a, SceneModel.SaveFormat);
            var recorder = new Recorder();
            model.Save(b, recorder);
            Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
            AssertRunsToOne(recorder);
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    [SceneTheory, SceneSamples]
    public void FbxExportWithProgress_WritesTheSameBytes(string path)
    {
        var model = TestFiles.LoadScene(path);
        string file = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-progress-{Guid.NewGuid():N}.fbx");
        try
        {
            var options = new FbxExportOptions();
            var expected = FbxExporter.Export(model, options);
            var recorder = new Recorder();
            FbxExporter.Export(model, file, options, recorder);
            Assert.Equal(expected, File.ReadAllBytes(file));
            AssertRunsToOne(recorder);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void WriteFile_ReportsChunksAndWritesEveryByte()
    {
        var bytes = new byte[3 * 1024 * 1024 + 17];
        new Random(7).NextBytes(bytes);
        string file = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-progress-{Guid.NewGuid():N}.bin");
        try
        {
            var recorder = new Recorder();
            WriteProgress.WriteFile(file, bytes, recorder);
            Assert.Equal(bytes, File.ReadAllBytes(file));
            AssertRunsToOne(recorder);
            Assert.True(recorder.Values.Count >= 4);
        }
        finally
        {
            File.Delete(file);
        }
    }
}

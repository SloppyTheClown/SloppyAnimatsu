using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Blendshapes. No sample scene contains one, so the tests build
/// targets, controllers and keys in a real scene and check the values, the influence array, the
/// deformation, the schema extension and save/reload.
/// </summary>
public class BlendshapeTests
{
    private static string? Sample() =>
        TestFiles.SceneSamples.FirstOrDefault(p => Path.GetFileName(p).StartsWith("7_AddingIK", StringComparison.Ordinal))
        ?? TestFiles.SceneSamples.FirstOrDefault(p => new FileInfo(p).Length < 20_000_000)
        ?? TestFiles.SceneSamples.FirstOrDefault();

    private static (Character Character, Mesh Mesh)? Pick(SceneModel model)
    {
        foreach (var mesh in model.Scene.Meshes)
        {
            var character = mesh.BindPose?.Character ?? mesh.Ancestor<Character>();
            if (character != null && mesh.ControlPoints.Length > 0) return (character, mesh);
        }
        return null;
    }

    private static (SceneModel Model, Character Character, Mesh Mesh) Build(string path)
    {
        var model = TestFiles.LoadScene(path);
        var (character, mesh) = Pick(model) ?? throw new Xunit.Sdk.XunitException("The sample has no character mesh.");
        return (model, character, mesh);
    }

    [Fact]
    public void Schema_AddsTheTargetControllerKeyAndTrack()
    {
        if (Sample() is not { } path) return;
        var model = TestFiles.LoadScene(path);
        var schema = model.Document.Schema;
        Assert.Null(schema.FindClass(BlendshapeSchema.TargetClass));
        Assert.Null(schema.FindStruct(BlendshapeSchema.KeyStruct));

        Assert.True(BlendshapeSchema.Ensure(schema));
        Assert.Equal("CObject3D", schema.FindClass(BlendshapeSchema.TargetClass)!.Parent!.Key);
        Assert.Equal(new[] { "VertexIArray", "VertexArray", "NormalArray", "MaxLength" },
            schema.FindClass(BlendshapeSchema.TargetClass)!.Members.Select(m => m.Name));
        Assert.Equal("CDataObject", schema.FindClass(BlendshapeSchema.ChannelClass)!.Parent!.Key);
        Assert.Equal("CController3D", schema.FindClass(BlendshapeSchema.ControllerClass)!.Parent!.Key);
        Assert.Equal("CAnimTrackBase", schema.FindClass(BlendshapeSchema.TrackClass)!.Parent!.Key);
        Assert.Equal(new[] { "_values" }, schema.FindStruct(BlendshapeSchema.KeyStruct)!.Members.Select(m => m.Name));
        Assert.Equal(new[] { "CKeyBase", "CKeyBlendshape" }, AktNames.PolymorphicChain(BlendshapeSchema.KeyStruct));
        Assert.False(BlendshapeSchema.Ensure(schema));
    }

    [Fact]
    public void AddTargetAndKey_DeformsTheMeshByTheDelta()
    {
        var path = Sample();
        if (path == null) return;
        var (model, character, mesh) = Build(path);
        var points = mesh.ControlPoints;
        var delta = points.Select(_ => new Vector4(0f, 0f, 0.1f, 0f)).ToArray();

        var editor = new BlendshapeEditor(character);
        var target = editor.AddTarget(mesh, "Nudge Z", delta);
        Assert.Equal(points.Length, target.VertexArray.Length);
        Assert.Same(target, mesh.BlendshapeTargets[^1]);
        Assert.Equal(0.1f, target.MaxLength, 5);

        var controller = editor.AddController(mesh, "BS Nudge");
        Assert.Contains(controller, character.Controllers.OfType<BlendshapeController>());
        var channel = editor.AddChannel(controller, 0);
        editor.AddInfo(channel, target, 1f);
        var track = (BlendshapeTrack)character.RigAnimation!.Stacks.First().TrackFor(controller)!;
        Assert.Equal(2, track.ValuesAt(0).Length);
        BlendshapeEditor.SetKeyValues(track, 0, 0f, 1f);

        var evaluator = new BlendshapeEvaluator(new PoseEvaluator(character));
        var values = evaluator.Values(controller, character.RigAnimation, 0f);
        Assert.Equal(0f, values[0], 5);
        Assert.Equal(1f, values[1], 5);

        var influences = evaluator.Influences(mesh, character.RigAnimation, 0f);
        int index = mesh.BlendshapeTargets.ToList().FindIndex(t => ReferenceEquals(t, target));
        Assert.Equal(1f, influences[index], 5);

        var deformed = evaluator.ControlPoints(mesh, character.RigAnimation, 0f);
        var original = mesh.ControlPoints;
        for (int p = 0; p < original.Length; p++)
            Assert.Equal(original[p].Z + 0.1f, deformed[p].Z, 5);
    }

    [Fact]
    public void Attenuation_FadesBetweenTheSentinelAndTheTarget()
    {
        var path = Sample();
        if (path == null) return;
        var (_, character, mesh) = Build(path);
        var editor = new BlendshapeEditor(character);
        var delta = mesh.ControlPoints.Select(_ => new Vector4(0f, 1f, 0f, 0f)).ToArray();
        var target = editor.AddTarget(mesh, "Up", delta);
        var controller = editor.AddController(mesh, "BS Fade");
        var channel = editor.AddChannel(controller, 0, attenuation: 0.5f);
        editor.AddInfo(channel, target, 1f);
        var track = (BlendshapeTrack)character.RigAnimation!.Stacks.First().TrackFor(controller)!;
        BlendshapeEditor.SetKeyValues(track, 0, 0f, 1f);

        var evaluator = new BlendshapeEvaluator(new PoseEvaluator(character));
        var influences = evaluator.Influences(mesh, character.RigAnimation, 0f);
        int index = mesh.BlendshapeTargets.ToList().FindIndex(t => ReferenceEquals(t, target));
        Assert.Equal(0.5f, influences[index], 5);
        var deformed = evaluator.ControlPoints(mesh, character.RigAnimation, 0f);
        var original = mesh.ControlPoints;
        for (int p = 0; p < original.Length; p++)
            Assert.Equal(original[p].Y + 0.5f, deformed[p].Y, 5);
    }

    [Fact]
    public void TwoTargets_InterpolateBetweenTheirPositions()
    {
        var path = Sample();
        if (path == null) return;
        var (_, character, mesh) = Build(path);
        var editor = new BlendshapeEditor(character);
        var first = editor.AddTarget(mesh, "First", mesh.ControlPoints.Select(_ => new Vector4(1f, 0f, 0f, 0f)).ToArray());
        var second = editor.AddTarget(mesh, "Second", mesh.ControlPoints.Select(_ => new Vector4(2f, 0f, 0f, 0f)).ToArray());
        var controller = editor.AddController(mesh, "BS Two");
        var channel = editor.AddChannel(controller, 0, attenuation: 0.5f);
        editor.AddInfo(channel, first, 0.25f);
        editor.AddInfo(channel, second, 0.75f);
        var track = (BlendshapeTrack)character.RigAnimation!.Stacks.First().TrackFor(controller)!;
        BlendshapeEditor.SetKeyValues(track, 0, 0f, 1f);

        var evaluator = new BlendshapeEvaluator(new PoseEvaluator(character));
        var influences = evaluator.Influences(mesh, character.RigAnimation, 0f);
        var targets = mesh.BlendshapeTargets.ToList();
        Assert.Equal(0.5f, influences[targets.FindIndex(t => ReferenceEquals(t, first))], 5);
        Assert.Equal(0.5f, influences[targets.FindIndex(t => ReferenceEquals(t, second))], 5);
    }

    [Fact]
    public void PositiveChannel_ClampsTheValueToZeroOne()
    {
        var path = Sample();
        if (path == null) return;
        var (_, character, mesh) = Build(path);
        var editor = new BlendshapeEditor(character);
        var target = editor.AddTarget(mesh, "Any");
        var controller = editor.AddController(mesh, "BS Clamp");
        var channel = editor.AddChannel(controller, 0); // positive: 0..1
        editor.AddInfo(channel, target, 1f);
        var track = (BlendshapeTrack)character.RigAnimation!.Stacks.First().TrackFor(controller)!;
        BlendshapeEditor.SetKeyValues(track, 0, 0f, -1f);

        var evaluator = new BlendshapeEvaluator(new PoseEvaluator(character));
        var values = evaluator.Values(controller, character.RigAnimation, 0f);
        Assert.Equal(0f, values[1], 5);
    }

    [Fact]
    public void StackOpacity_MixesOverrideAndAdditiveValues()
    {
        var path = Sample();
        if (path == null) return;
        var (_, character, mesh) = Build(path);
        var editor = new BlendshapeEditor(character);
        var target = editor.AddTarget(mesh, "Any");
        var controller = editor.AddController(mesh, "BS Opacity");
        editor.AddInfo(editor.AddChannel(controller, 0), target, 1f);
        var stack = character.RigAnimation!.Stacks.First();
        var track = (BlendshapeTrack)stack.TrackFor(controller)!;
        BlendshapeEditor.SetKeyValues(track, 0, 0f, 1f);
        stack.Source.Set("Opacity", 0.5f);

        var overrideValues = new BlendshapeEvaluator(new PoseEvaluator(character))
            .Values(controller, character.RigAnimation, 0f);
        Assert.Equal(0.5f, overrideValues[1], 5);

        stack.Source.Set("Flags", 0x2u | 0x8u); // enabled and additive
        var additiveValues = new BlendshapeEvaluator(new PoseEvaluator(character))
            .Values(controller, character.RigAnimation, 0f);
        Assert.Equal(0.5f, additiveValues[1], 5);
    }

    [Fact]
    public void SaveAndReload_KeepsTheTargetsValuesAndInfluence()
    {
        var path = Sample();
        if (path == null) return;
        var (model, character, mesh) = Build(path);
        var editor = new BlendshapeEditor(character);
        var delta = mesh.ControlPoints.Select(_ => new Vector4(0f, 0f, 0.25f, 0f)).ToArray();
        var target = editor.AddTarget(mesh, "Saved Target", delta);
        var controller = editor.AddController(mesh, "BS Saved");
        var channel = editor.AddChannel(controller, 0);
        editor.AddInfo(channel, target, 1f);
        var track = (BlendshapeTrack)character.RigAnimation!.Stacks.First().TrackFor(controller)!;
        BlendshapeEditor.SetKeyValues(track, 0, 0f, 1f);

        string file = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-blendshape-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(file);
            var reloaded = TestFiles.LoadScene(file);
            var (reloadedCharacter, reloadedMesh) = Pick(reloaded) ?? throw new Xunit.Sdk.XunitException("mesh lost");
            var reloadedController = reloadedCharacter.Controllers.OfType<BlendshapeController>()
                .Single(c => c.Name == "BS Saved");
            var reloadedTarget = reloadedMesh.BlendshapeTargets.Single(t => t.Name == "Saved Target");
            Assert.Equal(delta.Length, reloadedTarget.VertexArray.Length);
            Assert.Equal(0.25f, reloadedTarget.VertexArray[0].Z, 5);

            var evaluator = new BlendshapeEvaluator(new PoseEvaluator(reloadedCharacter));
            var values = evaluator.Values(reloadedController, reloadedCharacter.RigAnimation, 0f);
            Assert.Equal(1f, values[1], 5);
            var influences = evaluator.Influences(reloadedMesh, reloadedCharacter.RigAnimation, 0f);
            int index = reloadedMesh.BlendshapeTargets.ToList().FindIndex(t => ReferenceEquals(t, reloadedTarget));
            Assert.Equal(1f, influences[index], 5);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void CliBlendshapeAdd_WritesThePickZoneAndReloads()
    {
        var path = Sample();
        if (path == null) return;
        var (_, character, mesh) = Build(path);
        int triangles = mesh.Triangles().Count();
        if (triangles < 4) return;
        string sceneFile = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-zone-{Guid.NewGuid():N}.akt");
        string outFile = Path.ChangeExtension(sceneFile, ".out.akt");
        try
        {
            File.Copy(path, sceneFile);
            Assert.Equal(0, SloppyAnimatsu.Cli.Program.Main([
                "blendshape-add", sceneFile, character.Name!, mesh.Name!,
                "--target=Jaw", "--controller=Zone", "--face=Smile",
                "--zone=1-3", $"--out={outFile}",
            ]));
            var reloaded = TestFiles.LoadScene(outFile);
            var (reloadedCharacter, reloadedMesh) = Pick(reloaded) ?? throw new Xunit.Sdk.XunitException("mesh lost");
            var controller = reloadedCharacter.Controllers.OfType<BlendshapeFaceController>().Single();
            Assert.Equal([1u, 2u, 3u], controller.InteractiveFaces);
            Assert.Equal(0u, controller.Click);
            // The reloaded zone answers a left click on its triangles only.
            var session = new BlendshapeSession(reloadedCharacter);
            Assert.Same(controller, session.FaceControllerAt(reloadedMesh, 2));
            Assert.Null(session.FaceControllerAt(reloadedMesh, 0));
            Assert.Null(session.FaceControllerAt(reloadedMesh, 2, 2));
            Assert.Same(controller, Assert.Single(reloadedMesh.BlendshapeFaces).Controllers.Single());
        }
        finally
        {
            if (File.Exists(sceneFile)) File.Delete(sceneFile);
            if (File.Exists(outFile)) File.Delete(outFile);
        }
    }

    [Fact]
    public void CliBlendshapeAdd_ZoneFacesClampsToTheMesh()
    {
        var path = Sample();
        if (path == null) return;
        var (_, character, mesh) = Build(path);
        int triangles = mesh.Triangles().Count();
        if (triangles < 4) return;
        string sceneFile = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-zonefaces-{Guid.NewGuid():N}.akt");
        string outFile = Path.ChangeExtension(sceneFile, ".out.akt");
        try
        {
            File.Copy(path, sceneFile);
            Assert.Equal(0, SloppyAnimatsu.Cli.Program.Main([
                "blendshape-add", sceneFile, character.Name!, mesh.Name!,
                "--target=Jaw", "--controller=Zone", "--face=Smile",
                $"--zone-faces=2,0,{triangles + 5}", $"--out={outFile}",
            ]));
            var reloaded = TestFiles.LoadScene(outFile);
            var (reloadedCharacter, _) = Pick(reloaded) ?? throw new Xunit.Sdk.XunitException("mesh lost");
            var controller = reloadedCharacter.Controllers.OfType<BlendshapeFaceController>().Single();
            Assert.Equal([2u, 0u], controller.InteractiveFaces);
        }
        finally
        {
            if (File.Exists(sceneFile)) File.Delete(sceneFile);
            if (File.Exists(outFile)) File.Delete(outFile);
        }
    }

    /// <summary>
    /// The point of view a keyed blendshape view needs
    /// <c>blendshape-pov</c> writes the two members the CMesh declares, a scene that
    /// lacks them gets them appended, the saved scene reloads with the same camera, and an eye equal
    /// to the target is refused because the distance is the camera's target distance.
    /// </summary>
    [Fact]
    public void CliBlendshapePov_WritesTheMembersAndRefusesADegenerateView()
    {
        var path = Sample();
        if (path == null) return;
        var (model, character, mesh) = Build(path);
        string sceneFile = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-pov-{Guid.NewGuid():N}.akt");
        string outFile = Path.ChangeExtension(sceneFile, ".out.akt");
        string againFile = Path.ChangeExtension(sceneFile, ".again.akt");
        string refusedFile = Path.ChangeExtension(sceneFile, ".refused.akt");
        try
        {
            File.Copy(path, sceneFile);
            // The samples declare both members already; strip them to model a scene written before
            // blendshapes, whose CMesh definition has none.
            var legacy = TestFiles.LoadScene(sceneFile);
            var meshClass = legacy.Document.Schema.FindClass(BlendshapeSchema.MeshClass);
            Assert.NotNull(meshClass);
            meshClass!.Members.RemoveAll(m =>
                m.Name is BlendshapeSchema.CameraPositionMember or BlendshapeSchema.CameraDistanceMember);
            foreach (var o in legacy.Document.AllObjects().Where(o => o.ClassKey == BlendshapeSchema.MeshClass))
                o.Fields.RemoveAll(f =>
                    f.Name is BlendshapeSchema.CameraPositionMember or BlendshapeSchema.CameraDistanceMember);
            legacy.Save(sceneFile);

            var eye = new Vector3(10f, 20f, 30f);
            var target = new Vector3(1f, 2f, 3f);
            Assert.Equal(0, SloppyAnimatsu.Cli.Program.Main([
                "blendshape-pov", sceneFile, mesh.Name!, "--eye=10,20,30", "--target=1,2,3", $"--out={outFile}",
            ]));

            var reloaded = TestFiles.LoadScene(outFile);
            var reloadedClass = reloaded.Document.Schema.FindClass(BlendshapeSchema.MeshClass)!;
            Assert.Contains(reloadedClass.Members, m => m.Name == BlendshapeSchema.CameraPositionMember && m.TypeName == "Mat4f");
            Assert.Contains(reloadedClass.Members, m => m.Name == BlendshapeSchema.CameraDistanceMember && m.TypeName == "float");
            var reloadedMesh = reloaded.Scene.Meshes.Single(m => m.Name == mesh.Name);
            Assert.True(reloadedMesh.HasBlendshapePointOfView);
            Assert.Equal(Vector3.Distance(eye, target), reloadedMesh.BlendshapeCameraDistanceFromAnchor, 4);
            var pointOfView = BlendshapePointOfView.Of(reloadedMesh);
            Assert.NotNull(pointOfView);
            Assert.True((eye - pointOfView!.Value.Translation).Length() < 1e-3f,
                $"the stored eye reads back as {pointOfView!.Value.Translation}");
            Assert.True((target - BlendshapePointOfView.TargetOf(pointOfView.Value, reloadedMesh.BlendshapeCameraDistanceFromAnchor)).Length() < 1e-3f);

            // The extended scene round-trips byte for byte after a reload.
            reloaded.Save(againFile);
            Assert.Equal(File.ReadAllBytes(outFile), File.ReadAllBytes(againFile));

            // eye == target has no target distance: refused, and no file is written.
            Assert.Equal(1, SloppyAnimatsu.Cli.Program.Main([
                "blendshape-pov", sceneFile, mesh.Name!, "--eye=1,2,3", "--target=1,2,3", $"--out={refusedFile}",
            ]));
            Assert.False(File.Exists(refusedFile));
            Assert.NotEqual(0, SloppyAnimatsu.Cli.Program.Main(["blendshape-pov", sceneFile, mesh.Name!, "--eye=1,2,3"]));

            // A scene that already declares both members keeps its class schema as it is.
            string asIsFile = Path.ChangeExtension(sceneFile, ".asis.akt");
            File.Copy(path, sceneFile, overwrite: true);
            try
            {
                Assert.Equal(0, SloppyAnimatsu.Cli.Program.Main([
                    "blendshape-pov", sceneFile, mesh.Name!, "--eye=0,0,5", "--target=0,0,0", $"--out={asIsFile}",
                ]));
                var asIs = TestFiles.LoadScene(asIsFile);
                Assert.Equal(model.Document.Schema.FindClass(BlendshapeSchema.MeshClass)!.Members.Select(m => m.Name),
                    asIs.Document.Schema.FindClass(BlendshapeSchema.MeshClass)!.Members.Select(m => m.Name));
                Assert.True(asIs.Scene.Meshes.Single(m => m.Name == mesh.Name).HasBlendshapePointOfView);
            }
            finally
            {
                if (File.Exists(asIsFile)) File.Delete(asIsFile);
            }
        }
        finally
        {
            foreach (string file in new[] { sceneFile, outFile, againFile, refusedFile })
                if (File.Exists(file)) File.Delete(file);
        }
    }
}

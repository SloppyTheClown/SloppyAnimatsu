using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The Blendshapes panel's headless session
/// which controllers, faces and sights it lists, what a value edit writes at the
/// playhead, what a face applies to the current stack's keys, and the timeline rows its tracks get.
/// No sample scene carries a blendshape, so every fixture builds one in a real scene.
/// </summary>
public class BlendshapeSessionTests
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

    private sealed record Fixture(
        SceneModel Model, Character Character, Mesh Mesh, Animation Animation, AnimStack Stack,
        BlendshapeEditor Editor, Blendshape Target, BlendshapeController Controller, BlendshapeSession Session);

    /// <summary>
    /// A sample character with a target, one controller and a channel, on a stack of at least four
    /// keys. The small sample has no user animation, so its hidden <c>__RigAnim</c> stack is grown
    /// from two keys to four; key edits on it behave like those on any other stack.
    /// </summary>
    private static Fixture? Prepare(bool addController = true)
    {
        if (Sample() is not { } path) return null;
        var model = TestFiles.LoadScene(path);
        var (character, mesh) = Pick(model) ?? throw new Xunit.Sdk.XunitException("The sample has no character mesh.");
        var animation = character.Animations
            .Concat(character.RigAnimation != null ? [character.RigAnimation] : [])
            .FirstOrDefault(a => a.Stacks.Count > 0);
        if (animation == null) return null;
        var stack = animation.Stacks.FirstOrDefault(s => Suitable(s)) ?? animation.Stacks[0];
        if (!Suitable(stack))
        {
            var editor0 = new KeyEditor(character);
            // [0, 30] becomes [0, 10, 20, 30]; each create keys every track, as K does.
            editor0.CreateKey(stack, 10);
            editor0.CreateKey(stack, 20);
        }
        if (!Suitable(stack)) return null;

        var editor = new BlendshapeEditor(character);
        var target = editor.AddTarget(mesh, "Jaw");
        BlendshapeController? controller = null;
        if (addController)
        {
            controller = editor.AddController(mesh, "BS Jaw");
            editor.AddInfo(editor.AddChannel(controller, 0), target, 1f);
        }
        var session = new BlendshapeSession(character) { Animation = animation, Stack = stack };
        return new Fixture(model, character, mesh, animation, stack, editor, target, controller!, session);
    }

    private static bool Suitable(AnimStack stack) =>
        stack.Keys.Count >= 4 && stack.Keys[1].Time - stack.Keys[0].Time > 1 && stack.Keys[2].Time - stack.Keys[1].Time > 1;

    [Fact]
    public void Listing_ShowsControllersChannelsFacesAndSights()
    {
        var fixture = Prepare();
        if (fixture == null) return;
        var session = fixture.Session;
        session.Time = fixture.Stack.Keys[1].Time;
        var faceController = fixture.Editor.AddFaceController(fixture.Mesh, "Mouth Zone");
        fixture.Editor.AddFace(fixture.Mesh, "Smile", [faceController], [[0.2f, 0.4f]]);
        var sight = fixture.Editor.AddSight("Sight", new Vector3(0f, 10f, 20f));

        var rows = session.ControllerRows;
        Assert.Equal(2, rows.Count);
        Assert.Equal("BS Jaw", rows[0].Controller.Name);
        Assert.Equal(fixture.Mesh.Name, rows[0].MeshName);
        Assert.Single(rows[0].Channels);
        Assert.Contains("Jaw", rows[0].Channels[0].Label);
        Assert.Equal(1, rows[0].KeyIndex); // the playhead is on the second stack key

        Assert.Single(session.Faces);
        Assert.Equal("#0 Smile", session.Faces[0].Label);
        Assert.Equal(["Mouth Zone"], session.Faces[0].Controllers);
        Assert.Equal([0.2f, 0.4f], session.Faces[0].Values[0]);
        Assert.True(session.Faces[0].Applies);

        var sightRow = Assert.Single(session.Sights);
        Assert.Same(sight, sightRow.Sight);
        Assert.Equal(new Vector3(0f, 10f, 20f), sightRow.Position);
    }

    [Fact]
    public void SetValues_UpdatesTheKeyUnderThePlayheadAndMirrorsKeyZero()
    {
        var fixture = Prepare();
        if (fixture == null) return;
        var session = fixture.Session;
        session.Time = fixture.Stack.Keys[1].Time;
        var write = session.SetValues(fixture.Controller, 0.25f, 0.75f);
        Assert.False(write.Created);
        Assert.Equal(1, write.KeyIndex);
        var track = (BlendshapeTrack)fixture.Stack.TrackFor(fixture.Controller)!;
        Assert.Equal(0.25f, track.Keys[1]!.Values[0], 5);
        Assert.Equal(0.75f, track.Keys[1]!.Values[1], 5);

        // Key 0's value goes into the loop key too, as BlendshapeEditor.SetKeyValues does.
        session.Time = fixture.Stack.Keys[0].Time;
        Assert.Equal(0, session.SetValues(fixture.Controller, 0.1f, 0.9f).KeyIndex);
        Assert.Equal(0.1f, track.Keys[^1]!.Values[0], 5);
        Assert.Equal(0.9f, track.Keys[^1]!.Values[1], 5);
    }

    [Fact]
    public void SetValues_CreatesAKeyWhenThePlayheadIsBetweenKeys()
    {
        var fixture = Prepare();
        if (fixture == null) return;
        var session = fixture.Session;
        // The panel creates the controller's track in the current stack on its first edit.
        session.Time = fixture.Stack.Keys[1].Time;
        session.SetValues(fixture.Controller, 0f, 0f);
        var track = (BlendshapeTrack)fixture.Stack.TrackFor(fixture.Controller)!;

        int frame = (fixture.Stack.Keys[1].Time + fixture.Stack.Keys[2].Time) / 2;
        int before = fixture.Stack.Keys.Count;
        session.Time = frame;
        var write = session.SetValues(fixture.Controller, 0.3f, 0.6f);
        Assert.True(write.Created);
        Assert.Equal(before + 1, fixture.Stack.Keys.Count);
        Assert.Equal(frame, fixture.Stack.Keys[write.KeyIndex].Time);
        Assert.Equal(before + 1, track.Keys.Count);
        Assert.Equal(0.3f, track.Keys[write.KeyIndex]!.Values[0], 5);
        Assert.Equal(0.6f, track.Keys[write.KeyIndex]!.Values[1], 5);
    }

    [Fact]
    public void ApplyFace_WritesItsPairsAtTheStackKeyTimes()
    {
        var fixture = Prepare();
        if (fixture == null) return;
        var faceController = fixture.Editor.AddFaceController(fixture.Mesh, "Mouth Zone");
        var face = fixture.Editor.AddFace(fixture.Mesh, "Smile", [faceController], [[0.2f, 0.4f], [0.6f, 0.8f]]);
        var session = fixture.Session;
        session.Time = fixture.Stack.Keys[0].Time;

        Assert.Equal(1, session.ApplyFace(face));
        var track = (BlendshapeTrack)fixture.Stack.TrackFor(faceController)!;
        Assert.Equal(0.2f, track.Keys[0]!.Values[0], 5);
        Assert.Equal(0.4f, track.Keys[0]!.Values[1], 5);
        Assert.Equal(0.6f, track.Keys[1]!.Values[0], 5);
        Assert.Equal(0.8f, track.Keys[1]!.Values[1], 5);
        // The face has fewer pairs than the stack has keys: its last pair repeats, the loop key
        // replays key 0.
        Assert.Equal(0.6f, track.Keys[2]!.Values[0], 5);
        Assert.Equal(0.2f, track.Keys[^1]!.Values[0], 5);
    }

    [Fact]
    public void ApplyFace_IgnoresAControllerOfAnotherCharacter()
    {
        if (Sample() is not { } path) return;
        var fixture = Prepare();
        if (fixture == null) return;
        var otherModel = TestFiles.LoadScene(path);
        var (otherCharacter, otherMesh) = Pick(otherModel) ?? throw new Xunit.Sdk.XunitException("The sample has no character mesh.");
        var foreign = new BlendshapeEditor(otherCharacter).AddFaceController(otherMesh, "Other");
        var face = fixture.Editor.AddFace(fixture.Mesh, "Foreign", [foreign]);

        Assert.Equal(0, fixture.Session.ApplyFace(face));
        var row = fixture.Session.Faces.Single(f => ReferenceEquals(f.Face, face));
        Assert.Empty(row.Controllers);
        Assert.Equal(["Other"], row.Ignored);
        Assert.False(row.Applies);
    }

    [Fact]
    public void WithoutControllers_TheSessionListsNothingAndAnEmptyFaceAppliesNothing()
    {
        if (Sample() is not { } path) return;
        var model = TestFiles.LoadScene(path);
        var (character, mesh) = Pick(model) ?? throw new Xunit.Sdk.XunitException("The sample has no character mesh.");
        // The small sample has no user animation: grow its hidden rig stack to four keys.
        var animation = character.RigAnimation;
        if (animation == null || animation.Stacks.Count == 0) return;
        var stack = animation.Stacks[0];
        if (stack.Keys.Count < 4)
        {
            new KeyEditor(character).CreateKey(stack, stack.Keys[0].Time + 10);
            new KeyEditor(character).CreateKey(stack, stack.Keys[1].Time + 10);
        }
        var session = new BlendshapeSession(character) { Animation = animation, Stack = stack, Time = stack.Keys[0].Time };
        Assert.Empty(session.Controllers);
        Assert.Empty(session.ControllerRows);
        Assert.Empty(session.Faces);

        var face = new BlendshapeEditor(character).AddFace(mesh, "Empty", []);
        Assert.Equal(0, session.ApplyFace(face));
        Assert.Single(session.Faces);
        Assert.False(session.Faces[0].Applies);
    }

    [Fact]
    public void FaceControllerAt_PicksTheZoneByClickAndTheFirstClaimWins()
    {
        var fixture = Prepare();
        if (fixture == null) return;
        var mesh = fixture.Mesh;
        Assert.True(mesh.Triangles().Count() >= 3, "the sample mesh needs at least three triangles");
        var editor = fixture.Editor;
        var left = editor.AddFaceController(mesh, "Mouth Left");
        left.Click = 0;
        left.InteractiveFaces = [0, 1];
        var right = editor.AddFaceController(mesh, "Mouth Right");
        right.Click = 2;
        right.InteractiveFaces = [0];
        // A controller with an empty zone is in the mesh's array but never picked.
        var empty = editor.AddFaceController(mesh, "No Zone");
        empty.Click = 0;
        var session = fixture.Session;

        Assert.Same(left, session.FaceControllerAt(mesh, 1, 0));
        Assert.Null(session.FaceControllerAt(mesh, 1, 2));   // the right-click zone does not claim 1
        Assert.Same(right, session.FaceControllerAt(mesh, 0, 2));
        Assert.Null(session.FaceControllerAt(mesh, 0, 1));   // no middle-click zone at all
        Assert.Null(session.FaceControllerAt(mesh, 2, 0));   // no controller claims triangle 2
        Assert.Null(session.FaceControllerAt(mesh, -1, 0));

        // Two controllers claim triangle 0 for the left button: the first in Controllers order wins.
        var later = editor.AddFaceController(mesh, "Mouth Later");
        later.Click = 0;
        later.InteractiveFaces = [0];
        Assert.Same(left, session.FaceControllerAt(mesh, 0, 0));
        Assert.Equal([left, right, empty, later], session.FaceControllersOf(mesh));
    }

    [Fact]
    public void ApplyFaceAt_WritesOnlyThePickedZonesFace()
    {
        var fixture = Prepare();
        if (fixture == null) return;
        var editor = fixture.Editor;
        var other = editor.AddController(fixture.Mesh, "BS Brow");
        editor.AddInfo(editor.AddChannel(other, 0), fixture.Target, 1f);
        var zone = editor.AddFaceController(fixture.Mesh, "Mouth Zone");
        zone.InteractiveFaces = [0];
        var face = editor.AddFace(fixture.Mesh, "Smile", [zone], [[0.2f, 0.4f], [0.6f, 0.8f]]);
        var session = fixture.Session;
        session.Time = fixture.Stack.Keys[0].Time;

        var hit = session.PickZone(fixture.Mesh, 0);
        Assert.NotNull(hit);
        Assert.Same(zone, hit!.Controller);
        Assert.Same(face, hit.Face);
        Assert.Equal(0, hit.Triangle);
        Assert.Equal(1, session.ApplyFaceAt(fixture.Mesh, 0));

        var track = (BlendshapeTrack)fixture.Stack.TrackFor(zone)!;
        Assert.Equal(0.2f, track.Keys[0]!.Values[0], 5);
        Assert.Equal(0.4f, track.Keys[0]!.Values[1], 5);
        Assert.Equal(0.2f, track.Keys[^1]!.Values[0], 5);   // the loop key replays key 0
        // The controller outside the zone keeps its own key values: the pick wrote one track only.
        var otherTrack = (BlendshapeTrack)fixture.Stack.TrackFor(other)!;
        Assert.Equal(0f, otherTrack.Keys[0]!.Values[0], 5);
        Assert.Equal(0f, otherTrack.Keys[0]!.Values[1], 5);
        // A triangle outside the zone, and the wrong mouse button, apply nothing.
        Assert.Equal(0, session.ApplyFaceAt(fixture.Mesh, 1));
        Assert.Equal(0, session.ApplyFaceAt(fixture.Mesh, 0, 2));
    }

    [Fact]
    public void Quadrant_ReadsTheCellsInReadingOrder()
    {
        var size = new Vector2(40f, 20f);
        Assert.Equal(0, BlendshapeSession.Quadrant(new Vector2(1f, 1f), size));    // top-left
        Assert.Equal(1, BlendshapeSession.Quadrant(new Vector2(39f, 1f), size));   // top-right
        Assert.Equal(2, BlendshapeSession.Quadrant(new Vector2(1f, 19f), size));   // bottom-left
        Assert.Equal(3, BlendshapeSession.Quadrant(new Vector2(39f, 19f), size));  // bottom-right
        // A point on the middle belongs to the right/bottom cell; no size means no cell.
        Assert.Equal(3, BlendshapeSession.Quadrant(new Vector2(20f, 10f), size));
        Assert.Equal(0, BlendshapeSession.Quadrant(new Vector2(5f, 5f), Vector2.Zero));
        Assert.Equal(0, BlendshapeSession.Quadrant(new Vector2(-5f, -5f), Vector2.Zero));
    }

    [Fact]
    public void QuadrantDirection_IsTheChannelOrdersIdentityAndTheSelectionIsAMask()
    {
        Assert.Equal((1, 1f), BlendshapeSession.QuadrantDirection(0));   // up: +Y
        Assert.Equal((1, -1f), BlendshapeSession.QuadrantDirection(1));  // down: -Y
        Assert.Equal((0, -1f), BlendshapeSession.QuadrantDirection(2));  // left: -X
        Assert.Equal((0, 1f), BlendshapeSession.QuadrantDirection(3));   // right: +X
        Assert.Equal((0, 1f), BlendshapeSession.QuadrantDirection(-1));  // negative quadrants wrap
        // The preference is a mask (bit i = quadrant i) and cannot reorder the quadrants.
        Assert.Equal((0, 1f), BlendshapeSession.QuadrantDirection(3, 0b1001u));
        Assert.Equal(BlendshapeSession.QuadrantDirection(2), BlendshapeSession.QuadrantDirection(2, 0x0fu));
        Assert.True(BlendshapeSession.QuadrantEnabled(0, 0b0001u));
        Assert.False(BlendshapeSession.QuadrantEnabled(1, 0b0001u));
        Assert.False(BlendshapeSession.QuadrantEnabled(0, 0u));   // the shipped file's value
    }

    [Fact]
    public void SetQuadrant_WritesOneGroupsExtremeAndKeepsTheOther()
    {
        var fixture = Prepare();
        if (fixture == null) return;
        var controller = fixture.Controller;
        // All four directions, so Range is -1..1 on both groups.
        foreach (uint direction in (ReadOnlySpan<uint>)[1u, 2u, 3u]) fixture.Editor.AddChannel(controller, direction);
        var session = fixture.Session;
        session.Time = fixture.Stack.Keys[0].Time;
        int keys = fixture.Stack.Keys.Count;
        var start = session.SetValues(controller, 0.2f, 0.3f);
        var track = (BlendshapeTrack)fixture.Stack.TrackFor(controller)!;

        var right = session.SetQuadrant(controller, 3);   // right: +X, Y kept
        Assert.Equal(start.KeyIndex, right.KeyIndex);
        Assert.False(right.Created);
        Assert.Equal(1f, right.X, 5);
        Assert.Equal(0.3f, right.Y, 5);
        Assert.Equal(1f, track.Keys[right.KeyIndex]!.Values[0], 5);
        Assert.Equal(0.3f, track.Keys[right.KeyIndex]!.Values[1], 5);

        var down = session.SetQuadrant(controller, 1);    // down: -Y, X kept
        Assert.Equal(1f, down.X, 5);
        Assert.Equal(-1f, down.Y, 5);
        Assert.Equal(-1f, track.Keys[down.KeyIndex]!.Values[1], 5);
        Assert.Equal(keys, fixture.Stack.Keys.Count);        // still one key: no key was added

        // A group the controller has no channel for cannot be pushed: its bound is 0.
        var single = fixture.Editor.AddController(fixture.Mesh, "BS Jaw Only");
        session.Time = fixture.Stack.Keys[0].Time;
        session.SetValues(single, 0.5f, 0f);
        Assert.Equal(0f, session.SetQuadrant(single, 2).X, 5);   // left: no negative X channel
        Assert.Equal(keys, fixture.Stack.Keys.Count);
    }

    [Fact]
    public void SetQuadrant_LandsOneKeyWhenThePlayheadIsBetweenKeys()
    {
        var fixture = Prepare();
        if (fixture == null) return;
        var controller = fixture.Controller;
        foreach (uint direction in (ReadOnlySpan<uint>)[1u, 2u, 3u]) fixture.Editor.AddChannel(controller, direction);
        var session = fixture.Session;
        session.Time = fixture.Stack.Keys[0].Time;
        session.SetValues(controller, 0f, 0f);
        int frame = (fixture.Stack.Keys[1].Time + fixture.Stack.Keys[2].Time) / 2;
        Assert.InRange(frame, fixture.Stack.Keys[1].Time + 1, fixture.Stack.Keys[2].Time - 1);

        int before = fixture.Stack.Keys.Count;
        session.Time = frame;
        var write = session.SetQuadrant(controller, 2);   // left: -X
        Assert.True(write.Created);
        Assert.Equal(before + 1, fixture.Stack.Keys.Count);
        Assert.Equal(frame, fixture.Stack.Keys[write.KeyIndex].Time);
        Assert.Equal(-1f, write.X, 5);
        Assert.Equal(0f, write.Y, 5);                     // the other group kept its value
    }

    [Fact]
    public void Timeline_GivesABlendshapeTrackItsOwnRowBelowTheStacks()
    {
        var fixture = Prepare();
        if (fixture == null) return;
        var session = fixture.Session;
        session.Time = fixture.Stack.Keys[1].Time;
        session.SetValues(fixture.Controller, 0.4f, 0.5f);
        var track = (BlendshapeTrack)fixture.Stack.TrackFor(fixture.Controller)!;

        var layout = new TimelineLayout
        {
            Width = 4000,
            Height = 400,
            Animation = fixture.Animation,
            ActiveStack = fixture.Stack,
        };
        Assert.Equal(fixture.Animation.Stacks.Count + 1, layout.RowCount);
        Assert.Single(layout.BlendshapeTracks);
        int row = fixture.Animation.Stacks.Count; // blendshape rows come after every stack row
        Assert.Same(fixture.Stack, layout.StackAt(row));

        // The blendshape row's key is picked at the stack's key time and reports its two values.
        var at = new Vector2((float)layout.X(fixture.Stack.Keys[1].Time),
            (float)(TimelineLayout.RowTop(row) + TimelineLayout.RowHeight / 2));
        var (hitRow, hitKey) = layout.KeyAt(at);
        Assert.Equal(row, hitRow);
        Assert.Equal(1, hitKey);
        Assert.NotNull(layout.BlendshapeValuesAt(row, 1));
        // A stack row above still picks its own key at the same x.
        Assert.Equal(0, layout.KeyAt(new Vector2(at.X, (float)(TimelineLayout.RowTop(0) + 2))).Row);
        Assert.Equal(1, layout.KeyAt(new Vector2(at.X, (float)(TimelineLayout.RowTop(0) + 2))).Key);
    }

    [Fact]
    public void CreateKey_KeysTheBlendshapeTracksCurrentValue()
    {
        var fixture = Prepare();
        if (fixture == null) return;
        var session = fixture.Session;
        var track = (BlendshapeTrack)fixture.Stack.TrackFor(fixture.Controller)!;
        session.Time = fixture.Stack.Keys[1].Time;
        session.SetValues(fixture.Controller, 0.2f, 0.4f);
        session.Time = fixture.Stack.Keys[2].Time;
        session.SetValues(fixture.Controller, 0.6f, 0.8f);

        int frame = (fixture.Stack.Keys[1].Time + fixture.Stack.Keys[2].Time) / 2;
        Assert.InRange(frame, fixture.Stack.Keys[1].Time + 1, fixture.Stack.Keys[2].Time - 1);
        var expected = session.Poses.LayerBlendshapeValues(fixture.Stack, track, frame, out _);
        int key = new KeyEditor(fixture.Character).CreateKey(fixture.Stack, frame);

        // K wrote the value the track had at the playhead, not a copy of the previous key.
        Assert.Equal(expected[0], track.Keys[key]!.Values[0], 4);
        Assert.Equal(expected[1], track.Keys[key]!.Values[1], 4);
        Assert.NotEqual(track.Keys[1]!.Values[0], track.Keys[key]!.Values[0]);
    }

    [Fact]
    public void CliBlendshapeAdd_SavesAndReloadsTheCreatedObjects()
    {
        if (Sample() is not { } path) return;
        var model = TestFiles.LoadScene(path);
        var (character, mesh) = Pick(model) ?? throw new Xunit.Sdk.XunitException("The sample has no character mesh.");
        string sceneFile = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-bsadd-{Guid.NewGuid():N}.akt");
        string outFile = Path.ChangeExtension(sceneFile, ".out.akt");
        try
        {
            File.Copy(path, sceneFile);
            int code = SloppyAnimatsu.Cli.Program.Main([
                "blendshape-add", sceneFile, character.Name!, mesh.Name!,
                "--target=Jaw", "--controller=BS Jaw", "--face=Smile", "--face-values=0,1",
                "--sight=Sight", "--context=animation", $"--out={outFile}",
            ]);
            Assert.Equal(0, code);

            var reloaded = TestFiles.LoadScene(outFile);
            Assert.Equal(InterfaceContext.Animation, reloaded.Scene.Context);
            var (reloadedCharacter, reloadedMesh) = Pick(reloaded) ?? throw new Xunit.Sdk.XunitException("mesh lost");
            Assert.Single(reloadedCharacter.Controllers.OfType<BlendshapeController>());
            var controller = reloadedCharacter.Controllers.OfType<BlendshapeFaceController>().Single();
            Assert.Equal("BS Jaw", controller.Name);
            Assert.Same(reloadedMesh, controller.Mesh);
            var channel = Assert.Single(controller.Channels);
            Assert.Equal(0u, channel.Direction);
            var info = Assert.Single(channel.Infos);
            Assert.Equal("Jaw", info.Blendshape?.Name);
            Assert.Equal(1f, info.Position);
            Assert.Equal("Jaw", reloadedMesh.BlendshapeTargets.Last().Name);

            var face = Assert.Single(reloadedMesh.BlendshapeFaces);
            Assert.Equal("Smile", face.Name);
            Assert.Equal(0, face.Index);
            Assert.Equal([0f, 1f], face.Values[0]);
            Assert.Same(controller, Assert.Single(face.Controllers));

            var sight = Assert.Single(reloaded.Scene.Descendants<BlendshapeSight>());
            Assert.Equal("Sight", sight.Name);
        }
        finally
        {
            if (File.Exists(sceneFile)) File.Delete(sceneFile);
            if (File.Exists(outFile)) File.Delete(outFile);
        }
    }

    // ---- the point of view -----------------

    [Fact]
    public void PointOfView_IsUnsetUntilStoredAndThenReadsBackAsTheSameCamera()
    {
        var fixture = Prepare();
        if (fixture == null) return;
        var mesh = fixture.Mesh;
        var session = fixture.Session;
        Assert.False(mesh.HasBlendshapePointOfView);
        Assert.Equal(0f, mesh.BlendshapeCameraDistanceFromAnchor);
        Assert.Equal(Matrix4x4.Identity, mesh.BlendshapeCameraPositionFromAnchor);
        Assert.Null(session.PointOfView(mesh));
        Assert.Null(session.SightPosition(mesh));

        var eye = new Vector3(12f, 34f, 56f);
        var target = new Vector3(1f, 2f, 3f);
        float distance = Vector3.Distance(eye, target);
        var world = BlendshapePointOfView.FromLookAt(eye, target, Vector3.UnitY);
        session.SetPointOfView(mesh, world, distance);

        Assert.True(mesh.HasBlendshapePointOfView);
        Assert.Equal(distance, mesh.BlendshapeCameraDistanceFromAnchor, 4);
        var stored = session.PointOfView(mesh);
        Assert.NotNull(stored);
        float difference = AktMath.MaxDifference(world, stored!.Value);
        Assert.True(difference < 1e-4f, $"the stored camera differs from the viewport camera by {difference}");

        // The camera's target is its eye plus its forward direction times the stored distance.
        var readTarget = BlendshapePointOfView.TargetOf(stored.Value, distance);
        Assert.True((target - readTarget).Length() < 1e-3f, $"target {readTarget} vs {target}");
        var readEye = session.SightPosition(mesh)!.Value;
        Assert.True((eye - readEye).Length() < 1e-3f, $"the eye reads back as {readEye}");
    }

    [Fact]
    public void PointOfView_MovesWithItsMeshAndFollowsAnAnchorWhenOneIsStored()
    {
        var fixture = Prepare();
        if (fixture == null) return;
        var mesh = fixture.Mesh;
        var session = fixture.Session;
        var eye = new Vector3(3f, 4f, 5f);
        var target = new Vector3(0f, 0f, 0f);
        var world = BlendshapePointOfView.FromLookAt(eye, target, Vector3.UnitY);
        session.SetPointOfView(mesh, world, Vector3.Distance(eye, target));
        Matrix4x4 stored = mesh.BlendshapeCameraPositionFromAnchor;

        // The transform is relative to the mesh, so its eye in the mesh's own space stays put when
        // the mesh moves.
        Vector3 localEye = Vector3.Transform(eye, BlendshapePointOfView.Inverse(mesh.WorldMatrix));
        Vector3 before = session.PointOfView(mesh)!.Value.Translation;
        mesh.LocalMatrix = mesh.LocalMatrix * Matrix4x4.CreateTranslation(0.5f, 1.5f, 2.5f);
        Vector3 after = session.PointOfView(mesh)!.Value.Translation;
        Assert.True((before - after).Length() > 1e-3f, "the point of view moves with its mesh");
        Assert.True(AktMath.MaxDifference(stored, mesh.BlendshapeCameraPositionFromAnchor) < 1e-5f,
            "the stored transform is relative, so a moved mesh keeps it");
        Vector3 movedLocalEye = Vector3.Transform(after, BlendshapePointOfView.Inverse(mesh.WorldMatrix));
        Assert.True((localEye - movedLocalEye).Length() < 1e-3f, $"the eye left the mesh's space: {localEye} vs {movedLocalEye}");

        // With an anchor the transform is relative to that object instead: the anchor moving moves
        // the view, and the mesh moving no longer does.
        var anchor = fixture.Character.Descendants<Joint>()
            .FirstOrDefault(joint => Matrix4x4.Invert(joint.WorldMatrix, out _));
        if (anchor == null) return;
        BlendshapeSchema.EnsureMeshMembers(fixture.Model.Document.Schema, withAnchor: true);
        mesh.BlendshapeCameraAnchor = anchor;
        Assert.Same(anchor, mesh.BlendshapeCameraAnchor);
        Matrix4x4 anchorWorld = anchor.WorldMatrix;
        var anchoredEye = anchorWorld.Translation + new Vector3(0f, 10f, 0f);
        session.SetPointOfView(mesh, BlendshapePointOfView.FromLookAt(anchoredEye, anchorWorld.Translation, Vector3.UnitY), 10f);
        Vector3 anchorLocalEye = Vector3.Transform(anchoredEye, BlendshapePointOfView.Inverse(anchorWorld));

        anchor.LocalMatrix = anchor.LocalMatrix * Matrix4x4.CreateTranslation(0f, 2f, 0f);
        Vector3 movedEye = session.PointOfView(mesh)!.Value.Translation;
        Assert.True((anchoredEye - movedEye).Length() > 1e-3f, "the point of view follows its anchor");
        Vector3 movedAnchorLocalEye = Vector3.Transform(movedEye, BlendshapePointOfView.Inverse(anchor.WorldMatrix));
        Assert.True((anchorLocalEye - movedAnchorLocalEye).Length() < 1e-3f,
            $"the eye left the anchor's space: {anchorLocalEye} vs {movedAnchorLocalEye}");

        Vector3 anchored = session.PointOfView(mesh)!.Value.Translation;
        mesh.LocalMatrix = mesh.LocalMatrix * Matrix4x4.CreateTranslation(1f, 0f, 0f);
        Assert.True((anchored - session.PointOfView(mesh)!.Value.Translation).Length() < 1e-3f,
            "with an anchor the mesh's own move does not move the view");
    }

    [Fact]
    public void Sights_StandAtTheStoredPointOfViewOrKeepTheirOwnPlacement()
    {
        var fixture = Prepare();
        if (fixture == null) return;
        var session = fixture.Session;
        var own = new Vector3(0f, 10f, 20f);
        fixture.Editor.AddSight("Sight", own);
        Assert.Equal(own, Assert.Single(session.Sights).Position);

        var eye = new Vector3(4f, 5f, 6f);
        var target = new Vector3(0f, 0f, 0f);
        session.SetPointOfView(fixture.Mesh, BlendshapePointOfView.FromLookAt(eye, target, Vector3.UnitY),
            Vector3.Distance(eye, target));
        Vector3 position = Assert.Single(session.Sights).Position;
        Assert.True((eye - position).Length() < 1e-3f, $"the sight stands at {position}, not the point of view's eye {eye}");
    }
}
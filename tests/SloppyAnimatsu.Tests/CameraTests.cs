using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Camera animation and the Photo and orthographic views.
/// No shipped scene has an animated camera, so the tests build one in a real scene: the schema
/// extension, the camera track and keys, the FOV mixing and the save/reload are all exercised on a
/// sample character.
/// </summary>
public class CameraTests
{
    private static Character? FirstCharacter(SceneModel model) =>
        model.Scene.Characters.FirstOrDefault(c => c.Controllers.Count > 0);

    // ---- schema and model ----------------------------------------------------------------------

    [Fact]
    public void CameraSchema_AddsTheCameraClassOnce()
    {
        if (TestFiles.Resource3D is not { } path) return;
        var schema = TestFiles.LoadScene(path).Document.Schema;
        Assert.Null(schema.FindClass("CCameraAnimated"));
        // Resource3D has no animation, so it has no track or key classes: only the camera class is added.
        Assert.Null(schema.FindClass("CAnimTrackMRS"));
        Assert.Null(schema.FindStruct("CKeyMRS"));

        Assert.True(CameraSchema.Ensure(schema));
        var camera = schema.FindClass("CCameraAnimated");
        Assert.NotNull(camera);
        Assert.Equal("CCamera", camera!.Parent?.Key);
        Assert.Equal(new[] { "Mode", "Ratio", "NearClipPlane", "FarClipPlane" }, camera.Members.Select(m => m.Name));
        Assert.Null(schema.FindClass("CAnimTrackCamera"));
        Assert.Null(schema.FindStruct("CKeyCamera"));
        Assert.False(CameraSchema.Ensure(schema));
    }

    [SceneTheory, SceneSamples]
    public void CameraSchema_AddsTheTrackAndKeyClassesInAnAnimatedScene(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        var schema = model.Document.Schema;
        CameraSchema.Ensure(schema);
        Assert.Equal("CAnimTrackMRS", schema.FindClass("CAnimTrackCamera")!.Parent?.Key);
        Assert.Equal(new[] { "Flags", "FOV" }, schema.FindStruct("CKeyCamera")!.Members.Select(m => m.Name));
        Assert.Equal(new[] { "CTangentInfoListBase", "CTangentInfoListMRS", "CTangentInfoListCamera" },
            AktNames.PolymorphicChain("CTangentInfoListCamera"));
        Assert.False(CameraSchema.Ensure(schema));
    }

    [Fact]
    public void CameraAnimated_RatioPresetsMatchGetRatio()
    {
        if (TestFiles.Resource3D is not { } path) return;
        var model = TestFiles.LoadScene(path);
        CameraSchema.Ensure(model.Document.Schema);
        var camera = Assert.IsType<CameraAnimated>(model.Wrap(model.Document.Schema.NewObject("CCameraAnimated")));
        camera.Ratio = 3; // the constructor default; NewObject leaves zeroed values

        Assert.Equal(16f / 9f, camera.AspectRatio, 5);
        camera.Ratio = 0;
        Assert.Equal(32f / 9f, camera.AspectRatio, 5);
        camera.Mode = 1;
        Assert.Equal(9f / 32f, camera.AspectRatio, 5);
        camera.Ratio = 2;
        Assert.Equal(0.5f, camera.AspectRatio, 5);
        camera.Mode = 7; // any mode other than 0 and 1 has ratio 1 (GetRatio)
        Assert.Equal(1f, camera.AspectRatio, 5);
        Assert.Equal(30f, CameraKey.DefaultFov);
    }

    // ---- views ---------------------------------------------------------------------------------

    private static Vector2 Project(SceneCameraView view, Vector3 world) =>
        view.Project(world, new Vector2(1000, 1000)) ?? throw new Xunit.Sdk.XunitException("behind the view");

    [Fact]
    public void OrthoView_MapsWorldAxesToTheScreen()
    {
        var viewport = new Vector2(1000, 1000);
        void Check(int type, Vector3 right, Vector3 up)
        {
            var view = SceneCameraView.Ortho(type, 100f, Vector2.Zero);
            var centre = view.Project(Vector3.Zero, viewport)!.Value;
            Assert.InRange(centre.X, 499.9f, 500.1f);
            Assert.InRange(centre.Y, 499.9f, 500.1f);
            var r = view.Project(right * 10f, viewport)!.Value;
            var u = view.Project(up * 10f, viewport)!.Value;
            Assert.True(r.X > centre.X, $"type {type}: {right} should go right ({r} vs {centre})");
            Assert.True(u.Y < centre.Y, $"type {type}: {up} should go up ({u} vs {centre})");
        }
        Check(0, Vector3.UnitX, Vector3.UnitY);   // Front
        Check(1, -Vector3.UnitX, Vector3.UnitY);  // Back
        Check(2, -Vector3.UnitZ, Vector3.UnitY);  // Right
        Check(3, Vector3.UnitZ, Vector3.UnitY);   // Left
        Check(4, Vector3.UnitX, -Vector3.UnitZ);  // Top
        Check(5, Vector3.UnitX, Vector3.UnitZ);   // Bottom
        Assert.Equal(100f, SceneCameraView.Ortho(0, 100f, Vector2.Zero).ViewHeightAt(new Vector3(10, 20, 30)), 3);
    }

    [Fact]
    public void OrthoView_WorldOffsetShiftsTheCentre()
    {
        var view = SceneCameraView.Ortho(0, 100f, new Vector2(10f, 5f));
        var point = view.Project(new Vector3(10, 5, 0), new Vector2(1000, 1000))!.Value;
        Assert.InRange(point.X, 499.9f, 500.1f);
        Assert.InRange(point.Y, 499.9f, 500.1f);
    }

    [Fact]
    public void FixedViews_UseTheStoredCameraPose()
    {
        if (TestFiles.Resource3D is not { } path) return;
        var scene = TestFiles.LoadScene(path).Scene;
        var photo = (PerspectiveCamera)scene.Cameras.First(c => c.Name == "Photo");
        var view = SceneCameraView.Photo(photo);
        Assert.Equal(photo.ViewPose.Translation, view.Eye);
        Assert.Equal(photo.AspectRatio, view.FrameAspect!.Value, 5);
        // The stored pose puts the model in front of the camera.
        Assert.NotNull(view.Project(Vector3.Zero, new Vector2(800, 600)));
        var perspective = (PerspectiveCamera)scene.Cameras.First(c => c.Name == "Perspective");
        Assert.Equal(perspective.ViewPose.Translation, SceneCameraView.Perspective(perspective).Eye);

        // An orthographic camera's LTM is its view pose, so it projects to the centre of the view.
        foreach (var ortho in scene.Cameras.OfType<OrthoCamera>())
        {
            var orthoView = SceneCameraView.Ortho(ortho);
            var centre = orthoView.Project(ortho.WorldMatrix.Translation, new Vector2(1000, 1000))!.Value;
            Assert.InRange(centre.X, 499.9f, 500.1f);
            Assert.InRange(centre.Y, 499.9f, 500.1f);
        }
    }

    [Fact]
    public void AnimatedCamera_ViewIsTheInversePose()
    {
        if (TestFiles.Resource3D is not { } path) return;
        var model = TestFiles.LoadScene(path);
        CameraSchema.Ensure(model.Document.Schema);
        var camera = Assert.IsType<CameraAnimated>(model.Wrap(model.Document.Schema.NewObject("CCameraAnimated")));
        camera.Ratio = 3;
        var pose = Matrix4x4.CreateTranslation(0, 0, 100);
        var view = SceneCameraView.Animated(camera, pose, 60f);
        Assert.Equal(16f / 9f, view.FrameAspect!.Value, 5);
        var centre = view.Project(Vector3.Zero, new Vector2(800, 450))!.Value;
        Assert.InRange(centre.X, 399.9f, 400.1f);
        Assert.InRange(centre.Y, 224.9f, 225.1f);
        Assert.Equal(100f, view.Eye.Z, 3);
    }

    // ---- the animated camera in a scene ---------------------------------------------------------

    [SceneTheory, SceneSamples]
    public void AddAnimatedCamera_AddsControllerPoseSlotsAndTheCameraClass(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        var poses = character.BindPoses.Prepend(character.RefPose).OfType<CharacterPose>().Distinct().ToList();
        int controllers = character.Controllers.Count;
        var poseSlots = poses.Select(p => p.Count).ToArray();
        var session = new PoseEditSession(new ScenePose(model), character);

        var camera = session.AddAnimatedCamera("Test Camera");

        Assert.Equal("Test Camera", camera.Name);
        Assert.Same(character, camera.Parent);
        var target = Assert.IsType<CameraTarget>(camera.Target);
        var hide = Assert.IsType<Joint>(camera.HideJoint);
        // The rig's objects are controllers too: camera, hidden joint, target, in file order.
        Assert.Equal(new Object3D[] { camera, hide, target }, character.Controllers.TakeLast(3).ToArray());
        Assert.Equal(controllers + 3, character.Controllers.Count);
        Assert.All(poses.Select((p, i) => (p, i)), x => Assert.Equal(poseSlots[x.i] + 3, x.p.Count));
        Assert.Equal(session.Evaluator.Controllers.Count, character.Controllers.Count);
        Assert.True(session.Evaluator.IndexOf(camera) >= 0);
        Assert.True(session.Evaluator.IndexOf(hide) >= 0);
        Assert.True(session.Evaluator.IndexOf(target) >= 0);
        // The camera's pose slot carries its starting pose (the evaluated rest transform).
        int slot = session.Evaluator.IndexOf(camera);
        var expected = Transform.FromMatrixLoose(camera.LocalMatrix);
        Assert.True(System.Numerics.Vector3.Distance(expected.Translation, session.Evaluator.RestTransforms[slot].Translation) < 1e-3f);
        Assert.Equal(-1, camera.CameraType);
        Assert.Equal(0, camera.Mode);
        Assert.Equal(3, camera.Ratio);
        Assert.Equal(15f, camera.NearClipPlane);
        Assert.Equal(30000f, camera.FarClipPlane);

        // Undo detaches all three again (posing and the controller list), redo puts them back.
        Assert.True(session.Undo());
        Assert.DoesNotContain(camera, character.Controllers);
        Assert.DoesNotContain(hide, character.Controllers);
        Assert.DoesNotContain(target, character.Controllers);
        Assert.Equal(controllers, character.Controllers.Count);
        Assert.All(poses.Select((p, i) => (p, i)), x => Assert.Equal(poseSlots[x.i], x.p.Count));
        Assert.True(session.Redo());
        Assert.Contains(camera, character.Controllers);
        Assert.Contains(hide, character.Controllers);
        Assert.Contains(target, character.Controllers);
        Assert.Equal(controllers + 3, character.Controllers.Count);
    }

    [Fact]
    public void AddAnimatedCamera_BuildsTheLookerRigOnANewScene()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var session = new PoseEditSession(new ScenePose(model), character);

        var camera = session.AddAnimatedCamera("Rig Camera");

        var target = Assert.IsType<CameraTarget>(camera.Target);
        var hide = Assert.IsType<Joint>(camera.HideJoint);
        Assert.Equal("Rig Camera_Target", target.Name);
        Assert.Equal("Default name", hide.Name);
        // The flags: shown + "new object" (0x400); the hidden joint adds compose mode 1 and bit 7.
        Assert.Equal(0x401u, camera.Flags);
        Assert.Equal(0x401u, target.Flags);
        Assert.Equal(0x482u, hide.Flags);
        Assert.Equal(0f, hide.LocalMatrix.Translation.X, 4);
        Assert.Equal(0f, hide.LocalMatrix.Translation.Y, 4);
        Assert.Equal(-40f, hide.LocalMatrix.Translation.Z, 4);
        // The target is the camera's own -Z point expressed in the character's space.
        var expectedTarget = Matrix4x4.CreateTranslation(0f, 0f, -40f) * camera.LocalMatrix;
        Assert.True(AktMath.MaxDifference(expectedTarget, target.LocalMatrix) < 1e-4f,
            $"the target's LTM {target.LocalMatrix.Translation} is not the camera's -Z point {expectedTarget.Translation}");
        Assert.Same(target, camera.Constraint);
        Assert.Same(target, hide.Constraint);
        Assert.Null(target.Constraint);
        Assert.Same(camera, hide.Parent);
        Assert.Same(character, target.Parent);
        Assert.Equal(new Object3D[] { camera, hide }, target.Chain.ToArray());
        Assert.Empty(target.ChainParents);
        Assert.Equal(2, target.DecoratorAxis);
        Assert.Equal(1f, target.TargetRenderSizeRatio);
        Assert.Contains(hide, character.Joints);

        // All three are controllers with a matrix in every pose, in the same order.
        Assert.Equal(new Object3D[] { camera, hide, target }, character.Controllers.ToArray());
        foreach (var pose in new[] { character.RefPose, character.CurrentPose, character.RefPoseOld }.OfType<CharacterPose>().Distinct())
        {
            Assert.Equal(3, pose.Count);
            Assert.True(AktMath.MaxDifference(camera.LocalMatrix, pose.Matrix(0)) < 1e-4f);
            Assert.True(AktMath.MaxDifference(hide.LocalMatrix, pose.Matrix(1)) < 1e-4f);
            Assert.True(AktMath.MaxDifference(target.LocalMatrix, pose.Matrix(2)) < 1e-4f);
        }
    }

    [SceneTheory, SceneSamples]
    public void AddAnimatedCamera_BuildsTheLookerRig(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        var session = new PoseEditSession(new ScenePose(model), character);

        var camera = session.AddAnimatedCamera("Rig Camera");

        var target = Assert.IsType<CameraTarget>(camera.Target);
        var hide = Assert.IsType<Joint>(camera.HideJoint);
        Assert.Equal("Rig Camera_Target", target.Name);
        Assert.Equal("Default name", hide.Name);
        Assert.Equal(0x401u, camera.Flags);
        Assert.Equal(0x401u, target.Flags);
        Assert.Equal(0x482u, hide.Flags);
        Assert.Equal(-40f, hide.LocalMatrix.Translation.Z, 4);
        var expectedTarget = Matrix4x4.CreateTranslation(0f, 0f, -40f) * camera.LocalMatrix;
        Assert.True(AktMath.MaxDifference(expectedTarget, target.LocalMatrix) < 1e-4f);
        Assert.Same(target, camera.Constraint);
        Assert.Same(target, hide.Constraint);
        Assert.Null(target.Constraint);
        Assert.Equal(new Object3D[] { camera, hide }, target.Chain.ToArray());
        Assert.Empty(target.ChainParents);
        Assert.Equal(2, target.DecoratorAxis);
        Assert.Equal(new Object3D[] { camera, hide, target }, character.Controllers.TakeLast(3).ToArray());
        int slot = character.Controllers.Count - 3;
        foreach (var pose in character.BindPoses.Prepend(character.RefPose).OfType<CharacterPose>().Distinct())
        {
            Assert.True(pose.Count >= slot + 3);
            Assert.True(AktMath.MaxDifference(camera.LocalMatrix, pose.Matrix(slot)) < 1e-3f);
            Assert.True(AktMath.MaxDifference(hide.LocalMatrix, pose.Matrix(slot + 1)) < 1e-3f);
            Assert.True(AktMath.MaxDifference(target.LocalMatrix, pose.Matrix(slot + 2)) < 1e-3f);
        }
    }

    [Fact]
    public void AddAnimatedCamera_AddsTheLookerSchema()
    {
        var model = SceneFactory.NewSceneModel(60);
        var schema = model.Document.Schema;
        // A new scene declares neither the rig classes nor the animation layer they need.
        foreach (string name in new[] { "CHideJointForLooker", "CCameraTarget", "CAnimTrackLooker_VK", "CAnimTrackMRS", "CJoint" })
            Assert.Null(schema.FindClass(name));

        var session = new PoseEditSession(new ScenePose(model), model.Scene.Characters.First());
        session.AddAnimatedCamera();

        // The app resolves class definitions by their MSVC spelling, so the definition keeps "class ".
        Assert.Equal("class CHideJointForLooker", schema.FindClass("CHideJointForLooker")!.Name);
        Assert.Equal("class CCameraTarget", schema.FindClass("CCameraTarget")!.Name);
        Assert.Equal("class CAnimTrackLooker_VK", schema.FindClass("CAnimTrackLooker_VK")!.Name);
        Assert.Equal("CJoint", schema.FindClass("CHideJointForLooker")!.Parent?.Key);
        Assert.Equal("CLooker", schema.FindClass("CCameraTarget")!.Parent?.Key);
        Assert.Equal("CAnimTrackIK_VK", schema.FindClass("CAnimTrackLooker_VK")!.Parent?.Key);
        Assert.Null(schema.FindClass("CHideJointForLooker")!.Members.FirstOrDefault());
        // The key struct the looker keys use is CKeyIK, with its own members.
        Assert.Equal(new[] { "RefArray", "Flags", "PoleAngle", "RefPoleAngle", "RenderKey", "TargetControllerIK" },
            schema.FindStruct("CKeyIK")!.Members.Select(m => m.Name));
        // A second ensure adds nothing.
        Assert.False(CameraSchema.EnsureRig(schema));
    }

    [Fact]
    public void NewAnimation_GivesTheCameraThreeTracks()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var session = new PoseEditSession(new ScenePose(model), character);
        var camera = session.AddAnimatedCamera();
        var animation = session.CreateAnimation();
        var stack = Assert.Single(animation.Stacks);

        var tracks = stack.Tracks.ToList();
        Assert.Equal(3, tracks.Count);
        Assert.Equal(new[] { "CAnimTrackCamera", "CAnimTrackMRS", "CAnimTrackLooker_VK" }, tracks.Select(t => t.ClassName));
        Assert.Equal(new[] { "Camera Track", "Track", "Track_02" }, tracks.Select(t => t.Name));
        Assert.Same(camera, tracks[0].Target);
        Assert.Same(camera.HideJoint, tracks[1].Target);
        Assert.Same(camera.Target, tracks[2].Target);
        Assert.True(tracks[0].IsCameraTrack);
        Assert.True(tracks[0].HasFov);
        Assert.Equal(AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Fov, tracks[0].Channels);
        Assert.Equal(0u, (uint)(tracks[0].Channels & AnimChannels.Scale));
        Assert.All(tracks[0].Keys, k => Assert.IsType<CameraKey>(k));
        Assert.All(tracks[0].Keys, k => Assert.Equal(CameraKey.DefaultFov, Assert.IsType<CameraKey>(k).Fov, 5));
        // The hidden joint's MRS track animates the whole transform, and the looker's IK track its chain.
        Assert.Equal(AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Scale, tracks[1].Channels);
        Assert.All(tracks[1].Keys, k => Assert.IsType<TransformKey>(k));
        Assert.Equal(AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.PoleAngle, tracks[2].Channels);
        Assert.All(tracks[2].Keys, k => Assert.IsType<IKKey>(k));
        // The looker keys already name the chain: the camera and its hidden joint.
        var looker = Assert.IsType<IKKey>(tracks[2].Keys[0]);
        Assert.Equal(2, looker.ChainTransforms.Count);
        Assert.Equal(0f, looker.Flags);
        Assert.Equal(30f, session.Evaluator.CameraFov(animation, camera, 0), 5);
    }

    [SceneTheory, SceneSamples]
    public void NewAnimation_GivesTheCameraACameraTrack(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var camera = session.AddAnimatedCamera();
        var animation = session.CreateAnimation();
        var stack = Assert.Single(animation.Stacks);

        var track = Assert.IsType<AnimTrack>(stack.TrackFor(camera));
        Assert.Equal("CAnimTrackCamera", track.ClassName);
        Assert.True(track.IsCameraTrack);
        Assert.True(track.HasFov);
        Assert.Equal(AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Fov, track.Channels);
        Assert.Equal(0u, (uint)(track.Channels & AnimChannels.Scale));
        Assert.All(track.Keys, k => Assert.IsType<CameraKey>(k));
        Assert.All(track.Keys, k => Assert.Equal(CameraKey.DefaultFov, Assert.IsType<CameraKey>(k).Fov, 5));
        Assert.All(track.TangentModes, modes => Assert.Equal(10, modes.Length));
        Assert.Equal(30f, session.Evaluator.CameraFov(animation, camera, 0), 5);
        // The rig's two other tracks come with it, named and targeted like the.
        Assert.NotNull(stack.TrackFor(camera.HideJoint!));
        Assert.NotNull(stack.TrackFor(camera.Target!));
        var hideTrack = stack.TrackFor(camera.HideJoint!)!;
        var lookerTrack = stack.TrackFor(camera.Target!)!;
        Assert.Equal("CAnimTrackMRS", hideTrack.ClassName);
        Assert.Equal("Track", hideTrack.Name);
        Assert.Equal("CAnimTrackLooker_VK", lookerTrack.ClassName);
        Assert.Equal("Track_02", lookerTrack.Name);
    }

    [Fact]
    public void KeyingTheCamera_WritesTheJointAndLookerKeys()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var session = new PoseEditSession(new ScenePose(model), character);
        var camera = session.AddAnimatedCamera("Rig Camera");
        var animation = session.CreateAnimation();
        var stack = Assert.Single(animation.Stacks);
        session.SetKeyTime(stack, 1, 20);
        // A stack keys at its penultimate time, so give it an editable middle key before frame 2.
        session.CreateKey(stack, 10);
        session.SetKeyTime(stack, 2, 20);
        var cameraTrack = stack.TrackFor(camera)!;
        var hideTrack = stack.TrackFor(camera.HideJoint!)!;
        var lookerTrack = stack.TrackFor(camera.Target!)!;
        int before = stack.Keys.Count;

        // K at frame 2, then a 15 degree turn about the camera's own X (the gizmo path).
        session.CreateKey(stack, 2);
        Assert.Equal(before + 1, stack.Keys.Count);
        int keyIndex = Array.FindIndex(stack.Keys.ToArray(), k => k.Time == 2);
        Assert.True(keyIndex >= 0);
        var drag = session.Begin(animation, camera, 2, out string? reason);
        Assert.Null(reason);
        Assert.NotNull(drag);
        drag!.Rotate(0, MathF.PI / 12);
        drag.Commit();

        // The three tracks each hold a key at the new frame.
        Assert.Equal(stack.Keys.Count, cameraTrack.Keys.Count);
        Assert.Equal(stack.Keys.Count, hideTrack.Keys.Count);
        Assert.Equal(stack.Keys.Count, lookerTrack.Keys.Count);
        var cameraKey = Assert.IsType<CameraKey>(cameraTrack.Keys[keyIndex]);
        var hideKey = Assert.IsType<TransformKey>(hideTrack.Keys[keyIndex]);
        var lookerKey = Assert.IsType<IKKey>(lookerTrack.Keys[keyIndex]);

        var evaluator = session.Evaluator;
        evaluator.Invalidate();
        var local = evaluator.LocalTransforms(animation, 2);
        int cameraIndex = evaluator.IndexOf(camera), hideIndex = evaluator.IndexOf(camera.HideJoint!), targetIndex = evaluator.IndexOf(camera.Target!);
        var cameraLocal = local[cameraIndex];
        var hideLocal = local[hideIndex];
        var targetLocal = local[targetIndex];
        // The camera key is the 15 degree turn.
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 12);
        Assert.True(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(cameraKey.Value.Rotation), turn)) > 0.9999f,
            $"the camera key's turn is {cameraKey.Value.Rotation}, not 15 degrees about X");
        // The hidden joint keeps the rest orbit distance (40) and its own key stays an identity offset.
        Assert.Equal(-40f, hideLocal.Translation.Z, 3);
        Assert.True(Vector3.Distance(Transform.Identity.Translation, hideKey.Value.Translation) < 1e-4f);
        // The looker's key is the target position in the character's space: the camera's -Z point.
        float distance = -hideLocal.Translation.Z;
        var expected = (Matrix4x4.CreateTranslation(0f, 0f, -distance) * cameraLocal.ToMatrix()).Translation;
        var targetRest = evaluator.RestTransforms[targetIndex];
        var posedTarget = PoseEvaluator.Compose(targetRest, lookerKey.Value);
        Assert.True(Vector3.Distance(expected, posedTarget.Translation) < 1e-3f,
            $"the looker key puts the target at {posedTarget.Translation}, not {expected}");
        Assert.True(Vector3.Distance(expected, targetLocal.Translation) < 1e-3f,
            $"the evaluated target is at {targetLocal.Translation}, not {expected}");
        // Its RefArray is [the camera's key pose, the hidden joint's key pose], as in the reference file.
        Assert.Equal(2, lookerKey.ChainTransforms.Count);
        Assert.True(Vector3.Distance(cameraLocal.Translation, lookerKey.ChainTransforms[0].Translation) < 1e-3f);
        Assert.True(MathF.Abs(Quaternion.Dot(cameraLocal.Rotation, lookerKey.ChainTransforms[0].Rotation)) > 0.9999f);
        Assert.True(Vector3.Distance(hideLocal.Translation, lookerKey.ChainTransforms[1].Translation) < 1e-3f);
        Assert.Equal(0u, lookerKey.Flags);
        Assert.Equal(0f, lookerKey.PoleAngle);
        Assert.Equal(0f, lookerKey.Source.GetOr("RefPoleAngle", 0f));
    }

    // ---- dragging the looker target -------------------------------------------------------------

    [Fact]
    public void Aim_KeepsTheEyeAndTurnsTheCamerasNegativeZAtThePoint()
    {
        var camera = Matrix4x4.CreateTranslation(10f, 20f, 30f);
        var eye = new Vector3(10f, 20f, 30f);

        // Straight ahead: identity, at the point's depth.
        Assert.True(CameraRigKeys.Aim(camera, eye + new Vector3(0f, 0f, -50f), out var ahead, out float distance));
        Assert.Equal(50f, distance, 3);
        Assert.True(MathF.Abs(Quaternion.Dot(ahead.Rotation, Quaternion.Identity)) > 0.9999f);
        Assert.Equal(eye, ahead.Translation);

        // To the camera's left (+X): its -Z becomes +X and its up stays +Y.
        Assert.True(CameraRigKeys.Aim(camera, eye + new Vector3(70f, 0f, 0f), out var left, out distance));
        Assert.Equal(70f, distance, 3);
        var m = left.ToMatrix();
        Assert.True(Vector3.Distance(-new Vector3(m.M31, m.M32, m.M33), Vector3.UnitX) < 1e-4f,
            $"the aimed camera looks along {new Vector3(m.M31, m.M32, m.M33)}");
        Assert.True(Vector3.Distance(new Vector3(m.M21, m.M22, m.M23), Vector3.UnitY) < 1e-4f);
        Assert.True(Vector3.Distance(new Vector3(m.M11, m.M12, m.M13), Vector3.UnitZ) < 1e-4f);

        // A point on the eye has nothing to aim at.
        Assert.False(CameraRigKeys.Aim(camera, eye, out _, out _));
        Assert.False(CameraRigKeys.Aim(camera, eye + new Vector3(0f, 0f, -1e-5f), out _, out _));
    }

    [Fact]
    public void AViewLookingThroughTheCamera_ForbidsDraggingItsTarget()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var session = new PoseEditSession(new ScenePose(model), character);
        var camera = session.AddAnimatedCamera("Rig Camera");
        var other = session.AddAnimatedCamera("Other Camera");
        var target = Assert.IsType<CameraTarget>(camera.Target);

        // The CLooker::ForbidMRSManipulatorWhenLastSelected checks only the constrained root
        // being an animated camera and being the one the view shows.
        Assert.True(CameraEditor.ForbidsTargetDrag(target, camera));
        Assert.False(CameraEditor.ForbidsTargetDrag(target, other));
        Assert.False(CameraEditor.ForbidsTargetDrag(target, null));
        Assert.True(CameraEditor.ForbidsTargetDrag(Assert.IsType<CameraTarget>(other.Target), other));
        Assert.False(CameraEditor.ForbidsTargetDrag(Assert.IsType<CameraTarget>(other.Target), camera));
    }

    [Fact]
    public void DraggingTheTarget_TurnsTheCameraAndMovesTheEffectorOntoThePoint()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var session = new PoseEditSession(new ScenePose(model), character);
        var camera = session.AddAnimatedCamera("Rig Camera");
        var target = Assert.IsType<CameraTarget>(camera.Target);
        var hide = Assert.IsType<Joint>(camera.HideJoint);
        var animation = session.CreateAnimation();
        var stack = Assert.Single(animation.Stacks);
        session.SetKeyTime(stack, 1, 20);
        // An editable middle key first: a stack only keys up to its penultimate time.
        session.CreateKey(stack, 10);
        session.SetKeyTime(stack, 2, 20);
        session.CreateKey(stack, 2);

        var evaluator = session.Evaluator;
        evaluator.Invalidate();
        var before = evaluator.LocalTransforms(animation, 2);
        int cameraIndex = evaluator.IndexOf(camera), hideIndex = evaluator.IndexOf(hide), targetIndex = evaluator.IndexOf(target);
        var eye = before[cameraIndex].Translation;
        var lookPoint = before[targetIndex].Translation;
        Assert.True(Vector3.Distance(lookPoint, (Matrix4x4.CreateTranslation(0f, 0f, -40f) * before[cameraIndex].ToMatrix()).Translation) < 1e-3f,
            "the new rig starts with the target on the camera's -Z axis");
        int keyIndex = Array.FindIndex(stack.Keys.ToArray(), k => k.Time == 2);
        Assert.True(keyIndex >= 0);
        var lookerTrack = stack.TrackFor(target)!;
        var lookerBefore = Assert.IsType<IKKey>(lookerTrack.Keys[keyIndex]);
        var chainBefore = lookerBefore.ChainTransforms.ToArray();
        var cameraBefore = Assert.IsAssignableFrom<TransformKey>(stack.TrackFor(camera)!.Keys[keyIndex]).Value.Rotation;

        // Drag the target 100 units along the character's X; the camera is not the drag's controller.
        var drag = session.Begin(animation, target, 2, out string? reason);
        Assert.Null(reason);
        Assert.NotNull(drag);
        drag!.Translate(0, 100f);
        drag.Commit();

        var after = session.Evaluator.LocalTransforms(animation, 2);
        var cameraLocal = after[cameraIndex];
        var targetLocal = after[targetIndex];
        var hideLocal = after[hideIndex];
        var dragged = lookPoint + new Vector3(100f, 0f, 0f);
        // The target's key keeps the dragged point, exactly.
        Assert.True(Vector3.Distance(dragged, targetLocal.Translation) < 1e-3f,
            $"the target is at {targetLocal.Translation}, not {dragged}");
        // The camera's eye is unchanged and its -Z now points at the point.
        Assert.True(Vector3.Distance(eye, cameraLocal.Translation) < 1e-4f);
        var cameraMatrix = cameraLocal.ToMatrix();
        var forward = -Vector3.Normalize(new Vector3(cameraMatrix.M31, cameraMatrix.M32, cameraMatrix.M33));
        Assert.True(Vector3.Distance(forward, Vector3.Normalize(dragged - eye)) < 1e-3f,
            $"the camera looks along {forward}, not at {dragged}");
        // The hidden joint's effector moved onto the point: its own -Z distance is the point's.
        Assert.Equal(-Vector3.Distance(dragged, eye), hideLocal.Translation.Z, 2);
        // The looker key is the point. Its own rotation stays at the target's rest orientation and
        // its RefArray keeps the pre-edit camera and joint poses, which is what the edit
        // left in the file (it does not refresh the chain).
        var lookerKey = Assert.IsType<IKKey>(lookerTrack.Keys[keyIndex]);
        var posedTarget = PoseEvaluator.Compose(evaluator.RestTransforms[targetIndex], lookerKey.Value);
        Assert.True(Vector3.Distance(dragged, posedTarget.Translation) < 1e-3f);
        Assert.True(MathF.Abs(Quaternion.Dot(lookerKey.Value.Rotation, Quaternion.Identity)) > 0.9999f,
            $"the looker key's own rotation is {lookerKey.Value.Rotation}");
        Assert.Equal(chainBefore, lookerKey.ChainTransforms);
        // The joint's key carries the camera key's inverse rotation, every tangent recomputed.
        var cameraKey = Assert.IsAssignableFrom<TransformKey>(stack.TrackFor(camera)!.Keys[keyIndex]);
        var hideKey = Assert.IsType<TransformKey>(stack.TrackFor(hide)!.Keys[keyIndex]);
        var composed = Quaternion.Normalize(Quaternion.Multiply(hideKey.Value.Rotation, cameraKey.Value.Rotation));
        Assert.True(MathF.Abs(Quaternion.Dot(composed, Quaternion.Identity)) > 0.99999f,
            "the hidden joint's key is not the camera key's inverse rotation");
        // The camera kept its roll: the turn is a pure swing, so in the camera's own frame its axis
        // has no Z component.
        var swing = Quaternion.Normalize(Quaternion.Multiply(cameraKey.Value.Rotation, Quaternion.Inverse(cameraBefore)));
        var swingAxis = new Vector3(swing.X, swing.Y, swing.Z);
        Assert.True(swingAxis.Length() > 1e-3f);
        Assert.True(MathF.Abs(Vector3.Normalize(swingAxis).Z) < 1e-3f, "the aim added a roll");
        Assert.Equal(0u, lookerKey.Flags);
        Assert.Equal(0f, lookerKey.PoleAngle);
    }

    [Fact]
    public void Aim_OnAReferenceEdit_ReproducesTheReferenceKey()
    {
        // A camera rig: this is the camera's rest pose, and the live
        // edit moved the target 5000 units along the character's X (a parent-space delta, the
        // Spinner field's write). The app re-saved the camera key as (-0.076938, -0.75909, 0,
        // 0.646423) - a pure swing about the camera's local Y. The aim must reproduce it.
        var rest = new Matrix4x4(
            0.980067f, 0f, -0.198669f, 0f,
            -0.099335f, 0.866025f, -0.490033f, 0f,
            0.172053f, 0.5f, 0.848763f, 0f,
            120.4369f, 350f, 594.1338f, 1f);
        var look = new Vector3(113.5548f, 330f, 560.1833f) + new Vector3(5000f, 0f, 0f);

        Assert.True(CameraRigKeys.Aim(rest, look, out var aimed, out float distance));
        Assert.Equal((look - rest.Translation).Length(), distance, 1);
        var restRotation = Transform.FromMatrixLoose(rest).Rotation;
        var key = Quaternion.Normalize(Quaternion.Multiply(Quaternion.Inverse(restRotation), aimed.Rotation));
        var measured = Quaternion.Normalize(new Quaternion(-0.076938f, -0.75909f, 0f, 0.646423f));
        Assert.True(MathF.Abs(Quaternion.Dot(key, measured)) > 0.99999f,
            $"the aimed key is {key}, the reference is {measured}");
        // The joint's key the app wrote with it is the exact conjugate.
        var joint = Quaternion.Inverse(key);
        var measuredJoint = Quaternion.Normalize(new Quaternion(0.076938f, 0.75909f, 0f, 0.646423f));
        Assert.True(MathF.Abs(Quaternion.Dot(joint, measuredJoint)) > 0.99999f);
    }

    [Fact]
    public void DraggingTheTargetAtTheFirstKey_LeavesTheLoopEndKeyAlone()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var session = new PoseEditSession(new ScenePose(model), character);
        var camera = session.AddAnimatedCamera("Rig Camera");
        var target = Assert.IsType<CameraTarget>(camera.Target);
        var hide = Assert.IsType<Joint>(camera.HideJoint);
        var animation = session.CreateAnimation();
        var stack = Assert.Single(animation.Stacks);
        // Three stack keys: 0 and 5 authored, the end key (at 10) replays key 0.
        session.CreateKey(stack, 5);
        Assert.Equal(3, stack.Keys.Count);
        var cameraTrack = stack.TrackFor(camera)!;
        var hideTrack = stack.TrackFor(hide)!;
        var lookerTrack = stack.TrackFor(target)!;
        var endCamera = ((TransformKey)cameraTrack.Keys[2]!).Value;
        var endHide = ((TransformKey)hideTrack.Keys[2]!).Value;
        var endLooker = ((IKKey)lookerTrack.Keys[2]!).Value;

        var drag = session.Begin(animation, target, 0, out string? reason);
        Assert.Null(reason);
        Assert.NotNull(drag);
        drag!.Translate(0, 100f);
        drag.Commit();

        // The live target edit wrote only the key at the playhead, so the replay end key
        // keeps every one of its values.
        var afterCamera = ((TransformKey)cameraTrack.Keys[2]!).Value;
        var afterHide = ((TransformKey)hideTrack.Keys[2]!).Value;
        var afterLooker = ((IKKey)lookerTrack.Keys[2]!).Value;
        Assert.True(endCamera.Equals(afterCamera), $"the camera end key changed: {endCamera} -> {afterCamera}");
        Assert.True(endHide.Equals(afterHide), $"the joint end key changed: {endHide} -> {afterHide}");
        Assert.True(endLooker.Equals(afterLooker), $"the looker end key changed: {endLooker} -> {afterLooker}");
    }

    [Fact]
    public void DraggingTheTarget_TowardsTheCameraShortensTheOrbit()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var session = new PoseEditSession(new ScenePose(model), character);
        var camera = session.AddAnimatedCamera("Rig Camera");
        var target = Assert.IsType<CameraTarget>(camera.Target);
        var hide = Assert.IsType<Joint>(camera.HideJoint);
        var animation = session.CreateAnimation();
        var stack = Assert.Single(animation.Stacks);
        session.SetKeyTime(stack, 1, 20);
        session.CreateKey(stack, 10);
        session.SetKeyTime(stack, 2, 20);
        session.CreateKey(stack, 2);

        var evaluator = session.Evaluator;
        evaluator.Invalidate();
        var before = evaluator.LocalTransforms(animation, 2);
        int cameraIndex = evaluator.IndexOf(camera), hideIndex = evaluator.IndexOf(hide), targetIndex = evaluator.IndexOf(target);
        var eye = before[cameraIndex].Translation;
        var lookPoint = before[targetIndex].Translation;
        var cameraMatrix = before[cameraIndex].ToMatrix();
        var forward = -Vector3.Normalize(new Vector3(cameraMatrix.M31, cameraMatrix.M32, cameraMatrix.M33));
        float start = Vector3.Distance(lookPoint, eye);

        // Pull the target 10 units towards the camera along the camera's own -Z.
        var drag = session.Begin(animation, target, 2, out _);
        Assert.NotNull(drag);
        drag!.Set(drag.BaseLocal with { Translation = lookPoint - forward * 10f }, AnimChannels.Translate);
        drag.Commit();

        var after = session.Evaluator.LocalTransforms(animation, 2);
        var cameraLocal = after[cameraIndex];
        var targetLocal = after[targetIndex];
        float distance = Vector3.Distance(targetLocal.Translation, cameraLocal.Translation);
        Assert.Equal(start - 10f, distance, 2);
        // The camera kept its eye and its aim (the point stayed on its -Z axis).
        Assert.True(Vector3.Distance(eye, cameraLocal.Translation) < 1e-4f);
        var aimedMatrix = cameraLocal.ToMatrix();
        var aimedForward = -Vector3.Normalize(new Vector3(aimedMatrix.M31, aimedMatrix.M32, aimedMatrix.M33));
        Assert.True(Vector3.Distance(aimedForward, Vector3.Normalize(targetLocal.Translation - eye)) < 1e-3f);
        // The joint's key carries the new orbit distance.
        Assert.Equal(-distance, after[hideIndex].Translation.Z, 2);
    }

    [Fact]
    public void DraggingTheTarget_OntoTheEye_LeavesTheCameraAndTheRigAlone()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var session = new PoseEditSession(new ScenePose(model), character);
        var camera = session.AddAnimatedCamera("Rig Camera");
        var target = Assert.IsType<CameraTarget>(camera.Target);
        var animation = session.CreateAnimation();
        var stack = Assert.Single(animation.Stacks);
        session.SetKeyTime(stack, 1, 20);
        session.CreateKey(stack, 10);
        session.SetKeyTime(stack, 2, 20);
        session.CreateKey(stack, 2);

        var evaluator = session.Evaluator;
        evaluator.Invalidate();
        var before = evaluator.LocalTransforms(animation, 2);
        int cameraIndex = evaluator.IndexOf(camera), targetIndex = evaluator.IndexOf(target);
        var cameraLocal = before[cameraIndex];
        var lookPoint = before[targetIndex].Translation;

        // Drag the target exactly onto the eye: there is no direction to aim at, so the rig holds.
        var drag = session.Begin(animation, target, 2, out _);
        Assert.NotNull(drag);
        drag!.Set(drag.BaseLocal with { Translation = cameraLocal.Translation }, AnimChannels.Translate);
        drag.Commit();

        var after = session.Evaluator.LocalTransforms(animation, 2);
        Assert.True(Vector3.Distance(after[targetIndex].Translation, lookPoint) < 1e-2f,
            $"the target is at {after[targetIndex].Translation}, not back at {lookPoint}");
        Assert.True(MathF.Abs(Quaternion.Dot(after[cameraIndex].Rotation, cameraLocal.Rotation)) > 0.9999f,
            "the camera turned although the look point was on its eye");
        Assert.False(float.IsNaN(after[targetIndex].Translation.X));
    }

    [Fact]
    public void DraggingATargetWithNoCamera_JustKeysThePoint()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var session = new PoseEditSession(new ScenePose(model), character);
        var camera = session.AddAnimatedCamera("Rig Camera");
        var target = Assert.IsType<CameraTarget>(camera.Target);
        var animation = session.CreateAnimation();
        var stack = Assert.Single(animation.Stacks);
        session.SetKeyTime(stack, 1, 20);
        session.CreateKey(stack, 10);
        session.SetKeyTime(stack, 2, 20);
        session.CreateKey(stack, 2);
        // A lone target row (no chain) is a plain controller 3D: a drag writes its point and nothing
        // else, as the delete tests' leftover rows do.
        target.Source.Set("JointsString", new List<AktReference>());

        var evaluator = session.Evaluator;
        evaluator.Invalidate();
        var before = evaluator.LocalTransforms(animation, 2);
        int cameraIndex = evaluator.IndexOf(camera), targetIndex = evaluator.IndexOf(target);

        var drag = session.Begin(animation, target, 2, out string? reason);
        Assert.Null(reason);
        Assert.NotNull(drag);
        drag!.Translate(1, 25f);
        drag.Commit();

        var after = session.Evaluator.LocalTransforms(animation, 2);
        Assert.Equal(before[targetIndex].Translation.Y + 25f, after[targetIndex].Translation.Y, 2);
        Assert.True(MathF.Abs(Quaternion.Dot(after[cameraIndex].Rotation, before[cameraIndex].Rotation)) > 0.9999f);
    }

    [Fact]
    public void DraggingTheTarget_IsOneUndoStep()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var session = new PoseEditSession(new ScenePose(model), character);
        var camera = session.AddAnimatedCamera("Rig Camera");
        var target = Assert.IsType<CameraTarget>(camera.Target);
        var animation = session.CreateAnimation();
        var stack = Assert.Single(animation.Stacks);
        session.SetKeyTime(stack, 1, 20);
        session.CreateKey(stack, 10);
        session.SetKeyTime(stack, 2, 20);
        session.CreateKey(stack, 2);

        var evaluator = session.Evaluator;
        evaluator.Invalidate();
        var before = evaluator.LocalTransforms(animation, 2);
        int cameraIndex = evaluator.IndexOf(camera), targetIndex = evaluator.IndexOf(target);

        var drag = session.Begin(animation, target, 2, out _);
        Assert.NotNull(drag);
        drag!.Translate(0, 100f);
        var record = drag.Commit();
        Assert.Contains("Rig Camera_Target", record.Description);
        Assert.True(session.Undo());

        var after = session.Evaluator.LocalTransforms(animation, 2);
        Assert.True(Vector3.Distance(before[targetIndex].Translation, after[targetIndex].Translation) < 1e-3f,
            "undo did not put the target back");
        Assert.True(MathF.Abs(Quaternion.Dot(before[cameraIndex].Rotation, after[cameraIndex].Rotation)) > 0.9999f,
            "undo did not put the camera back");
        Assert.Equal(before[cameraIndex].Translation, after[cameraIndex].Translation);
    }

    [Fact]
    public void AnimatedCameraWithRig_RoundTripsSaveAndReload()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var session = new PoseEditSession(new ScenePose(model), character);
        var camera = session.AddAnimatedCamera("Rig Camera");
        var animation = session.CreateAnimation();
        var stack = Assert.Single(animation.Stacks);
        session.SetKeyTime(stack, 1, 20);
        // An editable middle key first: a stack only keys up to its penultimate time.
        session.CreateKey(stack, 10);
        session.SetKeyTime(stack, 2, 20);
        session.CreateKey(stack, 2);
        var drag = session.Begin(animation, camera, 2, out string? reason);
        Assert.Null(reason);
        drag!.Rotate(0, MathF.PI / 12);
        drag.Commit();

        string first = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-camera-rig-{Guid.NewGuid():N}.akt");
        string second = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-camera-rig-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(first);
            var reloaded = TestFiles.LoadScene(first);
            var reloadedCharacter = reloaded.Scene.Characters.First(c => c.Name == character.Name);
            var reloadedCamera = Assert.Single(reloadedCharacter.AnimatedCameras);
            Assert.Equal("Rig Camera", reloadedCamera.Name);
            var reloadedTarget = Assert.IsType<CameraTarget>(reloadedCamera.Target);
            var reloadedHide = Assert.IsType<Joint>(reloadedCamera.HideJoint);
            Assert.Equal("Rig Camera_Target", reloadedTarget.Name);
            Assert.Equal("Default name", reloadedHide.Name);
            Assert.Equal(0x401u, reloadedCamera.Flags);
            Assert.Equal(0x482u, reloadedHide.Flags);
            Assert.Equal(-40f, reloadedHide.LocalMatrix.Translation.Z, 4);
            Assert.Same(reloadedTarget, reloadedCamera.Constraint);
            Assert.Same(reloadedTarget, reloadedHide.Constraint);
            Assert.Equal(new Object3D[] { reloadedCamera, reloadedHide }, reloadedTarget.Chain.ToArray());
            Assert.Equal(new Object3D[] { reloadedCamera, reloadedHide, reloadedTarget }, reloadedCharacter.Controllers.TakeLast(3).ToArray());
            var reloadedAnimation = reloadedCharacter.Animations.First(a => a.Name == animation.Name);
            var reloadedStack = reloadedAnimation.Stacks[0];
            var cameraTrack = reloadedStack.TrackFor(reloadedCamera)!;
            var hideTrack = reloadedStack.TrackFor(reloadedHide)!;
            var lookerTrack = reloadedStack.TrackFor(reloadedTarget)!;
            Assert.Equal("CAnimTrackCamera", cameraTrack.ClassName);
            Assert.Equal("CAnimTrackMRS", hideTrack.ClassName);
            Assert.Equal("CAnimTrackLooker_VK", lookerTrack.ClassName);
            var keys = cameraTrack.Keys.OfType<CameraKey>().ToList();
            Assert.Equal(reloadedStack.Keys.Count, keys.Count);
            Assert.True(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(keys[1].Value.Rotation),
                Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 12))) > 0.9999f);
            var lookerKeys = lookerTrack.Keys.OfType<IKKey>().ToList();
            Assert.Equal(reloadedStack.Keys.Count, lookerKeys.Count);
            Assert.All(lookerKeys, k => Assert.Equal(2, k.ChainTransforms.Count));

            reloaded.Save(second);
            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }
        finally
        {
            if (File.Exists(first)) File.Delete(first);
            if (File.Exists(second)) File.Delete(second);
        }
    }


    [Fact]
    public void DeletingTheCamera_TakesItsRigTracksSlotsAndCameraListEntry()
    {
        var model = SceneFactory.NewSceneModel(60);
        var scene = model.Scene;
        var character = scene.Characters.First();
        var session = new PoseEditSession(new ScenePose(model), character);
        var camera = session.AddAnimatedCamera("Rig Camera");
        var hide = Assert.IsType<Joint>(camera.HideJoint);
        var target = Assert.IsType<CameraTarget>(camera.Target);
        var animation = session.CreateAnimation();
        var stack = Assert.Single(animation.Stacks);
        // A view that looks through the camera lists it in CameraList (the Set camera).
        var views = new SceneViews(scene);
        int cameraIndex = scene.EnsureCameraInList(camera);
        views.SetCamera(views.Layout, 0, cameraIndex);

        int controllers = character.Controllers.Count;
        int joints = character.Joints.Count;
        var editor = new RigEditor(character);

        Assert.Equal(1, editor.DeleteControllers([camera]));

        // The selection gathers the camera's hierarchy and every gathered object's
        // __Constraint: the hidden joint (a child of the camera) and the target both go.
        Assert.DoesNotContain(camera, character.Controllers);
        Assert.DoesNotContain(hide, character.Controllers);
        Assert.DoesNotContain(target, character.Controllers);
        Assert.DoesNotContain(hide, character.Joints);
        Assert.Equal(controllers - 3, character.Controllers.Count);
        Assert.Equal(joints - 1, character.Joints.Count);
        Assert.All(editor.Poses(), pose => Assert.Equal(character.Controllers.Count, pose.Count));
        Assert.DoesNotContain(character.Source.PreOrder(), o => ReferenceEquals(o, camera.Source));
        // RemoveController drops every track that targets a deleted object (all three of the rig's).
        Assert.Empty(stack.Tracks);
        // When the camera is deleted it leaves CameraList and its view turns to Perspective.
        Assert.Equal(-1, scene.IndexOfCamera(camera));
        Assert.Equal(0, views.CameraIndex(views.Layout, 0));

        // The saved file has no camera, target or hidden joint, and no dangling view slot.
        string path = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-camera-delete-{Guid.NewGuid():N}.akt");
        string second = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-camera-delete-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(path);
            var reloaded = TestFiles.LoadScene(path);
            var reloadedScene = reloaded.Scene;
            var reloadedCharacter = reloadedScene.Characters.First(c => c.Name == character.Name);
            Assert.Empty(reloadedCharacter.AnimatedCameras);
            Assert.Empty(reloadedCharacter.Descendants<CameraTarget>());
            Assert.DoesNotContain(reloadedCharacter.Descendants<Joint>(), j => j.Name == hide.Name);
            var reloadedViews = new SceneViews(reloadedScene);
            Assert.Equal(0, reloadedViews.CameraIndex(reloadedViews.Layout, 0));
            // Byte-stable: the deleted scene re-saves identically.
            reloaded.Save(second);
            Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(second));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(second)) File.Delete(second);
        }
    }

    [Fact]
    public void ACameraTargetCannotBeDeletedWithoutItsCamera()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var session = new PoseEditSession(new ScenePose(model), character);
        var camera = session.AddAnimatedCamera("Rig Camera");
        var target = Assert.IsType<CameraTarget>(camera.Target);
        var editor = new RigEditor(character);
        int controllers = character.Controllers.Count;

        // CCameraTarget::CanDelete: the target's chain root is the camera, and it must go with it.
        var error = Assert.Throws<InvalidOperationException>(() => editor.DeleteControllers([target]));
        Assert.Contains(camera.Name!, error.Message);
        Assert.Equal(controllers, character.Controllers.Count);
        Assert.Contains(target, character.Controllers);
        Assert.Contains(Assert.IsType<Joint>(camera.HideJoint), character.Joints);
    }

    [Fact]
    public void AddToStack_BringsACameraRigIntoAnExistingStack()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var session = new PoseEditSession(new ScenePose(model), character);
        // The animation exists before the camera. The Add camera command
        // creates no tracks, so the three come from the hammer's add.
        var animation = session.CreateAnimation();
        var stack = Assert.Single(animation.Stacks);
        int tracks = stack.Tracks.Count();
        var camera = session.AddAnimatedCamera("Late Camera");
        Assert.Equal(tracks, stack.Tracks.Count());

        Assert.Equal(1, session.AddToStack(stack, [camera]));

        Assert.Equal(tracks + 3, stack.Tracks.Count());
        var cameraTrack = stack.TrackFor(camera)!;
        var hideTrack = stack.TrackFor(camera.HideJoint!)!;
        var lookerTrack = stack.TrackFor(camera.Target!)!;
        Assert.Equal(new[] { "CAnimTrackCamera", "CAnimTrackMRS", "CAnimTrackLooker_VK" },
            new[] { cameraTrack.ClassName, hideTrack.ClassName, lookerTrack.ClassName });
        Assert.Equal(stack.Keys.Count, cameraTrack.Keys.Count);
        Assert.Equal(stack.Keys.Count, hideTrack.Keys.Count);
        Assert.Equal(stack.Keys.Count, lookerTrack.Keys.Count);
        // The looker's keys already name the chain, as a new animation's do.
        Assert.All(lookerTrack.Keys.OfType<IKKey>(), key => Assert.Equal(2, key.ChainTransforms.Count));
        // Adding again changes nothing.
        Assert.Equal(0, session.AddToStack(stack, [camera]));
        Assert.Equal(tracks + 3, stack.Tracks.Count());
    }

    [SceneTheory, SceneSamples]
    public void CameraFov_InterpolatesBetweenKeys(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var camera = session.AddAnimatedCamera();
        var animation = session.CreateAnimation();
        var stack = Assert.Single(animation.Stacks);
        // Three stack keys: 0 and 10 authored, the end key (at 20) replays key 0.
        session.SetKeyTime(stack, 1, 20);
        session.CreateKey(stack, 10);

        Assert.Equal(1, session.SetFov(animation, [camera], 0, 30, out string? reason));
        Assert.Null(reason);
        Assert.Equal(1, session.SetFov(animation, [camera], 10, 60, out reason));
        Assert.Null(reason);

        Assert.Equal(30f, session.Evaluator.CameraFov(animation, camera, 0), 4);
        Assert.Equal(60f, session.Evaluator.CameraFov(animation, camera, 10), 4);
        // The FOV curve is the same cubic Hermite as the transform channels: the midpoint of a
        // symmetric two-key segment is the average.
        Assert.Equal(45f, session.Evaluator.CameraFov(animation, camera, 5), 3);
    }

    [SceneTheory, SceneSamples]
    public void CameraFov_MixesWithOpacity_AndIgnoresAdditiveStacks(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var camera = session.AddAnimatedCamera();
        var animation = session.CreateAnimation();
        var bottom = Assert.Single(animation.Stacks);

        // The bottom stack holds 20; a top stack at half opacity solves its own key (60) so that
        // the mixed FOV is 40: 20 * 0.5 + 60 * 0.5.
        Assert.Equal(1, session.SetFov(animation, [camera], 0, 20, out string? reason));
        var top = session.CreateStack(animation, [camera]);
        Assert.NotSame(bottom, top);
        session.SetStackOpacity(top, 0.5f);
        Assert.Equal(1, session.SetFov(animation, [camera], 0, 40, out reason));
        Assert.Equal(60f, ((CameraKey)top.TrackFor(camera)!.Keys[0]!).Fov, 3);
        session.Evaluator.Invalidate();
        Assert.Equal(40f, session.Evaluator.CameraFov(animation, camera, 0), 3);

        // An additive stack is skipped for FOV, even when it has an FOV key.
        session.SetStackAdditive(top, true);
        session.Evaluator.Invalidate();
        Assert.Equal(20f, session.Evaluator.CameraFov(animation, camera, 0), 3);
    }

    [SceneTheory, SceneSamples]
    public void AnimatedCamera_SceneRoundTripsSaveAndReload(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var camera = session.AddAnimatedCamera();
        var animation = session.CreateAnimation();
        var stack = Assert.Single(animation.Stacks);
        session.SetKeyTime(stack, 1, 20);
        session.CreateKey(stack, 10);
        session.SetFov(animation, [camera], 0, 35, out _);
        session.SetFov(animation, [camera], 10, 55, out _);
        camera.Ratio = 4;
        camera.Mode = 1;
        camera.NearClipPlane = 3;
        camera.FarClipPlane = 1234;

        string first = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-camera-{Guid.NewGuid():N}.akt");
        string second = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-camera-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(first);
            var reloaded = TestFiles.LoadScene(first);
            var reloadedCharacter = reloaded.Scene.Characters.First(c => c.Name == character.Name);
            var reloadedCamera = Assert.Single(reloadedCharacter.AnimatedCameras);
            Assert.Equal("Camera Animated", reloadedCamera.Name);
            Assert.Equal(1, reloadedCamera.Mode);
            Assert.Equal(4, reloadedCamera.Ratio);
            Assert.Equal(3f, reloadedCamera.NearClipPlane);
            Assert.Equal(1234f, reloadedCamera.FarClipPlane);
            // The camera's pose slot survives with the camera in the controller list.
            Assert.NotNull(reloadedCharacter.RefPose);
            Assert.Equal(character.RefPose!.Count, reloadedCharacter.RefPose!.Count);
            Assert.Contains(reloadedCamera, reloadedCharacter.Controllers);
            Assert.True(new PoseEvaluator(reloadedCharacter).IndexOf(reloadedCamera) >= 0);
            var reloadedAnimation = reloadedCharacter.Animations.First(a => a.Name == animation.Name);
            var track = reloadedAnimation.Stacks[0].TrackFor(reloadedCamera);
            Assert.NotNull(track);
            Assert.Equal("CAnimTrackCamera", track!.ClassName);
            var keys = track.Keys.OfType<CameraKey>().ToList();
            Assert.Equal(3, keys.Count);
            Assert.Equal(35f, keys[0].Fov, 4);
            Assert.Equal(55f, keys[1].Fov, 4);
            var evaluator = new PoseEvaluator(reloadedCharacter);
            Assert.Equal(45f, evaluator.CameraFov(reloadedAnimation, reloadedCamera, 5), 3);

            reloaded.Save(second);
            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }
        finally
        {
            if (File.Exists(first)) File.Delete(first);
            if (File.Exists(second)) File.Delete(second);
        }
    }

    // ---- the looker target's marker ------------------------------------------------------------

    /// <summary>
    /// An animated camera's target is drawn by the camera glyph's arrow, not by the looker disc
    /// Neither the camera nor
    /// its target gets a disc, a handle dot or pivot axes; the scene bounds still take them in.
    /// </summary>
    [Fact]
    public void AnAnimatedCamerasTarget_HasNoDiscMarker_TheArrowDrawsIt()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var camera = CameraEditor.AddAnimatedCamera(character, "Rig Camera");
        var target = Assert.IsType<CameraTarget>(camera.Target);
        Assert.True(CameraShapes.IsGlyphPart(camera));
        Assert.True(CameraShapes.IsGlyphPart(target));
        Assert.False(CameraShapes.IsGlyphPart(null));

        var pose = new ScenePose(model);
        pose.Update(character, null, 0);
        Assert.Empty(pose.Lookers);
        var at = pose.WorldOf(target).Translation;
        Assert.True(at.X >= pose.Bounds.Min.X - 1e-3f && at.X <= pose.Bounds.Max.X + 1e-3f,
            "the scene bounds left the camera's target out");
    }

    /// <summary>
    /// The glyph is <see cref="CameraShapes.SizeRatio"/> (5) scene target sizes, its arrow runs from
    /// the lens to the target point as a shaft a quarter of the lens wide with a head, and selecting
    /// lights only its own part: the camera lights the body, the target the arrow.
    /// </summary>
    [Fact]
    public void TheCameraGlyph_LightsOnlyTheSelectedPart_AndItsArrowReachesTheTarget()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var camera = CameraEditor.AddAnimatedCamera(character, "Rig Camera");
        var target = Assert.IsType<CameraTarget>(camera.Target);
        Assert.Equal(5f, CameraShapes.SizeRatio);
        Assert.Equal(CameraShapes.LensWidth / 4f, CameraShapes.ShaftWidth, 6);

        var tip = new Vector3(0, 0, -200f);
        var glyph = new CameraGlyph(camera, target, Matrix4x4.Identity, tip, 20f, false);
        var segments = CameraShapes.Segments(glyph).ToList();
        Assert.Contains(segments, s => s.Arrow);
        Assert.Contains(segments, s => !s.Arrow);
        Assert.Contains(segments, s => s.Arrow && (Vector3.Distance(s.A, tip) < 1e-3f || Vector3.Distance(s.B, tip) < 1e-3f));
        // The shaft is a tube, not a line: some arrow segment runs off the lens-to-tip axis.
        Assert.Contains(segments, s => s.Arrow && new Vector2(s.A.X, s.A.Y).Length() > 1e-3f);

        var eye = new Vector3(500, 500, 500);
        void Check(Object3D? selected, bool bodyLit, bool arrowLit)
        {
            float[] lines = CameraShapes.Lines([glyph], eye, selected, []);
            Assert.Equal(segments.Count * 2 * ScenePose.LineVertexFloats, lines.Length);
            for (int s = 0; s < segments.Count; s++)
            {
                bool lit = segments[s].Arrow ? arrowLit : bodyLit;
                float red = lines[s * 2 * ScenePose.LineVertexFloats + 3];
                Assert.Equal(lit ? CameraShapes.SelectedColor.X : CameraShapes.DefaultColor.X, red, 4);
            }
        }
        Check(null, false, false);
        Check(camera, true, false);
        Check(target, false, true);
    }

    /// <summary>
    /// The disc faces the viewer: every vertex lies in the plane whose normal is the direction to
    /// the eye, the rim at <see cref="LookerMarker.Radius"/> and the centre on the marker, in
    /// <c>CLooker::GetColor</c>'s grey - white when the target is selected.
    /// </summary>
    [Fact]
    public void TheMarker_DrawsAViewFacingDiscAroundThePoint()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var camera = CameraEditor.AddAnimatedCamera(character, "Rig Camera");
        var target = Assert.IsType<CameraTarget>(camera.Target);
        var point = new Vector3(10f, 20f, 30f);
        var eye = point + new Vector3(0f, 0f, 50f);
        var marker = new LookerMarker(target, point, 1.2f);

        float[] plain = LookerShapes.Discs([marker], eye, null);
        Assert.Equal(LookerShapes.Steps * 3 * ScenePose.LineVertexFloats, plain.Length);
        var normal = Vector3.Normalize(eye - point);
        int rim = 0, centre = 0;
        for (int v = 0; v < plain.Length; v += ScenePose.LineVertexFloats)
        {
            var p = new Vector3(plain[v], plain[v + 1], plain[v + 2]);
            Assert.True(MathF.Abs(Vector3.Dot(p - point, normal)) < 1e-3f, "a vertex left the disc's plane");
            float d = Vector3.Distance(p, point);
            if (d < 1e-4f) centre++;
            else
            {
                Assert.Equal(1.2f, d, 3);
                rim++;
            }
            Assert.Equal(0xB8 / 255f, plain[v + 3], 4);
        }
        Assert.Equal(2 * LookerShapes.Steps, rim);
        Assert.Equal(LookerShapes.Steps, centre);

        // Selected: white, with the same geometry.
        float[] selected = LookerShapes.Discs([marker], eye, target);
        Assert.Equal(plain.Length, selected.Length);
        Assert.All(Enumerable.Range(0, selected.Length / ScenePose.LineVertexFloats),
            i => Assert.Equal(1f, selected[i * ScenePose.LineVertexFloats + 3], 4));

        // A marker on the eye itself has no facing and is left out.
        Assert.Empty(LookerShapes.Discs([marker], point, null));
    }

    // ---- dolly in without stalling, clip planes that keep the scene --------------------------------

    /// <summary>
    /// A user report: after a long dolly in, zooming barely moved and the scene was clipped
    /// as if too far away. Zooming in now never stalls (below <see cref="OrbitCamera.MinDistance"/>
    /// the target moves forward with the eye), and the far plane always reaches past the scene.
    /// </summary>
    [Fact]
    public void OrbitCamera_DollyInKeepsMoving_AndTheFarPlaneKeepsTheScene()
    {
        var camera = new OrbitCamera { Target = Vector3.Zero, Distance = 500f };
        camera.SceneBounds = (new Vector3(-100), new Vector3(100));
        float radius = new Vector3(200).Length() / 2;
        Assert.Equal(radius * 0.02f, camera.MinDistance, 3);

        var lastEye = camera.Eye;
        for (int i = 0; i < 400; i++)
        {
            camera.Zoom(0.9f);
            var eye = camera.Eye;
            Assert.True(Vector3.Distance(eye, lastEye) >= camera.MinDistance * 0.1f * 0.99f,
                $"step {i} moved the eye only {Vector3.Distance(eye, lastEye)}");
            Assert.True(camera.Distance >= camera.MinDistance - 1e-4f);
            float toFarSide = Vector3.Distance(eye, Vector3.Zero) + radius;
            Assert.True(camera.FarPlane >= toFarSide, $"step {i}: far {camera.FarPlane} < {toFarSide}");
            lastEye = eye;
        }

        // Zooming out from the minimum grows the distance again.
        float d = camera.Distance;
        camera.Zoom(1.5f);
        Assert.Equal(d * 1.5f, camera.Distance, 3);

        // Bad factors are ignored.
        d = camera.Distance;
        camera.Zoom(0f);
        camera.Zoom(float.NaN);
        Assert.Equal(d, camera.Distance);
    }

    // ---- the camera view window's placement ---------------------------------------------------------

    [Fact]
    public void FreeSpotNearTopLeft_TakesTheCornerOrTheNearestSpotBesideThePanels()
    {
        // An empty view: the corner.
        Assert.Equal((8d, 8d), WindowPlacementStore.FreeSpotNearTopLeft(1000, 800, [], 300, 200, 8));

        // A toolbar across the top-left: below it (y 58) is nearer than right of it (x 418).
        var bar = (0d, 0d, 400d, 50d);
        Assert.Equal((8d, 58d), WindowPlacementStore.FreeSpotNearTopLeft(1000, 800, [bar], 300, 200, 8));

        // A tall panel down the left side as well: right of it, under the toolbar.
        var side = (0d, 0d, 120d, 790d);
        Assert.Equal((128d, 58d), WindowPlacementStore.FreeSpotNearTopLeft(1000, 800, [bar, side], 300, 200, 8));

        // Nothing fits: the corner.
        Assert.Equal((8d, 8d), WindowPlacementStore.FreeSpotNearTopLeft(200, 100, [bar], 300, 200, 8));
    }

    [Fact]
    public void WindowPlacementStore_RemembersAPlacementAcrossLoads()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-placements-{Guid.NewGuid():N}", WindowPlacementStore.FileName);
        try
        {
            var store = new WindowPlacementStore(path);
            Assert.Null(store.Get("camera-view"));
            store.Set("camera-view", new WindowPlacement(40, 60, 480, true));
            Assert.Null(store.LastError);

            var again = new WindowPlacementStore(path);
            Assert.Equal(new WindowPlacement(40, 60, 480, true), again.Get("camera-view"));

            // A damaged file reads as empty instead of failing.
            File.WriteAllText(path, "{ not json");
            Assert.Null(new WindowPlacementStore(path).Get("camera-view"));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch (IOException) { }
        }
    }

    // ---- the orbit camera's world transform, which a stored point of view needs -----------------

    /// <summary>
    /// <see cref="OrbitCamera.WorldMatrix"/> and <see cref="OrbitCamera.Restore"/> are the conversion
    /// the blendshape camera toggle uses both ways
    /// a camera restored from its own world matrix and distance lands on the same eye and
    /// target, and one restored from a stored look-at view keeps that eye and target.
    /// </summary>
    [Fact]
    public void OrbitCamera_RestoreReproducesTheEyeAndTargetOfItsWorldMatrix()
    {
        var camera = new OrbitCamera
        {
            Target = new Vector3(2f, 3f, -4f),
            Distance = 12.5f,
            Yaw = 0.7f,
            Pitch = -0.4f,
            FieldOfView = MathF.PI / 3,
        };
        var world = camera.WorldMatrix;
        Vector3 eye = camera.Eye, target = camera.Target;
        Assert.True((eye - world.Translation).Length() < 1e-4f, $"the eye is the translation: {world.Translation} vs {eye}");
        Assert.True((target - BlendshapePointOfView.TargetOf(world, camera.Distance)).Length() < 1e-4f);

        var restored = OrbitCamera.FromWorldMatrix(world, camera.Distance);
        Assert.True((eye - restored.Eye).Length() < 1e-4f, $"eye {restored.Eye} vs {eye}");
        Assert.True((target - restored.Target).Length() < 1e-4f, $"target {restored.Target} vs {target}");
        Assert.Equal(camera.Distance, restored.Distance, 4);
        // The field of view is not part of a stored point of view: it is left at the default.
        Assert.Equal(new OrbitCamera().FieldOfView, restored.FieldOfView, 4);

        // A stored point of view (a look-at camera) comes back with the eye and target it was made from.
        var viewEye = new Vector3(30f, 40f, 50f);
        var viewTarget = new Vector3(1f, 2f, 3f);
        var pointOfView = BlendshapePointOfView.FromLookAt(viewEye, viewTarget, Vector3.UnitY);
        var fromPov = OrbitCamera.FromWorldMatrix(pointOfView, Vector3.Distance(viewEye, viewTarget));
        Assert.True((viewEye - fromPov.Eye).Length() < 1e-3f, $"eye {fromPov.Eye} vs {viewEye}");
        Assert.True((viewTarget - fromPov.Target).Length() < 1e-3f, $"target {fromPov.Target} vs {viewTarget}");
        // And the toggle's way back is exact.
        var back = OrbitCamera.FromWorldMatrix(fromPov.WorldMatrix, fromPov.Distance);
        Assert.True((viewEye - back.Eye).Length() < 1e-4f);
        Assert.True((viewTarget - back.Target).Length() < 1e-4f);
    }

    // ---- the rest pose (the rigging context) ---------------------------------------------------

    /// <summary>
    /// The ref-pose access refuses a controller 3D: the looker target's TRANSFORM rows are
    /// disabled in the Rigging context
    /// and only <c>CCameraAnimated</c>
    /// overrides the refusal. The camera itself is editable there.
    /// </summary>
    [Fact]
    public void RestPose_RefusesTheLookerTargetButNotItsCamera()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var session = new PoseEditSession(new ScenePose(model) { RiggingContext = true }, character);
        var camera = session.AddAnimatedCamera("Rig Camera");
        var target = Assert.IsType<CameraTarget>(camera.Target);
        var rest = session.RestPose;

        Assert.True(rest.CanEdit(camera, out _));
        Assert.False(rest.CanEdit(target, out string? reason));
        Assert.Contains("controller", reason);
        Assert.Null(session.BeginRestPose(target, out string? blocked));
        Assert.Contains("controller", blocked);
        Assert.NotNull(session.BeginRestPose(camera, out _));
    }

    /// <summary>
    /// Moving the animated camera in the rest pose re-derives its looker target, as the
    /// rest-pose edit does
    /// left the target at 4,993.118 - 40 units along the camera's -Z - with the camera's rotation,
    /// and the hidden joint's own (0,0,-40) alone).
    /// </summary>
    [Fact]
    public void RestPose_CameraDragMovesTheLookerTargetToTheLookPoint()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var session = new PoseEditSession(new ScenePose(model) { RiggingContext = true }, character);
        var camera = session.AddAnimatedCamera("Rig Camera");
        var target = Assert.IsType<CameraTarget>(camera.Target);
        var hide = Assert.IsType<Joint>(camera.HideJoint);
        var rest = session.RestPose;
        var cameraBefore = rest.GetLocal(camera);
        var jointBefore = rest.GetLocal(hide);

        var drag = session.BeginRestPose(camera, out string? reason);
        Assert.Null(reason);
        Assert.NotNull(drag);
        drag!.Translate(0, 5_000f);
        drag.Commit();

        var cameraLocal = rest.GetLocal(camera);
        Assert.True(MathF.Abs(cameraLocal.Translation.X - cameraBefore.Translation.X) > 1_000f,
            "the camera's rest pose did not move");
        var lookPoint = Matrix4x4.CreateTranslation(0f, 0f, -40f) * cameraLocal.ToMatrix();
        Assert.True(AktMath.MaxDifference(lookPoint, rest.GetLocal(target).ToMatrix()) < 1e-3f,
            $"the target's rest {rest.GetLocal(target).Translation} is not the camera's look point {lookPoint.Translation}");
        Assert.True(MathF.Abs(Quaternion.Dot(cameraLocal.Rotation, rest.GetLocal(target).Rotation)) > 0.9999f,
            "the target's rest rotation is not the camera's");
        Assert.True(AktMath.MaxDifference(jointBefore.ToMatrix(), rest.GetLocal(hide).ToMatrix()) < 1e-6f,
            "the hidden joint's own rest moved");
    }
}







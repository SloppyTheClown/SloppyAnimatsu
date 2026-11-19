using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Skeleton editing: creating, inserting, deleting, reparenting and
/// orienting joints keeps the character's controller arrays, every pose, the <c>__RigAnim</c> tracks
/// and the skinned meshes consistent, and the edited scene saves and reloads byte-exact.
/// </summary>
public class RigEditingTests
{
    // ---- creating -----------------------------------------------------------------------------

    [SceneTheory, SceneSamples]
    public void CreateJoint_AppendsTheControllerPoseSlotsAndRigTrack(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Setup(model) is not var (character, editor)) return;
        var poses = editor.Poses();
        int joints = character.Joints.Count;
        int controllers = character.Controllers.Count;
        var matrices = poses.Select(p => p.Count).ToArray();
        var rigStack = character.RigAnimation?.Stacks.FirstOrDefault();
        int tracks = rigStack?.Tracks.Count() ?? 0;
        var local = Matrix4x4.CreateTranslation(1, 2, 3);

        var joint = editor.CreateJoint(character, "TestBone", local);

        Assert.Equal("TestBone", joint.Name);
        Assert.Equal(RigEditor.NewControllerFlags, joint.Flags);
        Assert.Same(character, joint.Ancestor<Character>());
        Assert.Equal(joints + 1, character.Joints.Count);
        Assert.Equal(controllers + 1, character.Controllers.Count);
        Assert.Same(joint, character.Controllers[controllers]);
        Assert.Same(joint, character.Joints[joints]);
        Assert.Equal(controllers, editor.SlotOf(joint));
        for (int i = 0; i < poses.Count; i++)
        {
            Assert.Equal(matrices[i] + 1, poses[i].Count);
            Assert.True(AktMath.MaxDifference(local, poses[i].Matrix(controllers)) < 1e-5);
        }
        if (rigStack != null)
        {
            var track = Assert.IsType<AnimTrack>(rigStack.TrackFor(joint));
            Assert.Equal(tracks + 1, rigStack.Tracks.Count());
            Assert.Equal(rigStack.Keys.Count, track.Keys.Count);
            Assert.All(track.Keys, k => Assert.Equal(Transform.Identity, Assert.IsType<TransformKey>(k).Value));
        }
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void CreateJoint_NumbersRepeatedDefaultNames(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Setup(model) is not var (character, editor)) return;

        var first = editor.CreateJoint(character);
        var second = editor.CreateJoint(character);

        Assert.Equal(RigEditor.DefaultJointName, first.Name);
        Assert.Equal("Joint_02", second.Name);
        Assert.Equal("Joint_03", RigEditor.UniqueControllerName(character, RigEditor.DefaultJointName));
        Assert.Equal("RigTestName", RigEditor.UniqueControllerName(character, "RigTestName"));
    }

    [SceneTheory, SceneSamples]
    public void CreateJointAt_PutsTheJointAtTheWorldPoint(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Setup(model) is not var (character, editor)) return;
        if (FindJoint(character, j => j.Parent3D != null) is not { } parent) return;
        var pose = editor.RigPose!;
        var start = editor.WorldInPose(pose, parent).Translation;
        var target = start + new Vector3(4, 5, 6) * Scale(pose, editor, parent);

        var joint = editor.CreateJointAt(parent, target, "TestAt");

        var world = editor.WorldInPose(pose, joint);
        Assert.True(Vector3.Distance(world.Translation, target) < 0.1f,
            $"{world.Translation} != {target}");
        Assert.Same(parent, joint.Parent3D);
        var parentWorld = editor.WorldInPose(pose, parent);
        var parentRow = new Vector3(parentWorld.M11, parentWorld.M12, parentWorld.M13);
        var jointRow = new Vector3(world.M11, world.M12, world.M13);
        Assert.True(Vector3.Dot(Vector3.Normalize(parentRow), Vector3.Normalize(jointRow)) > 0.9999f,
            "the new joint keeps the parent's orientation");
        // A joint that compensates parent scale does not inherit the parent's basis lengths.
        if (!joint.CompensatesParentScale) Assert.Equal(parentRow.Length(), jointRow.Length(), 3);
    }

    // ---- inserting ----------------------------------------------------------------------------

    [SceneTheory, SceneSamples]
    public void InsertJoint_KeepsTheSelectedJointsWorldInEveryPose(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Setup(model) is not var (character, editor)) return;
        if (FindJoint(character, j => j.Parent3D is Joint && j.Parent is Joint) is not { } joint) return;
        var oldParent = joint.Parent!;
        int oldIndex = oldParent.Source.Children.IndexOf(joint.Source);
        var all = character.Controllers.ToList();
        var worlds = editor.Poses().Select(p => editor.WorldInPose(p, joint)).ToArray();
        var worldsAll = editor.Poses().Select(p => all.Select(c => editor.WorldInPose(p, c)).ToArray()).ToList();

        var inserted = editor.InsertJoint(joint, "TestMid");

        Assert.Same(inserted, joint.Parent);
        Assert.Same(oldParent, inserted.Parent);
        Assert.Equal(oldIndex, oldParent.Source.Children.IndexOf(inserted.Source));
        var poses = editor.Poses();
        for (int i = 0; i < poses.Count; i++)
        {
            var now = editor.WorldInPose(poses[i], joint);
            Assert.True(AktMath.MaxDifference(worlds[i], now) < 1e-3,
                $"pose {i}: {worlds[i].Translation} -> {now.Translation}");
            for (int c = 0; c < all.Count; c++)
                Assert.True(AktMath.MaxDifference(worldsAll[i][c], editor.WorldInPose(poses[i], all[c])) < 1e-3,
                    $"pose {i}: {all[c].Name} moved");
        }
        AssertRoundtrip(model);
    }

    // ---- reparenting --------------------------------------------------------------------------

    [SceneTheory, SceneSamples]
    public void ParentAndUnparent_KeepTheWorldAndRefuseConstraints(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Setup(model) is not var (character, editor)) return;
        if (FreePair(character) is not var (joint, newParent)) return;
        var pose = editor.RigPose!;
        var before = editor.WorldInPose(pose, joint);

        Assert.Equal(1, editor.Parent([joint], newParent));
        Assert.Same(newParent, joint.Parent3D);
        Assert.True(AktMath.MaxDifference(before, editor.WorldInPose(pose, joint)) < 1e-3);

        Assert.Equal(1, editor.Unparent([joint]));
        Assert.Same(character, joint.Parent);
        Assert.True(AktMath.MaxDifference(before, editor.WorldInPose(pose, joint)) < 1e-3);

        var constrained = character.Controllers.OfType<ReverseFootLocator>().FirstOrDefault(l => l.Constraint != null);
        if (constrained != null)
            Assert.Throws<InvalidOperationException>(() => editor.Unparent([constrained]));
    }

    // ---- Properties window and shoe locators --------------------------------------------------

    [SceneTheory, SceneSamples]
    public void PropertiesEdits_AndShoeLocatorsShowOnlyWhenAsked(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Setup(model) is not var (character, _)) return;

        var joint = character.Joints.First();
        uint flags = joint.Flags;
        joint.CompensatesParentScale = false;
        Assert.Equal(flags & ~6u, joint.Flags);
        joint.CompensatesParentScale = true;
        Assert.Equal((flags & ~6u) | 2u, joint.Flags);

        if (character.Controllers.OfType<ControllerIK>().FirstOrDefault(c => c.HasTargetRenderSizeRatio) is { } ik)
        {
            ik.TargetRenderSizeRatio = 2.5f;
            Assert.Equal(2.5f, ik.TargetRenderSizeRatio);
        }

        var pose = new ScenePose(model);
        int locators = pose.Evaluator(character).Controllers.Count(c => c is ReverseFootLocator { IsSolePivot: true } && c.IsShown);
        pose.Update(character, null, 0);
        Assert.Empty(pose.ShoeMarkers);
        pose.ShowShoeLocators = true;
        pose.Update(character, null, 0);
        // A plus is three lines, two vertices each.
        Assert.Equal(locators * 6 * ScenePose.LineVertexFloats, pose.ShoeMarkers.Length);
        Assert.All(pose.ShoeMarkers, v => Assert.True(float.IsFinite(v)));
    }

    [SceneTheory, SceneSamples]
    public void Reparent_KeepsTheRigPoseWorldAndRewritesLtm(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Setup(model) is not var (character, editor)) return;
        if (FreePair(character) is not var (joint, newParent)) return;
        var pose = editor.RigPose!;
        var before = editor.WorldInPose(pose, joint);
        joint.LocalMatrix = Matrix4x4.CreateTranslation(9, 9, 9);

        editor.Reparent([joint], newParent);

        Assert.True(AktMath.MaxDifference(before, editor.WorldInPose(pose, joint)) < 1e-3);
        Assert.True(AktMath.MaxDifference(joint.LocalMatrix, Matrix4x4.CreateTranslation(9, 9, 9)) > 1e-3);
    }

    // ---- deleting -----------------------------------------------------------------------------

    [SceneTheory, SceneSamples]
    public void DeleteJoint_RemovesSlotsTracksAndKeepsTheChildren(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Setup(model) is not var (character, editor)) return;
        var joint = character.Joints.FirstOrDefault(j =>
            j.Constraint == null && j.Children.OfType<Joint>().Any() &&
            !character.IKControllers.Any(ik => ik.Chain.Contains(j) || ik.ChainParents.Contains(j)));
        if (joint == null) return;
        var children = joint.Children.OfType<Joint>().ToList();
        var worlds = editor.Poses().ToList()
            .Select(p => children.Select(c => editor.WorldInPose(p, c)).ToArray()).ToList();
        var poses = editor.Poses();
        var counts = poses.Select(p => p.Count).ToArray();
        int joints = character.Joints.Count;
        int controllers = character.Controllers.Count;
        int slot = editor.SlotOf(joint);

        Assert.Equal(1, editor.DeleteJoints([joint]));

        Assert.Equal(joints - 1, character.Joints.Count);
        Assert.Equal(controllers - 1, character.Controllers.Count);
        Assert.DoesNotContain(joint, character.Controllers);
        for (int i = 0; i < poses.Count; i++)
        {
            Assert.Equal(counts[i] - 1, poses[i].Count);
            for (int c = 0; c < children.Count; c++)
            {
                Assert.Same(character, children[c].Parent);
                Assert.True(AktMath.MaxDifference(worlds[i][c], editor.WorldInPose(poses[i], children[c])) < 1e-3);
            }
        }
        foreach (var animation in character.Animations.Append(character.RigAnimation).OfType<Animation>())
            foreach (var stack in animation.Stacks)
                Assert.DoesNotContain(stack.Tracks, t => t.Target == joint);
        AssertWeightsAreValid(character);
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void DeleteJoint_TakesItsControllerAndClearsTheOtherConstraints(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Setup(model) is not var (character, editor)) return;
        if (character.Controllers.OfType<Controller3D>().FirstOrDefault() is not { } controller) return;
        if (FindJoint(character, j => j.Constraint == null && !j.Descendants<Joint>().Any()) is not { } joint) return;
        joint.Source.Set("Constraint", new AktReference(controller.Source));
        var chain = character.Joints.Where(j => ReferenceEquals(j.Constraint, controller)).ToList();
        int controllers = character.Controllers.Count;
        var slotSet = character.Controllers.ToHashSet(ReferenceEqualityComparer.Instance);
        int shoes = Outliner.Subtree(controller).Count(slotSet.Contains);

        // The predicate deletes the controller with the joint (open-questions 24); every joint
        // that stays loses the reference, and the controller's shoe goes with it.
        Assert.Equal(1, editor.DeleteJoints([joint]));

        Assert.DoesNotContain(joint, character.Joints);
        Assert.DoesNotContain(controller, character.Controllers);
        Assert.Equal(controllers - 1 - shoes, character.Controllers.Count);
        Assert.All(chain.Where(j => !ReferenceEquals(j, joint)), j => Assert.Null(j.Constraint));
        AssertRoundtrip(model);
    }

    // ---- orienting ----------------------------------------------------------------------------

    [SceneTheory, SceneSamples]
    public void OrientJoints_PointsTheAimAxisAtTheChildAndKeepsTheChildren(string path)
    {
        var model = TestFiles.LoadScene(path);
        Outliner.ShowAll(model.Scene);
        if (Setup(model) is not var (character, editor)) return;
        var pose = editor.RigPose!;
        // A joint whose child sits exactly on it has no direction to aim at, and OrientJoints
        // leaves such a joint alone; pick one whose child really is somewhere else.
        Joint? joint = null;
        Joint? child = null;
        foreach (var candidate in character.Joints)
        {
            if (candidate.Constraint != null) continue;
            var first = candidate.Children.OfType<Joint>().FirstOrDefault(c => c.Parent3D == candidate);
            if (first == null) continue;
            if (Vector3.Distance(editor.WorldInPose(pose, candidate).Translation,
                    editor.WorldInPose(pose, first).Translation) < 1e-4f) continue;
            joint = candidate;
            child = first;
            break;
        }
        if (joint == null || child == null) return;
        var beforeWorld = editor.WorldInPose(pose, joint);
        var beforeChildWorld = editor.WorldInPose(pose, child);
        var beforeChildLocal = editor.LocalInPose(pose, child);
        var beforeLocal = editor.LocalInPose(pose, joint);
        var direction = Vector3.Normalize(beforeChildWorld.Translation - beforeWorld.Translation);
        float scaleY = new Vector3(beforeWorld.M21, beforeWorld.M22, beforeWorld.M23).Length();

        Assert.Equal(1, editor.OrientJoints([joint]));

        var afterWorld = editor.WorldInPose(pose, joint);
        var afterLocal = editor.LocalInPose(pose, joint);
        var afterChildWorld = editor.WorldInPose(pose, child);
        var y = Vector3.Normalize(new Vector3(afterWorld.M21, afterWorld.M22, afterWorld.M23));
        Assert.True(Vector3.Dot(y, direction) > 0.9999f, $"{y} vs {direction}");
        Assert.True(Vector3.Distance(beforeWorld.Translation, afterWorld.Translation) < 1e-3);
        Assert.True(Vector3.Distance(beforeLocal.Translation, afterLocal.Translation) < 1e-5);
        float afterScaleY = new Vector3(afterWorld.M21, afterWorld.M22, afterWorld.M23).Length();
        Assert.True(Math.Abs(scaleY - afterScaleY) < 1e-3, $"scale {scaleY} -> {afterScaleY}");
        Assert.True(AktMath.MaxDifference(beforeChildWorld, afterChildWorld) < 1e-3);
        Assert.True(AktMath.MaxDifference(beforeChildLocal, editor.LocalInPose(pose, child)) > 1e-6);

        // A second orient is a no-op.
        editor.OrientJoints([joint]);
        Assert.True(AktMath.MaxDifference(afterLocal, editor.LocalInPose(pose, joint)) < 1e-5,
            $"second orient differs by {AktMath.MaxDifference(afterLocal, editor.LocalInPose(pose, joint))}");

        // A leaf has nothing to aim at. OrientJoints aims at any controller child, so the leaf must
        // have no controller below it at all, not merely no joint.
        var leaf = character.Joints.FirstOrDefault(j => j.Constraint == null
            && !j.Children.OfType<Object3D>().Any(c => character.Controllers.Contains(c)));
        if (leaf != null) Assert.Equal(0, editor.OrientJoints([leaf]));
        AssertRoundtrip(model);
    }

    // ---- LTM ----------------------------------------------------------------------------------

    [SceneTheory, SceneSamples]
    public void SyncLtm_CopiesTheBindPoseSlot(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Setup(model) is not var (character, editor)) return;
        var bind = character.BindPoses.FirstOrDefault() ?? character.RefPose;
        if (bind == null || character.Joints.Count == 0) return;
        var joint = character.Joints.FirstOrDefault(j => editor.SlotOf(j) is var s && s >= 0 && s < bind.Count);
        if (joint == null) return;
        joint.LocalMatrix = Matrix4x4.CreateTranslation(99, 98, 97);

        editor.SyncLtm([joint]);

        Assert.True(AktMath.MaxDifference(bind.Matrix(editor.SlotOf(joint)), joint.LocalMatrix) < 1e-5);
    }

    // ---- interactive placement -----------------------------------------------------------------

    [SceneTheory, SceneSamples]
    public void PlaceJoint_AChainTipIsTheParentAndTheSlotIsTheClickInItsSpace(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Setup(model) is not var (character, editor)) return;
        var pose = editor.RigPose!;
        var tip = character.Joints.FirstOrDefault(j => !j.Children.OfType<Joint>().Any() && editor.SlotOf(j) >= 0);
        if (tip == null) return;
        var tipWorld = editor.WorldInPose(pose, tip);
        var point = tipWorld.Translation + new Vector3(1, 2, 3) * Scale(pose, editor, tip);

        var joint = editor.PlaceJoint(tip, point, "PlacedTip", JointAlignment.NoAlign);

        Assert.Same(tip, joint.Parent3D);
        Assert.True(Vector3.Distance(editor.WorldInPose(pose, joint).Translation, point) < 1e-2f,
            $"{editor.WorldInPose(pose, joint).Translation} != {point}");
        // The working matrix and the parent's frame are the same here, so the slot is the click in
        // the tip's space: no rotation and no scale.
        var local = joint.LocalMatrix;
        Assert.True(AktMath.MaxDifference(local, Matrix4x4.CreateTranslation(local.Translation)) < 1e-3f,
            "the slot is rotation-free and unit-scaled");
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void PlaceJoint_OtherSelectionsUseTheirWorldUnderTheCharacter(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Setup(model) is not var (character, editor)) return;
        var pose = editor.RigPose!;
        Object3D selected = character.Meshes.FirstOrDefault(m => m.Skinned) ?? (Object3D)character;
        var characterWorld = editor.WorldInPose(pose, character);
        var selectedWorld = editor.WorldInPose(pose, selected);
        var point = characterWorld.Translation + new Vector3(2, 3, 4) * Scale(pose, editor, character);

        var joint = editor.PlaceJoint(selected, point, "PlacedRoot");

        Assert.Same(character, joint.Parent3D);
        // The working matrix is the selected object's world with the click as its translation, and
        // the stored slot is that in the character's frame.
        var working = selectedWorld;
        working.Translation = point;
        var expected = Matrix4x4.Invert(characterWorld, out var inverse) ? working * inverse : working;
        Assert.True(AktMath.MaxDifference(joint.LocalMatrix, expected) < 1e-3f);
        Assert.True(Vector3.Distance(editor.WorldInPose(pose, joint).Translation, point) < 1e-2f);
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void PlaceJoint_WithoutASelectionStartsFromTheIdentity(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Setup(model) is not var (character, editor)) return;
        var pose = editor.RigPose!;
        var characterWorld = editor.WorldInPose(pose, character);
        var point = characterWorld.Translation + new Vector3(1, 1, 1) * Scale(pose, editor, character);

        var joint = editor.PlaceJoint(null, point, "PlacedNothing");

        Assert.Same(character, joint.Parent3D);
        var expected = Matrix4x4.Invert(characterWorld, out var inverse)
            ? Matrix4x4.CreateTranslation(point) * inverse
            : Matrix4x4.CreateTranslation(point);
        Assert.True(AktMath.MaxDifference(joint.LocalMatrix, expected) < 1e-3f);
        Assert.True(Vector3.Distance(editor.WorldInPose(pose, joint).Translation, point) < 1e-2f);
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void PlaceInsertedJoint_InsertsAboveAtThePointAndKeepsTheWorld(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Setup(model) is not var (character, editor)) return;
        var pose = editor.RigPose!;
        var child = FindJoint(character, j => j.Parent3D is Joint && editor.SlotOf(j) >= 0);
        if (child?.Parent3D is not Joint host) return;
        var beforeWorld = editor.WorldInPose(pose, child);
        var point = Vector3.Lerp(editor.WorldInPose(pose, host).Translation, beforeWorld.Translation, 0.3f);

        var inserted = editor.PlaceInsertedJoint(child, point, "PlacedInsert");

        Assert.Same(host, inserted.Parent3D);
        Assert.Same(inserted, child.Parent3D);
        Assert.True(Vector3.Distance(editor.WorldInPose(pose, inserted).Translation, point) < 1e-2f);
        Assert.True(AktMath.MaxDifference(editor.WorldInPose(pose, child), beforeWorld) < 1e-3f);
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void PlacementParent_IsTheChainTipOrTheCharacter(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Setup(model) is not var (character, editor)) return;
        var tip = FindJoint(character, j => !j.Children.OfType<Joint>().Any());
        var branch = FindJoint(character, j => j.Children.OfType<Joint>().Any());
        if (tip != null) Assert.Same(tip, editor.PlacementParent(tip));
        if (branch != null) Assert.Same(character, editor.PlacementParent(branch));
        Assert.Same(character, editor.PlacementParent(null));
        var mesh = character.Meshes.FirstOrDefault();
        if (mesh != null) Assert.Same(character, editor.PlacementParent(mesh));
    }

    // ---- the axis enum and the align-on-create preference (26-10-23) -------------------------

    [Fact]
    public void JointAlignment_MapsTheSpinnerValuesAndTheFlags()
    {
        // The shipped file: JointAim = 2, JointUp = 1, Flags = 4. The index order is X, Z, Y, so
        // the shipped values are Y as the aim and Z as the up axis, and Create Joint aligns.
        Assert.Equal(JointAxis.Y, JointAlignment.Default.Aim);
        Assert.Equal(JointAxis.Z, JointAlignment.Default.Up);
        Assert.True(JointAlignment.Default.AlignOnCreate);
        Assert.Equal(0, (int)JointAxis.X);
        Assert.Equal(1, (int)JointAxis.Z);
        Assert.Equal(2, (int)JointAxis.Y);
        Assert.Equal(new JointAlignment(JointAxis.Y, JointAxis.Z), JointAlignment.FromIndices(2, 1));
        Assert.Equal(new JointAlignment(JointAxis.Z, JointAxis.X, true, false, false),
            JointAlignment.FromIndices(4, 0, alignOnCreate: false));
        Assert.Equal(2, JointAlignment.Default.AimIndex); // Y = 2
        Assert.Equal(1, JointAlignment.Default.UpIndex); // Z = 1
        Assert.Equal(5, new JointAlignment(JointAxis.Y, JointAxis.Z, AimNegative: true).AimIndex);
        Assert.Equal(6, new JointAlignment(JointAxis.X, JointAxis.Y, false, true, true).Flags); // 2 + 4
        Assert.Equal(new JointAlignment(JointAxis.Y, JointAxis.Z, true, true, true),
            JointAlignment.FromPreferences(2, 1, 7));
        Assert.Equal(new JointAlignment(JointAxis.Y, JointAxis.Z, true, true, true),
            JointAlignment.FromSpinnerIndices(5, 4, 7));
    }

    [SceneTheory, SceneSamples]
    public void OrientJoints_UsesTheConfiguredAimAndUpAxes(string path)
    {
        var model = TestFiles.LoadScene(path);
        Outliner.ShowAll(model.Scene);
        if (Setup(model) is not var (character, editor)) return;
        var pose = editor.RigPose!;
        Joint? joint = null;
        Joint? child = null;
        foreach (var candidate in character.Joints)
        {
            if (candidate.Constraint != null) continue;
            var first = candidate.Children.OfType<Joint>().FirstOrDefault(c => c.Parent3D == candidate);
            if (first == null) continue;
            if (Vector3.Distance(editor.WorldInPose(pose, candidate).Translation,
                    editor.WorldInPose(pose, first).Translation) < 1e-4f) continue;
            joint = candidate;
            child = first;
            break;
        }
        if (joint == null || child == null) return;
        var childWorld = editor.WorldInPose(pose, child);
        var jointWorld = editor.WorldInPose(pose, joint);

        // Aim X, up Z: the first basis row points at the child.
        Assert.Equal(1, editor.OrientJoints([joint], new JointAlignment(JointAxis.X, JointAxis.Z)));
        var after = editor.WorldInPose(pose, joint);
        var direction = Vector3.Normalize(childWorld.Translation - jointWorld.Translation);
        var x = Vector3.Normalize(new Vector3(after.M11, after.M12, after.M13));
        Assert.True(Vector3.Dot(x, direction) > 0.9999f, $"x {x} vs {direction}");
        // The up axis is orthogonal to it.
        var z = Vector3.Normalize(new Vector3(after.M31, after.M32, after.M33));
        Assert.True(Math.Abs(Vector3.Dot(x, z)) < 1e-3f, $"x·z = {Vector3.Dot(x, z)}");

        // A negative aim puts the same axis against the child.
        editor.OrientJoints([joint], new JointAlignment(JointAxis.Y, JointAxis.Z, AimNegative: true));
        after = editor.WorldInPose(pose, joint);
        var y = Vector3.Normalize(new Vector3(after.M21, after.M22, after.M23));
        Assert.True(Vector3.Dot(y, direction) < -0.9999f, $"y {y} vs {direction}");
        Assert.True(AktMath.MaxDifference(childWorld, editor.WorldInPose(pose, child)) < 1e-3);
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void OrientJoints_KeepsEveryWorldOnAThreeJointChain(string path)
    {
        var model = TestFiles.LoadScene(path);
        Outliner.ShowAll(model.Scene);
        if (Setup(model) is not var (character, editor)) return;
        var pose = editor.RigPose!;
        // A straight diagonal chain, so every joint's aim axis (the preference's local Y) has to
        // turn by the same angle: the AutoRigger's phalanx is exactly this shape (hand -> phalanx
        // 1 -> 2 -> 3 at the tip hot point).
        var root = character.Joints.First(j => editor.SlotOf(j) >= 0);
        var origin = editor.WorldInPose(pose, root).Translation;
        var direction = Vector3.Normalize(new Vector3(1f, 2f, 0f));
        float unit = 5f;
        var first = editor.CreateJointAt(character, origin + direction * (1 * unit), "ChainA");
        var second = editor.CreateJointAt(first, origin + direction * (2 * unit), "ChainB");
        var third = editor.CreateJointAt(second, origin + direction * (3 * unit), "ChainC");
        var chain = new[] { first, second, third };
        var beforeWorld = chain.Select(j => editor.WorldInPose(pose, j)).ToArray();
        var beforeLtm = chain.Select(j => j.WorldMatrix).ToArray();

        // The last joint of the chain is a leaf: it has nothing to aim at and keeps its basis.
        Assert.Equal(2, editor.OrientJoints(chain));

        for (int i = 0; i < chain.Length; i++)
        {
            // Orienting turns a joint's own basis, so only its translation has to stay (the pivot
            // rule); every joint *below* it has to keep its whole world.
            var afterWorld = editor.WorldInPose(pose, chain[i]);
            float poseMoved = Vector3.Distance(beforeWorld[i].Translation, afterWorld.Translation);
            Assert.True(poseMoved < 1e-3f, $"{chain[i].Name}: pose world moved by {poseMoved}");
            var afterLtm = chain[i].WorldMatrix;
            float ltmMoved = Vector3.Distance(beforeLtm[i].Translation, afterLtm.Translation);
            Assert.True(ltmMoved < 1e-3f, $"{chain[i].Name}: LTM world moved by {ltmMoved}");
        }
        // The leaf is never oriented, so its whole world survives (the pose the save reads too).
        Assert.True(AktMath.MaxDifference(beforeWorld[2], editor.WorldInPose(pose, third)) < 1e-3f);
        Assert.True(AktMath.MaxDifference(beforeLtm[2], third.WorldMatrix) < 1e-3f);
        // Every joint with a child aims along it.
        for (int i = 0; i < chain.Length - 1; i++)
        {
            var world = editor.WorldInPose(pose, chain[i]);
            var next = editor.WorldInPose(pose, chain[i + 1]);
            var aim = Vector3.Normalize(new Vector3(world.M21, world.M22, world.M23));
            var aimDirection = Vector3.Normalize(next.Translation - world.Translation);
            Assert.True(Vector3.Dot(aim, aimDirection) > 0.9999f,
                $"{chain[i].Name}: {aim} vs {aimDirection}");
        }
        // The spans are untouched: the chain is the one that was built, not a kinked one.
        float span = Vector3.Distance(beforeLtm[0].Translation, beforeLtm[1].Translation)
                     + Vector3.Distance(beforeLtm[1].Translation, beforeLtm[2].Translation);
        float reach = Vector3.Distance(chain[0].WorldMatrix.Translation, chain[2].WorldMatrix.Translation);
        Assert.True(Math.Abs(span - reach) < 1e-2f, $"chain changed length: spans {span} vs reach {reach}");
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void PlaceJoint_AlignOnCreateAlignsTheParentAndUsesTheIdentitySlot(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Setup(model) is not var (character, editor)) return;
        var pose = editor.RigPose!;
        var tip = character.Joints.FirstOrDefault(j =>
            j.Constraint == null && editor.SlotOf(j) >= 0 && ReferenceEquals(editor.PlacementParent(j), j));
        if (tip == null) return;
        var tipWorld = editor.WorldInPose(pose, tip);
        var point = tipWorld.Translation + new Vector3(1, 2, 3);

        var joint = editor.PlaceJoint(tip, point, "AlignedTip", JointAlignment.Default);

        Assert.Same(tip, joint.Parent3D);
        // The visible placement is the parent: every pose slot of the new joint is the identity.
        foreach (var p in editor.Poses())
        {
            int slot = editor.SlotOf(joint);
            if (slot < 0 || slot >= p.Count) continue;
            Assert.True(AktMath.MaxDifference(p.Matrix(slot), Matrix4x4.Identity) < 1e-6f,
                $"pose '{p.Name}' slot {slot}/{p.Count}: {p.Matrix(slot)}");
        }
        Assert.True(Vector3.Distance(editor.WorldInPose(pose, joint).Translation, tipWorld.Translation) < 1e-3f);
        AssertRoundtrip(model);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static (Character, RigEditor)? Setup(SceneModel model)
    {
        var character = model.Scene.Characters.FirstOrDefault(c =>
            c.Controllers.OfType<Joint>().Count() >= 2 && PoseEvaluator.RestPose(c, riggingContext: true) != null);
        return character == null ? null : (character, new RigEditor(character));
    }

    private static Joint? FindJoint(Character character, Func<Joint, bool> predicate) =>
        character.Joints.FirstOrDefault(j => j.Constraint == null && predicate(j));

    /// <summary>Two joints that may be reparented onto each other: no constraint, no ancestry.</summary>
    private static (Joint, Joint)? FreePair(Character character)
    {
        var free = character.Joints.Where(j => j.Constraint == null).ToList();
        foreach (var a in free)
            foreach (var b in free)
            {
                if (ReferenceEquals(a, b)) continue;
                if (a.Source.Parent == null) continue;
                if (IsAncestor(a, b) || IsAncestor(b, a)) continue;
                if (character.IKControllers.Any(ik =>
                        (ik.Chain.Contains(a) || ik.ChainParents.Contains(a)) &&
                        (ik.Chain.Contains(b) || ik.ChainParents.Contains(b)))) continue;
                return (a, b);
            }
        return null;
    }

    private static bool IsAncestor(Object3D ancestor, Object3D node)
    {
        for (var p = node.Parent; p != null; p = p.Parent)
            if (ReferenceEquals(p, ancestor)) return true;
        return false;
    }

    private static float Scale(CharacterPose pose, RigEditor editor, Object3D o)
    {
        var world = editor.WorldInPose(pose, o);
        return new Vector3(world.M11, world.M12, world.M13).Length();
    }

    /// <summary>Every weight names a joint of the character, sums to 1 and has at least one entry.</summary>
    private static void AssertWeightsAreValid(Character character)
    {
        int count = character.Joints.Count;
        foreach (var mesh in character.Meshes)
        {
            foreach (var weights in mesh.SkinWeights.Concat(mesh.RigidSkinWeights))
            {
                if (weights.Length == 0) continue;
                Assert.All(weights, w => Assert.InRange(w.Bone, 0, count - 1));
                Assert.InRange(weights.Sum(w => w.Weight), 0.999f, 1.001f);
            }
        }
    }

    /// <summary>The edited document writes a file that reads and writes back byte-exact.</summary>
    private static void AssertRoundtrip(SceneModel model)
    {
        string first = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-rig-{Guid.NewGuid():N}.akt");
        string second = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-rig-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(first);
            TestFiles.LoadScene(first).Save(second);
            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }
}

using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Add IK: creating an IK controller for a joint chain, its
/// registration in the controller arrays, the poses and <c>__RigAnim</c>, and the fact that it leaves
/// the evaluated poses and the user animations alone. The last test compares the pieces with the
/// controller in the <c>7_AddingIK</c> sample.
/// </summary>
public class ControllerCreationTests
{
    [SceneTheory, SceneSamples]
    public void AddIK_RegistersTheControllerEverywhere(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Setup(model) is not var (character, editor)) return;
        if (FreeChain(character, editor) is not var (upper, lower)) return;
        int controllers = character.Controllers.Count;
        int joints = character.Joints.Count;
        var poses = editor.Poses();
        var counts = poses.Select(p => p.Count).ToArray();
        var rigStack = character.RigAnimation?.Stacks.FirstOrDefault();
        int tracks = rigStack?.Tracks.Count() ?? 0;
        var animations = character.Animations.ToList();
        var animationTracks = animations.Sum(a => a.Stacks.Sum(s => s.Tracks.Count()));

        var controller = editor.AddIK(upper, lower);

        Assert.Equal("IK_" + lower.Name, controller.Name);
        Assert.Equal(RigEditor.NewIkControllerFlags, controller.Flags);
        Assert.Equal(RigEditor.IkTargetRenderSizeRatio, controller.TargetRenderSizeRatio);
        Assert.InRange(controller.DecoratorAxis, (byte)0, (byte)2);
        Assert.Same(character, controller.Parent);
        Assert.Equal(2, controller.Chain.Count);
        Assert.Same(upper, controller.Chain[0]);
        Assert.Same(lower, controller.Chain[1]);
        Assert.Empty(controller.ChainParents);
        Assert.Equal(joints, character.Joints.Count);
        Assert.Equal(controllers + 1, character.Controllers.Count);
        Assert.Same(controller, character.Controllers[controllers]);
        Assert.Equal(controllers, editor.SlotOf(controller));
        // The controller sits on the chain's end and in its frame.
        var pose = editor.RigPose!;
        Assert.True(AktMath.MaxDifference(editor.WorldInPose(pose, controller), editor.WorldInPose(pose, lower)) < 1e-3);
        // Every pose gains one slot, holding the controller's LTM.
        for (int i = 0; i < poses.Count; i++)
        {
            Assert.Equal(counts[i] + 1, poses[i].Count);
            Assert.True(AktMath.MaxDifference(controller.LocalMatrix, poses[i].Matrix(controllers)) < 1e-5);
        }
        // The chain joints are driven by it.
        Assert.Same(controller, upper.Constraint);
        Assert.Same(controller, lower.Constraint);
        // The rig gets a track; no user animation is touched (flag bit 10 waits for the
        // "Add new controllers to animations").
        if (rigStack != null)
        {
            var track = Assert.IsType<AnimTrack>(rigStack.TrackFor(controller));
            Assert.Equal(tracks + 1, rigStack.Tracks.Count());
            Assert.Equal(rigStack.Keys.Count, track.Keys.Count);
            Assert.All(track.Keys, k => Assert.Equal(Transform.Identity, Assert.IsAssignableFrom<TransformKey>(k).Value));
            // The rig key carries the chain's FK locals (as in the 7_AddingIK sample).
            var key = Assert.IsAssignableFrom<IKKey>(track.Keys[0]);
            Assert.Equal(2, key.ChainTransforms.Count);
            Assert.True(Vector3.Distance(key.ChainTransforms[0].Translation, editor.LocalInPose(pose, upper).Translation) < 1e-5);
            Assert.True(Vector3.Distance(key.ChainTransforms[1].Translation, editor.LocalInPose(pose, lower).Translation) < 1e-5);
        }
        Assert.Equal(animationTracks, animations.Sum(a => a.Stacks.Sum(s => s.Tracks.Count())));
        Assert.All(animations, a => Assert.All(a.Stacks,
            s => Assert.DoesNotContain(s.Tracks, t => ReferenceEquals(t.Target, controller))));
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void AddIK_LeavesTheEvaluatedPosesAlone(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Setup(model) is not var (character, editor)) return;
        if (FreeChain(character, editor) is not var (upper, lower)) return;
        var animation = character.CurrentAnimation ?? character.Animations.FirstOrDefault();
        if (animation == null) return;
        float[] frames = [0f, 1f, 5f, 11f];
        var before = frames.Select(f => new PoseEvaluator(character).LocalTransforms(animation, f)).ToArray();

        editor.AddIK(upper, lower);

        for (int i = 0; i < frames.Length; i++)
        {
            var after = new PoseEvaluator(character).LocalTransforms(animation, frames[i]);
            // The new controller is appended, so the existing slots keep their index; nothing else moves.
            Assert.Equal(before[i].Length + 1, after.Length);
            for (int c = 0; c < before[i].Length; c++)
            {
                Assert.True(Vector3.Distance(before[i][c].Translation, after[c].Translation) < 1e-4,
                    $"frame {frames[i]}, {character.Controllers[c].Name} moved");
                Assert.True(Quaternion.Dot(Quaternion.Normalize(before[i][c].Rotation), Quaternion.Normalize(after[c].Rotation)) > 0.99999f,
                    $"frame {frames[i]}, {character.Controllers[c].Name} turned");
            }
        }
    }

    [Fact]
    public void AddIK_RefusesAChainWhoseEndsAreNotNested()
    {
        var model = Load("7_AddingIK.akt");
        if (model == null) return;
        var character = Dup(model);
        var editor = new RigEditor(character);
        var a = JointNamed(character, "UpperArm_R");
        var b = JointNamed(character, "UpperArm_L");

        Assert.Throws<InvalidOperationException>(() => editor.AddIK(a, b));
        Assert.Throws<InvalidOperationException>(() => editor.AddIK(a, a));
        // Nothing was created by the refused calls.
        Assert.Equal(2, character.IKControllers.Count(c => c is not ControllerReverseFoot));
    }

    // ---- reverse foot ----------------------------------------------------------------------

    [SceneTheory, SceneSamples]
    public void AddReverseFoot_CreatesTheControllerAndItsShoe(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Setup(model) is not var (character, editor)) return;
        if (FreeChain(character, editor) is not var (upper, lower)) return;
        var poses = editor.Poses();
        var counts = poses.Select(p => p.Count).ToArray();
        int controllers = character.Controllers.Count;
        var rigStack = character.RigAnimation?.Stacks.FirstOrDefault();
        int tracks = rigStack?.Tracks.Count() ?? 0;
        var before = character.Controllers.ToList().Select(c => editor.WorldInPose(editor.RigPose, c)).ToArray();
        var ankle = editor.WorldInPose(editor.RigPose, upper);
        var ball = editor.WorldInPose(editor.RigPose, lower);

        var controller = editor.AddReverseFoot(upper, lower);

        Assert.Equal("RF_" + lower.Name, controller.Name);
        Assert.Equal(RigEditor.NewIkControllerFlags, controller.Flags);
        Assert.Equal(RigEditor.ReverseFootTargetRenderSizeRatio, controller.TargetRenderSizeRatio);
        Assert.Equal(RigEditor.ShoeDecoratorAxis, controller.DecoratorAxis);
        Assert.Equal(2, controller.Chain.Count);
        Assert.Same(upper, controller.Chain[0]);
        Assert.Same(lower, controller.Chain[1]);
        Assert.Equal(Matrix4x4.Identity, controller.LocalMatrix);
        Assert.Equal(new Vector4(1, 0, 0, 0), controller.RollAxisPlant);
        Assert.Equal(Vector4.Zero, controller.RollAxisToes);
        // Parents: the chain root's joint ancestors, nearest first.
        var expectedParents = new List<Joint>();
        for (var ancestor = upper.Parent3D; ancestor != null; ancestor = ancestor.Parent3D)
            if (ancestor is Joint parent) expectedParents.Add(parent);
        Assert.Equal(expectedParents.Select(j => j.Name), controller.ChainParents.Select(j => j.Name));
        // Seven shoe locators: names, types, indices, category, parent chain and references.
        var shoe = controller.Shoe;
        Assert.Equal(7, shoe.Count);
        for (int i = 0; i < shoe.Count; i++)
        {
            var locator = shoe[i];
            Assert.Equal($"{controller.Name}_{RigEditor.ShoeTypes[i]}", locator.Name);
            Assert.Equal(RigEditor.ShoeType(i), locator.Type);
            Assert.Equal(1u, locator.Category);
            Assert.Equal((uint)i, locator.Index);
            Assert.Same(controller, locator.Controller);
            Assert.Same(controller, locator.Constraint);
            Assert.Equal(Matrix4x4.Identity, locator.LocalMatrix);
            Assert.Same(i == 0 ? (Object3D)controller : shoe[i - 1], locator.Parent3D);
        }
        // The shoe's skeleton lands on the leg: the ankle, the ball and the tip half a foot on.
        var pose = editor.RigPose!;
        var shoeAnkle = editor.WorldInPose(pose, shoe[6]);
        Assert.True(Vector3.Distance(shoeAnkle.Translation, ankle.Translation) < 1e-3,
            $"shoe ankle {shoeAnkle.Translation} vs {ankle.Translation}");
        Assert.True(Vector3.Distance(editor.WorldInPose(pose, shoe[5]).Translation, ball.Translation) < 1e-3, "shoe plant");
        var tip = ball.Translation + (ball.Translation - ankle.Translation) * 0.5f;
        Assert.True(Vector3.Distance(editor.WorldInPose(pose, shoe[4]).Translation, tip) < 1e-3, "shoe tip");
        // The controller sits on the ground under the ankle.
        var controllerWorld = editor.WorldInPose(pose, controller);
        Assert.Equal(0f, controllerWorld.Translation.Y, 2);
        Assert.Equal(ankle.Translation.X, controllerWorld.Translation.X, 2);
        Assert.Equal(ankle.Translation.Z, controllerWorld.Translation.Z, 2);
        // Every pose gains the controller and its seven locators; the joints and the other
        // controllers keep their slots.
        for (int i = 0; i < poses.Count; i++) Assert.Equal(counts[i] + 8, poses[i].Count);
        for (int c = 0; c < before.Length; c++)
            Assert.True(AktMath.MaxDifference(before[c], editor.WorldInPose(pose, character.Controllers[c])) < 1e-3);
        Assert.Same(controller, upper.Constraint);
        Assert.Same(controller, lower.Constraint);
        // The controller and every locator get a rig track; the user animations are untouched.
        if (rigStack != null) Assert.Equal(tracks + 8, rigStack.Tracks.Count());
        Assert.All(character.Animations, a => Assert.All(a.Stacks, s =>
            Assert.DoesNotContain(s.Tracks, t => ReferenceEquals(t.Target, controller) || shoe.Contains(t.Target))));
        AssertRoundtrip(model);
    }

    /// <summary>
    /// The reverse foot SloppyAnimatsu creates on <c>6_AddingExtraBones</c> has the shape of the one
    /// The app created there (<c>7_AddingIK</c>): name, chain, joint ancestors as <c>Parents</c>,
    /// decorator axis, the seven locators' names, types, indices and parent chain, and the identity
    /// <c>LTM</c>. The shoe's matrices are compared by
    /// <see cref="AddReverseFoot_WritesTheReferenceShoeIn7"/> and
    /// <see cref="AddReverseFoot_FitsTheSoleToTheFootMeshesOutline"/>.
    /// </summary>
    [Fact]
    public void AddReverseFoot_MatchesTheReferenceController()
    {
        var six = Load("6_AddingExtraBones.akt");
        var seven = Load("7_AddingIK.akt");
        if (six == null || seven == null) return;
        var source = Dup(six);
        var editor = new RigEditor(source);
        foreach (string side in new[] { "R", "L" })
            editor.AddReverseFoot(JointNamed(source, "UpperLeg_" + side), JointNamed(source, "Toes_" + side));

        var reference = Dup(seven);
        foreach (string side in new[] { "R", "L" })
        {
            var ours = source.Controllers.OfType<ControllerReverseFoot>().First(c => c.Name == "RF_Toes_" + side);
            var theirs = reference.Controllers.OfType<ControllerReverseFoot>().First(c => c.Name == "RF_Toes_" + side);
            Assert.Equal(theirs.ClassName, ours.ClassName);
            Assert.Equal(theirs.Flags, ours.Flags);
            Assert.Equal(theirs.TargetRenderSizeRatio, ours.TargetRenderSizeRatio);
            Assert.Equal(theirs.DecoratorAxis, ours.DecoratorAxis);
            Assert.Equal(theirs.Chain.Select(j => j.Name), ours.Chain.Select(j => j.Name));
            Assert.Equal(theirs.ChainParents.Select(j => j.Name), ours.ChainParents.Select(j => j.Name));
            Assert.Equal(Matrix4x4.Identity, theirs.LocalMatrix);
            Assert.Equal(theirs.Shoe.Count, ours.Shoe.Count);
            for (int i = 0; i < ours.Shoe.Count; i++)
            {
                Assert.Equal(theirs.Shoe[i].Name, ours.Shoe[i].Name);
                Assert.Equal(theirs.Shoe[i].Type, ours.Shoe[i].Type);
                Assert.Equal(theirs.Shoe[i].Category, ours.Shoe[i].Category);
                Assert.Equal(theirs.Shoe[i].Index, ours.Shoe[i].Index);
                Assert.Equal(theirs.Shoe[i].Parent!.Name, ours.Shoe[i].Parent!.Name);
                Assert.Equal(Matrix4x4.Identity, ours.Shoe[i].LocalMatrix);
            }
            // The rig key carries the four chain locals plus the controller relative to the ball.
            var ourKey = Assert.IsAssignableFrom<IKKey>(
                source.RigAnimation!.Stacks.First().TrackFor(ours)!.Keys[0]);
            var theirKey = Assert.IsAssignableFrom<IKKey>(
                reference.RigAnimation!.Stacks.First().TrackFor(theirs)!.Keys[0]);
            Assert.Equal(theirKey.ChainTransforms.Count, ourKey.ChainTransforms.Count);
            Assert.Equal(5, ourKey.ChainTransforms.Count);
        }
        AssertRoundtrip(six);
    }

    /// <summary>
    /// The two hand controllers SloppyAnimatsu creates on <c>6_AddingExtraBones</c> are the ones
    /// created there (that file is 6 after Add IK twice) in everything but the transform's last
    /// digits: the <c>LTM</c> differs from the pose slot its creation wrote by up to 0.024,
    /// which is what its smart-transform update stores
    /// </summary>
    [Fact]
    public void AddIK_MatchesTheReferenceControllers()
    {
        var six = Load("6_AddingExtraBones.akt");
        var seven = Load("7_AddingIK.akt");
        if (six == null || seven == null) return;
        var source = Dup(six);
        var editor = new RigEditor(source);
        foreach (string side in new[] { "R", "L" })
            editor.AddIK(JointNamed(source, "UpperArm_" + side), JointNamed(source, "Hand_" + side));

        var reference = Dup(seven);
        var referenceEvaluator = new PoseEvaluator(reference);
        foreach (string side in new[] { "R", "L" })
        {
            var ours = source.IKControllers.First(c => c.Name == "IK_Hand_" + side);
            var theirs = reference.IKControllers.First(c => c.Name == "IK_Hand_" + side);
            Assert.Equal(theirs.ClassName, ours.ClassName);
            Assert.Equal(theirs.Flags, ours.Flags);
            Assert.Equal(theirs.TargetRenderSizeRatio, ours.TargetRenderSizeRatio);
            Assert.Equal(theirs.DecoratorAxis, ours.DecoratorAxis);
            Assert.Equal(theirs.Chain.Select(j => j.Name), ours.Chain.Select(j => j.Name));
            Assert.Empty(theirs.ChainParents);
            Assert.Empty(ours.ChainParents);
            Assert.All(ours.Chain, j => Assert.Same(ours, j.Constraint));
            // The pose slot is the value the creation wrote; the LTM drifted from it.
            var theirSlot = reference.RefPose!.Matrix(referenceEvaluator.IndexOf(theirs));
            var slot = AktMath.MaxDifference(ours.LocalMatrix, theirSlot);
            Assert.True(slot < 5e-3, $"pose slot differs by {slot}");
            var ltm = AktMath.MaxDifference(ours.LocalMatrix, theirs.LocalMatrix);
            Assert.True(ltm < 0.05, $"LTM differs by {ltm}");
            // The rig key's RefArray is the chain's FK locals in both files.
            var ourKey = Assert.IsAssignableFrom<IKKey>(
                source.RigAnimation!.Stacks.First().TrackFor(ours)!.Keys[0]);
            var theirKey = Assert.IsAssignableFrom<IKKey>(
                reference.RigAnimation!.Stacks.First().TrackFor(theirs)!.Keys[0]);
            Assert.Equal(theirKey.ChainTransforms.Count, ourKey.ChainTransforms.Count);
            for (int i = 0; i < theirKey.ChainTransforms.Count; i++)
            {
                var a = ourKey.ChainTransforms[i];
                var b = theirKey.ChainTransforms[i];
                Assert.True(Vector3.Distance(a.Translation, b.Translation) < 1e-4, $"chain {i} translation");
                // The same rotation: a quaternion and its negation compare with |dot|.
                Assert.True(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a.Rotation), Quaternion.Normalize(b.Rotation))) > 0.99999f,
                    $"chain {i} rotation");
                Assert.True(Vector3.Distance(a.Scale, b.Scale) < 1e-4, $"chain {i} scale");
            }
        }
        AssertRoundtrip(six);
    }

    /// <summary>
    /// The four sole pivots follow the foot's <em>mesh</em>, not the foot bone: they are the outline of
    /// the vertices the mesh weights to the last chain joint and to every joint below it, projected
    /// into the shoe frame - the heel and the toes on the centre line at the backward and forward
    /// extremes, the two sides midway between them at the lateral extremes, all four on the ground
    /// plane. The left shoe in <c>7_AddingIK</c> matches this to the file's last digit, and so
    /// does the left shoe SloppyAnimatsu writes on <c>6_AddingExtraBones</c>. The <em>right</em> shoe in
    /// 7 is the left one mirrored (its lateral pivots are negated), so it does not follow the right
    /// foot's own box; SloppyAnimatsu fits each foot. <c>19_run</c> is not checked: its shoe was fitted
    /// before its leg was edited and was never rebuilt.
    /// </summary>
    [Fact]
    public void AddReverseFoot_FitsTheSoleToTheFootMeshesOutline()
    {
        var six = Load("6_AddingExtraBones.akt");
        var seven = Load("7_AddingIK.akt");
        if (six == null || seven == null) return;

        var theirs = Dup(seven);
        var left = JointNamed(theirs, "Toes_L");
        var leftShoe = theirs.Controllers.OfType<ControllerReverseFoot>().First(c => c.Name == "RF_Toes_L");
        var leftOutline = FootOutline(theirs, left);
        AssertSole(theirs, leftShoe, left, leftOutline);
        // The right shoe is that outline mirrored: its lateral pivots swap and are negated, so it does
        // not follow the right foot's own box.
        var rightShoe = theirs.Controllers.OfType<ControllerReverseFoot>().First(c => c.Name == "RF_Toes_R");
        var mirrored = new[]
        {
            leftOutline[0],
            new Panel(-leftOutline[2].Lateral, leftOutline[2].Forward),
            new Panel(-leftOutline[1].Lateral, leftOutline[1].Forward),
            leftOutline[3],
        };
        AssertSole(theirs, rightShoe, JointNamed(theirs, "Toes_R"), mirrored);

        var ours = Dup(six);
        var editor = new RigEditor(ours);
        foreach (string side in new[] { "R", "L" })
        {
            var foot = JointNamed(ours, "Toes_" + side);
            var outline = FootOutline(ours, foot);
            var controller = editor.AddReverseFoot(JointNamed(ours, "UpperLeg_" + side), foot);
            AssertSole(ours, controller, foot, outline);
        }
        AssertRoundtrip(six);
    }

    /// <summary>
    /// SloppyAnimatsu's shoe on <c>6_AddingExtraBones</c> is the shoe fitted in <c>7_AddingIK</c>, in
    /// world units although the character is 267.08-scaled: the three skeleton pivots are exact and the
    /// four sole pivots sit within 2.5 world units - 7's weight array carries twelve extra smooth
    /// weights of 0.0001 on <c>Toes_L</c> (which widen the left foot's box) and its right shoe is the
    /// mirrored left one, while SloppyAnimatsu fits each foot to its own box.
    /// </summary>
    [Fact]
    public void AddReverseFoot_WritesTheReferenceShoeIn7()
    {
        var six = Load("6_AddingExtraBones.akt");
        var seven = Load("7_AddingIK.akt");
        if (six == null || seven == null) return;
        var source = Dup(six);
        var editor = new RigEditor(source);
        foreach (string side in new[] { "R", "L" })
            editor.AddReverseFoot(JointNamed(source, "UpperLeg_" + side), JointNamed(source, "Toes_" + side));

        var reference = Dup(seven);
        var referenceEditor = new RigEditor(reference);
        var referencePose = referenceEditor.RigPose!;
        var pose = editor.RigPose!;
        foreach (string side in new[] { "R", "L" })
        {
            var ours = source.Controllers.OfType<ControllerReverseFoot>()
                .First(c => c.Name == "RF_Toes_" + side);
            var theirs = reference.Controllers.OfType<ControllerReverseFoot>()
                .First(c => c.Name == "RF_Toes_" + side);
            for (int i = 0; i < ours.Shoe.Count; i++)
            {
                float difference = Vector3.Distance(editor.WorldInPose(pose, ours.Shoe[i]).Translation,
                    referenceEditor.WorldInPose(referencePose, theirs.Shoe[i]).Translation);
                if (i < 4)
                    Assert.True(difference < 2.5f, $"{theirs.Shoe[i].Name} differs by {difference}");
                else
                    Assert.True(difference < 1e-3f, $"{theirs.Shoe[i].Name} differs by {difference}");
            }
        }
    }

    /// <summary>The four sole pivots of a foot, as offsets in the shoe frame: heel, left, right, toes.</summary>
    private readonly record struct Panel(float Lateral, float Forward);

    /// <summary>The outline of the vertices the character's meshes weight to <paramref name="foot"/> and
    /// to the joints below it, in the shoe frame of that foot's leg.</summary>
    private static Panel[] FootOutline(Character character, Joint foot)
    {
        var editor = new RigEditor(character);
        var pose = editor.RigPose!;
        var (ground, right, forward) = ShoeFrame(character, editor, pose, foot);
        var joints = character.Joints;
        var indices = new HashSet<int>();
        for (int i = 0; i < joints.Count; i++)
            for (Object3D? node = joints[i]; node != null; node = node.Parent3D)
                if (ReferenceEquals(node, foot))
                {
                    indices.Add(i);
                    break;
                }
        float lateralMin = float.MaxValue, lateralMax = float.MinValue;
        float backward = float.MaxValue, ahead = float.MinValue;
        foreach (var mesh in character.Descendants<Mesh>())
        {
            var meshWorld = editor.WorldInPose(pose, mesh);
            var points = mesh.ControlPoints;
            for (int p = 0; p < points.Length; p++)
            {
                bool weighted = false;
                if (p < mesh.RigidSkinWeights.Length)
                    foreach (var weight in mesh.RigidSkinWeights[p])
                        if (weight.Weight > 0f && indices.Contains(weight.Bone))
                        {
                            weighted = true;
                            break;
                        }
                if (!weighted && p < mesh.SkinWeights.Length)
                    foreach (var weight in mesh.SkinWeights[p])
                        if (weight.Weight > 0f && indices.Contains(weight.Bone))
                        {
                            weighted = true;
                            break;
                        }
                if (!weighted) continue;
                var offset = Vector3.Transform(points[p], meshWorld) - ground;
                float lateral = Vector3.Dot(offset, right);
                float along = Vector3.Dot(offset, forward);
                lateralMin = MathF.Min(lateralMin, lateral);
                lateralMax = MathF.Max(lateralMax, lateral);
                backward = MathF.Min(backward, along);
                ahead = MathF.Max(ahead, along);
            }
        }
        Assert.True(backward < ahead, $"{foot.Name} has no foot geometry");
        float middle = (backward + ahead) * 0.5f;
        return [new(0f, backward), new(lateralMax, middle), new(lateralMin, middle), new(0f, ahead)];
    }

    /// <summary>The shoe frame of the chain that ends at <paramref name="foot"/>: origin on the ground
    /// under the ankle, <c>+z</c> along the horizontal ankle-to-foot direction, <c>+y</c> up.</summary>
    private static (Vector3 Ground, Vector3 Right, Vector3 Forward) ShoeFrame(Character character,
        RigEditor editor, CharacterPose pose, Joint foot)
    {
        var ankle = foot.Parent3D as Joint ?? throw new InvalidOperationException($"{foot.Name} has no ankle.");
        var ankleWorld = editor.WorldInPose(pose, ankle).Translation;
        var ballWorld = editor.WorldInPose(pose, foot).Translation;
        var forward = Vector3.Normalize(new Vector3(ballWorld.X - ankleWorld.X, 0f, ballWorld.Z - ankleWorld.Z));
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        return (new Vector3(ankleWorld.X, 0f, ankleWorld.Z), right, forward);
    }

    /// <summary>Asserts that a reverse foot's four sole locators are the given outline on the ground
    /// plane of its foot's shoe frame.</summary>
    private static void AssertSole(Character character, ControllerReverseFoot controller, Joint foot,
        Panel[] outline)
    {
        var editor = new RigEditor(character);
        var pose = editor.RigPose!;
        var (ground, right, forward) = ShoeFrame(character, editor, pose, foot);
        for (int i = 0; i < outline.Length; i++)
        {
            var expected = ground + right * outline[i].Lateral + forward * outline[i].Forward;
            var actual = editor.WorldInPose(pose, controller.Shoe[i]).Translation;
            Assert.True(Vector3.Distance(actual, expected) < 1e-2f,
                $"{controller.Shoe[i].Name}: {actual} vs the foot's outline {expected}");
            Assert.Equal(0f, actual.Y, 3);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static (Character, RigEditor)? Setup(SceneModel model)
    {
        var character = model.Scene.Characters.FirstOrDefault(c =>
            c.Controllers.OfType<Joint>().Count() >= 2 && PoseEvaluator.RestPose(c, riggingContext: true) != null);
        return character == null ? null : (character, new RigEditor(character));
    }

    /// <summary>An unconstrained two-joint chain with a bone of some length: the shortest IK test case.</summary>
    private static (Joint Upper, Joint Lower)? FreeChain(Character character, RigEditor editor) =>
        FreeChain(character, editor, 2);

    /// <summary>
    /// The lowest unconstrained joint chain of <paramref name="length"/> joints, root first, whose
    /// bones all have a length (a reverse foot refuses a zero-length foot bone).
    /// </summary>
    private static (Joint Upper, Joint Lower)? FreeChain(Character character, RigEditor editor, int length)
    {
        var pose = editor.RigPose;
        if (pose == null) return null;
        foreach (var lower in character.Joints)
        {
            if (lower.Constraint != null || !character.Controllers.Contains(lower)) continue;
            var joints = new List<Joint>();
            for (Object3D? node = lower; node is Joint joint; node = node.Parent3D)
            {
                if (joint.Constraint != null || !character.Controllers.Contains(joint)) break;
                if (joints.Count > 0 &&
                    Vector3.Distance(editor.WorldInPose(pose, joint).Translation,
                        editor.WorldInPose(pose, joints[^1]).Translation) < 1e-4f) break;
                joints.Add(joint);
                if (joints.Count == length) return (joints[^1], lower);
            }
        }
        return null;
    }

    private static Joint JointNamed(Character character, string name) =>
        character.Joints.First(j => j.Name == name);

    private static Character Dup(SceneModel model) => model.Scene.Characters.First(c => c.Name == "Dup");

    private static SceneModel? Load(string fileName) =>
        TestFiles.AllSceneSamples.FirstOrDefault(p => Path.GetFileName(p) == fileName) is { } path
            ? TestFiles.LoadScene(path)
            : null;

    private static void AssertRoundtrip(SceneModel model)
    {
        string first = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-ik-{Guid.NewGuid():N}.akt");
        string second = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-ik-{Guid.NewGuid():N}.akt");
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












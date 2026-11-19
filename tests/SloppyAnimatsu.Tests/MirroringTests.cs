using System.Numerics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>Mirroring poses and keys.</summary>
public class MirroringTests
{
    private static readonly MirrorMatch Match = MirrorMatch.Default;

    [Fact]
    public void MirrorMatch_UsesTheShippedNamePatterns()
    {
        // Suffix, prefix, whole-word, pattern-list-2 and up/down pairs.
        Assert.True(Match.TryMirror("Hand_L", out string mirrored, out var side));
        Assert.Equal("Hand_R", mirrored);
        Assert.Equal(MirrorSide.Left, side);
        Assert.True(Match.TryMirror("Hand_R", out mirrored, out side));
        Assert.Equal("Hand_L", mirrored);
        Assert.Equal(MirrorSide.Right, side);

        Assert.Equal("R_UpperArm", Mirror("L_UpperArm"));
        Assert.Equal("Arm.R", Mirror("Arm.L"));
        Assert.Equal("RightUpLeg", Mirror("LeftUpLeg"));
        Assert.Equal("Arm_L_02", Mirror("Arm_R_02"));
        Assert.Equal("D_Spine", Mirror("U_Spine"));

        Assert.False(Match.TryMirror("Spine", out mirrored, out side));
        Assert.Equal("Spine", mirrored);
        Assert.Equal(MirrorSide.None, side);
        Assert.True(Match.IsUp("U_Spine"));
        Assert.True(Match.IsDown("D_Spine"));
        Assert.True(Match.IsLeft("Hand_L"));
        Assert.True(Match.IsRight("Hand_R"));

        // Every first alternative is tried before any second one: "Left_R" takes the Left/Right pair.
        Assert.Equal("Right_R", Mirror("Left_R"));

        static string Mirror(string name) => Match.TryMirror(name, out string m, out _) ? m : name;
    }

    [Fact]
    public void MirrorMatch_AcceptsOtherPatternLists()
    {
        var match = new MirrorMatch("_a|_b", "_a|_b", "One|Two", "x|y", "N|S");
        Assert.Equal("Foot_b", match.TryMirror("Foot_a", out var m, out _) ? m : null);
        Assert.Equal("TwoThing", match.TryMirror("OneThing", out m, out _) ? m : null);
        Assert.Equal("S_Pole", match.TryMirror("N_Pole", out m, out _) ? m : null);
        Assert.Equal(MirrorSide.Down, match.SideOf("S_Pole"));
    }

    [Fact]
    public void MirrorMatch_FindsTheCounterpartOfTheSameClass()
    {
        var pair = FindPair();
        if (pair is null) return;
        var (character, stack, source, target) = pair.Value;
        var found = Match.Find(character, source);
        Assert.Same(target, found);
        Assert.Equal(source.GetType(), found!.GetType());

        // A name without a side marker is its own counterpart (a central object).
        var central = character.Controllers.FirstOrDefault(c => Match.SideOf(c.Name ?? "") == MirrorSide.None
                                                               && c.GetType() == typeof(Joint)
                                                               && Match.Find(character, c) is not null);
        if (central != null) Assert.Same(central, Match.Find(character, central));
    }

    [SceneTheory, SceneSamples]
    public void MirrorKeys_PutsTheCounterpartWhereTheMirroredSourceWas(string path)
    {
        var model = TestFiles.LoadScene(path);
        var candidate = Candidate(model);
        if (candidate is null) return;
        var (character, animation, stack, source, target) = candidate.Value;
        var session = new PoseEditSession(new ScenePose(model), character);
        var editor = session.Editor;
        int k = 1;
        var sourceTrack = stack.TrackFor(source)!;
        var targetTrack = stack.TrackFor(target)!;
        int frame = stack.Keys[k].Time;

        // A distinctive pose on the source key.
        if (sourceTrack.Keys[k] is not TransformKey sourceKey) return;
        sourceKey.Value = new Transform(new Vector3(0.1f, 0.2f, 0.3f),
            Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.3f, -0.2f, 0.5f)), Vector3.One);
        editor.Evaluator.Invalidate();
        string? beforeUndo = session.History.NextUndo?.Description;

        var mirror = MirrorOf(editor.Evaluator, character, animation, frame);
        var sourceWorld = World(editor.Evaluator, animation, source, frame);
        var world = mirror * sourceWorld * mirror;

        Assert.True(session.MirrorKeys(stack, [k], [source]) > 0);
        var targetWorld = World(editor.Evaluator, animation, target, frame);
        Assert.True(MaxDifference(world, targetWorld) < 1e-3f,
            $"{source.Name} -> {target.Name} at frame {frame}: {MaxDifference(world, targetWorld)}");
        Assert.NotEqual(beforeUndo, session.History.NextUndo?.Description);
        Assert.True(session.Undo());
        Assert.True(session.Redo());
    }

    [SceneTheory, SceneSamples]
    public void MirrorKeys_MirrorsKeyZeroIntoTheLoopKey(string path)
    {
        var model = TestFiles.LoadScene(path);
        var candidate = Candidate(model);
        if (candidate is null) return;
        var (character, animation, stack, source, target) = candidate.Value;
        var session = new PoseEditSession(new ScenePose(model), character);
        if (stack.Keys.Count < 3 || stack.TrackFor(source)!.Keys[0] is not TransformKey sourceKey) return;
        sourceKey.Value = new Transform(new Vector3(1, 2, 3), Quaternion.CreateFromYawPitchRoll(0.2f, 0.3f, 0.4f), Vector3.One);
        session.Editor.Evaluator.Invalidate();
        if (session.MirrorKeys(stack, [0], [source]) == 0) return;

        var targetTrack = stack.TrackFor(target)!;
        int last = stack.Keys.Count - 1;
        var key0 = ((TransformKey)targetTrack.Keys[0]!).Value;
        var loop = ((TransformKey)targetTrack.Keys[last]!).Value;
        Assert.Equal(key0.Translation, loop.Translation);
        Assert.Equal(key0.Scale, loop.Scale);
        Assert.True(MathF.Abs(Quaternion.Dot(key0.Rotation, loop.Rotation)) > 0.999999f);
    }

    [SceneTheory, SceneSamples]
    public void MirrorKeys_CarriesTheIKFlagPoleAndChainLength(string path)
    {
        var model = TestFiles.LoadScene(path);
        var ik = IkCandidate(model, out int k);
        if (ik is null) return;
        var (character, animation, stack, source, target, sourceKey) = ik.Value;
        var session = new PoseEditSession(new ScenePose(model), character);
        sourceKey.PoleAngle = 0.25f;
        sourceKey.SetIkKey(true);
        session.Editor.Evaluator.Invalidate();

        int written = session.MirrorKeys(stack, [k], [source]);
        Assert.True(written > 0);
        var targetKey = Assert.IsType<IKKey>(stack.TrackFor(target)!.Keys[k]);
        Assert.True((targetKey.Flags & 1) != 0);
        Assert.Equal(sourceKey.PoleAngle, targetKey.PoleAngle, 3);
        Assert.Equal(sourceKey.ChainTransforms.Count, targetKey.ChainTransforms.Count);
    }

    [SceneTheory, SceneSamples]
    public void MirrorKeys_MirrorsTheSpaceOfTheKey(string path)
    {
        var model = TestFiles.LoadScene(path);
        var candidate = Candidate(model);
        if (candidate is null) return;
        var (character, animation, stack, source, target) = candidate.Value;
        var session = new PoseEditSession(new ScenePose(model), character);
        var sourceTrack = stack.TrackFor(source)!;
        var targetTrack = stack.TrackFor(target)!;
        const int k = 1;
        int frame = stack.Keys[k].Time;
        if (sourceTrack.Keys[k] is not TransformKey sourceKey || targetTrack.Keys[k] is not TransformKey) return;

        // A central object as the key's space: it mirrors to itself, so the target key must use it too.
        var space = character.Controllers.FirstOrDefault(c => !ReferenceEquals(c, source) && !ReferenceEquals(c, target)
            && Match.SideOf(c.Name ?? "") == MirrorSide.None && c is Joint);
        if (space is null) return;
        sourceKey.Space = space;
        sourceKey.Value = new Transform(new Vector3(0.1f, 0.2f, 0.3f),
            Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.3f, -0.2f, 0.5f)), Vector3.One);
        session.Editor.Evaluator.Invalidate();

        var mirror = MirrorOf(session.Evaluator, character, animation, frame);
        var expected = mirror * KeyWorld(session.Evaluator, animation, stack, sourceTrack, k) * mirror;
        Assert.True(session.MirrorKeys(stack, [k], [source]) > 0);
        var targetKey = (TransformKey)targetTrack.Keys[k]!;
        Assert.Same(space, targetKey.Space);

        var actual = KeyWorld(session.Evaluator, animation, stack, targetTrack, k);
        Assert.True(MaxDifference(expected, actual) < 1e-3f, $"space {space.Name}: {MaxDifference(expected, actual)}");
    }

    [SceneTheory, SceneSamples]
    public void MirrorKeys_SkipsAPairWhoseChannelMasksDiffer(string path)
    {
        var model = TestFiles.LoadScene(path);
        var candidate = Candidate(model);
        if (candidate is null) return;
        var (character, animation, stack, source, target) = candidate.Value;
        var session = new PoseEditSession(new ScenePose(model), character);
        var sourceTrack = stack.TrackFor(source)!;
        var targetTrack = stack.TrackFor(target)!;
        // Candidate pairs have equal masks, so remove a channel they share; a pair without scale
        // makes removing Scale from the target a no-op on the samples that have none.
        int channel = Curves.Channels(sourceTrack.Channels & KeyEditor.AllChannels).First();
        targetTrack.Source.Set("ChannelMask", (uint)(targetTrack.Channels & ~Curves.Bit(channel)));

        Assert.Equal(0, session.MirrorKeys(stack, [1], [source]));
        Assert.Null(session.History.NextUndo);
    }


    [SceneTheory, SceneSamples]
    public void KeepChildrenFixed_LeavesTheChildWorldAlone(string path)
    {
        var model = TestFiles.LoadScene(path);
        var parentChild = ParentWithKeyedChild(model);
        if (parentChild is null) return;
        var (character, animation, stack, parent, child) = parentChild.Value;
        var session = new PoseEditSession(new ScenePose(model), character) { KeepChildrenFixed = true };
        int frame = stack.Keys[0].Time;
        var childBefore = World(session.Evaluator, animation, child, frame);
        var parentBefore = World(session.Evaluator, animation, parent, frame);

        var drag = session.Begin(animation, parent, frame, out string? reason);
        Assert.True(drag != null, reason);
        drag.Rotate(1, 0.2f);
        drag.Commit();

        var parentAfter = World(session.Evaluator, animation, parent, frame);
        var childAfter = World(session.Evaluator, animation, child, frame);
        Assert.True(MaxDifference(parentBefore, parentAfter) > 1e-3f, "the parent did not move");
        Assert.True(MaxDifference(childBefore, childAfter) < 1e-3f,
            $"the child moved by {MaxDifference(childBefore, childAfter)}");

        Assert.True(session.Undo());
        Assert.True(MaxDifference(childBefore, World(session.Evaluator, animation, child, frame)) < 1e-5f);
    }

    [SceneTheory, SceneSamples]
    public void MirrorPose_PutsTheCounterpartWhereTheMirroredSourceWas(string path)
    {
        var model = TestFiles.LoadScene(path);
        var restPair = RestPair(model);
        if (restPair is null) return;
        var (character, source, target) = restPair.Value;
        var rest = new RestPoseEditor(character);
        if (!rest.CanEdit(source, out _) || !rest.CanEdit(target, out _) || rest.EditedPose == null) return;
        rest.SetLocal(source, new Transform(new Vector3(0.1f, 0.2f, 0.3f),
            Quaternion.CreateFromYawPitchRoll(0.2f, -0.3f, 0.4f), Vector3.One));

        var frames = new RestWorlds(rest);
        var mirror = frames.Mirror;
        var world = mirror * frames.World(source) * mirror;

        Assert.True(rest.MirrorPose([source]) > 0);
        var moved = frames.World(target);
        Assert.True(MaxDifference(world, moved) < 1e-3f, $"{source.Name} -> {target.Name}: {MaxDifference(world, moved)}");
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static (Character Character, Animation Animation, AnimStack Stack, Object3D Source, Object3D Target, IKKey SourceKey)? IkCandidate(SceneModel model, out int key)
    {
        key = -1;
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
                foreach (var stack in animation.Stacks)
                    foreach (var track in stack.Tracks)
                    {
                        if (track.Target is not ControllerIK source || source is ControllerReverseFoot) continue;
                        if (Match.Find(character, source) is not ControllerIK target || ReferenceEquals(target, source)) continue;
                        if (target is ControllerReverseFoot || stack.TrackFor(target) is not { } targetTrack) continue;
                        if ((track.Channels & KeyEditor.AllChannels) != (targetTrack.Channels & KeyEditor.AllChannels)) continue;
                        for (int k = 0; k < Math.Min(stack.Keys.Count - 1, track.Keys.Count); k++)
                        {
                            if (track.Keys[k] is not IKKey ik || ik.ChainTransforms.Count == 0) continue;
                            if (targetTrack.Keys[k] is not IKKey other || other.ChainTransforms.Count != ik.ChainTransforms.Count) continue;
                            key = k;
                            return (character, animation, stack, source, target, ik);
                        }
                    }
        return null;
    }

    private static (Character Character, Animation Animation, AnimStack Stack, Object3D Source, Object3D Target)? Candidate(SceneModel model)
    {
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
                foreach (var stack in animation.Stacks)
                {
                    if (stack.Keys.Count < 3 || animation.Stacks.Count != 1 || !stack.IsEnabled || stack.IsAdditive) continue;
                    foreach (var track in stack.Tracks)
                    {
                        if (track.Target is not { } source) continue;
                        if (Match.Find(character, source) is not { } target || ReferenceEquals(target, source)) continue;
                        if (stack.TrackFor(target) is not { } targetTrack || targetTrack.Keys.Count < stack.Keys.Count) continue;
                        if ((track.Channels & KeyEditor.AllChannels) != (targetTrack.Channels & KeyEditor.AllChannels)) continue;
                        return (character, animation, stack, source, target);
                    }
                }
        return null;
    }

    private static (Character Character, AnimStack Stack, Object3D Source, Object3D Target)? FindPair()
    {
        foreach (string path in Samples())
        {
            var candidate = Candidate(TestFiles.LoadScene(path));
            if (candidate != null) return (candidate.Value.Character, candidate.Value.Stack, candidate.Value.Source, candidate.Value.Target);
        }
        return null;
    }

    private static (Character Character, Animation Animation, AnimStack Stack, Object3D Parent, Object3D Child)? ParentWithKeyedChild(SceneModel model)
    {
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
                foreach (var stack in animation.Stacks)
                {
                    if (stack.Keys.Count == 0) continue;
                    foreach (var parent in character.Controllers)
                    {
                        if (stack.TrackFor(parent) == null) continue;
                        if (parent.Children.OfType<Object3D>().FirstOrDefault(c => stack.TrackFor(c) != null) is { } child)
                            return (character, animation, stack, parent, child);
                    }
                }
        return null;
    }

    private static (Character Character, Object3D Source, Object3D Target)? RestPair(SceneModel model)
    {
        foreach (var character in model.Scene.Characters)
        {
        var rest = new RestPoseEditor(character);
            if (rest.EditedPose == null) continue;
            foreach (var source in character.Controllers)
            {
                if (rest.SlotOf(source) < 0) continue;
                if (Match.Find(character, source) is not { } target || ReferenceEquals(target, source)) continue;
                if (rest.SlotOf(target) < 0) continue;
                return (character, source, target);
            }
        }
        return null;
    }

    private static IEnumerable<string> Samples()
    {
        if (TestFiles.Resource3D != null) yield return TestFiles.Resource3D;
        foreach (var f in TestFiles.SceneSamples) yield return f;
    }

    /// <summary>A key's own world (its rest composed with its value under its space), without the other stacks.</summary>
    private static Matrix4x4 KeyWorld(PoseEvaluator evaluator, Animation animation, AnimStack stack, AnimTrack track, int index)
    {
        var target = track.Target!;
        int ti = evaluator.IndexOf(target);
        var key = (TransformKey)track.Keys[index]!;
        var local = PoseEvaluator.Compose(evaluator.RestTransforms[ti], key.Value).ToMatrix();
        float time = stack.Keys[index].Time;
        var frame = key.Space ?? target.Parent3D;
        if (frame is null) return local;
        int fi = evaluator.IndexOf(frame);
        var worlds = evaluator.WorldMatrices(animation, time);
        var locals = evaluator.LocalTransforms(animation, time);
        var frameWorld = fi >= 0 ? worlds[fi] : frame.WorldMatrix;
        var frameScale = fi >= 0 ? locals[fi].Scale : Object3D.LocalScale(frame.LocalMatrix);
        return WorldComposition.Compose(local, frameWorld, frameScale, evaluator.ScaleCompensation && target.CompensatesParentScale);
    }

    private static Matrix4x4 MirrorOf(PoseEvaluator evaluator, Character character, Animation animation, float time)
    {
        var world = evaluator.IndexOf(character) is var i and >= 0
            ? evaluator.WorldMatrices(animation, time)[i]
            : character.WorldMatrix;
        Matrix4x4.Invert(world, out var inverse);
        return inverse * Matrix4x4.CreateScale(-1, 1, 1) * world;
    }
    private static Matrix4x4 World(PoseEvaluator evaluator, Animation animation, Object3D o, float time) =>
        evaluator.WorldMatrices(animation, time)[evaluator.IndexOf(o)];

    private static float MaxDifference(Matrix4x4 a, Matrix4x4 b) => AktMath.MaxDifference(a, b);

    /// <summary>Rest-pose worlds for the test above (a mirror of <c>RestPoseEditor</c>'s private helper).</summary>
    private sealed class RestWorlds(RestPoseEditor editor)
    {
        public Matrix4x4 Mirror
        {
            get
            {
                var world = World(editor.Character);
                Matrix4x4.Invert(world, out var inverse);
                return inverse * Matrix4x4.CreateScale(-1, 1, 1) * world;
            }
        }

        public Matrix4x4 World(Object3D? o)
        {
            if (o is null) return Matrix4x4.Identity;
            int slot = editor.SlotOf(o);
            var local = slot >= 0 ? editor.EditedPose!.Matrix(slot) : o.LocalMatrix;
            if (o.Parent3D is not { } parent) return local;
            return WorldComposition.Compose(local, World(parent), Object3D.LocalScale(Local(parent)), o.CompensatesParentScale);
        }

        private Matrix4x4 Local(Object3D o)
        {
            int slot = editor.SlotOf(o);
            return slot >= 0 ? editor.EditedPose!.Matrix(slot) : o.LocalMatrix;
        }
    }
}

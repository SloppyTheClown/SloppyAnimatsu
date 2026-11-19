using System.Numerics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>The Cyclemaker: Cycle Step and Cycle Side.</summary>
public class CyclemakerTests
{
    private static readonly MirrorMatch Match = MirrorMatch.Default;

    [SceneTheory, SceneSamples]
    public void CycleStep_InsertsOneStepBeforeTheLoopKey(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Find(model, "Common", "Walk", "All") is not var (character, animation, stack)) return;
        if (stack.Keys.Count < 2) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var old = stack.Keys.Select(k => k.Time).ToArray();
        int n = old.Length, step = n - 1;
        int delta = stack.IsCycle ? old[n - 1] - old[n - 2]
            : n >= 3 ? old[n - 2] - old[n - 3]
            : 5;

        int written = session.CycleStep(stack);
        Assert.True(written > 0);
        var times = stack.Keys.Select(k => k.Time).ToArray();
        Assert.Equal(2 * n - 1, times.Length);
        Assert.Equal(old.Take(step), times.Take(step));
        Assert.Equal(old[n - 2] + delta, times[step]);
        for (int k = 1; k < step; k++)
            Assert.Equal(old[k] - old[k - 1] + times[step + k - 1], times[step + k]);
        if (stack.IsCycle) Assert.Equal(times[2 * step - 1] + delta, times[2 * step]);
        else Assert.Equal(old[n - 1], times[2 * step]);

        Assert.True(session.Undo());
        Assert.Equal(n, stack.Keys.Count);
        Assert.Equal(old, stack.Keys.Select(k => k.Time));
    }

    [SceneTheory, SceneSamples]
    public void CycleStep_RecreatesTheReferenceSecondStep(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Find(model, "Common", "Walk", "All") is not var (character, animation, stack)) return;
        if (stack.Keys.Count != 9 || !stack.IsCycle) return;
        var session = new PoseEditSession(new ScenePose(model), character);

        // The sample cycle: first step at 0,1,2,3, second at 4,5,6,7, loop at 8.
        const int firstStep = 4;
        var original = Snapshot(stack, firstStep, stack.Keys.Count - 1 - firstStep);
        int stepStart = stack.Keys[firstStep].Time;

        // Keep the first step and the old loop key only; retime the loop to the step length the
        // sample uses (the gap before the second step), then let Cycle Step rebuild that step.
        Assert.Equal(firstStep, session.Editor.DeleteKeys(stack, Enumerable.Range(firstStep, 4), false));
        Assert.Equal(5, stack.Keys.Count);
        stack.SetKeyTime(firstStep, stepStart);

        Assert.True(session.CycleStep(stack) > 0);
        Assert.Equal(9, stack.Keys.Count);
        var generated = Snapshot(stack, firstStep, 4);
        AssertValuesEqual(original, generated, 1e-2f, 0.5f);
    }

    [SceneTheory, SceneSamples]
    public void CycleStep_WithoutTheCycleFlagLeavesTheLastTimeAlone(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Find(model, "Common", "RunBrake", "All") is not var (character, _, stack)) return;
        if (stack.IsCycle || stack.Keys.Count < 3) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var old = stack.Keys.Select(k => k.Time).ToArray();
        int n = old.Length, step = n - 1;
        int delta = old[n - 2] - old[n - 3];

        Assert.True(session.CycleStep(stack) > 0);
        var times = stack.Keys.Select(k => k.Time).ToArray();
        Assert.Equal(2 * n - 1, times.Length);
        Assert.Equal(old[n - 1], times[2 * step]);
        Assert.Equal(old[n - 2] + delta, times[step]);
        Assert.True(session.Undo());
    }

    [SceneTheory, SceneSamples]
    public void CycleSide_LeavesASymmetricCycleAlone(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Find(model, "Common", "Walk", "All") is not var (character, animation, stack)) return;
        if (!KeyEditor.CanCycleSide(stack, out _)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var before = Snapshot(stack, 0, stack.Keys.Count - 1);

        Assert.True(session.CycleSide(stack, character.Controllers) > 0);
        var after = Snapshot(stack, 0, stack.Keys.Count - 1);
        AssertValuesEqual(before, after, 5e-3f, 0.1f);
        Assert.True(session.Undo());
        Assert.True(session.Redo());
    }

    [SceneTheory, SceneSamples]
    public void CycleSide_KeepsTheIKAndReverseFootDataOfASymmetricCycle(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Find(model, "Common", "Walk", "All") is not var (character, _, stack)) return;
        if (!KeyEditor.CanCycleSide(stack, out _)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var before = IkSnapshot(stack);

        Assert.True(session.CycleSide(stack, character.Controllers) > 0);
        var after = IkSnapshot(stack);
        foreach (var (track, states) in before)
        {
            Assert.True(after.TryGetValue(track, out var now));
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i] is not { } a || now![i] is not { } b)
                {
                    Assert.True(states[i] is null && now[i] is null, $"{track.Target?.Name} key {i}: IK null mismatch");
                    continue;
                }
                Assert.Equal(a.Flags, b.Flags);
                Assert.True(MathF.Abs(a.Pole - b.Pole) < 1e-3f, $"{track.Target?.Name} key {i}: pole");
                Assert.True(MathF.Abs(a.Roll - b.Roll) < 1e-4f, $"{track.Target?.Name} key {i}: roll");
                Assert.True(MathF.Abs(a.Twist - b.Twist) < 1e-4f, $"{track.Target?.Name} key {i}: twist");
                Assert.True(MathF.Abs(a.Bank - b.Bank) < 1e-4f, $"{track.Target?.Name} key {i}: bank");
                Assert.Equal(a.Chain.Length, b.Chain.Length);
                for (int j = 0; j < a.Chain.Length; j++)
                    AssertValuesEqual(a.Chain[j], b.Chain[j], 5e-3f, 0.1f);
            }
        }
    }

    [SceneTheory, SceneSamples]
    public void CycleSide_PropagatesAnEditToTheCounterpartAtTheHalfShift(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Find(model, "Common", "Walk", "All") is not var (character, animation, stack)) return;
        if (!KeyEditor.CanCycleSide(stack, out _)) return;
        int half = (stack.Keys.Count - 1) / 2;
        (Object3D Source, Object3D Target, AnimTrack SourceTrack, AnimTrack TargetTrack)? pair = null;
        foreach (var track in stack.Tracks)
        {
            if (track.Target is not { } source) continue;
            if (Match.Find(character, source) is not { } target || ReferenceEquals(target, source)) continue;
            if (stack.TrackFor(target) is not { } counterTrack) continue;
            if (track.Channels != counterTrack.Channels) continue;
            if (track.Keys[0] is not TransformKey || counterTrack.Keys[half] is not TransformKey) continue;
            pair = (source, target, track, counterTrack);
            break;
        }
        if (pair is not { } found) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var (sourceObject, targetObject, sourceTrack, targetTrack) = found;

        var key = (TransformKey)sourceTrack.Keys[0]!;
        key.Value = new Transform(new Vector3(0.1f, 0.2f, 0.3f),
            Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.3f, -0.2f, 0.5f)), Vector3.One);
        session.Editor.Evaluator.Invalidate();
        var changed = key.Value;

        var mirror = MirrorOf(session.Evaluator, character, animation, stack.Keys[0].Time);
        var expected = mirror * KeyWorld(session.Evaluator, animation, stack, sourceTrack, 0) * mirror;
        Assert.True(session.CycleSide(stack, [sourceObject]) > 0);
        AssertValuesEqual(changed, key.Value, 1e-4f, 1e-3f);
        Assert.True(MaxDifference(expected, KeyWorld(session.Editor.Evaluator, animation, stack, targetTrack, half)) < 1e-2f,
            $"{sourceObject.Name} -> {targetObject.Name}: {MaxDifference(expected, KeyWorld(session.Editor.Evaluator, animation, stack, targetTrack, half))}");
    }

    [SceneTheory, SceneSamples]
    public void CycleSide_RefusesAnOddNumberOfKeys(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Find(model, "Common", "Run", "All") is not var (character, _, stack)) return;
        if (!stack.IsCycle || stack.Keys.Count % 2 == 1) return;
        Assert.False(KeyEditor.CanCycleSide(stack, out string? reason));
        Assert.Equal(KeyEditor.CycleSideOddKeysMessage, reason);
        var editor = new KeyEditor(character);
        var error = Assert.Throws<InvalidOperationException>(() => editor.CycleSide(stack, character.Controllers));
        Assert.Equal(KeyEditor.CycleSideOddKeysMessage, error.Message);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static (Character Character, Animation Animation, AnimStack Stack)? Find(
        SceneModel model, params string[] path)
    {
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
            {
                if (!animation.FolderPath.Concat([animation.Name ?? ""]).SequenceEqual(path)) continue;
                if (animation.Stacks.FirstOrDefault() is { } stack) return (character, animation, stack);
            }
        return null;
    }

    private static Dictionary<AnimTrack, Transform?[]> Snapshot(AnimStack stack, int from, int count) =>
        stack.Tracks.ToDictionary(t => t, t => t.Keys.Skip(from).Take(count)
            .Select(k => k is TransformKey tk ? (Transform?)tk.Value : null).ToArray());

    private static void AssertValuesEqual(Dictionary<AnimTrack, Transform?[]> expected,
        Dictionary<AnimTrack, Transform?[]> actual, float distance, float degrees)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (track, before) in expected)
        {
            Assert.True(actual.TryGetValue(track, out var after), $"{track.Target?.Name} is missing");
            for (int i = 0; i < before.Length; i++)
            {
                if (before[i] is not { } a || after![i] is not { } b)
                {
                    Assert.True(before[i] is null && after[i] is null, $"{track.Target?.Name} key {i}: null mismatch");
                    continue;
                }
                float dt = Vector3.Distance(a.Translation, b.Translation);
                float ds = Vector3.Distance(a.Scale, b.Scale);
                float dr = Angle(a.Rotation, b.Rotation);
                Assert.True(dt <= distance, $"{track.Target?.Name} key {i}: translation differs by {dt}");
                Assert.True(ds <= distance, $"{track.Target?.Name} key {i}: scale differs by {ds}");
                Assert.True(dr <= degrees, $"{track.Target?.Name} key {i}: rotation differs by {dr:0.###} deg");
            }
        }
    }

    private static void AssertValuesEqual(Transform expected, Transform actual, float distance, float degrees)
    {
        Assert.True(Vector3.Distance(expected.Translation, actual.Translation) <= distance);
        Assert.True(Vector3.Distance(expected.Scale, actual.Scale) <= distance);
        Assert.True(Angle(expected.Rotation, actual.Rotation) <= degrees);
    }

    private static float Angle(Quaternion a, Quaternion b)
    {
        float dot = MathF.Min(1f, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))));
        return 2f * MathF.Acos(dot) * 180f / MathF.PI;
    }

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

    private static float MaxDifference(Matrix4x4 a, Matrix4x4 b) => AktMath.MaxDifference(a, b);
    private sealed record IkState(uint Flags, float Pole, float Roll, float Twist, float Bank, Transform[] Chain);

    private static Dictionary<AnimTrack, IkState?[]> IkSnapshot(AnimStack stack) =>
        stack.Tracks.ToDictionary(t => t, t => t.Keys.Select(k => k switch
        {
            ReverseFootKey rf => new IkState(rf.Flags, rf.PoleAngle, rf.RollAngle, rf.TwistAngle, rf.BankAngle,
                rf.ChainTransforms.ToArray()),
            IKKey ik => new IkState(ik.Flags, ik.PoleAngle, 0, 0, 0, ik.ChainTransforms.ToArray()),
            _ => null,
        }).ToArray());
}




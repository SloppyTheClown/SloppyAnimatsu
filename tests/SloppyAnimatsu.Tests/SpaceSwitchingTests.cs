using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>Space switching edits.</summary>
public class SpaceSwitchingTests
{
    [SceneTheory, SceneSamples]
    public void ChangeSpace_KeepsTheWorldAtTheKeyFrames(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, animation, stack, target, space)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var keys = Enumerable.Range(0, stack.Keys.Count - 1).ToArray();
        var frames = keys.Select(k => stack.Keys[k].Time).ToArray();
        var before = frames.Select(f => World(session, animation, target, f)).ToArray();

        Assert.Equal(keys.Length, session.ChangeSpace(stack, keys, [target], space));

        var track = stack.TrackFor(target)!;
        Assert.All(keys, k => Assert.Same(space, ((TransformKey)track.Keys[k]).Space));
        for (int i = 0; i < frames.Length; i++)
            AssertClose(before[i], World(session, animation, target, frames[i]), $"frame {frames[i]}");

        // And back to the parent: the stored values return.
        string edited = Tracks(stack);
        Assert.True(session.Undo());
        string original = Tracks(stack);
        Assert.True(session.Redo());
        Assert.Equal(edited, Tracks(stack));
        Assert.Equal(keys.Length, session.ChangeSpace(stack, keys, [target], null));
        Assert.All(keys, k => Assert.Null(((TransformKey)track.Keys[k]).Space));
        for (int i = 0; i < frames.Length; i++)
            AssertClose(before[i], World(session, animation, target, frames[i]), $"frame {frames[i]} (back)");
        Assert.True(session.Undo());
        Assert.True(session.Undo());
        Assert.Equal(original, Tracks(stack));
    }

    [SceneTheory, SceneSamples]
    public void ChangeSpace_RefusesLoopsAndIKChainJoints(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, _, stack, target, _)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        string original = Tracks(stack);

        Assert.False(KeyEditor.CanChangeSpace(target, target, out string? reason));
        Assert.Contains("loop", reason);
        Assert.Throws<InvalidOperationException>(() => session.ChangeSpace(stack, [0], [target], target));
        Assert.Equal(original, Tracks(stack));
        Assert.Null(session.History.NextUndo);

        if (character.IKControllers.SelectMany(c => c.Chain).FirstOrDefault() is { } joint)
        {
            Assert.False(KeyEditor.CanChangeSpace(joint, null, out reason));
            Assert.Contains("ik/rf chain", reason);
        }
    }

    [SceneTheory, SceneSamples]
    public void ChangeSpace_ReachesEveryStackKeyingTheFrame(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, _, stack, target, space)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var copy = session.DuplicateStack(stack);
        int k = stack.Keys.Count / 2 - 1;
        if (k < 0) k = 0;

        Assert.Equal(2, session.ChangeSpace(stack, [k], [target], space));

        Assert.Same(space, ((TransformKey)stack.TrackFor(target)!.Keys[k]).Space);
        Assert.Same(space, ((TransformKey)copy.TrackFor(target)!.Keys[k]).Space);
    }

    [SceneTheory, SceneSamples]
    public void Retime_TakesTheSpaceOfTheOtherStacks(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, _, stack, target, space)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var times = stack.Keys.Select(k => k.Time).ToArray();
        int k = Enumerable.Range(0, times.Length - 1).FirstOrDefault(i => times[i + 1] - times[i] >= 2, -1);
        if (k < 0) return;
        var track = stack.TrackFor(target)!;
        var stored = ((TransformKey)track.Keys[k]).Space;
        var copy = session.DuplicateStack(stack); // above the stack, so it answers first

        // Off the shared frame, nothing answers there: the key keeps its space.
        session.MoveKeys(stack, [k], 1);
        Assert.Same(stored, ((TransformKey)track.Keys[k]).Space);

        // Only the copy keys frame times[k] now.
        Assert.Equal(1, session.ChangeSpace(copy, [k], [target], space));
        Assert.Same(stored, ((TransformKey)track.Keys[k]).Space);

        // Back onto it: the key takes the copy's space there.
        session.MoveKeys(stack, [k], -1);
        Assert.Equal(times[k], stack.Keys[k].Time);
        Assert.Same(space, ((TransformKey)track.Keys[k]).Space);
    }

    [SceneTheory, SceneSamples]
    public void Hammer_ConvertsTheNewKeysIntoTheOtherStacksSpace(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, animation, stack, target, space)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var copy = session.DuplicateStack(stack);
        session.MoveStackTo(copy, animation.Stacks.ToList().IndexOf(stack) + 1); // below the stack
        Assert.True(animation.Stacks.ToList().IndexOf(copy) > animation.Stacks.ToList().IndexOf(stack));
        int k = 0;
        int frame = stack.Keys[k].Time;
        session.ChangeSpace(copy, [k], [target], space);
        session.RemoveFromStack(stack, [target], target);
        Assert.Null(stack.TrackFor(target));

        Assert.Equal(1, session.AddToStack(stack, [target]));

        var track = stack.TrackFor(target)!;
        var key = (TransformKey)track.Keys[k];
        Assert.Same(space, key.Space);
        // The identity reference under the parent, re-expressed under the space.
        var converter = new KeySpaceConverter(session.Evaluator, animation);
        AssertClose(Transform.Identity, converter.Convert(target, key.Value, space, null, frame), "round trip");
        // Frames where the copy is in the parent keep the plain reference.
        for (int i = 1; i < track.Keys.Count - 1; i++)
            if (((TransformKey)copy.TrackFor(target)!.Keys[i]).Space == null)
            {
                Assert.Null(((TransformKey)track.Keys[i]).Space);
                Assert.Equal(Transform.Identity, ((TransformKey)track.Keys[i]).Value);
            }
    }

    [SceneTheory, SceneSamples]
    public void ToggleSpaceable_FlipsTheFlagAndUndoes(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, _, _, target, space)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        bool was = space.IsSpaceable;
        uint flags = target.Flags;

        Assert.Equal(!was, session.ToggleSpaceable(space, [target]));
        Assert.Equal(!was, space.IsSpaceable);
        Assert.Equal(!was, target.IsSpaceable);
        Assert.Equal(!was, KeyEditor.SpaceableObjects(model.Scene).Contains(space));

        Assert.True(session.Undo());
        Assert.Equal(was, space.IsSpaceable);
        Assert.Equal(flags, target.Flags);
    }

    [SceneTheory, SceneSamples]
    public void ChangeSpace_SurvivesSaveAndReload(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, animation, stack, target, space)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        session.ChangeSpace(stack, [0], [target], space);
        string edited = Tracks(stack);
        int ci = model.Scene.Characters.ToList().IndexOf(character);
        int ai = character.Animations.ToList().IndexOf(animation);
        int si = animation.Stacks.ToList().IndexOf(stack);

        string file = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-space-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(file);
            var reloaded = TestFiles.LoadScene(file);
            var again = reloaded.Scene.Characters.ElementAt(ci).Animations.ElementAt(ai).Stacks.ElementAt(si);
            Assert.Equal(edited, Tracks(again));
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// The TRANSFORM window's Space row (<see cref="PoseEditSession.ChangeSpaceAt"/>): between two
    /// keys it creates the key first, switches it, keeps the world there, and one undo puts the
    /// stack back.
    /// </summary>
    [SceneTheory, SceneSamples]
    public void ChangeSpaceAt_KeysThePlayheadKeepsTheWorldInOneUndo(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, animation, stack, target, space)) return;
        if (!ReferenceEquals(PoseEditSession.EditableStack(animation, target), stack)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        int gap = Enumerable.Range(0, stack.Keys.Count - 1).FirstOrDefault(k => stack.Keys[k + 1].Time - stack.Keys[k].Time >= 2, -1);
        if (gap < 0) return;
        int frame = (int)stack.Keys[gap].Time + 1;
        Assert.True(PoseEditSession.KeyAt(stack, frame) < 0);
        int keyCount = stack.Keys.Count;
        string original = Tracks(stack);
        var before = World(session, animation, target, frame);

        Assert.Equal(1, session.ChangeSpaceAt(animation, [target], frame, space, null, out string? reason));
        Assert.Null(reason);

        Assert.Equal(keyCount + 1, stack.Keys.Count);
        int key = PoseEditSession.KeyAt(stack, frame);
        Assert.True(key >= 0);
        Assert.Same(space, ((TransformKey)stack.TrackFor(target)!.Keys[key]).Space);
        AssertClose(before, World(session, animation, target, frame), $"frame {frame}");

        var record = session.History.NextUndo!;
        Assert.True(session.Undo());
        Assert.Equal(original, Tracks(stack));
        Assert.Equal(keyCount, stack.Keys.Count);
        // A drag preview undone this way is then dropped, so Redo cannot bring it back.
        Assert.True(session.History.ForgetRedo(record));
        Assert.False(session.History.CanRedo);

        // A refused space writes nothing and records nothing.
        var undo = session.History.NextUndo;
        Assert.Equal(0, session.ChangeSpaceAt(animation, [target], frame, target, null, out reason));
        Assert.Contains("loop", reason);
        Assert.Same(undo, session.History.NextUndo);
        Assert.Equal(original, Tracks(stack));
    }

    /// <summary>
    /// The Space Switch window's rows for the character in its drop-down: the parent, then only
    /// that character's spaceable objects by name; the space in force is the selected row, and the
    /// drop-down starts on the current space's character.
    /// </summary>
    [SceneTheory, SceneSamples]
    public void SpaceSwitchChoice_ListsTheDropDownCharactersSpaces(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, _, _, target, space)) return;
        space.IsSpaceable = true;
        var spaceable = KeyEditor.SpaceableObjects(model.Scene).ToList();
        var characters = model.Scene.Characters.ToList();

        var items = SpaceSwitchChoice.ItemsFor(character, spaceable);
        Assert.True(items[0].IsParent);
        Assert.All(items.Skip(1), i => Assert.Same(character, SpaceSwitchChoice.OwnerOf(i.Space!)));
        Assert.Contains(items, i => ReferenceEquals(i.Space, space) && i.Label == space.Name);
        Assert.Equal(spaceable.Count(s => ReferenceEquals(SpaceSwitchChoice.OwnerOf(s), character)) + 1, items.Count);

        Assert.Equal(0, SpaceSwitchChoice.IndexOf(items, null));
        Assert.Same(space, items[SpaceSwitchChoice.IndexOf(items, space)].Space);
        if (model.Scene.Characters.SelectMany(c => c.Descendants<Object3D>()).FirstOrDefault(o => !spaceable.Contains(o)) is { } unlisted)
            Assert.Equal(0, SpaceSwitchChoice.IndexOf(items, unlisted)); // not listed: the parent row

        int index = SpaceSwitchChoice.DefaultCharacter(characters, null, target);
        Assert.Same(character, characters[index]);
        Assert.Single(SpaceSwitchChoice.ItemsFor(null, spaceable));
        Assert.Equal(-1, SpaceSwitchChoice.DefaultCharacter([], null, target));
    }

    /// <summary>
    /// An enabled, full-opacity override stack with keys, a controller that only it tracks (outside
    /// any IK chain, every key in its parent), and a space for it: another controller of the
    /// character, not below it and not its parent.
    /// </summary>
    private static (Character, Animation, AnimStack, Object3D, Object3D)? Candidate(SceneModel model)
    {
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
                foreach (var stack in animation.Stacks)
                {
                    if (!stack.IsEnabled || stack.IsAdditive || MathF.Abs(stack.Opacity - 1) > 1e-6f || stack.Keys.Count < 3) continue;
                    foreach (var target in character.Controllers)
                    {
                        if (stack.TrackFor(target) is not { } track || track.ClassName != "CAnimTrackMRS") continue;
                        if (animation.Stacks.Count(s => s.TrackFor(target) != null) != 1) continue;
                        if (track.Keys.Any(k => k is not TransformKey { Space: null })) continue;
                        if (character.IKControllers.Any(ik => ReferenceEquals(ik, target) || ik.Chain.Contains(target))) continue;
                        var space = character.Controllers.FirstOrDefault(s => !ReferenceEquals(s, target.Parent3D)
                            && KeyEditor.CanChangeSpace(target, s, out _) && !character.IKControllers.Any(ik => ik.Chain.Contains(s)));
                        if (space != null) return (character, animation, stack, target, space);
                    }
                }
        return null;
    }

    private static Matrix4x4 World(PoseEditSession session, Animation animation, Object3D target, int frame) =>
        session.Evaluator.WorldMatrices(animation, frame)[session.Evaluator.IndexOf(target)];

    private static string Tracks(AnimStack stack) =>
        string.Join("|", stack.Tracks.Select(t =>
            $"{t.Target?.Name}:" + string.Join(";", t.Keys.Select(k => k is TransformKey tk ? $"{tk.Value}@{tk.Space?.Name}" : "-"))));

    private static void AssertClose(Matrix4x4 expected, Matrix4x4 actual, string what)
    {
        float scale = Math.Max(1, new Vector3(expected.M41, expected.M42, expected.M43).Length());
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
            {
                float e = expected[r, c], a = actual[r, c];
                Assert.True(MathF.Abs(e - a) < (r == 3 ? 1e-3f * scale : 1e-3f), $"{what}: [{r},{c}] {e} != {a}");
            }
    }

    private static void AssertClose(Transform expected, Transform actual, string what)
    {
        Assert.True(Vector3.Distance(expected.Translation, actual.Translation) < 1e-3f * Math.Max(1, expected.Translation.Length()),
            $"{what}: translation {expected.Translation} != {actual.Translation}");
        Assert.True(Vector3.Distance(expected.Scale, actual.Scale) < 1e-4f, $"{what}: scale {expected.Scale} != {actual.Scale}");
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected.Rotation), Quaternion.Normalize(actual.Rotation)));
        Assert.True(dot > 1 - 1e-5f, $"{what}: rotation {expected.Rotation} != {actual.Rotation}");
    }
}

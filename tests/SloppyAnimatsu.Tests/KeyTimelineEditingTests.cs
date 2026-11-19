using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The viewer's key mark, gap and order commands, each one undoable <see cref="PoseEditSession"/>
/// step.
/// </summary>
public class KeyTimelineEditingTests
{
    [SceneTheory, SceneSamples]
    public void KeyMarks_AreRecorded_SkipTheEndKey_AndUndoRedo(string path)
    {
        if (Candidate(TestFiles.LoadScene(path)) is not var (session, stack, _, _, _)) return;
        int n = stack.Keys.Count;
        var original = Values(stack);

        Assert.Equal(2, session.SetKeyMarks(stack, [0, 1, n - 1], KeyEditor.ExtremeMark));
        Assert.Equal(KeyEditor.ExtremeMark, stack.Keys[0].Flags);
        Assert.Equal(KeyEditor.ExtremeMark, stack.Keys[1].Flags);
        Assert.Equal(KeyEditor.NoMark, stack.Keys[n - 1].Flags);
        var marked = Values(stack);

        Assert.Equal(1, session.SetKeyMarks(stack, [1], KeyEditor.BreakdownMark));
        Assert.Equal(KeyEditor.BreakdownMark, stack.Keys[1].Flags);

        // Marking the end key alone, or setting a mark that is already there, records nothing.
        var undoable = session.History.NextUndo;
        Assert.Equal(0, session.SetKeyMarks(stack, [n - 1], KeyEditor.ExtremeMark));
        Assert.Equal(0, session.SetKeyMarks(stack, [1], KeyEditor.BreakdownMark));
        Assert.Same(undoable, session.History.NextUndo);

        Assert.True(session.Undo());
        Assert.Equal(marked, Values(stack));
        Assert.True(session.Undo());
        Assert.Equal(original, Values(stack));
        Assert.True(session.Redo());
        Assert.Equal(marked, Values(stack));

        // Remove marks on all keys.
        session.SetKeyMarks(stack, Enumerable.Range(0, n).ToArray(), KeyEditor.NoMark);
        Assert.All(stack.Keys, k => Assert.Equal(KeyEditor.NoMark, k.Flags));
        Assert.True(session.Undo());
        Assert.Equal(marked, Values(stack));
    }

    [SceneTheory, SceneSamples]
    public void SetKeyTime_RipplesTheLaterKeys_AndClampsAfterThePreviousKey(string path)
    {
        if (Candidate(TestFiles.LoadScene(path)) is not var (session, stack, _, _, _)) return;
        var original = Values(stack);
        var times = Times(stack);

        // Later: key 2 and every key after it move by 5 frames.
        Assert.Equal(times[2] + 5, session.SetKeyTime(stack, 2, times[2] + 5));
        Assert.Equal(times.Select((t, i) => i >= 2 ? t + 5 : t), Times(stack));
        var edited = Values(stack);
        Assert.True(session.Undo());
        Assert.Equal(original, Values(stack));
        Assert.True(session.Redo());
        Assert.Equal(edited, Values(stack));
        Assert.True(session.Undo());

        // Earlier than key 0: key 1 stops one frame after it, and the later keys keep their gaps.
        int delta = times[0] + 1 - times[1];
        Assert.Equal(times[0] + 1, session.SetKeyTime(stack, 1, times[0] - 10));
        Assert.Equal(times.Select((t, i) => i >= 1 ? t + delta : t), Times(stack));
        Assert.True(session.Undo());

        // No change, no record.
        var undoable = session.History.NextUndo;
        session.SetKeyTime(stack, 2, times[2]);
        Assert.Same(undoable, session.History.NextUndo);
        Assert.Equal(original, Values(stack));
    }

    [SceneTheory, SceneSamples]
    public void KeyGaps_AreSetAndChanged_AndNeverDropBelowOneFrame(string path)
    {
        if (Candidate(TestFiles.LoadScene(path)) is not var (session, stack, _, _, _)) return;
        var original = Values(stack);
        var times = Times(stack);
        int gap1 = times[1] - times[0];

        // Set the gap before key 1 to 1: the later keys keep their own gaps.
        Assert.True(session.SetKeyGap(stack, [1], 1));
        Assert.Equal(times.Select((t, i) => i >= 1 ? t + 1 - gap1 : t), Times(stack));
        var tight = Values(stack);

        // Narrowing a one-frame gap is refused and not recorded.
        var undoable = session.History.NextUndo;
        Assert.False(session.ChangeKeyGap(stack, [1], -1));
        Assert.Same(undoable, session.History.NextUndo);
        Assert.Equal(tight, Values(stack));

        // Widening by one ripples everything after it.
        Assert.True(session.ChangeKeyGap(stack, [1], 1));
        Assert.Equal(times[0] + 2, stack.Keys[1].Time);
        Assert.Equal(times[^1] + 2 - gap1, stack.Keys[^1].Time);

        // Key 0 has no gap before it: nothing to change.
        undoable = session.History.NextUndo;
        Assert.False(session.ChangeKeyGap(stack, [0], 1));
        Assert.Same(undoable, session.History.NextUndo);

        Assert.True(session.Undo());
        Assert.Equal(tight, Values(stack));
        Assert.True(session.Undo());
        Assert.Equal(original, Values(stack));
        Assert.True(session.Redo());
        Assert.Equal(tight, Values(stack));
    }

    [SceneTheory, SceneSamples]
    public void ReorderKeys_KeepsGaps_MovesMarksAndValues_AndUndoRedo(string path)
    {
        if (Candidate(TestFiles.LoadScene(path)) is not var (session, stack, _, _, _)) return;
        int n = stack.Keys.Count;
        var times = Times(stack);
        session.SetKeyMarks(stack, [1], KeyEditor.ExtremeMark);
        var original = Values(stack);
        var key0 = KeyValues(stack, 0);
        var key1 = KeyValues(stack, 1);

        // Key 1 before key 0.
        var order = session.ReorderKeys(stack, [1], 0);
        Assert.Equal(0, order[1]);
        Assert.Equal(1, order[0]);
        Assert.Equal(n - 1, order[n - 1]);
        Assert.Equal(n, stack.Keys.Count);
        Assert.Equal(KeyEditor.ExtremeMark, stack.Keys[0].Flags);
        Assert.Equal(KeyEditor.NoMark, stack.Keys[1].Flags);
        Assert.Equal(key1, KeyValues(stack, 0));
        Assert.Equal(key0, KeyValues(stack, 1));
        // The times are rebuilt from the old first time, with each key keeping the gap before it
        // (key 0 the gap to key 1), so swapping the first two keys keeps every time.
        Assert.Equal(times, Times(stack));
        var edited = Values(stack);

        Assert.True(session.Undo());
        Assert.Equal(original, Values(stack));
        Assert.True(session.Redo());
        Assert.Equal(edited, Values(stack));
        Assert.True(session.Undo());

        // The end key never moves, and a reorder that changes nothing is not recorded.
        var undoable = session.History.NextUndo;
        session.ReorderKeys(stack, [n - 1], 0);
        session.ReorderKeys(stack, [1], 1);
        Assert.Same(undoable, session.History.NextUndo);
        Assert.Equal(original, Values(stack));
    }

    [SceneTheory, SceneSamples]
    public void MarksGapsAndOrder_SurviveSaveAndReload(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (session, stack, character, animation, controller)) return;
        session.SetKeyMarks(stack, [0], KeyEditor.ExtremeMark);
        session.SetKeyMarks(stack, [1], KeyEditor.BreakdownMark);
        session.ReorderKeys(stack, [1], 0);
        session.ChangeKeyGap(stack, [2], 3);
        var expected = Values(stack);

        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-key-marks-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(temp);
            var reloaded = TestFiles.LoadScene(temp);
            var c2 = reloaded.Scene.Characters.Single(c => c.Name == character.Name && c.Controllers.Any(o => o.Name == controller.Name));
            var a2 = c2.Animations.First(a => a.Name == animation.Name);
            var o2 = c2.Controllers.First(o => o.Name == controller.Name);
            var stack2 = PoseEditSession.EditableStack(a2, o2)!;
            Assert.Equal(expected, Values(stack2));
            Assert.Equal(KeyEditor.BreakdownMark, stack2.Keys[0].Flags);
            Assert.Equal(KeyEditor.ExtremeMark, stack2.Keys[1].Flags);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [SceneTheory, SceneSamples]
    public void KeyFades_AreAddedAndRemoved_IncludingOnTheEndKey_AndUndoRedo(string path)
    {
        if (Candidate(TestFiles.LoadScene(path)) is not var (session, stack, _, _, _)) return;
        int n = stack.Keys.Count;
        // Start from keys without fades, whatever the sample has.
        session.SetKeyFades(stack, Enumerable.Range(0, n).ToArray(), true, 0);
        session.SetKeyFades(stack, Enumerable.Range(0, n).ToArray(), false, 0);
        var original = Values(stack);
        var marks = stack.Keys.Select(k => k.Flags).ToArray();

        // Unlike marks, the end key is not skipped.
        Assert.Equal(2, session.SetKeyFades(stack, [1, n - 1], true, KeyEditor.DefaultFade));
        Assert.Equal(KeyEditor.DefaultFade, stack.Keys[1].FadeIn);
        Assert.Equal(KeyEditor.DefaultFade, stack.Keys[n - 1].FadeIn);
        Assert.Equal(0, stack.Keys[1].FadeOut);
        Assert.Equal(1, session.SetKeyFades(stack, [2], false, KeyEditor.DefaultFade));
        Assert.Equal(KeyEditor.DefaultFade, stack.Keys[2].FadeOut);
        Assert.Equal(0, stack.Keys[2].FadeIn);
        Assert.Equal(marks, stack.Keys.Select(k => k.Flags));
        var faded = Values(stack);

        // A fade that is already there records nothing.
        var undoable = session.History.NextUndo;
        Assert.Equal(0, session.SetKeyFades(stack, [1], true, KeyEditor.DefaultFade));
        Assert.Equal(0, session.SetKeyFades(stack, [0], false, 0));
        Assert.Same(undoable, session.History.NextUndo);

        Assert.Equal(1, session.SetKeyFades(stack, [1, 2], true, 0));
        Assert.Equal(0, stack.Keys[1].FadeIn);
        Assert.True(session.Undo());
        Assert.Equal(faded, Values(stack));
        Assert.True(session.Undo());
        Assert.True(session.Undo());
        Assert.Equal(original, Values(stack));
        Assert.True(session.Redo());
        Assert.True(session.Redo());
        Assert.Equal(faded, Values(stack));
    }

    [SceneTheory, SceneSamples]
    public void FadeDrag_ReappliesFromThePress_StaysAtLeastOne_AndIsOneUndoStep(string path)
    {
        if (Candidate(TestFiles.LoadScene(path)) is not var (session, stack, _, _, _)) return;
        session.SetKeyFades(stack, [1], true, KeyEditor.DefaultFade);
        session.SetKeyFades(stack, [1], false, KeyEditor.DefaultFade);
        var before = Values(stack);
        var undoable = session.History.NextUndo;

        // Fade-out: dragging right lengthens it.
        var drag = session.BeginFadeDrag(stack, 1, false);
        Assert.Equal(8, drag.Update(3));
        Assert.Equal(7, drag.Update(2)); // from the press, not from the last move
        Assert.Equal(1, drag.Update(-20));
        Assert.Equal(1, stack.Keys[1].FadeOut);
        Assert.Equal(KeyEditor.DefaultFade, stack.Keys[1].FadeIn);
        Assert.Same(undoable, session.History.NextUndo);
        drag.Update(4);
        Assert.NotNull(drag.Commit());
        Assert.Equal(9, stack.Keys[1].FadeOut);
        Assert.True(session.Undo());
        Assert.Equal(before, Values(stack));

        // Fade-in: dragging left lengthens it; cancelling puts it back and records nothing.
        drag = session.BeginFadeDrag(stack, 1, true);
        Assert.Equal(7, drag.Update(-2));
        Assert.Equal(1, drag.Update(10));
        drag.Cancel();
        Assert.Equal(before, Values(stack));

        // A drag back to where it started records nothing.
        undoable = session.History.NextUndo;
        drag = session.BeginFadeDrag(stack, 1, true);
        drag.Update(2);
        drag.Update(0);
        Assert.Null(drag.Commit());
        Assert.Same(undoable, session.History.NextUndo);
    }

    [SceneTheory, SceneSamples]
    public void KeyFades_ChangeTheEvaluatedWeight_AndThePose(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (session, stack, character, animation, _)) return;
        float frame = stack.Keys[1].Time - 1;
        session.SetKeyFades(stack, [1], true, 0);
        session.Evaluator.LocalTransforms(animation, frame, out _); // fill the evaluator's cache

        session.SetKeyFades(stack, [1], true, KeyEditor.DefaultFade);
        if (stack.Keys[0].FadeOut == 0)
            Assert.Equal(1f - 1f / KeyEditor.DefaultFade, PoseEvaluator.Timing(stack.Keys, frame, out _), 5);
        // The edit is seen at once: the session's evaluator agrees with a fresh one.
        var fresh = new PoseEditSession(new ScenePose(model), character);
        Assert.Equal(fresh.Evaluator.LocalTransforms(animation, frame, out _), session.Evaluator.LocalTransforms(animation, frame, out _));
    }

    [SceneTheory, SceneSamples]
    public void KeyFades_SurviveSaveAndReload(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (session, stack, character, animation, controller)) return;
        session.SetKeyFades(stack, [1], true, KeyEditor.DefaultFade);
        var drag = session.BeginFadeDrag(stack, 2, false);
        drag.Update(3 - drag.PressFade); // a fade-out of 3, whatever the sample had
        drag.Commit();
        var expected = Values(stack);

        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-key-fades-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(temp);
            var reloaded = TestFiles.LoadScene(temp);
            var c2 = reloaded.Scene.Characters.Single(c => c.Name == character.Name && c.Controllers.Any(o => o.Name == controller.Name));
            var a2 = c2.Animations.First(a => a.Name == animation.Name);
            var stack2 = PoseEditSession.EditableStack(a2, c2.Controllers.First(o => o.Name == controller.Name))!;
            Assert.Equal(expected, Values(stack2));
            Assert.Equal(KeyEditor.DefaultFade, stack2.Keys[1].FadeIn);
            Assert.Equal(3, stack2.Keys[2].FadeOut);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>The first editable stack with at least four keys, the first two at least two frames apart.</summary>
    private static (PoseEditSession, AnimStack, Character, Animation, Object3D)? Candidate(SceneModel model)
    {
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
                foreach (var controller in character.Controllers)
                {
                    if (controller is ControllerIK) continue;
                    if (PoseEditSession.EditableStack(animation, controller) is not { } stack) continue;
                    var keys = stack.Keys;
                    if (keys.Count < 4 || keys[1].Time - keys[0].Time < 2) continue;
                    return (new PoseEditSession(new ScenePose(model), character), stack, character, animation, controller);
                }
        return null;
    }

    private static int[] Times(AnimStack stack) => stack.Keys.Select(k => k.Time).ToArray();

    /// <summary>Every track's value at key <paramref name="key"/>.</summary>
    private static string KeyValues(AnimStack stack, int key) =>
        string.Join("|", stack.Tracks.Select(t => t.Keys[key] is TransformKey tk ? tk.Value.ToString() : "-"));

    /// <summary>The stack's keys (times and marks) and every track's key values, as comparable text.</summary>
    private static string Values(AnimStack stack) =>
        string.Join(";", stack.Keys) + "|" + string.Join("|", stack.Tracks.Select(t =>
            string.Join(";", t.Keys.Select(k => k is TransformKey tk ? tk.Value.ToString() : "-"))));
}

using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Audio;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>The Anim Bank and the Mixer's stack actions.</summary>
public class AnimationBankTests
{
    /// <summary>A user autosave holding an untouched "New animation" made by the app (one foot posed at frame 0).</summary>
    /// The workspace keeps its copy under samples/autosave; the test returns early without it.
    private static readonly string SentaiAutosave = WorkspaceSample(
        Path.Combine("samples", "autosave", "153 sentai can hold overrides + weak hold attack_autosave_01.akt"));

    private static string WorkspaceSample(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Path.Combine(dir.FullName, relative) is var candidate && File.Exists(candidate))
                return candidate;
        return relative;
    }

    // ---- unique names and colours --------------------------------------------------------------

    [Fact]
    public void UniqueNames_KeepFreeNames_AndNumberTakenOnes()
    {
        var parent = new AktObject("class CFolder");
        AktObject Child(string name)
        {
            var o = new AktObject("class CDataObject");
            o.Fields.Add(new AktField("class CDataObject", "Name", "class CAtom", name));
            parent.AddChild(o);
            return o;
        }

        Assert.Equal("Track", UniqueNames.For(parent, "Track"));
        var first = Child("Track");
        Assert.Equal("Track", UniqueNames.For(parent, "Track", first)); // its own name is free for itself
        Assert.Equal("Track_02", UniqueNames.For(parent, "Track"));
        Child("Track_02");
        Child("Track_07");
        Assert.Equal("Track_08", UniqueNames.For(parent, "Track"));
        Assert.Equal("Track_08", UniqueNames.For(parent, "Track_02"));
        Assert.Equal("Track_05", UniqueNames.For(parent, "Track_05"));
        Child("All_2");
        Assert.Equal("All", UniqueNames.For(parent, "All"));
        Assert.Equal("All_03", UniqueNames.For(parent, "All_2"));

        // A base that already ends in an underscore keeps one separator: the.akt import
        // renamed the material "Dup Bomb_" to "Dup Bomb_02".
        Child("Dup Bomb_");
        Assert.Equal("Dup Bomb_02", UniqueNames.For(parent, "Dup Bomb_"));
        Child("Dup Bomb_02");
        Assert.Equal("Dup Bomb_03", UniqueNames.For(parent, "Dup Bomb_"));

        // A file-like name keeps its extension last, as the import did for the textures
        // ("Dup_b_NoTrans.png" -> "Dup_b_NoTrans_02.png"), and an authoring name with a numeric dot
        // suffix is not treated as an extension.
        Child("Dup_b_NoTrans.png");
        Assert.Equal("Dup_b_NoTrans_02.png", UniqueNames.For(parent, "Dup_b_NoTrans.png"));
        Child("Dup_b_NoTrans_02.png");
        Assert.Equal("Dup_b_NoTrans_03.png", UniqueNames.For(parent, "Dup_b_NoTrans.png"));
        Child("Spine.001");
        Assert.Equal("Spine.001_02", UniqueNames.For(parent, "Spine.001"));
    }

    [Fact]
    public void StackColor_MatchesTheStoredColours()
    {
        Assert.Equal(new byte[] { 0x99, 0x99, 0x99, 0xFF }, AnimationBankEditor.StackColor(0));
        Assert.Equal(new byte[] { 0x52, 0xC5, 0xA9, 0xFF }, AnimationBankEditor.StackColor(1));
        Assert.Equal(new byte[] { 0x52, 0xB2, 0xC5, 0xFF }, AnimationBankEditor.StackColor(2));
        for (int n = 1; n < 40; n++) Assert.Equal(0xFF, AnimationBankEditor.StackColor(n)[3]);
    }

    // ---- animations ----------------------------------------------------------------------------

    [SceneTheory, SceneSamples]
    public void CreateAnimation_HasOneStackWithATrackPerController(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        var session = new PoseEditSession(new ScenePose(model), character);

        var animation = session.CreateAnimation();

        Assert.StartsWith("New animation", animation.Name);
        Assert.Same(character.AnimBank, animation.Parent);
        Assert.Same(animation, character.CurrentAnimation);
        var stack = Assert.Single(animation.Stacks);
        Assert.Same(stack, animation.CurrentStack);
        Assert.Equal("All", stack.Name);
        Assert.Equal(2u, stack.Flags);
        Assert.Equal(1f, stack.Opacity);
        Assert.Equal(0.5f, stack.Tweenage);
        Assert.Equal(new byte[] { 0x99, 0x99, 0x99, 0xFF }, stack.Color);
        Assert.Equal([new StackKey(0, 0, 0, 0), new StackKey(0, 0, 0, 0)], stack.Keys);

        var tracks = stack.Tracks.ToList();
        Assert.Equal(character.Controllers.Reverse(), tracks.Select(t => t.Target));
        Assert.Equal(tracks.Count, tracks.Select(t => t.Name).Distinct().Count());
        Assert.All(tracks, t =>
        {
            Assert.Equal(2, t.Keys.Count);
            Assert.Equal(2, t.TangentModes.Count);
        });

        // The new animation is at rest everywhere.
        var rest = session.Evaluator.RestTransforms;
        var local = session.Evaluator.LocalTransforms(animation, 0, out _);
        for (int i = 0; i < rest.Length; i++)
        {
            if (character.Controllers[i] is ControllerIK) continue;
            Assert.True(System.Numerics.Vector3.Distance(rest[i].Translation, local[i].Translation) < 1e-3f * Math.Max(1, rest[i].Translation.Length()),
                $"{character.Controllers[i].Name}: {rest[i].Translation} != {local[i].Translation}");
        }

        // Undo removes it and restores the current animation; redo puts it back.
        var before = Bank(character);
        Assert.True(session.Undo());
        Assert.DoesNotContain(animation, character.Animations);
        Assert.True(session.Redo());
        Assert.Same(animation, character.CurrentAnimation);
        Assert.Equal(before, Bank(character));
    }

    [Fact]
    public void CreateAnimation_MatchesTheReferenceNewAnimation()
    {
        if (!File.Exists(SentaiAutosave)) return;
        var model = TestFiles.LoadScene(SentaiAutosave);
        var character = model.Scene.Characters.First(c => c.Animations.Any(a => a.Name == "New animation"));
        var expected = character.Animations.First(a => a.Name == "New animation");
        var session = new PoseEditSession(new ScenePose(model), character);

        var created = session.CreateAnimation();

        Assert.Equal("New animation_02", created.Name);
        var e = Assert.Single(expected.Stacks);
        var c = Assert.Single(created.Stacks);
        Assert.Equal(e.Name, c.Name);
        Assert.Equal(e.Flags, c.Flags);
        Assert.Equal(e.Color, c.Color);
        Assert.Equal(e.Opacity, c.Opacity);
        Assert.Equal(e.Tweenage, c.Tweenage);
        var et = e.Tracks.ToList();
        var ct = c.Tracks.ToList();
        Assert.Equal(et.Count, ct.Count);
        for (int i = 0; i < et.Count; i++)
        {
            string where = $"track {i} ({et[i].Target?.Name})";
            Assert.True(et[i].Target == ct[i].Target, where);
            Assert.True(et[i].Name == ct[i].Name, $"{where}: {et[i].Name} != {ct[i].Name}");
            Assert.True(et[i].ClassName == ct[i].ClassName, $"{where}: {et[i].ClassName} != {ct[i].ClassName}");
            Assert.True(et[i].Channels == ct[i].Channels, $"{where}: {et[i].Channels} != {ct[i].Channels}");
            Assert.Equal(et[i].Keys.Count, ct[i].Keys.Count);
            Assert.True(et[i].TangentModes.Select(m => string.Join(",", m)).SequenceEqual(ct[i].TangentModes.Select(m => string.Join(",", m))),
                $"{where} {et[i].ClassName}: {string.Join(" / ", et[i].TangentModes.Select(m => string.Join(",", m)))} != {string.Join(" / ", ct[i].TangentModes.Select(m => string.Join(",", m)))}");
            for (int k = 0; k < ct[i].Keys.Count; k++)
                Assert.Equal(et[i].Keys[k]!.KeyType, ct[i].Keys[k]!.KeyType);

            // The loop key was not edited: its IK reference pose is the chain's rest pose.
            if (et[i].Keys[1]!.Source["RefArray"] is List<object?> eRefs)
            {
                var cRefs = (List<object?>)ct[i].Keys[1]!.Source["RefArray"]!;
                Assert.Equal(eRefs.Count, cRefs.Count);
                for (int r = 0; r < eRefs.Count; r++)
                    foreach (var field in new[] { "Translation", "Rotation", "Scale" })
                    {
                        var ev = ((AktStruct)eRefs[r]!).Get<float[]>(field);
                        var cv = ((AktStruct)cRefs[r]!).Get<float[]>(field);
                        Assert.Equal(ev.Length, cv.Length);
                        for (int j = 0; j < ev.Length; j++)
                            Assert.True(MathF.Abs(MathF.Abs(ev[j]) - MathF.Abs(cv[j])) < 1e-3f * Math.Max(1, MathF.Abs(ev[j])),
                                $"{where} RefArray[{r}].{field}: {string.Join(" ", ev)} != {string.Join(" ", cv)}");
                    }
            }
        }
    }

    [SceneTheory, SceneSamples]
    public void CopyAnimation_IsADeepCopyWithItsOwnReferences(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstAnimation(model) is not var (character, original)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        string originalText = Describe(original);

        var copy = session.CopyAnimation(original);

        Assert.NotSame(original, copy);
        Assert.Same(original.Parent, copy.Parent);
        Assert.NotEqual(original.Name, copy.Name);
        Assert.Equal(original.Stacks.Count, copy.Stacks.Count);
        Assert.All(copy.Stacks, s => Assert.Same(copy, s.Animation));
        Assert.True(copy.CurrentStack == null || copy.CurrentStack.Animation == copy);
        Assert.All(copy.Stacks.SelectMany(s => s.Tracks).SelectMany(t => t.Keys.OfType<TransformKey>()),
            k => Assert.True(k.Track == null || k.Track.Stack!.Animation == copy));
        Assert.Equal(originalText, Describe(copy));
        Assert.Equal(originalText, Describe(original));

        // A copy survives save and reload.
        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-copy-anim-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(temp);
            var reloaded = TestFiles.LoadScene(temp);
            var a2 = reloaded.Scene.Characters.SelectMany(c => c.Animations).First(a => a.Name == copy.Name);
            Assert.Equal(originalText, Describe(a2));
        }
        finally
        {
            File.Delete(temp);
        }

        Assert.True(session.Undo());
        Assert.DoesNotContain(copy, character.Animations);
    }

    [SceneTheory, SceneSamples]
    public void DeleteAnimation_IsUndoable(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstAnimation(model) is not var (character, animation)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        character.CurrentAnimation = animation;
        var before = Bank(character);

        session.DeleteAnimation(animation);
        Assert.DoesNotContain(animation, character.Animations);
        Assert.Null(character.CurrentAnimation);

        Assert.True(session.Undo());
        Assert.Equal(before, Bank(character));
        Assert.Same(animation, character.CurrentAnimation);
    }

    [SceneTheory, SceneSamples]
    public void Rename_UsesTheUniqueNameRule(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstAnimation(model) is not var (character, animation)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        string old = animation.Name!;
        var sibling = animation.Parent!.Children.OfType<Animation>().FirstOrDefault(a => a != animation);

        Assert.Equal("Renamed", session.Rename(animation, "Renamed"));
        int edits = 1;
        if (sibling != null)
        {
            Assert.NotEqual(sibling.Name, session.Rename(animation, sibling.Name!));
            edits++;
        }
        // Undo once per rename: a scene whose first animation has no sibling has only one step.
        for (int i = 0; i < edits; i++) Assert.True(session.Undo());
        Assert.Equal(old, animation.Name);
    }

    // ---- stacks --------------------------------------------------------------------------------

    [SceneTheory, SceneSamples]
    public void CreateStack_GoesAboveTheCurrentStack(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstAnimation(model) is not var (character, animation)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var current = animation.Stacks[^1];
        session.SetCurrentStack(current);
        Assert.False(session.History.CanUndo);
        var controller = character.Controllers.FirstOrDefault(o => KeyEditor.CanAddToStack(o, out _));
        int count = animation.Stacks.Count;
        var before = Bank(character);

        var stack = session.CreateStack(animation, controller == null ? [] : [controller]);

        Assert.Equal(count + 1, animation.Stacks.Count);
        Assert.Equal(count - 1, animation.Stacks.ToList().IndexOf(stack));
        Assert.Equal(count, animation.Stacks.ToList().IndexOf(current));
        Assert.Same(stack, animation.CurrentStack);
        Assert.StartsWith("New Stack", stack.Name);
        Assert.Equal(AnimationBankEditor.StackColor(count), stack.Color);
        Assert.Equal(2u, stack.Flags);
        Assert.Equal(2, stack.Keys.Count);
        if (controller != null) Assert.NotNull(stack.TrackFor(controller));

        Assert.True(session.Undo());
        Assert.Equal(before, Bank(character));
        Assert.Same(current, animation.CurrentStack);
    }

    [SceneTheory, SceneSamples]
    public void DuplicateStack_InsertsACopyAboveTheOriginal(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstAnimation(model) is not var (character, animation)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var original = animation.Stacks[^1];
        session.SetCurrentStack(original);
        int index = animation.Stacks.Count - 1;
        string text = Describe(original);

        var copy = session.DuplicateStack(original);

        Assert.Equal(index, animation.Stacks.ToList().IndexOf(copy));
        Assert.Equal(index + 1, animation.Stacks.ToList().IndexOf(original));
        Assert.Same(copy, animation.CurrentStack);
        Assert.NotEqual(original.Name, copy.Name);
        Assert.Equal(text, Describe(copy));
        Assert.All(copy.Tracks, t => Assert.All(t.Keys.OfType<TransformKey>(), k => Assert.True(k.Track == null || k.Track == t)));
    }

    [SceneTheory, SceneSamples]
    public void DeleteStack_KeepsTheLastStack_AndPicksANewCurrent(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstAnimation(model) is not var (character, animation)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        while (animation.Stacks.Count < 2) session.DuplicateStack(animation.Stacks[0]);
        var top = animation.Stacks[0];
        var below = animation.Stacks[1];
        session.SetCurrentStack(top);
        var before = Bank(character);

        Assert.True(session.DeleteStack(top));
        Assert.DoesNotContain(top, animation.Stacks);
        Assert.Same(below, animation.CurrentStack);

        while (animation.Stacks.Count > 1) Assert.True(session.DeleteStack(animation.Stacks[^1]));
        var undoable = session.History.NextUndo;
        Assert.False(session.DeleteStack(animation.Stacks[0]));
        Assert.Same(undoable, session.History.NextUndo);

        while (Bank(character) != before) Assert.True(session.Undo());
    }

    [SceneTheory, SceneSamples]
    public void MoveStack_ReordersTheMixer(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstAnimation(model) is not var (character, animation)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        while (animation.Stacks.Count < 2) session.DuplicateStack(animation.Stacks[0]);
        var top = animation.Stacks[0];

        Assert.False(session.MoveStack(top, -1));
        Assert.True(session.MoveStack(top, 1));
        Assert.Same(top, animation.Stacks[1]);
        Assert.True(session.Undo());
        Assert.Same(top, animation.Stacks[0]);
    }

    [SceneTheory, SceneSamples]
    public void MoveStackTo_IsTheMixerDragAndUndoes(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstAnimation(model) is not var (character, animation)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        while (animation.Stacks.Count < 3) session.DuplicateStack(animation.Stacks[0]);
        var before = animation.Stacks.ToList();
        var bank = new AnimationBankEditor(character);

        Assert.False(bank.ReorderStacks(animation, [0, 0, 1, .. Enumerable.Range(3, before.Count - 3)]));
        Assert.False(bank.ReorderStacks(animation, [.. Enumerable.Range(0, before.Count)]));
        Assert.False(session.MoveStackTo(before[0], 0));

        // The top stack dropped at the bottom; the others keep their order.
        Assert.True(session.MoveStackTo(before[0], before.Count - 1));
        Assert.Equal([.. before.Skip(1), before[0]], animation.Stacks.ToList());
        Assert.True(session.MoveStackTo(before[^1], 0));
        Assert.Same(before[^1], animation.Stacks[0]);

        Assert.True(session.Undo());
        Assert.True(session.Undo());
        Assert.Equal(before, animation.Stacks.ToList());
    }

    [SceneTheory, SceneSamples]
    public void CreateFolder_AppendsToTheRootAndUndoes(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var root = character.AnimBank!;

        var a = session.CreateFolder();
        var b = session.CreateFolder();

        Assert.Same(root, a.Parent);
        Assert.Same(b, root.Children.Last());
        Assert.StartsWith("New folder", a.Name);
        Assert.NotEqual(a.Name, b.Name);

        // A folder survives save and reload.
        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-folder-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(temp);
            var reloaded = TestFiles.LoadScene(temp);
            var c2 = reloaded.Scene.Characters.First(c => c.Name == character.Name);
            Assert.Contains(c2.AnimBank!.Children, c => c is Folder && c.Name == b.Name);
        }
        finally
        {
            File.Delete(temp);
        }

        Assert.True(session.Undo());
        Assert.True(session.Undo());
        Assert.DoesNotContain(a, root.Children);
        Assert.DoesNotContain(b, root.Children);
    }

    [SceneTheory, SceneSamples]
    public void MoveToFolder_KeepsTheNameAndUndoesAcrossParents(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstAnimation(model) is not var (character, animation)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var root = character.AnimBank!;
        var oldParent = animation.Parent!;
        int oldIndex = oldParent.Children.ToList().IndexOf(animation);
        string name = animation.Name!;
        string bank = Bank(character);

        var folder = session.CreateFolder();
        var moved = session.MoveToFolder([animation], folder);

        Assert.Equal([animation], moved);
        Assert.Same(folder, animation.Parent);
        Assert.Equal(name, animation.Name);
        Assert.Empty(session.MoveToFolder([animation], folder));

        // Back to the root: appended as the last child, still with its name.
        session.MoveToFolder([animation], root);
        Assert.Same(animation, root.Children.Last());
        Assert.Equal(name, animation.Name);

        Assert.True(session.Undo());
        Assert.Same(folder, animation.Parent);
        Assert.True(session.Undo());
        Assert.Same(oldParent, animation.Parent);
        Assert.Equal(oldIndex, oldParent.Children.ToList().IndexOf(animation));
        Assert.True(session.Undo());
        Assert.Equal(bank, Bank(character));
    }

    [SceneTheory, SceneSamples]
    public void MoveToFolder_RefusesAFolderIntoItselfAndMovesSeveral(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var bank = new AnimationBankEditor(character);
        var root = character.AnimBank!;

        var outer = session.CreateFolder();
        var inner = session.CreateFolder();
        session.MoveToFolder([inner], outer);
        Assert.Same(outer, inner.Parent);

        Assert.False(bank.CanMove(outer, outer, out _));
        Assert.False(bank.CanMove(outer, inner, out _));
        Assert.False(bank.CanMove(root, outer, out _));
        Assert.False(bank.CanMove(outer, character, out _));
        Assert.Throws<InvalidOperationException>(() => session.MoveToFolder([outer], inner));
        Assert.Same(outer, inner.Parent);

        // Several items: the one inside a moved folder stays with it.
        var anim = session.CreateAnimation();
        var target = session.CreateFolder();
        var moved = session.MoveToFolder([inner, anim, outer], target);
        Assert.Equal([outer, anim], moved);
        Assert.Equal([outer, anim], target.Children.ToList());
        Assert.Same(outer, inner.Parent);

        Assert.True(session.Undo());
        Assert.Same(root, outer.Parent);
        Assert.Same(root, anim.Parent);
    }

    [SceneTheory, SceneSamples]
    public void CopyFolder_DuplicatesItsContentsBesideItAndUndoes(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstAnimation(model) is not var (character, animation)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var folder = session.CreateFolder();
        var inner = session.CreateFolder();
        session.MoveToFolder([animation], inner);
        session.MoveToFolder([inner], folder);
        int count = character.Animations.Count();

        var copy = session.CopyFolder(folder);

        Assert.NotSame(folder, copy);
        Assert.Same(folder.Parent, copy.Parent);
        Assert.NotEqual(folder.Name, copy.Name);
        Assert.Equal(count + 1, character.Animations.Count());
        var copiedInner = Assert.IsType<Folder>(Assert.Single(copy.Children));
        Assert.Equal(inner.Name, copiedInner.Name);
        var copiedAnimation = Assert.IsType<Animation>(Assert.Single(copiedInner.Children));
        Assert.Equal(animation.Name, copiedAnimation.Name);
        Assert.NotSame(animation.Source, copiedAnimation.Source);
        Assert.Equal(animation.Stacks.Sum(s => s.Tracks.Count()), copiedAnimation.Stacks.Sum(s => s.Tracks.Count()));
        Assert.Throws<InvalidOperationException>(() => session.CopyFolder(character.AnimBank!));

        Assert.True(session.Undo());
        Assert.Null(copy.Source.Parent);
        Assert.Equal(count, character.Animations.Count());
    }

    [SceneTheory, SceneSamples]
    public void DeleteFolder_RemovesItsContentsAndUndoes(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstAnimation(model) is not var (character, animation)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        character.CurrentAnimation = animation;
        string bank = Bank(character);

        var folder = session.CreateFolder();
        session.MoveToFolder([animation], folder);
        session.DeleteFolder(folder);

        Assert.DoesNotContain(animation, character.Animations);
        Assert.Null(character.CurrentAnimation);
        Assert.Throws<InvalidOperationException>(() => session.DeleteFolder(character.AnimBank!));

        Assert.True(session.Undo());
        Assert.Same(animation, character.CurrentAnimation);
        Assert.Same(folder, animation.Parent);
        Assert.True(session.Undo());
        Assert.True(session.Undo());
        Assert.Equal(bank, Bank(character));
    }

    [SceneTheory, SceneSamples]
    public void Setters_ChangeTheStackAndUndo(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstAnimation(model) is not var (character, animation)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var stack = animation.Stacks[0];
        var flags = stack.Flags;
        var opacity = stack.Opacity;
        var keys = stack.Keys;

        session.SetStackOpacity(stack, 1.5f);
        Assert.Equal(1f, stack.Opacity);
        session.SetStackOpacity(stack, 0.25f);
        Assert.Equal(0.25f, stack.Opacity);
        session.SetStackAdditive(stack, !stack.IsAdditive);
        Assert.Equal((flags ^ 8) & 8, stack.Flags & 8);
        session.ToggleStackFlag(stack, AnimationBankEditor.EnabledFlag);
        Assert.Equal((flags ^ 2) & 2, stack.Flags & 2);

        for (int i = 0; i < 4; i++) Assert.True(session.Undo());
        Assert.Equal(flags, stack.Flags);
        Assert.Equal(opacity, stack.Opacity);
        Assert.Equal(keys, stack.Keys);
    }

    [SceneTheory, SceneSamples]
    public void TurningCycleOn_PutsTheLoopKeyAfterTheLastKey(string path)
    {
        var model = TestFiles.LoadScene(path);
        var candidates = model.Scene.Characters.SelectMany(c => c.Animations.SelectMany(a => a.Stacks.Select(s => (c, s))))
            .Where(p => !p.s.IsCycle && p.s.Keys.Count >= 2).ToList();
        if (candidates.Count == 0) return;
        var (character, stack) = candidates[0];
        var session = new PoseEditSession(new ScenePose(model), character);
        var keys = stack.Keys;
        int n = keys.Count;

        session.ToggleStackFlag(stack, AnimationBankEditor.CycleFlag);

        Assert.True(stack.IsCycle);
        int expected = keys[n - 1].Time > keys[n - 2].Time ? keys[n - 1].Time
            : n == 2 ? keys[0].Time + 10 : 2 * keys[n - 2].Time - keys[n - 3].Time;
        Assert.Equal(expected, stack.Keys[n - 1].Time);

        // Turning it off again keeps the times.
        session.ToggleStackFlag(stack, AnimationBankEditor.CycleFlag);
        Assert.False(stack.IsCycle);
        Assert.Equal(expected, stack.Keys[n - 1].Time);
        Assert.True(session.Undo());
        Assert.True(session.Undo());
        Assert.Equal(keys, stack.Keys);
    }

    // ---- the Mixer's Merge, Extract and Audio actions ---------------------------

    [SceneTheory, SceneSamples]
    public void MergeStacks_BakesTheMixAndDeletesTheSources(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstAnimation(model) is not var (character, animation)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        while (animation.Stacks.Count < 2) session.DuplicateStack(animation.Stacks[0]);
        var sources = animation.Stacks.Take(2).ToList();
        session.SetCurrentStack(sources[0]);
        int before = animation.Stacks.Count;
        var times = sources.SelectMany(s => s.Keys.Skip(1).Select(k => k.Time)).Append(0)
            .Distinct().OrderBy(t => t).ToList();
        var sampled = times.ToDictionary(t => t, t => session.Evaluator.LocalTransforms(animation, t));

        var merged = session.MergeStacks(animation, sources);

        Assert.Equal(before - 1, animation.Stacks.Count);
        Assert.DoesNotContain(sources[0], animation.Stacks);
        Assert.DoesNotContain(sources[1], animation.Stacks);
        Assert.Same(merged, animation.CurrentStack);
        Assert.Equal(times, merged.Keys.Select(k => k.Time).ToList());
        Assert.Single(animation.SelectedStacks);
        Assert.Same(merged, animation.SelectedStacks[0]);

        // The baked stack reproduces the mixed pose at every collected key time.
        session.Evaluator.Invalidate();
        foreach (int time in times)
        {
            var after = session.Evaluator.LocalTransforms(animation, time);
            for (int i = 0; i < after.Length; i++)
            {
                var a = sampled[time][i];
                var b = after[i];
                Assert.True(System.Numerics.Vector3.Distance(a.Translation, b.Translation)
                            < 2e-2f * Math.Max(1f, a.Translation.Length()),
                    $"{character.Controllers[i].Name} at {time}: {a.Translation} != {b.Translation}");
            }
        }
        Assert.True(session.Undo());
        Assert.Equal(before, animation.Stacks.Count);
        Assert.Contains(sources[0], animation.Stacks);
        Assert.Contains(sources[1], animation.Stacks);
    }

    [SceneTheory, SceneSamples]
    public void MergeStacks_PairsOneStackWithTheOneBelowIt(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstAnimation(model) is not var (character, animation)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        while (animation.Stacks.Count < 2) session.DuplicateStack(animation.Stacks[0]);
        var top = animation.Stacks[0];
        var below = animation.Stacks[1];
        session.SetCurrentStack(top);
        int before = animation.Stacks.Count;

        var merged = session.MergeStacks(animation, [top]);

        Assert.Equal(before - 1, animation.Stacks.Count);
        Assert.DoesNotContain(top, animation.Stacks);
        Assert.DoesNotContain(below, animation.Stacks);
        Assert.Same(merged, animation.Stacks[0]);
    }

    [SceneTheory, SceneSamples]
    public void ExtractChannels_MovesTheTracksChannelsIntoANewStack(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstAnimation(model) is not var (character, animation)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var source = animation.Stacks[^1];
        var track = source.Tracks.FirstOrDefault(t => t.Target != null && (t.Channels & AnimChannels.Translate) != 0);
        if (track == null) return;
        session.SetCurrentStack(source);
        int before = animation.Stacks.Count;
        var keys = source.Keys;
        int count = track.Keys.Count;
        var original = track.Channels;   // the source track's mask changes below

        var extracted = session.ExtractChannels(source, AnimChannels.Translate, [track]);

        Assert.Equal(before + 1, animation.Stacks.Count);
        Assert.StartsWith(track.Target!.Name!, extracted.Name);
        Assert.Same(extracted, animation.CurrentStack);
        Assert.Single(animation.SelectedStacks);
        Assert.Equal(keys, extracted.Keys);
        var copy = extracted.TrackFor(track.Target);
        Assert.NotNull(copy);
        Assert.Equal(original & AnimChannels.Translate, copy!.Channels);
        Assert.Equal(count, copy.Keys.Count);
        uint rest = (uint)(original & ~AnimChannels.Translate);
        if (rest == 0) Assert.Null(source.TrackFor(track.Target));
        else Assert.Equal((AnimChannels)rest, source.TrackFor(track.Target)!.Channels);

        Assert.True(session.Undo());
        Assert.Equal(before, animation.Stacks.Count);
        Assert.Equal(original, source.TrackFor(track.Target)!.Channels);
    }

    [SceneTheory, SceneSamples]
    public void AddAudioStack_WritesAnEntryAndTwoKeys(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        if (character.Animations.FirstOrDefault(a => a.Stacks.Count > 0) is not { } animation) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var samples = new float[2000];
        for (int i = 0; i < samples.Length; i++) samples[i] = MathF.Sin(i * 0.05f) * 0.4f;
        byte[] sound = WaveFile.EncodeMono(samples, 8000);   // 0.25 s

        var stack = session.AddAudioStack(animation, "step.wav", sound, 0.25);

        Assert.IsType<AudioStack>(stack);
        Assert.Same(stack, animation.CurrentStack);
        Assert.Contains(stack, animation.Stacks);
        Assert.Equal(2u, stack.Flags);
        Assert.Equal(AnimationBankEditor.StackColor(animation.Stacks.Count - 1), stack.Color);
        Assert.Equal(2, stack.Keys.Count);
        uint rate = model.Scene.FrameRate;
        Assert.Equal((int)Math.Ceiling(0.25 * (rate == 0 ? 30 : rate)), stack.Keys[1].Time);
        var audioObject = Assert.Single(character.Children.OfType<AudioObject>());
        var entry = Assert.Single(character.Children.OfType<AudioEntry>());
        Assert.Equal("step.wav", entry.FileName);
        Assert.Equal(0.25, entry.DurationSeconds, 3);
        // The audio layout
        // the character points at the object, it is a controller with a pose slot, and the
        // entry holds decoded stereo PCM at 44.1 kHz - not the WAV file it came from.
        Assert.Same(audioObject, character.AudioObject);
        Assert.Contains(audioObject, character.Controllers);
        Assert.Equal((int)Math.Round(0.25 * WaveFile.EmbeddedSampleRate), entry.Audio!.FrameCount);
        Assert.Equal(WaveFile.EmbeddedSampleRate, entry.Audio!.SampleRate);
        Assert.Equal(2, entry.Audio!.Channels);
        Assert.Equal(entry.Audio!.FrameCount * 4, entry.Data.Length);
        Assert.Null(WaveFile.TryDecode(entry.Data));
        var audioTrack = Assert.IsType<AudioTrack>(Assert.Single(stack.Tracks));
        Assert.Same(audioObject, audioTrack.Target);
        var events = ((AudioStack)stack).Events;
        Assert.Single(events);
        Assert.Same(entry, events[0].Entry);
        Assert.Equal(0, events[0].StartFrame);

        // The added classes are in the schema and the scene reloads with the audio stack intact.
        string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sloppyanimatsu-mixer-audio-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(temp);
            var reloaded = SceneModel.Load(temp);
            var reloadedStack = reloaded.Scene.Descendants<AudioStack>().FirstOrDefault(s => s.Name == stack.Name);
            Assert.NotNull(reloadedStack);
            Assert.Single(reloadedStack!.Events);
            Assert.Equal("step.wav", reloadedStack.Events[0].Entry?.FileName);
        }
        finally { System.IO.File.Delete(temp); }

        Assert.True(session.Undo());
        Assert.DoesNotContain(stack, animation.Stacks);
        Assert.Empty(character.Children.OfType<AudioObject>());
        Assert.Empty(character.Children.OfType<AudioEntry>());
    }

    [SceneTheory, SceneSamples]
    public void SelectedStacks_AreRuntimeOnlyAndDropRemovedStacks(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstAnimation(model) is not var (character, animation)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        while (animation.Stacks.Count < 2) session.DuplicateStack(animation.Stacks[0]);
        var chosen = animation.Stacks.Take(2).ToList();
        animation.SelectStacks(chosen);
        Assert.Equal(chosen, animation.SelectedStacks);

        // The __SelectedStackList has no serializer: the selection is not in the file.
        string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sloppyanimatsu-mixer-selection-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(temp);
            var reloaded = SceneModel.Load(temp);
            var again = reloaded.Scene.Descendants<Animation>().First(a => a.Name == animation.Name);
            Assert.Empty(again.SelectedStacks);
        }
        finally { System.IO.File.Delete(temp); }

        session.SetCurrentStack(chosen[0]);
        Assert.True(session.DeleteStack(chosen[0]));
        Assert.DoesNotContain(chosen[0], animation.SelectedStacks);
        Assert.Contains(chosen[1], animation.SelectedStacks);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static Character? FirstCharacter(SceneModel model) =>
        model.Scene.Characters.FirstOrDefault(c => c.AnimBank != null && c.Controllers.Count > 0);

    private static (Character, Animation)? FirstAnimation(SceneModel model)
    {
        foreach (var character in model.Scene.Characters)
            if (character.Animations.FirstOrDefault(a => a.Stacks.Count > 0) is { } animation)
                return (character, animation);
        return null;
    }

    /// <summary>The character's animations, their stacks (order, current, flags) and track counts, as text.</summary>
    private static string Bank(Character character) =>
        $"current={character.CurrentAnimation?.Name}|" + string.Join("|", character.Animations.Select(a =>
            $"{a.Name}[{a.CurrentStack?.Name}]:" + string.Join(",", a.Stacks.Select(s => $"{s.Name}/{s.Flags}/{s.Opacity}/{s.Tracks.Count()}"))));

    /// <summary>Stacks, tracks and key values of an animation or a stack, without names of the copied objects themselves.</summary>
    private static string Describe(SceneObject o) => o switch
    {
        Animation a => string.Join("||", a.Stacks.Select(Describe)) + $"|current={a.Stacks.ToList().IndexOf(a.CurrentStack!)}",
        AnimStack s => $"{s.Flags}/{s.Opacity}/{string.Join(",", s.Keys)}:" + string.Join("|", s.Tracks.Select(t =>
            $"{t.Name}>{t.Target?.Name}:{t.Channels}:" +
            string.Join(";", t.Keys.Select(k => k is TransformKey tk ? $"{tk.Value}@{tk.Space?.Name}" : "-")))),
        _ => o.ToString()!,
    };

    // ---- the bank panel's sort and search (AnimBankView) and the free sort's reorder ------------

    private static int Pos(SceneObject parent, SceneObject child) => parent.Children.ToList().IndexOf(child);

    [Fact]
    public void NaturalComparer_OrdersNumbersByValueAndIgnoresCase()
    {
        string[] names = ["walk10", "Walk2", "idle", "walk1", "Attack"];
        Assert.Equal(["Attack", "idle", "walk1", "Walk2", "walk10"],
            names.OrderBy(n => n, AnimBankView.NaturalComparer.Instance).ToArray());
        Assert.True(AnimBankView.Matches("Run_Fast", "fast"));
        Assert.True(AnimBankView.Matches("Run_Fast", "  "));
        Assert.False(AnimBankView.Matches("Run_Fast", "walk"));
    }

    [SceneTheory, SceneSamples]
    public void PlaceInBank_KeepsTheFreeOrder_AndAlphabeticalIsAViewOnly(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var root = character.AnimBank!;
        var a = session.CreateFolder();
        var b = session.CreateFolder();
        var c = session.CreateFolder();
        string bank = Bank(character);

        // c before a: the scene's own child order changes, as one undo step.
        Assert.True(session.PlaceInBank([c], root, a));
        int ia = Pos(root, a), ib = Pos(root, b), ic = Pos(root, c);
        Assert.True(ic < ia && ia < ib);
        // Placing it where it already is changes nothing.
        Assert.False(session.PlaceInBank([c], root, a));

        // The alphabetical order is computed, so the free order is still there afterwards.
        var sorted = AnimBankView.Order(root.Children, AnimBankSort.Alphabetical).OfType<Folder>().ToList();
        Assert.Equal(sorted.Select(f => f.Name).OrderBy(n => n, AnimBankView.NaturalComparer.Instance), sorted.Select(f => f.Name));
        Assert.True(Pos(root, c) < Pos(root, a));
        Assert.Equal(root.Children, AnimBankView.Order(root.Children, AnimBankSort.Free));

        // At the end of a folder (before = null), then the moved row may not be its own anchor.
        Assert.True(session.PlaceInBank([a], b, null));
        Assert.Same(b, a.Parent);
        Assert.Throws<InvalidOperationException>(() => session.PlaceInBank([c], root, c));

        Assert.True(session.Undo());
        Assert.True(session.Undo());
        Assert.Equal(bank, Bank(character));
    }

    [SceneTheory, SceneSamples]
    public void AddedSort_ListsNewestFirst_AndSurvivesRearranging(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var root = character.AnimBank!;
        var a = session.CreateAnimation();
        var b = session.CreateAnimation();
        var c = session.CreateAnimation();
        List<SceneObject> Added(bool reversed = false) =>
            AnimBankView.Order(root.Children, AnimBankSort.Added, reversed).Where(o => o == a || o == b || o == c).ToList();

        // Unnumbered animations are as new as their place in the bank: appended, so c is the newest.
        Assert.Equal([c, b, a], Added());
        Assert.Equal([a, b, c], Added(reversed: true));

        // Rearranging numbers the bank first, so the Added order does not follow the new free order.
        Assert.True(session.PlaceInBank([c], root, a));
        Assert.True(Pos(root, c) < Pos(root, a));
        Assert.True(AnimBankView.AddedNumber(a) > 0);
        Assert.Equal([c, b, a], Added());

        // A copy is new: unnumbered, so it lists above every numbered animation.
        var d = session.CopyAnimation(a);
        Assert.Equal(0, AnimBankView.AddedNumber(d));
        Assert.Same(d, AnimBankView.Order(root.Children, AnimBankSort.Added).OfType<Animation>().First());

        // A folder lists by the newest animation inside it (c is newer than b), still above the animations.
        var older = session.CreateFolder();
        var newer = session.CreateFolder();
        session.MoveToFolder([b], newer);
        session.MoveToFolder([c], older);
        List<SceneObject> Folders(bool reversed = false) =>
            AnimBankView.Order(root.Children, AnimBankSort.Added, reversed).Where(o => o == older || o == newer).ToList();
        Assert.Equal([older, newer], Folders());
        Assert.Equal([newer, older], Folders(reversed: true));
        Assert.IsType<Folder>(AnimBankView.Order(root.Children, AnimBankSort.Added).First());

        // Folders stay first in every sort; A-Z reversed is Z-A.
        var z = AnimBankView.Order(root.Children, AnimBankSort.Alphabetical, reversed: true).OfType<Animation>().Select(x => x.Name).ToList();
        Assert.Equal(z.OrderByDescending(n => n, AnimBankView.NaturalComparer.Instance), z);
    }

    [Fact]
    public void AnimBankSort_DefaultsToAlphabetical_AndRoundTrips()
    {
        if (TestFiles.Preferences is not { } path) return;
        var preferences = Preferences.Load(path);
        Assert.Equal(AnimBankSort.Alphabetical, preferences.AnimBankSort);
        Assert.False(preferences.AnimBankSortReversed);
        string copy = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-banksort-{Guid.NewGuid():N}.akt");
        try
        {
            preferences.AnimBankSort = AnimBankSort.Added;
            preferences.AnimBankSortReversed = true;
            preferences.SaveAs(copy);
            var loaded = Preferences.Load(copy);
            Assert.Equal(AnimBankSort.Added, loaded.AnimBankSort);
            Assert.True(loaded.AnimBankSortReversed);
        }
        finally
        {
            File.Delete(copy);
        }
    }
}

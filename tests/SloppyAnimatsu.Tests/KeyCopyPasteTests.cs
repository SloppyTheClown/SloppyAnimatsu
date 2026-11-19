using System.Numerics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>Copy, paste and paste-after of keys.</summary>
public class KeyCopyPasteTests
{
    [SceneTheory, SceneSamples]
    public void Paste_WritesTheCopiedKeysInOrder_AndUndoRedo(string path)
    {
        if (Candidate(TestFiles.LoadScene(path)) is not var (session, stack, a, _)) return;
        var track = stack.TrackFor(a)!;
        var original = Values(stack);

        var clipboard = session.Editor.CopyKeys(stack, [0, 1], [a], keepSpaces: true);
        Assert.Equal([10, stack.Keys[1].Time - stack.Keys[0].Time], clipboard.Gaps);
        var entry = Assert.Single(clipboard.Entries);
        Assert.Same(a, entry.Target);
        Assert.Equal(2, entry.Keys.Count);

        // Two copied keys onto three selected keys: the third is left alone.
        var third = Key(track, 3);
        Assert.Equal(2, session.PasteKeys(stack, clipboard, [1, 2, 3], [a]));
        AssertClose(entry.Keys[0].Value, Key(track, 1));
        AssertClose(entry.Keys[1].Value, Key(track, 2));
        Assert.Equal(third, Key(track, 3));
        var pasted = Values(stack);

        Assert.True(session.Undo());
        Assert.Equal(original, Values(stack));
        Assert.True(session.Redo());
        Assert.Equal(pasted, Values(stack));
    }

    [SceneTheory, SceneSamples]
    public void PasteAt_OverwritesAKeyEntirely_CreatesOneBetweenKeys_AndStopsAtTheEndKey(string path)
    {
        if (Candidate(TestFiles.LoadScene(path)) is not var (session, stack, a, _)) return;
        var track = stack.TrackFor(a)!;
        var editor = session.Editor;
        stack.SetKeyFlags(2, stack.Keys[2].Flags, 3, 4);
        var clipboard = editor.CopyKeys(stack, [2], [a], keepSpaces: true);
        var copied = clipboard.Entries[0].Keys[0].Value;
        Assert.Equal(stack.Keys[2], Assert.Single(clipboard.StackKeys!));

        // Onto an existing key: same key count, the values, flags and fades are the copied ones.
        int n = stack.Keys.Count;
        Assert.Equal(1, editor.PasteKeysAt(stack, clipboard, stack.Keys[1].Time, [a]));
        Assert.Equal(n, stack.Keys.Count);
        AssertClose(copied, Key(track, 1));
        Assert.Equal((stack.Keys[2].Flags, (ushort)3, (ushort)4), (stack.Keys[1].Flags, stack.Keys[1].FadeIn, stack.Keys[1].FadeOut));

        // Between two keys a key is created there first.
        if (stack.Keys[2].Time - stack.Keys[1].Time >= 2)
        {
            int frame = stack.Keys[1].Time + 1;
            Assert.Equal(2, editor.PasteKeysAt(stack, clipboard, frame, [a]));
            Assert.Equal(n + 1, stack.Keys.Count);
            Assert.Equal(frame, stack.Keys[2].Time);
            AssertClose(copied, Key(track, 2));
        }

        // The end key is never pasted onto.
        Assert.Equal(-1, editor.PasteKeysAt(stack, clipboard, stack.Keys[^1].Time, [a]));

        // The whole-key paste onto selected keys also takes the flags and fades.
        Assert.True(session.PasteKeys(stack, clipboard, [3], [a], withKeyFlags: true) > 0);
        Assert.Equal(((ushort)3, (ushort)4), (stack.Keys[3].FadeIn, stack.Keys[3].FadeOut));
    }

    [SceneTheory, SceneSamples]
    public void OneCopiedKey_FillsEverySelectedKey_ButNeverTheEndKey(string path)
    {
        if (Candidate(TestFiles.LoadScene(path)) is not var (session, stack, a, _)) return;
        var track = stack.TrackFor(a)!;
        int n = stack.Keys.Count;
        var endKey = Key(track, n - 1);

        var clipboard = session.Editor.CopyKeys(stack, [2], [a], keepSpaces: true);
        var copied = clipboard.Entries[0].Keys[0].Value;
        Assert.Equal(3, session.PasteKeys(stack, clipboard, [1, 2, 3, n - 1], [a]));
        for (int i = 1; i <= 3; i++) AssertClose(copied, Key(track, i));
        Assert.Equal(endKey, Key(track, n - 1));

        // Pasting onto key 0 mirrors it into the end key.
        session.PasteKeys(stack, clipboard, [0], [a]);
        AssertClose(copied, Key(track, 0));
        AssertClose(copied, Key(track, n - 1));
    }

    [SceneTheory, SceneSamples]
    public void ChannelFilter_LimitsWhatIsPasted(string path)
    {
        if (Candidate(TestFiles.LoadScene(path)) is not var (session, stack, a, _)) return;
        var track = stack.TrackFor(a)!;
        if ((track.Channels & AnimChannels.Translate) == 0) return;
        var before = Key(track, 1);

        var clipboard = session.Editor.CopyKeys(stack, [2], [a], AnimChannels.Translate, keepSpaces: true);
        Assert.Equal(track.Channels & AnimChannels.Translate, clipboard.Entries[0].Mask);
        session.PasteKeys(stack, clipboard, [1], [a]);
        var after = Key(track, 1);
        var copied = clipboard.Entries[0].Keys[0].Value;
        Assert.Equal(before.Rotation, after.Rotation);
        Assert.Equal(before.Scale, after.Scale);
        Assert.Equal(Pick(copied.Translation, before.Translation, track.Channels), after.Translation);
    }

    [SceneTheory, SceneSamples]
    public void Paste_DealsOneEntryOutToAnotherController(string path)
    {
        if (Candidate(TestFiles.LoadScene(path)) is not var (session, stack, a, b)) return;

        var clipboard = session.Editor.CopyKeys(stack, [1], [a], keepSpaces: true);
        Assert.Equal(1, session.PasteKeys(stack, clipboard, [2], [b]));
        AssertClose(clipboard.Entries[0].Keys[0].Value, Key(stack.TrackFor(b)!, 2));
    }

    [SceneTheory, SceneSamples]
    public void PasteAfter_InsertsTheCopiedKeysWithTheirGaps_AndSaves(string path)
    {
        if (Candidate(TestFiles.LoadScene(path)) is not var (session, stack, a, b)) return;
        var times = stack.Keys.Select(k => k.Time).ToArray();
        int n = times.Length;
        var trackA = stack.TrackFor(a)!;
        var trackB = stack.TrackFor(b)!;
        var original = Values(stack);
        var heldB = Key(trackB, 2);

        var clipboard = session.Editor.CopyKeys(stack, [1, 2], [a], keepSpaces: true);
        int[] gaps = [times[1] - times[0], times[2] - times[1]];
        Assert.Equal(gaps, clipboard.Gaps);

        // After keys {0, 2}: the last selected key (2) decides.
        Assert.Equal(3, session.PasteKeysAfter(stack, clipboard, [0, 2], [a]));
        var newTimes = stack.Keys.Select(k => k.Time).ToArray();
        Assert.Equal(n + 2, newTimes.Length);
        Assert.Equal(times[..3], newTimes[..3]);
        Assert.Equal(times[2] + gaps[0], newTimes[3]);
        Assert.Equal(times[2] + gaps[0] + gaps[1], newTimes[4]);
        Assert.Equal(times[3..].Select(t => t + gaps.Sum()), newTimes[5..]);
        AssertClose(clipboard.Entries[0].Keys[0].Value, Key(trackA, 3));
        AssertClose(clipboard.Entries[0].Keys[1].Value, Key(trackA, 4));
        // A track that is not pasted holds the last selected key.
        Assert.Equal(heldB, Key(trackB, 3));
        Assert.Equal(heldB, Key(trackB, 4));
        Assert.All(stack.Tracks, t => Assert.Equal(n + 2, t.Keys.Count));
        var pasted = Values(stack);

        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-paste-{Guid.NewGuid():N}.akt");
        try
        {
            stack.Model.Save(temp);
            var reloaded = TestFiles.LoadScene(temp);
            var stack2 = reloaded.Scene.Characters.SelectMany(c => c.Animations).SelectMany(an => an.Stacks)
                .First(s => s.Name == stack.Name && s.Keys.Count == n + 2);
            Assert.Equal(pasted, Values(stack2));
        }
        finally { File.Delete(temp); }

        Assert.True(session.Undo());
        Assert.Equal(original, Values(stack));
    }

    [SceneTheory, SceneSamples]
    public void CopyWithoutKeepSpaces_StoresTheParentSpaceValue(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
                foreach (var stack in animation.Stacks.Where(s => s.IsEnabled && s.Keys.Count >= 3))
                    foreach (var track in stack.Tracks)
                    {
                        if (track.Target is not { } target || target is ControllerIK || track.Keys.Any(k => k is IKKey)) continue;
                        int key = Enumerable.Range(0, stack.Keys.Count - 1).FirstOrDefault(i => track.Keys[i] is TransformKey { Space: not null }, -1);
                        if (key < 0) continue;
                        var session = new PoseEditSession(new ScenePose(model), character);
                        int ci = session.Evaluator.IndexOf(target);
                        if (ci < 0) continue;
                        var world = session.Evaluator.WorldMatrices(animation, stack.Keys[key].Time)[ci];

                        var clipboard = session.Editor.CopyKeys(stack, [key], [target]);
                        Assert.Null(clipboard.Entries[0].Keys[0].Space);
                        session.PasteKeys(stack, clipboard, [key], [target]);
                        Assert.Null(((TransformKey)track.Keys[key]!).Space);
                        // Same pose at that key, now expressed in the parent's space.
                        var after = session.Evaluator.WorldMatrices(animation, stack.Keys[key].Time)[ci];
                        for (int r = 0; r < 4; r++)
                            for (int c = 0; c < 4; c++)
                                Assert.True(MathF.Abs(world[r, c] - after[r, c]) < 1e-2f * MathF.Max(1, MathF.Abs(world[r, c])),
                                    $"{target.Name} key {key}: [{r},{c}] {world[r, c]} vs {after[r, c]}");
                        return;
                    }
    }

    [SceneTheory, SceneSamples]
    public void CopyPaste_IKKey_CarriesThePoleAngleAndIKFlag_ButNotTheRefArray(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (IKCandidate(model, reverseFoot: false) is not var (session, stack, ik)) return;
        var track = stack.TrackFor(ik)!;
        int n = stack.Keys.Count;
        int source = 0, target = 1;
        for (int i = 1; i < n - 1; i++)
            if (!SameValue((IKKey)track.Keys[0]!, (IKKey)track.Keys[i]!)) { target = i; break; }
        var refs = ((IKKey)track.Keys[target]!).ChainTransforms.ToArray();

        var clipboard = session.Editor.CopyKeys(stack, [source], [ik], keepSpaces: true);
        var entry = Assert.Single(clipboard.Entries);
        Assert.Equal(track.Channels, entry.Mask);
        var copied = Assert.Single(entry.Keys);
        var sourceKey = (IKKey)track.Keys[source]!;
        Assert.Equal(sourceKey.PoleAngle, copied.PoleAngle);
        Assert.Equal((sourceKey.Flags & 1) != 0, copied.IkKey);

        Assert.Equal(1, session.PasteKeys(stack, clipboard, [target], [ik]));
        var pasted = (IKKey)track.Keys[target]!;
        AssertClose(copied.Value, pasted.Value);
        Assert.Equal(copied.PoleAngle, pasted.PoleAngle);
        Assert.Equal(copied.IkKey, (pasted.Flags & 1) != 0);
        // The clipboard has no chain: a key that already has one keeps it.
        Assert.Equal(refs, pasted.ChainTransforms);

        var pastedText = Values(stack);
        Assert.True(session.Undo());
        Assert.NotEqual(pastedText, Values(stack));
        Assert.True(session.Redo());
        Assert.Equal(pastedText, Values(stack));
    }

    [SceneTheory, SceneSamples]
    public void CopyPaste_IKFlag_MovesBetweenIKAndFKKeys(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (IKCandidate(model, reverseFoot: false) is not var (session, stack, ik)) return;
        var track = stack.TrackFor(ik)!;
        int n = stack.Keys.Count;
        int ikKey = Enumerable.Range(0, n - 1).FirstOrDefault(i => ((IKKey)track.Keys[i]!).Flags is 1, -1);
        int fkKey = Enumerable.Range(0, n - 1).FirstOrDefault(i => (((IKKey)track.Keys[i]!).Flags & 1) == 0, -1);
        if (ikKey < 0 || fkKey < 0) return;
        var original = Values(stack);

        var clipboard = session.Editor.CopyKeys(stack, [ikKey], [ik], keepSpaces: true);
        session.PasteKeys(stack, clipboard, [fkKey], [ik]);
        Assert.True((((IKKey)track.Keys[fkKey]!).Flags & 1) != 0);
        Assert.True(session.Undo());
        Assert.Equal(original, Values(stack));

        clipboard = session.Editor.CopyKeys(stack, [fkKey], [ik], keepSpaces: true);
        session.PasteKeys(stack, clipboard, [ikKey], [ik]);
        Assert.Equal(0u, ((IKKey)track.Keys[ikKey]!).Flags & 1);
        Assert.True(session.Undo());
        Assert.Equal(original, Values(stack));
    }

    [SceneTheory, SceneSamples]
    public void CopyPaste_ReverseFootKey_CarriesTheFourAnglesAndTheIKFlag(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (IKCandidate(model, reverseFoot: true) is not var (session, stack, foot)) return;
        var track = stack.TrackFor(foot)!;
        int n = stack.Keys.Count;
        // A key with a real angle, so that the copy has something to carry.
        int source = Enumerable.Range(0, n - 1).FirstOrDefault(i =>
            track.Keys[i] is ReverseFootKey k && (k.RollAngle != 0 || k.TwistAngle != 0 || k.BankAngle != 0 || k.PoleAngle != 0), -1);
        if (source < 0) source = 1;
        int target = source == 0 ? 1 : 0;
        var refs = ((ReverseFootKey)track.Keys[target]!).ChainTransforms.ToArray();

        var clipboard = session.Editor.CopyKeys(stack, [source], [foot], keepSpaces: true);
        var copied = Assert.Single(Assert.Single(clipboard.Entries).Keys);
        Assert.Equal(1, session.PasteKeys(stack, clipboard, [target], [foot]));

        var pasted = (ReverseFootKey)track.Keys[target]!;
        AssertClose(copied.Value, pasted.Value);
        Assert.Equal(copied.PoleAngle, pasted.PoleAngle);
        Assert.Equal(copied.RollAngle, pasted.RollAngle);
        Assert.Equal(copied.TwistAngle, pasted.TwistAngle);
        Assert.Equal(copied.BankAngle, pasted.BankAngle);
        Assert.Equal(copied.IkKey, (pasted.Flags & 1) != 0);
        Assert.Equal(refs, pasted.ChainTransforms);
    }

    [SceneTheory, SceneSamples]
    public void PasteAfter_InsertsTheCopiedIKKeys_AndTheySurviveSaveAndReload(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (IKCandidate(model, reverseFoot: false) is not var (session, stack, ik)) return;
        var track = stack.TrackFor(ik)!;
        var times = stack.Keys.Select(k => k.Time).ToArray();
        int n = times.Length;
        var original = Values(stack);

        var clipboard = session.Editor.CopyKeys(stack, [1, 2], [ik], keepSpaces: true);
        int[] gaps = [times[1] - times[0], times[2] - times[1]];
        Assert.Equal(gaps, clipboard.Gaps);
        Assert.Equal(3, session.PasteKeysAfter(stack, clipboard, [0, 2], [ik]));

        var newTimes = stack.Keys.Select(k => k.Time).ToArray();
        Assert.Equal(n + 2, newTimes.Length);
        Assert.Equal(times[2] + gaps[0], newTimes[3]);
        Assert.Equal(times[2] + gaps[0] + gaps[1], newTimes[4]);
        Assert.Equal(times[3..].Select(t => t + gaps.Sum()), newTimes[5..]);
        Assert.Equal(n + 2, track.Keys.Count);
        Assert.All(track.Keys, k => Assert.Equal(ik.Chain.Count, ((IKKey)k!).ChainTransforms.Count));
        for (int k = 0; k < 2; k++)
        {
            AssertClose(clipboard.Entries[0].Keys[k].Value, ((IKKey)track.Keys[3 + k]!).Value);
            Assert.Equal(clipboard.Entries[0].Keys[k].PoleAngle, ((IKKey)track.Keys[3 + k]!).PoleAngle);
        }
        var pasted = Values(stack);

        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-paste-ik-{Guid.NewGuid():N}.akt");
        try
        {
            stack.Model.Save(temp);
            var reloaded = TestFiles.LoadScene(temp);
            var stack2 = reloaded.Scene.Characters.SelectMany(c => c.Animations).SelectMany(a => a.Stacks)
                .First(s => s.Name == stack.Name && s.Keys.Count == n + 2);
            Assert.Equal(pasted, Values(stack2));
        }
        finally { File.Delete(temp); }

        Assert.True(session.Undo());
        Assert.Equal(original, Values(stack));
    }

    /// <summary>A stack of at least 4 keys with an IK or reverse-foot track that keys its whole chain.</summary>
    private static (PoseEditSession, AnimStack, ControllerIK)? IKCandidate(SceneModel model, bool reverseFoot)
    {
        foreach (var character in model.Scene.Characters)
        {
            var session = new PoseEditSession(new ScenePose(model), character);
            foreach (var animation in character.Animations)
                foreach (var stack in animation.Stacks)
                {
                    if (stack.Keys.Count < 4) continue;
                    foreach (var ik in character.IKControllers)
                    {
                        if ((ik is ControllerReverseFoot) != reverseFoot) continue;
                        if (stack.TrackFor(ik) is not { } track || track.Keys.Count != stack.Keys.Count) continue;
                        if (track.Keys.Take(track.Keys.Count - 1).Any(k => k is not IKKey)) continue;
                        if (ik.Chain.Count < 2 || ik.Chain.Any(j => stack.TrackFor(j) == null)) continue;
                        return (session, stack, ik);
                    }
                }
        }
        return null;
    }

    private static bool SameValue(IKKey a, IKKey b) =>
        Vector3.Distance(a.Value.Translation, b.Value.Translation) < 1e-6f
        && MathF.Abs(Quaternion.Dot(a.Value.Rotation, b.Value.Rotation)) > 1 - 1e-6f
        && Vector3.Distance(a.Value.Scale, b.Value.Scale) < 1e-6f;

    /// <summary>A stack of at least 5 keys with MRS tracks for two controllers.</summary>
    private static (PoseEditSession, AnimStack, Object3D, Object3D)? Candidate(SceneModel model)
    {
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
                foreach (var stack in animation.Stacks)
                {
                    if (!stack.IsEnabled || stack.Keys.Count < 5) continue;
                    var mrs = stack.Tracks
                        .Where(t => t.Target is { } o && o is not (ControllerIK or ControllerReverseFoot) && !t.Keys.Any(k => k is IKKey) && t.Keys.Count == stack.Keys.Count && (t.Channels & KeyEditor.MrsChannels) == KeyEditor.MrsChannels)
                        .Select(t => t.Target!).Take(2).ToList();
                    if (mrs.Count < 2) continue;
                    return (new PoseEditSession(new ScenePose(model), character), stack, mrs[0], mrs[1]);
                }
        return null;
    }

    private static Transform Key(AnimTrack track, int i) => ((TransformKey)track.Keys[i]!).Value;

    private static Vector3 Pick(Vector3 value, Vector3 kept, AnimChannels channels) => new(
        (channels & AnimChannels.TranslateX) != 0 ? value.X : kept.X,
        (channels & AnimChannels.TranslateY) != 0 ? value.Y : kept.Y,
        (channels & AnimChannels.TranslateZ) != 0 ? value.Z : kept.Z);

    private static void AssertClose(Transform expected, Transform actual)
    {
        Assert.True(Vector3.Distance(expected.Translation, actual.Translation) < 1e-5f, $"{expected} vs {actual}");
        Assert.True(MathF.Abs(Quaternion.Dot(expected.Rotation, actual.Rotation)) > 1 - 1e-6f, $"{expected} vs {actual}");
        Assert.True(Vector3.Distance(expected.Scale, actual.Scale) < 1e-5f, $"{expected} vs {actual}");
    }

    private static string Values(AnimStack stack) =>
        string.Join(";", stack.Keys) + "|" + string.Join("|", stack.Tracks.Select(t =>
            string.Join(";", t.Keys.Select(k => k is TransformKey tk ? ValueText(tk) : "-"))));

    private static string ValueText(TransformKey key) => key switch
    {
        ReverseFootKey rf => $"{rf.Value}@{rf.Space?.Name}[{rf.PoleAngle},{rf.RollAngle},{rf.TwistAngle},{rf.BankAngle},{rf.Flags}]{{{Chain(rf)}}}",
        IKKey ik => $"{ik.Value}@{ik.Space?.Name}[{ik.PoleAngle},{ik.Flags}]{{{Chain(ik)}}}",
        _ => $"{key.Value}@{key.Space?.Name}",
    };

    private static string Chain(IKKey key) => string.Join(",", key.ChainTransforms);
}

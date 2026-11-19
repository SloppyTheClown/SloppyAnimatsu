using System.Numerics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

public class KeyEditingTests
{
    private const AnimChannels All = AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Scale;

    [Theory]
    [InlineData(false, 1f)]
    [InlineData(false, 0.6f)]
    [InlineData(false, 0.3f)]
    [InlineData(true, 1f)]
    [InlineData(true, 0.6f)]
    [InlineData(true, 0.3f)]
    public void InverseMix_ReproducesPoseThroughForwardMix(bool additive, float weight)
    {
        var random = new Random(7);
        float R(float range) => (float)(random.NextDouble() * 2 - 1) * range;
        for (int i = 0; i < 200; i++)
        {
            var below = new Transform(new Vector3(R(50), R(50), R(50)),
                Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(R(1), R(1), R(1)) + Vector3.UnitY * 0.01f), R(2)),
                new Vector3(1 + R(0.5f), 1 + R(0.5f), 1 + R(0.5f)));
            // Poses near the one below, so that the solved key stays within a half turn.
            var posed = new Transform(below.Translation + new Vector3(R(10), R(10), R(10)),
                below.Rotation * Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(R(1), R(1), R(1)) + Vector3.UnitX * 0.01f), R(0.8f * weight)),
                below.Scale * new Vector3(1 + R(0.2f), 1 + R(0.2f), 1 + R(0.2f)));

            var key = KeyEditor.InverseMix(below, posed, weight, additive);
            var forward = additive
                ? PoseEvaluator.MixAdditive(below, key, weight, All)
                : PoseEvaluator.MixOverride(below, key, weight, All);
            AssertClose(posed, forward);
        }
    }

    [SceneTheory, SceneSamples]
    public void SetKey_MixesToThePosedTransform(string path)
    {
        var model = TestFiles.LoadScene(path);
        var candidates = Candidates(model).Take(3).ToList();
        if (candidates.Count == 0) return; // nothing to edit in this scene
        foreach (var (character, animation, stack, track) in candidates)
        {
            var editor = new KeyEditor(character);
            var controller = track.Target!;
            var rest = editor.Evaluator.RestTransforms[editor.Evaluator.IndexOf(controller)];
            float time = stack.Keys[1].Time;
            var current = PoseEvaluator.Compose(rest, editor.Evaluator.MixDelta(animation, controller, time));
            var posed = new Transform(current.Translation + new Vector3(0.5f, -0.3f, 0.2f),
                current.Rotation * Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 3)), 0.2f),
                current.Scale * 1.1f);

            editor.SetKey(stack, 1, controller, posed);

            var mixed = PoseEvaluator.Compose(rest, editor.Evaluator.MixDelta(animation, controller, time));
            AssertClose(posed, mixed);
        }
    }

    [SceneTheory, SceneSamples]
    public void SetKey_SurvivesSaveAndReload(string path)
    {
        var model = TestFiles.LoadScene(path);
        var candidate = Candidates(model).FirstOrDefault();
        if (candidate.Track is null) return; // nothing to edit in this scene
        var (character, _, stack, track) = candidate;
        var editor = new KeyEditor(character);
        var controller = track.Target!;
        var rest = editor.Evaluator.RestTransforms[editor.Evaluator.IndexOf(controller)];
        editor.SetKey(stack, 1, controller, new Transform(rest.Translation + Vector3.One, rest.Rotation, rest.Scale));
        var written = ((TransformKey)track.Keys[1]!).Value;
        var tangent = ((TransformKey)track.TangentsOut[1]!).Value;

        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-edit-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(temp);
            var reloaded = TestFiles.LoadScene(temp);
            var again = Candidates(reloaded).First(c => c.Stack.Name == stack.Name && c.Animation.Name == candidate.Animation.Name
                                                        && c.Track.Target?.Name == controller.Name);
            Assert.Equal(written, ((TransformKey)again.Track.Keys[1]!).Value);
            Assert.Equal(tangent, ((TransformKey)again.Track.TangentsOut[1]!).Value);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [SceneTheory, SceneSamples]
    public void CreateKey_BetweenKeys_KeepsTheValues(string path)
    {
        var model = TestFiles.LoadScene(path);
        var candidate = Candidates(model).FirstOrDefault(c => c.Stack.Keys[1].Time - c.Stack.Keys[0].Time >= 2);
        if (candidate.Track is null) return; // nothing to edit in this scene
        var (character, _, stack, _) = candidate;
        var editor = new KeyEditor(character);
        var oldKeys = stack.Keys.ToArray();
        int frame = oldKeys[0].Time + 1;
        var tracks = stack.Tracks.ToList();
        var atFrame = tracks.Select(t => editor.Evaluator.LayerValue(stack, t, frame, out _)).ToList();
        var atKeys = tracks.Select(t => oldKeys.SkipLast(1).Select(k => editor.Evaluator.LayerValue(stack, t, k.Time, out _)).ToList()).ToList();

        int index = editor.CreateKey(stack, frame);

        Assert.Equal(1, index);
        Assert.Equal(oldKeys.Length + 1, stack.Keys.Count);
        Assert.Equal(frame, stack.Keys[1].Time);
        for (int j = 0; j < tracks.Count; j++)
        {
            Assert.Equal(stack.Keys.Count, tracks[j].Keys.Count);
            AssertClose(atFrame[j], editor.Evaluator.LayerValue(stack, tracks[j], frame, out _));
            for (int k = 0; k < oldKeys.Length - 1; k++)
                AssertClose(atKeys[j][k], editor.Evaluator.LayerValue(stack, tracks[j], oldKeys[k].Time, out _));
        }
    }

    [SceneTheory, SceneSamples]
    public void CreateKey_OnAKey_DuplicatesItAndShiftsTheRest(string path)
    {
        var model = TestFiles.LoadScene(path);
        var candidate = Candidates(model).FirstOrDefault();
        if (candidate.Track is null) return; // nothing to edit in this scene
        var (character, _, stack, track) = candidate;
        var editor = new KeyEditor(character);
        var oldTimes = stack.Keys.Select(k => k.Time).ToArray();
        var oldValue = ((TransformKey)track.Keys[1]!).Value;
        int gap = oldTimes[1] - oldTimes[0];

        int index = editor.CreateKey(stack, oldTimes[1]);

        Assert.Equal(2, index);
        var times = stack.Keys.Select(k => k.Time).ToArray();
        Assert.Equal(oldTimes.Length + 1, times.Length);
        Assert.Equal(oldTimes[..2], times[..2]);
        Assert.Equal(oldTimes[1..].Select(t => t + gap), times[2..]);
        Assert.Equal(oldValue, ((TransformKey)track.Keys[2]!).Value);
        Assert.Equal(times.Length, track.Keys.Count);
        Assert.Equal(times.Length, track.TangentsIn.Count);
        Assert.Equal(times.Length, track.TangentModes.Count);
    }

    /// <summary>
    /// An inserted key's tangent modes are the per-channel new-key defaults: 0x144 on the nine
    /// MRS channels of an IK or reverse-foot controller, 0x133 on its scalar channels (pole, roll,
    /// twist, bank) and on a reverse-foot shoe locator's track
    /// </summary>
    [SceneTheory, SceneSamples]
    public void CreateKey_NewKeysUseTheTargetsPerChannelDefaults(string path)
    {
        var model = TestFiles.LoadScene(path);
        var found = (from character in model.Scene.Characters
                     from animation in character.Animations
                     from stack in animation.Stacks
                     where stack.Keys.Count >= 2 && stack.Keys[1].Time - stack.Keys[0].Time >= 2
                     let ik = stack.Tracks.FirstOrDefault(t => t.Target is ControllerIK)
                     let locator = stack.Tracks.FirstOrDefault(t => t.Target is ReverseFootLocator)
                     where ik != null || locator != null
                     select (Character: character, Stack: stack, Ik: ik, Locator: locator)).FirstOrDefault();
        if (found.Character is null) return; // no IK or reverse-foot track in this scene
        var editor = new KeyEditor(found.Character);

        int index = editor.CreateKey(found.Stack, found.Stack.Keys[0].Time + 1);

        Assert.Equal(1, index);
        if (found.Ik is { } ikTrack)
        {
            var modes = ikTrack.TangentModes[index];
            Assert.All(modes.Take(9), m => Assert.Equal(0x144u, m.Raw));
            Assert.All(modes.Skip(9), m => Assert.Equal(0x133u, m.Raw));
        }
        if (found.Locator is { } locatorTrack)
            Assert.All(locatorTrack.TangentModes[index], m => Assert.Equal(0x133u, m.Raw));
    }

    /// <summary>Recomputing unedited tracks should give back nearly all stored tangents.</summary>
    [SceneTheory, SceneSamples]
    public void TangentRecompute_MatchesStoredTangents(string path)
    {
        var model = TestFiles.LoadScene(path);
        int total = 0, match = 0;
        foreach (var track in model.Scene.Characters.SelectMany(c => c.Animations).SelectMany(a => a.Stacks).SelectMany(s => s.Tracks))
        {
            int n = Math.Min(track.Stack!.Keys.Count, track.Keys.Count);
            var before = Enumerable.Range(0, n)
                .Select(i => ((track.TangentsIn.ElementAtOrDefault(i) as TransformKey)?.Value, (track.TangentsOut.ElementAtOrDefault(i) as TransformKey)?.Value))
                .ToArray();
            TangentRecompute.Recompute(track);
            for (int i = 0; i < n - 1; i++)
            {
                if (before[i] is not ({ } storedIn, { } storedOut)) continue;
                var tIn = ((TransformKey)track.TangentsIn[i]!).Value;
                var tOut = ((TransformKey)track.TangentsOut[i]!).Value;
                // Counted per animated channel group, as `tangentcheck` does.
                if ((track.Channels & AnimChannels.Translate) != 0)
                {
                    total++;
                    if (Near(storedIn.Translation, tIn.Translation) && Near(storedOut.Translation, tOut.Translation)) match++;
                }
                if ((track.Channels & AnimChannels.Rotate) != 0)
                {
                    total++;
                    if (Near(Vec(storedIn.Rotation), Vec(tIn.Rotation)) && Near(Vec(storedOut.Rotation), Vec(tOut.Rotation))) match++;
                }
            }
        }
        if (total == 0) return; // nothing to edit in this scene
        Assert.True(match >= 0.97 * total, $"{match} of {total} key channel groups match ({100.0 * match / total:F1}%).");

        static Vector3 Vec(Quaternion q) => new(q.X, q.Y, q.Z);
        static bool Near(Vector3 a, Vector3 b) =>
            (a - b).Length() <= 2e-3f * MathF.Max(1, MathF.Max(a.Length(), b.Length()));
    }

    [SceneTheory, SceneSamples]
    public void DeleteKeys_Stacker_RemovesTheIntervalBeforeEachKey(string path)
    {
        var (editor, stack) = EditableStack(path, 4);
        if (stack is null) return; // nothing to edit in this scene
        var old = stack.Keys.Select(k => k.Time).ToArray();
        int n = old.Length;
        var values = TrackValues(stack);

        Assert.Equal(2, editor!.DeleteKeys(stack, [1, 2, n - 1], closeGaps: true));

        // Kept keys 0, 3, 4, …: each keeps the gap before it.
        var kept = new[] { 0 }.Concat(Enumerable.Range(3, n - 3)).ToArray();
        var expected = new int[kept.Length];
        expected[0] = old[0];
        for (int j = 1; j < kept.Length; j++) expected[j] = expected[j - 1] + old[kept[j]] - old[kept[j] - 1];
        Assert.Equal(expected, stack.Keys.Select(k => k.Time));
        AssertKeptValues(stack, values, kept);
    }

    [SceneTheory, SceneSamples]
    public void DeleteKeys_Timeline_KeepsTimesAndMirrorsTheNewFirstKey(string path)
    {
        var (editor, stack) = EditableStack(path, 3);
        if (stack is null) return; // nothing to edit in this scene
        var old = stack.Keys.Select(k => k.Time).ToArray();
        var values = TrackValues(stack);

        Assert.Equal(1, editor!.DeleteKeys(stack, [0], closeGaps: false));

        Assert.Equal(old[1..], stack.Keys.Select(k => k.Time));
        AssertKeptValues(stack, values, Enumerable.Range(1, old.Length - 1).ToArray());
        foreach (var track in stack.Tracks)
            if (track.Keys[0] is TransformKey first && track.Keys[^1] is TransformKey loop)
                Assert.Equal(first.Value, loop.Value);
    }

    [SceneTheory, SceneSamples]
    public void DeleteKeys_AllButTheEndKey_LeavesADefaultKey(string path)
    {
        var (editor, stack) = EditableStack(path, 3);
        if (stack is null) return; // nothing to edit in this scene
        var old = stack.Keys.Select(k => k.Time).ToArray();
        int n = old.Length;

        Assert.Equal(n - 1, editor!.DeleteKeys(stack, Enumerable.Range(0, n), closeGaps: true));

        Assert.Equal(2, stack.Keys.Count);
        Assert.Equal(old[0], stack.Keys[0].Time);
        Assert.True(stack.Keys[1].Time > stack.Keys[0].Time);
        foreach (var track in stack.Tracks)
        {
            AssertListLengths(stack, track);
            if (track.Keys[0] is TransformKey key)
            {
                Assert.Equal(Transform.Identity, key.Value);
                Assert.Null(key.Space);
            }
        }
    }

    [SceneTheory, SceneSamples]
    public void DeleteTrackKey_KeepsTheEntryAndTheOtherTracks(string path)
    {
        var model = TestFiles.LoadScene(path);
        var candidate = Candidates(model).FirstOrDefault(c => c.Stack.Keys.Count >= 4);
        if (candidate.Track is null) return; // nothing to edit in this scene
        var (character, _, stack, track) = candidate;
        var editor = new KeyEditor(character);
        var times = stack.Keys.Select(k => k.Time).ToArray();
        var values = TrackValues(stack);
        var expected = InterpolatedWithout(track, stack, 2);

        editor.DeleteTrackKey(stack, 2, track.Target!);

        Assert.Equal(times, stack.Keys.Select(k => k.Time));
        var tracks = stack.Tracks.ToList();
        for (int j = 0; j < tracks.Count; j++)
        {
            AssertListLengths(stack, tracks[j]);
            for (int i = 0; i < times.Length; i++)
                if (tracks[j] != track || i != 2)
                    Assert.Equal(values[j][i], (tracks[j].Keys[i] as TransformKey)?.Value);
        }
        AssertClose(expected, ((TransformKey)track.Keys[2]!).Value);
    }

    [SceneTheory, SceneSamples]
    public void MoveKeys_StepsBackOnCollisionsAndSorts(string path)
    {
        var (editor, stack) = EditableStack(path, 4);
        if (stack is null) return; // nothing to edit in this scene
        int n = stack.Keys.Count;
        for (int i = 0; i < n; i++) stack.SetKeyTime(i, 10 * i);
        var values = TrackValues(stack);

        // Onto key 2: steps back to 19.
        var map = editor!.MoveKeys(stack, [1], 10);
        Assert.Equal(Enumerable.Range(0, n), map);
        Assert.Equal(19, stack.Keys[1].Time);

        // Past key 2: the two swap and the values follow.
        map = editor.MoveKeys(stack, [1], 6);
        Assert.Equal(2, map[1]);
        Assert.Equal(1, map[2]);
        Assert.Equal(new[] { 0, 20, 25 }, stack.Keys.Take(3).Select(k => k.Time));
        var tracks = stack.Tracks.ToList();
        for (int j = 0; j < tracks.Count; j++)
        {
            AssertListLengths(stack, tracks[j]);
            Assert.Equal(values[j][1], (tracks[j].Keys[2] as TransformKey)?.Value);
            Assert.Equal(values[j][2], (tracks[j].Keys[1] as TransformKey)?.Value);
        }

        // Key n−2 past the end key: the end key follows one frame later.
        int last = stack.Keys[n - 2].Time;
        editor.MoveKeys(stack, [n - 2], 15);
        Assert.Equal(last + 15, stack.Keys[n - 2].Time);
        Assert.Equal(last + 16, stack.Keys[n - 1].Time);
    }

    [SceneTheory, SceneSamples]
    public void SetKeyTime_And_Gaps_ShiftTheFollowingKeys(string path)
    {
        var (editor, stack) = EditableStack(path, 4);
        if (stack is null) return; // nothing to edit in this scene
        int n = stack.Keys.Count;
        for (int i = 0; i < n; i++) stack.SetKeyTime(i, 10 * i);
        int[] Times() => stack.Keys.Select(k => k.Time).ToArray();

        Assert.Equal(15, editor!.SetKeyTime(stack, 1, 15));
        Assert.Equal(new[] { 0, 15 }.Concat(Enumerable.Range(2, n - 2).Select(i => 10 * i + 5)), Times());

        // Clamped to one frame after the previous key.
        Assert.Equal(16, editor.SetKeyTime(stack, 2, 3));
        Assert.Equal(16, Times()[2]);
        Assert.Equal(10, Times()[3] - 16);

        editor.SetKeyGap(stack, [1], 4);
        Assert.Equal(new[] { 0, 4, 5 }, Times().Take(3));
        Assert.False(editor.ChangeKeyGap(stack, [2], -1));
        Assert.True(editor.ChangeKeyGap(stack, [1, 2], 2));
        Assert.Equal(new[] { 0, 6, 9 }, Times().Take(3));
    }

    [SceneTheory, SceneSamples]
    public void ReorderKeys_KeepsEachKeysGap(string path)
    {
        var (editor, stack) = EditableStack(path, 4);
        if (stack is null) return; // nothing to edit in this scene
        int n = stack.Keys.Count;
        for (int i = 0; i < n; i++) stack.SetKeyTime(i, i * (i + 1)); // gaps 2, 4, 6, …
        var values = TrackValues(stack);

        var map = editor!.ReorderKeys(stack, [2], 1);

        Assert.Equal(new[] { 0, 2, 1 }, map.Take(3));
        Assert.Equal(new[] { 0, 4, 6 }, stack.Keys.Take(3).Select(k => k.Time));
        var tracks = stack.Tracks.ToList();
        for (int j = 0; j < tracks.Count; j++)
            Assert.Equal(values[j][2], (tracks[j].Keys[1] as TransformKey)?.Value);
    }

    [SceneTheory, SceneSamples]
    public void DeleteAndMove_SurviveSaveAndReload(string path)
    {
        var (editor, stack) = EditableStack(path, 4);
        if (stack is null) return; // nothing to edit in this scene
        editor!.DeleteKeys(stack, [1], closeGaps: true);
        editor.MoveKeys(stack, [0], 1);
        var times = stack.Keys.Select(k => k.Time).ToArray();

        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-edit-{Guid.NewGuid():N}.akt");
        try
        {
            stack.Model.Save(temp);
            var again = TestFiles.LoadScene(temp).Scene.Characters.SelectMany(c => c.Animations)
                .First(a => a.Name == stack.Animation!.Name).Stacks.First(s => s.Name == stack.Name);
            Assert.Equal(times, again.Keys.Select(k => k.Time));
            foreach (var track in again.Tracks) AssertListLengths(again, track);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>On tracks without Space keys, the space-aware recompute is the plain one.</summary>
    [SceneTheory, SceneSamples]
    public void TangentRecompute_WithEvaluator_ChangesOnlySpaceSwitchedTracks(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.Animations.Any());
        if (character is null) return; // nothing to edit in this scene
        var evaluator = new PoseEvaluator(character);
        foreach (var track in character.Animations.SelectMany(a => a.Stacks).SelectMany(s => s.Tracks).Take(200))
        {
            if (track.Keys.Any(k => k is TransformKey { Space: not null })) continue;
            TangentRecompute.Recompute(track);
            var plain = Tangents(track);
            TangentRecompute.Recompute(track, evaluator);
            Assert.Equal(plain, Tangents(track));
        }
    }

    /// <summary>
    /// The space-aware lookup should match about as many stored tangents of space-switched tracks
    /// as the plain one, or more.
    /// </summary>
    [SceneTheory, SceneSamples]
    public void TangentRecompute_SpaceAware_MatchesSpaceSwitchedTracksAtLeastAsWell(string path)
    {
        var model = TestFiles.LoadScene(path);
        int plain = 0, aware = 0, total = 0;
        foreach (var character in model.Scene.Characters)
        {
            var evaluator = new PoseEvaluator(character);
            foreach (var animation in character.Animations)
            {
                var converter = new KeySpaceConverter(evaluator, animation);
                foreach (var track in animation.Stacks.SelectMany(s => s.Tracks))
                {
                    if (!track.Keys.Any(k => k is TransformKey { Space: not null })) continue;
                    var stored = Tangents(track);
                    int Matches() => stored.Zip(Tangents(track)).Count(p => p.First is { } a && p.Second is { } b && NearTangent(a, b));
                    TangentRecompute.Recompute(track);
                    plain += Matches();
                    TangentRecompute.Recompute(track, converter);
                    aware += Matches();
                    total += stored.Length;
                    // Put the stored tangents back so later conversions see the unedited scene.
                    var keys = track.TangentsIn.Concat(track.TangentsOut).ToArray();
                    for (int i = 0; i < keys.Length; i++)
                        if (keys[i] is TransformKey k && stored[i] is { } v) k.Value = v;
                }
            }
        }
        if (total == 0) return; // no space switching in this scene
        // Per file the space-aware lookup can lose a key or two (1_InitialImport loses one); the
        // gain is over the samples as a whole (tangentcheck).
        Assert.True(aware >= plain - 2, $"space-aware {aware} vs plain {plain} of {total}");
    }

    private static Transform?[] Tangents(AnimTrack track) =>
        track.TangentsIn.Concat(track.TangentsOut).Select(k => (k as TransformKey)?.Value).ToArray();

    private static bool NearTangent(Transform a, Transform b)
    {
        static bool Near(Vector3 x, Vector3 y) => (x - y).Length() <= 2e-3f * MathF.Max(1, MathF.Max(x.Length(), y.Length()));
        static Vector3 Vec(Quaternion q) => new(q.X, q.Y, q.Z);
        return Near(a.Translation, b.Translation) && Near(Vec(a.Rotation), Vec(b.Rotation)) && Near(a.Scale, b.Scale);
    }

    /// <summary>An editor and the first candidate stack with at least <paramref name="minKeys"/> keys.</summary>
    private static (KeyEditor? Editor, AnimStack? Stack) EditableStack(string path, int minKeys)
    {
        var model = TestFiles.LoadScene(path);
        var candidate = Candidates(model).FirstOrDefault(c => c.Stack.Keys.Count >= minKeys);
        return candidate.Track is null ? (null, null) : (new KeyEditor(candidate.Character), candidate.Stack);
    }

    /// <summary>Per track (in <see cref="AnimStack.Tracks"/> order), each key's value.</summary>
    private static List<Transform?[]> TrackValues(AnimStack stack) =>
        stack.Tracks.Select(t => t.Keys.Select(k => (k as TransformKey)?.Value).ToArray()).ToList();

    /// <summary>
    /// Key j of every track now holds old key <paramref name="kept"/>[j]; the end key (which
    /// mirrors key 0) is not compared.
    /// </summary>
    private static void AssertKeptValues(AnimStack stack, List<Transform?[]> values, int[] kept)
    {
        var tracks = stack.Tracks.ToList();
        for (int j = 0; j < tracks.Count; j++)
        {
            AssertListLengths(stack, tracks[j]);
            for (int i = 0; i < kept.Length && i < stack.Keys.Count - 1; i++)
                Assert.Equal(values[j][kept[i]], (tracks[j].Keys[i] as TransformKey)?.Value);
        }
    }

    private static void AssertListLengths(AnimStack stack, AnimTrack track)
    {
        int n = stack.Keys.Count;
        Assert.Equal(n, track.Keys.Count);
        Assert.Equal(n, track.TangentsIn.Count);
        Assert.Equal(n, track.TangentsOut.Count);
        Assert.Equal(n, track.TangentModes.Count);
    }

    /// <summary>
    /// The track's value at key <paramref name="k"/>'s time with that key left out, computed on a
    /// reloaded copy of the scene saved to the temp folder.
    /// </summary>
    private static Transform InterpolatedWithout(AnimTrack track, AnimStack stack, int k)
    {
        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-without-{Guid.NewGuid():N}.akt");
        try
        {
            stack.Model.Save(temp);
            var copy = TestFiles.LoadScene(temp);
            var (character, copyStack) = (from c in copy.Scene.Characters
                                          from a in c.Animations
                                          where a.Name == stack.Animation!.Name
                                          from s in a.Stacks
                                          where s.Name == stack.Name
                                          select (c, s)).First();
            var copyTrack = copyStack.Tracks.First(t => t.Target?.Name == track.Target!.Name);
            int frame = copyStack.Keys[k].Time;
            copyStack.RemoveKeyTime(k);
            foreach (var t in copyStack.Tracks) t.RemoveKey(k);
            TangentRecompute.Recompute(copyTrack);
            return new PoseEvaluator(character).LayerValue(copyStack, copyTrack, frame, out _);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>
    /// First stacks of animations with at least three keys at distinct times, and their tracks on
    /// plain objects with all channels keyed.
    /// </summary>
    private static IEnumerable<(Character Character, Animation Animation, AnimStack Stack, AnimTrack Track)> Candidates(SceneModel model) =>
        from character in model.Scene.Characters
        from animation in character.Animations
        let stack = animation.Stacks.FirstOrDefault()
        where stack is { IsEnabled: true } && stack.Opacity > 0.2f && stack.Keys.Count >= 3
              && stack.Keys[0].Time < stack.Keys[1].Time && stack.Keys[1].Time < stack.Keys[2].Time
        from track in stack.Tracks
        where track.Target is { } target && target is not ControllerIK && (track.Channels & All) == All
              && track.Keys.Count == stack.Keys.Count && track.Keys.All(k => k is TransformKey)
              && track.TangentsIn.Count == stack.Keys.Count && track.TangentsOut.Count == stack.Keys.Count
        select (character, animation, stack, track);

    // ---- the IK lock (CKeyIK.Flags bit 0) -------------------------------------------------------
    // The IK lock toggle: the right-click on a controller, or
    // on a camera, which passes its looker target.

    /// <summary>A new 60 fps scene, an animated camera with its whole orbit rig, and its animation.</summary>
    private static (PoseEditSession Session, AnimStack Stack, CameraAnimated Camera) CameraRig()
    {
        var model = SceneFactory.NewSceneModel(60);
        var session = new PoseEditSession(new ScenePose(model), model.Scene.Characters.First());
        var camera = session.AddAnimatedCamera("Rig Camera");
        var animation = session.CreateAnimation();
        return (session, Assert.Single(animation.Stacks), camera);
    }

    [Fact]
    public void ToggleIkLock_TakesTheValueFromTheKeyAtTheFrame_AndWritesTheLookerTrack()
    {
        var (session, stack, camera) = CameraRig();
        var lookerTrack = stack.TrackFor(camera.Target!)!;
        var cameraTrack = stack.TrackFor(camera)!;
        int frame = stack.Keys[0].Time;

        int written = session.ToggleIkLock(stack, camera, [camera], frame, null, out bool locked, out string? reason);

        Assert.Null(reason);
        Assert.True(locked);
        Assert.Equal(lookerTrack.Keys.Count, written);
        Assert.Equal(stack.Keys.Count, lookerTrack.Keys.Count);
        Assert.All(lookerTrack.Keys, k => Assert.Equal(1u, Assert.IsType<IKKey>(k).Flags & 1u));
        // A camera's right-click acts on its looker target: the camera's own keys are untouched.
        Assert.All(cameraTrack.Keys, k => Assert.Equal(0u, Assert.IsType<CameraKey>(k).Flags));

        // The second toggle reads the value it wrote and clears every key again, as one more step.
        written = session.ToggleIkLock(stack, camera, [camera], frame, null, out locked, out reason);
        Assert.Null(reason);
        Assert.False(locked);
        Assert.Equal(lookerTrack.Keys.Count, written);
        Assert.All(lookerTrack.Keys, k => Assert.Equal(0u, Assert.IsType<IKKey>(k).Flags & 1u));

        // One undo step per toggle: undo puts the flags back as they were before it.
        Assert.True(session.Undo());
        Assert.All(lookerTrack.Keys, k => Assert.Equal(1u, Assert.IsType<IKKey>(k).Flags & 1u));
        Assert.True(session.Undo());
        Assert.All(lookerTrack.Keys, k => Assert.Equal(0u, Assert.IsType<IKKey>(k).Flags & 1u));
    }

    [Fact]
    public void ToggleIkLock_WritesOnlyTheChosenKeys()
    {
        var (session, stack, camera) = CameraRig();
        var lookerTrack = stack.TrackFor(camera.Target!)!;
        int frame = stack.Keys[0].Time;
        Assert.True(lookerTrack.Keys.Count >= 2);

        int written = session.ToggleIkLock(stack, camera, [camera], frame, [1], out bool locked, out string? reason);

        Assert.Null(reason);
        Assert.True(locked);
        Assert.Equal(1, written);
        Assert.Equal(1u, Assert.IsType<IKKey>(lookerTrack.Keys[1]).Flags & 1u);
        Assert.All(lookerTrack.Keys.Where((_, i) => i != 1), k => Assert.Equal(0u, Assert.IsType<IKKey>(k).Flags & 1u));
    }

    [Fact]
    public void ToggleIkLock_NeedsAKeyAtTheFrame_AndAnIkTrack()
    {
        var (session, stack, camera) = CameraRig();
        int frame = stack.Keys[0].Time;

        // The app reads the key exactly at the playhead and runs off
        // the list when there is none, so a toggle between keys is refused here.
        Assert.Equal(0, session.ToggleIkLock(stack, camera, [camera], frame + 1, null, out _, out string? betweenKeys));
        Assert.Contains($"no key at frame {frame + 1}", betweenKeys);

        // The hidden joint's MRS track carries MRS keys, which have no IK lock.
        var hideJoint = camera.HideJoint!;
        Assert.Equal(0, session.ToggleIkLock(stack, hideJoint, [hideJoint], frame, null, out _, out string? notIk));
        Assert.Contains("not an IK controller", notIk);

        // An object with no track in the stack.
        var character = session.Character;
        Assert.Equal(0, session.ToggleIkLock(stack, character, [character], frame, null, out _, out string? noTrack));
        Assert.Contains("has no track", noTrack);
    }

    [Fact]
    public void ToggleIkLock_GivesEverySelectedIkControllerTheClickedOnesValue()
    {
        var (session, stack, camera) = CameraRig();
        var lookerTrack = stack.TrackFor(camera.Target!)!;
        int frame = stack.Keys[0].Time;

        // The clicked one (the camera, through its target) decides the value; the selected target is
        // the same track, and a selected non-IK controller is skipped.
        int written = session.ToggleIkLock(stack, camera, [camera, camera.Target!, camera.HideJoint!], frame, null,
            out bool locked, out string? reason);

        Assert.Null(reason);
        Assert.True(locked);
        Assert.Equal(lookerTrack.Keys.Count, written); // the target is written once, the joint not at all
    }

    /// <summary>The first IK, reverse-foot or looker track of a sample with a key whose lock is off.</summary>
    [SceneTheory, SceneSamples]
    public void ToggleIkLock_FlipsAnIkControllersOwnTrack(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var character in model.Scene.Characters)
        foreach (var animation in character.Animations)
        foreach (var stack in animation.Stacks)
        foreach (var track in stack.Tracks)
        {
            if (track.Target is not ControllerIK controller) continue;
            int index = track.Keys.ToList().FindIndex(k => k is IKKey ik && (ik.Flags & 1u) == 0);
            if (index < 0 || index >= stack.Keys.Count) continue;
            var editor = new KeyEditor(character);

            int written = editor.ToggleIkLock(stack, controller, stack.Keys[index].Time, null, out bool locked, out string? reason);

            Assert.Null(reason);
            Assert.True(locked);
            Assert.True(written >= 1);
            Assert.All(track.Keys, k => Assert.Equal(1u, Assert.IsType<IKKey>(k).Flags & 1u));
            return; // one track per scene is enough
        }
    }

    private static void AssertClose(Transform expected, Transform actual)
    {
        static bool Near(Vector3 a, Vector3 b) => (a - b).Length() <= 2e-3f * MathF.Max(1, MathF.Max(a.Length(), b.Length()));
        Assert.True(Near(expected.Translation, actual.Translation), $"translation {expected.Translation} vs {actual.Translation}");
        Assert.True(Near(expected.Scale, actual.Scale), $"scale {expected.Scale} vs {actual.Scale}");
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected.Rotation), Quaternion.Normalize(actual.Rotation)));
        Assert.True(dot > 1 - 1e-5f, $"rotation {expected.Rotation} vs {actual.Rotation}");
    }
}

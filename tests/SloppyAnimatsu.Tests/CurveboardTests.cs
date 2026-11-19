using System.Numerics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

public class CurveboardTests
{
    private const AnimChannels All = AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Scale;

    [Fact]
    public void Euler_RoundTripsAwayFromGimbalLock()
    {
        var random = new Random(3);
        float R(float range) => (float)(random.NextDouble() * 2 - 1) * range;
        for (int i = 0; i < 500; i++)
        {
            var e = new Vector3(R(179), R(85), R(179));
            var q = Curves.FromEulerDegrees(e);
            Assert.Equal(1, q.Length(), 4);
            var back = Curves.ToEulerDegrees(q);
            Assert.True(Vector3.Distance(e, back) < 0.02f, $"{e} -> {back}");
        }
    }

    [Fact]
    public void Euler_IsZyx()
    {
        // R = Rz·Ry·Rx: X is applied first.
        var e = new Vector3(30, 20, 10);
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 10 / 57.29578f)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitY, 20 / 57.29578f)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitX, 30 / 57.29578f);
        var q = Curves.FromEulerDegrees(e);
        Assert.True(MathF.Abs(MathF.Abs(Quaternion.Dot(q, expected)) - 1) < 1e-5f);
    }

    [Fact]
    public void TangentTypes_AllowIKOnlyOnIKControllers()
    {
        Assert.False(TangentTypes.IsAllowed(null, TangentTypes.IK));
        Assert.True(TangentTypes.IsAllowed(null, TangentTypes.Auto));
        Assert.False(TangentTypes.IsAllowed(null, TangentTypes.Custom));
        Assert.Equal("Spline | Spline Unified", TangentTypes.Describe(new TangentMode(0x100)));
        Assert.Equal("Linear | Custom Broken", TangentTypes.Describe(new TangentMode(0xF1)));
    }

    [SceneTheory, SceneSamples]
    public void Sample_MatchesKeyValuesAtKeys(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var (character, _, stack, track) in Candidates(model).Take(3))
        {
            var evaluator = new PoseEvaluator(character);
            int first = stack.Keys[0].Time, last = stack.Keys[^2].Time;
            for (int c = 0; c < 9; c++)
            {
                var samples = Curves.Sample(evaluator, stack, track, c, first, last);
                for (int k = 0; k < stack.Keys.Count - 1; k++)
                {
                    float value = Curves.KeyValue(track.Keys[k], c)!.Value;
                    float sample = samples[stack.Keys[k].Time - first];
                    if (Curves.IsRotation(c)) sample = Curves.Unwrap(sample, value);
                    Assert.True(MathF.Abs(sample - value) < 0.05f, $"{track.Target!.Name} channel {c} key {k}: {sample} vs {value}");
                }
            }
        }
    }

    [SceneTheory, SceneSamples]
    public void MoveKeyValues_ChangesOnlyTheSelectedChannels(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var (character, _, stack, track) in Candidates(model).Take(3))
        {
            var editor = new KeyEditor(character);
            var before = ((TransformKey)track.Keys[1]!).Value;
            var eBefore = Curves.ToEulerDegrees(before.Rotation);
            if (MathF.Abs(eBefore.Y) > 70) continue; // too near gimbal lock for a clean comparison

            int changed = editor.MoveKeyValues([new(track, 1, 0, AnimChannels.TranslateX | AnimChannels.RotateZ, 0)], 2.5f);

            Assert.Equal(1, changed);
            var after = ((TransformKey)track.Keys[1]!).Value;
            var eAfter = Curves.ToEulerDegrees(after.Rotation);
            Assert.Equal(before.Translation.X + 2.5f, after.Translation.X, 3);
            Assert.Equal(before.Translation.Y, after.Translation.Y);
            Assert.Equal(before.Scale, after.Scale);
            Assert.True(MathF.Abs(Curves.Unwrap(eAfter.Z, eBefore.Z) - (eBefore.Z + 2.5f)) < 0.02f);
            Assert.True(MathF.Abs(Curves.Unwrap(eAfter.X, eBefore.X) - eBefore.X) < 0.02f);
            Assert.True(MathF.Abs(eAfter.Y - eBefore.Y) < 0.02f);
        }
    }

    [SceneTheory, SceneSamples]
    public void MoveKeyValues_OfKeyZeroMirrorsTheEndKey(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var (character, _, stack, track) in Candidates(model).Take(2))
        {
            var editor = new KeyEditor(character);
            float before = ((TransformKey)track.Keys[0]!).Value.Translation.Y;
            // Selecting the end key edits key 0.
            editor.MoveKeyValues([new(track, stack.Keys.Count - 1, 0, AnimChannels.TranslateY, 0)], -1);
            Assert.Equal(before - 1, ((TransformKey)track.Keys[0]!).Value.Translation.Y, 3);
            Assert.Equal(((TransformKey)track.Keys[0]!).Value, ((TransformKey)track.Keys[^1]!).Value);
        }
    }

    [SceneTheory, SceneSamples]
    public void ScaleKeyValues_ScalesAboutThePivot(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var (character, _, _, track) in Candidates(model).Take(2))
        {
            var editor = new KeyEditor(character);
            float a = ((TransformKey)track.Keys[1]!).Value.Scale.X;
            editor.ScaleKeyValues([new(track, 1, 0, AnimChannels.ScaleX, 0)], 2, 1);
            Assert.Equal((a - 1) * 2 + 1, ((TransformKey)track.Keys[1]!).Value.Scale.X, 3);
        }
    }

    [SceneTheory, SceneSamples]
    public void SetTangentSlope_ReadsBackAndSurvivesRecompute(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var (character, _, stack, track) in Candidates(model).Take(3))
        {
            var editor = new KeyEditor(character);
            editor.SetTangentsUnified([new(track, 1, 0, All, 0)], false, All);

            Assert.True(editor.SetTangentSlope(track, 1, 1, TangentSide.Out, 0.75f));
            Assert.Equal(0.75f, Curves.TangentSlope(stack, track, 1, 1, TangentSide.Out), 3);
            Assert.Equal(TangentTypes.Custom, track.TangentModes[1][1].Out);

            var rotation = ((TransformKey)track.Keys[1]!).Value.Rotation;
            if (MathF.Abs(Curves.ToEulerDegrees(rotation).Y) > 70) continue;
            float otherBefore = Curves.TangentSlope(stack, track, 1, 3, TangentSide.In);
            Assert.True(editor.SetTangentSlope(track, 1, 5, TangentSide.In, -3));
            Assert.Equal(-3, Curves.TangentSlope(stack, track, 1, 5, TangentSide.In), 2);
            Assert.Equal(otherBefore, Curves.TangentSlope(stack, track, 1, 3, TangentSide.In), 2);
            Assert.Equal(TangentTypes.Custom, track.TangentModes[1][3].In);

            // Another edit recomputes the tangents; Custom sides keep their values.
            editor.MoveKeyValues([new(track, 2, 0, AnimChannels.TranslateY, 0)], 1);
            Assert.Equal(0.75f, Curves.TangentSlope(stack, track, 1, 1, TangentSide.Out), 3);
            Assert.Equal(-3, Curves.TangentSlope(stack, track, 1, 5, TangentSide.In), 2);
        }
    }

    [SceneTheory, SceneSamples]
    public void SetTangentSlope_OnAUnifiedKeySetsBothSides(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var (character, _, stack, track) in Candidates(model).Take(2))
        {
            var editor = new KeyEditor(character);
            editor.SetTangentsUnified([new(track, 1, 0, All, 0)], true, All);
            editor.SetTangentSlope(track, 1, 0, TangentSide.In, 0.2f);
            Assert.Equal(0.2f, Curves.TangentSlope(stack, track, 1, 0, TangentSide.Out), 3);
            Assert.Equal(TangentTypes.Custom, track.TangentModes[1][0].In);
            Assert.Equal(TangentTypes.Custom, track.TangentModes[1][0].Out);
        }
    }

    [SceneTheory, SceneSamples]
    public void RotateTangents_TurnsTheHandle(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var (character, _, stack, track) in Candidates(model).Take(2))
        {
            var editor = new KeyEditor(character);
            editor.SetTangentsUnified([new(track, 1, 0, All, 0)], false, All);
            float before = Curves.TangentSlope(stack, track, 1, 2, TangentSide.Out);
            editor.RotateTangents([new(track, 1, 0, 0, AnimChannels.TranslateZ)], 0.1f);
            float after = Curves.TangentSlope(stack, track, 1, 2, TangentSide.Out);
            Assert.Equal(Math.Clamp(MathF.Atan(before / 0.001f) + 0.1f, -1.5707864f, 1.5707864f), MathF.Atan(after / 0.001f), 3);
            Assert.Equal(TangentTypes.Custom, track.TangentModes[1][2].Out);
        }
    }

    [SceneTheory, SceneSamples]
    public void ChangeTangentType_SetsAllowedTypesAndRecomputes(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var (character, _, stack, track) in Candidates(model).Take(2))
        {
            var editor = new KeyEditor(character);
            var selection = new CurveKeySelection[] { new(track, 1, 0, AnimChannels.TranslateX | AnimChannels.RotateY, 0) };

            Assert.Equal(0, editor.ChangeTangentType(selection, TangentTypes.IK, All));

            editor.ChangeTangentType(selection, TangentTypes.Linear, All);
            var modes = track.TangentModes[1];
            Assert.Equal(TangentTypes.Linear, modes[0].In);
            Assert.Equal(TangentTypes.Linear, modes[0].Out);
            // Rotation channels change together.
            Assert.All(new[] { 3, 4, 5 }, c => Assert.Equal(TangentTypes.Linear, modes[c].Out));
            // A linear out tangent is the whole difference to the next key.
            float value = ((TransformKey)track.Keys[1]!).Value.Translation.X;
            float next = ((TransformKey)track.Keys[2]!).Value.Translation.X;
            Assert.Equal(next - value, ((TransformKey)track.TangentsOut[1]!).Value.Translation.X, 3);

            // Only the out handle selected: only the out side changes.
            editor.ChangeTangentType([new(track, 1, 0, 0, AnimChannels.TranslateX)], TangentTypes.Step, All);
            Assert.Equal(TangentTypes.Linear, track.TangentModes[1][0].In);
            Assert.Equal(TangentTypes.Step, track.TangentModes[1][0].Out);

            // Channels not displayed are left alone.
            editor.ChangeTangentType(selection, TangentTypes.Clamped, AnimChannels.Rotate);
            Assert.Equal(TangentTypes.Linear, track.TangentModes[1][0].In);
            Assert.Equal(TangentTypes.Clamped, track.TangentModes[1][4].In);
        }
    }

    [SceneTheory, SceneSamples]
    public void SessionEdits_UndoAndRedo(string path)
    {
        var model = TestFiles.LoadScene(path);
        var candidate = Candidates(model).FirstOrDefault();
        if (candidate.Track == null) return;
        var (character, _, stack, track) = candidate;
        var session = new PoseEditSession(new ScenePose(model), character);
        var selection = new CurveKeySelection[] { new(track, 1, 0, AnimChannels.TranslateX, 0) };
        float before = ((TransformKey)track.Keys[1]!).Value.Translation.X;
        uint modeBefore = track.TangentModes[1][0].Raw;

        session.MoveKeyValues(stack, selection, 3);
        session.ChangeTangentType(stack, selection, TangentTypes.Step, All);
        Assert.Equal(TangentTypes.Step, track.TangentModes[1][0].In);

        Assert.True(session.Undo());
        Assert.Equal(modeBefore, track.TangentModes[1][0].Raw);
        Assert.True(session.Undo());
        Assert.Equal(before, ((TransformKey)track.Keys[1]!).Value.Translation.X);
        Assert.True(session.Redo());
        Assert.Equal(before + 3, ((TransformKey)track.Keys[1]!).Value.Translation.X, 3);

        // A drag re-applies from the press and is one undo step.
        var drag = session.BeginCurveDrag(stack, "Drag");
        drag.Update(e => e.MoveKeyValues(selection, 1));
        drag.Update(e => e.MoveKeyValues(selection, 2));
        drag.Commit();
        Assert.Equal(before + 5, ((TransformKey)track.Keys[1]!).Value.Translation.X, 3);
        Assert.True(session.Undo());
        Assert.Equal(before + 3, ((TransformKey)track.Keys[1]!).Value.Translation.X, 3);
    }

    private static IEnumerable<(Character Character, Animation Animation, AnimStack Stack, AnimTrack Track)> Candidates(SceneModel model) =>
        from character in model.Scene.Characters
        from animation in character.Animations
        let stack = animation.Stacks.FirstOrDefault()
        where stack is { IsEnabled: true } && stack.Keys.Count >= 4
              && stack.Keys[0].Time < stack.Keys[1].Time && stack.Keys[1].Time < stack.Keys[2].Time
        from track in stack.Tracks
        where track.Target is { } target && target is not ControllerIK && (track.Channels & All) == All
              && track.Keys.Count == stack.Keys.Count && track.Keys.All(k => k is TransformKey)
              && track.TangentsIn.Count == stack.Keys.Count && track.TangentsOut.Count == stack.Keys.Count
              && track.TangentModes.Count == stack.Keys.Count && track.TangentModes.All(m => m.Length >= 9)
        select (character, animation, stack, track);
}

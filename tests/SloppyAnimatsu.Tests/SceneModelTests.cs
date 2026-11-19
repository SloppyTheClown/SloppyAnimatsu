using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Tests;

/// <summary>The typed scene model, on Resource3D.akt and the user's samples.</summary>
public class SceneModelTests
{
    [Fact]
    public void Resource3DHasTypedCameras()
    {
        if (TestFiles.Resource3D == null) return; // reference install not present
        var model = TestFiles.LoadScene(TestFiles.Resource3D);

        Assert.Equal(8, model.Scene.Cameras.Count);
        Assert.IsType<PerspectiveCamera>(model.Scene.Cameras[0]);
        Assert.All(model.Scene.Cameras.Skip(1).Take(6), c => Assert.IsType<OrthoCamera>(c));
        Assert.Equal(60u, model.Scene.FrameRate);
        Assert.Null(model.Scene.Time);
        Assert.Same(model.Scene.Cameras[0], model.Wrap(model.Document.Root.Children[0]));
    }

    [Fact]
    public void SettersEditTheDocumentInPlace()
    {
        if (TestFiles.Resource3D == null) return;
        byte[] original = File.ReadAllBytes(TestFiles.Resource3D);
        var model = new SceneModel(AktReader.Read(original));

        uint rate = model.Scene.FrameRate;
        model.Scene.FrameRate = 24;
        var reread = new SceneModel(AktReader.Read(AktWriter.Write(model.Document)));
        Assert.Equal(24u, reread.Scene.FrameRate);

        model.Scene.FrameRate = rate;
        Assert.True(original.AsSpan().SequenceEqual(AktWriter.Write(model.Document)));
    }

    [Fact]
    public void VisibilityAndPickFlagsEditOnlyTheirBits()
    {
        if (TestFiles.Resource3D == null) return;
        var model = TestFiles.LoadScene(TestFiles.Resource3D);
        var helper = model.Scene.Descendants<Object3D>().First(o => o.Flags == 0xF9);
        Assert.True(helper.IsShown);
        Assert.False(helper.IsUnpickable);

        helper.IsShown = false;
        helper.IsUnpickable = true;
        Assert.Equal(0x2F8u, helper.Flags);

        var reread = new SceneModel(AktReader.Read(AktWriter.Write(model.Document)));
        var again = reread.Scene.Descendants<Object3D>().First(o => o.Flags == 0x2F8);
        Assert.False(again.IsShown);
        Assert.True(again.IsUnpickable);

        again.IsShown = true;
        again.IsUnpickable = false;
        Assert.Equal(0xF9u, again.Flags);
    }

    [SceneTheory]
    [SceneSamples]
    public void WrappingKeepsTheRoundTripExact(string path)
    {
        byte[] original = File.ReadAllBytes(path);
        var model = new SceneModel(AktReader.Read(original));
        if (model.Document.Root.ClassKey != "CScene") return; // preferences.akt

        // Touch every typed view.
        foreach (var ch in model.Scene.Characters)
        {
            _ = ch.Controllers.Select(c => c.LocalMatrix).ToList();
            _ = ch.RefPose?.Matrices;
            foreach (var anim in ch.Animations)
                foreach (var stack in anim.Stacks)
                    _ = stack.Tracks.SelectMany(t => t.Keys).ToList();
        }
        foreach (var mesh in model.Scene.Meshes)
            _ = mesh.Triangles().Count();

        Assert.True(original.AsSpan().SequenceEqual(AktWriter.Write(model.Document)));
    }

    [SceneTheory]
    [SceneSamples]
    public void PosesAndTracksMatchTheirIndexLists(string path)
    {
        var model = new SceneModel(AktDocument.Load(path));
        if (model.Document.Root.ClassKey != "CScene") return;

        foreach (var ch in model.Scene.Characters)
        {
            foreach (var pose in new[] { ch.RefPose, ch.CurrentPose, ch.RefPoseOld }.Concat(ch.BindPoses))
                if (pose != null)
                    Assert.Equal(ch.Controllers.Count, pose.Matrices.Length);

            foreach (var anim in ch.Animations)
                foreach (var stack in anim.Stacks)
                    foreach (var track in stack.Tracks)
                        Assert.Equal(stack.Keys.Count, track.Keys.Count);
        }
    }
}

/// <summary>Animation evaluation.</summary>
public class PoseEvaluatorTests
{
    private const float Eps = 1e-5f;

    private static Transform T(float x, float y, float z) => new(new Vector3(x, y, z), Quaternion.Identity, Vector3.One);

    [Fact]
    public void ComposeAddsTranslationAndMultipliesRotationAndScale()
    {
        var rest = new Transform(new Vector3(1, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f), new Vector3(2, 2, 2));
        var delta = new Transform(new Vector3(10, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f), new Vector3(1, 3, 1));
        var result = PoseEvaluator.Compose(rest, delta);

        Assert.Equal(new Vector3(11, 2, 3), result.Translation);
        Assert.Equal(rest.Rotation * delta.Rotation, result.Rotation);
        Assert.Equal(new Vector3(2, 6, 2), result.Scale);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(25f, 25f)]
    [InlineData(31f, 31f)]  // stops at the penultimate key
    [InlineData(36f, 31f)]
    [InlineData(48f, 31f)]
    public void NonCycleStackStopsAtItsPenultimateKey(float time, float expected)
    {
        StackKey[] keys = [new(0, 0, 0, 0), new(31, 0, 0, 0), new(48, 0, 0, 0)];
        Assert.Equal(expected, PoseEvaluator.LocalTime(keys, 0x2, time), Eps);
    }

    [Theory]
    [InlineData(4f, 4f)]
    [InlineData(10f, 10f)]
    [InlineData(36f, 4f)]   // the last key is the first key again
    [InlineData(40f, 8f)]
    [InlineData(2f, 34f)]   // before the first key wraps backwards
    public void CycleStackRepeatsFromFirstToLastKey(float time, float expected)
    {
        StackKey[] keys = [new(4, 0, 0, 0), new(20, 0, 0, 0), new(36, 0, 0, 0)];
        Assert.Equal(expected, PoseEvaluator.LocalTime(keys, 0x3, time), Eps);
    }

    [Fact]
    public void OverrideLayerBlendsByWeightOnMaskedChannelsOnly()
    {
        var acc = T(0, 0, 0);
        var value = new Transform(new Vector3(4, 8, 12), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f), new Vector3(3, 3, 3));

        var full = PoseEvaluator.MixOverride(acc, value, 1f, AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Scale);
        Assert.Equal(value.Translation, full.Translation);
        Assert.Equal(value.Scale, full.Scale);
        Assert.True(Quaternion.Dot(value.Rotation, full.Rotation) > 1 - Eps);

        var half = PoseEvaluator.MixOverride(acc, value, 0.5f, AnimChannels.TranslateY | AnimChannels.ScaleZ);
        Assert.Equal(new Vector3(0, 4, 0), half.Translation);
        Assert.Equal(new Vector3(1, 1, 2), half.Scale);
        Assert.Equal(Quaternion.Identity, half.Rotation);
    }

    [Fact]
    public void AdditiveLayerWeightsThenComposes()
    {
        var acc = new Transform(new Vector3(1, 0, 0), Quaternion.Identity, new Vector3(2, 2, 2));
        var value = new Transform(new Vector3(4, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f), new Vector3(4, 4, 4));
        var result = PoseEvaluator.MixAdditive(acc, value, 0.5f, AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Scale);

        Assert.Equal(new Vector3(3, 0, 0), result.Translation);
        Assert.Equal(new Vector3(4, 4, 4), result.Scale); // 2 * 4^0.5
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f);
        Assert.True(MathF.Abs(Quaternion.Dot(expected, result.Rotation)) > 1 - Eps);
    }

    [Fact]
    public void PowerUsesTheShortArc()
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.8f);
        var p = PoseEvaluator.Power(-q, 0.5f);
        Assert.True(Quaternion.Dot(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f), p) > 1 - Eps);
        Assert.Equal(Quaternion.Identity, PoseEvaluator.Power(Quaternion.Identity, 0.3f));
    }

    [Fact]
    public void TimingFindsExactKeysButNeverTheLast()
    {
        StackKey[] keys = [new(0, 0, 0, 0), new(10, 0, 0, 0), new(20, 0, 0, 0)];

        Assert.Equal(1f, PoseEvaluator.Timing(keys, 10, out int exact));
        Assert.Equal(1, exact);
        Assert.Equal(1f, PoseEvaluator.Timing(keys, 20, out exact));
        Assert.Equal(-1, exact);
        Assert.Equal(1f, PoseEvaluator.Timing(keys, 5, out exact));
        Assert.Equal(-1, exact);
    }

    [Fact]
    public void TimingFadesInBeforeAndOutAfterAKey()
    {
        // Key 1 fades in over 4 frames; key 0 fades out over 2 frames.
        StackKey[] keys = [new(0, 0, 0, 2), new(10, 0, 4, 0), new(20, 0, 0, 0)];

        Assert.Equal(0.5f, PoseEvaluator.Timing(keys, 1, out _), Eps);       // fade-out 0.5, fade-in 0 -> both < 1 -> max
        Assert.Equal(0.5f, PoseEvaluator.Timing(keys, 8, out _), Eps);       // fade-in 0.5, fade-out 0 -> max
        Assert.Equal(0f, PoseEvaluator.Timing(keys, 5, out _), Eps);         // both 0
        StackKey[] onlyIn = [new(0, 0, 0, 0), new(10, 0, 4, 0), new(20, 0, 0, 0)];
        Assert.Equal(0.25f, PoseEvaluator.Timing(onlyIn, 7, out _), Eps);    // fade-in 0.25, fade-out 1 -> min
    }

    [Fact]
    public void HermiteInterpolatorUsesSegmentTangentsAndLogSpaceRotation()
    {
        StackKey[] keys = [new(0, 0, 0, 0), new(10, 0, 0, 0)];
        var quarterTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        Transform[] values = [T(0, 0, 0), new(new Vector3(10, 0, 0), quarterTurn, Vector3.One)];
        var none = new Transform(Vector3.Zero, default, Vector3.Zero);
        // Linear tangents (the value delta over the segment) make the curve a straight line.
        var linear = new Transform(new Vector3(10, 0, 0), new Quaternion(HermiteKeyInterpolator.Log(quarterTurn), 0), Vector3.Zero);
        var interp = HermiteKeyInterpolator.Instance;

        var mid = interp.Interpolate(null!, new TrackKeys(keys, values, [none, linear], [linear, none]), 5f);
        Assert.Equal(5f, mid.Translation.X, Eps);
        Assert.Equal(MathF.PI / 4, 2 * MathF.Acos(mid.Rotation.W), Eps);

        // Flat tangents ease in and out: a quarter of the way along gives h01(0.25) = 0.15625.
        var eased = interp.Interpolate(null!, new TrackKeys(keys, values, [none, none], [none, none]), 2.5f);
        Assert.Equal(1.5625f, eased.Translation.X, Eps);
        Assert.Equal(10f, interp.Interpolate(null!, new TrackKeys(keys, values, [none, none], [none, none]), 12f).Translation.X);
    }

    [Fact]
    public void LinearInterpolatorBlendsNeighbouringKeysAndHoldsTheEnds()
    {
        StackKey[] keys = [new(0, 0, 0, 0), new(10, 0, 0, 0)];
        Transform[] values = [T(0, 0, 0), T(10, 0, 0)];
        var interp = LinearKeyInterpolator.Instance;

        Assert.Equal(new Vector3(2.5f, 0, 0), interp.Interpolate(null!, new TrackKeys(keys, values, [], []), 2.5f).Translation);
        Assert.Equal(new Vector3(10, 0, 0), interp.Interpolate(null!, new TrackKeys(keys, values, [], []), 15f).Translation);
        Assert.Equal(Vector3.Zero, interp.Interpolate(null!, new TrackKeys(keys, values, [], []), -3f).Translation);
    }

    [Fact]
    public void ScaleCompensationKeepsThePositionButDropsTheParentsOwnScale()
    {
        // Parent: scale 2, then rotated a quarter turn about Z and moved to (10, 0, 0).
        var parentLocal = new Transform(new Vector3(10, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), new Vector3(2, 2, 2));
        var parentWorld = parentLocal.ToMatrix();
        var childLocal = T(1, 0, 0).ToMatrix();

        var plain = WorldComposition.Compose(childLocal, parentWorld, parentLocal.Scale, compensate: false);
        var compensated = WorldComposition.Compose(childLocal, parentWorld, parentLocal.Scale, compensate: true);
        // Both put the child at parent position + rotated, scaled offset.
        Assert.True(Vector3.Distance(new Vector3(10, 2, 0), plain.Translation) < Eps);
        Assert.True(Vector3.Distance(new Vector3(10, 2, 0), compensated.Translation) < Eps);
        // Only the plain chain passes the parent's scale on.
        Assert.Equal(2f, Object3D.LocalScale(plain).X, Eps);
        Assert.Equal(1f, Object3D.LocalScale(compensated).X, Eps);

        Assert.True(WorldComposition.TryDecompose(compensated, parentWorld, parentLocal.Scale, compensate: true, out var back));
        Assert.True(AktMath.MaxDifference(childLocal, back) < Eps);
    }

    [SceneTheory]
    [SceneSamples]
    public void RestPoseIsTheReferencePose(string path)
    {
        var model = new SceneModel(AktDocument.Load(path));
        if (model.Document.Root.ClassKey != "CScene") return;

        foreach (var ch in model.Scene.Characters)
        {
            if (ch.RefPose is not { } refPose) continue;
            var evaluator = new PoseEvaluator(ch);
            var local = evaluator.LocalTransforms(null, 0);
            var matrices = refPose.Matrices;
            for (int i = 0; i < local.Length; i++)
                Assert.True(AktMath.MaxDifference(local[i].ToMatrix(), matrices[i]) < 1e-3f, $"{ch.Name} #{i} '{ch.Controllers[i].Name}'");
        }
    }

    [SceneTheory]
    [SceneSamples]
    public void RestPoseJointsLieInsideTheBoundMesh(string path)
    {
        // A plausibility check of world composition: with scale compensation the reference pose
        // of 7_AddingIK keeps 64/64 joints inside the body mesh; the plain chain throws fingers
        // ~81,500 units out.
        var model = new SceneModel(AktDocument.Load(path));
        if (model.Document.Root.ClassKey != "CScene") return;

        foreach (var ch in model.Scene.Characters)
        {
            var joints = ch.Joints.ToList();
            var meshes = ch.Meshes.ToList();
            if (meshes.Count == 0 || joints.Count == 0 || ch.RefPose == null) continue;
            var points = meshes.SelectMany(m => m.ControlPoints.Select(p => Vector3.Transform(p, m.WorldMatrix))).ToArray();
            if (points.Length == 0) continue;
            // Widened by the bounds' own size: an unfitted example skeleton (3_ExampleSkeleton) may
            // lie beside its mesh, but not tens of thousands of units away.
            Vector3 lo = points.Aggregate(Vector3.Min), hi = points.Aggregate(Vector3.Max), margin = hi - lo;
            lo -= margin; hi += margin;
            var evaluator = new PoseEvaluator(ch);
            var world = evaluator.WorldMatrices(null, 0);
            int inside = joints.Count(j => evaluator.IndexOf(j) is >= 0 and var i && Vector3.Clamp(world[i].Translation, lo, hi) == world[i].Translation);
            Assert.True(inside >= joints.Count * 0.9, $"{ch.Name}: {inside}/{joints.Count} joints inside its meshes");
        }
    }

    [SceneTheory]
    [SceneSamples]
    public void ExactKeysOfASingleFullLayerAreAddedToTheRestPose(string path)
    {
        var model = new SceneModel(AktDocument.Load(path));
        if (model.Document.Root.ClassKey != "CScene") return;

        foreach (var ch in model.Scene.Characters)
        {
            // IK override segments re-solve chain joints; at a key the
            // solve only adds float noise, which this exact check would flag.
            var evaluator = new PoseEvaluator(ch) { SolveIK = false };
            foreach (var anim in ch.Animations)
            {
                var enabled = anim.Stacks.Where(s => (s.Flags & 0x2) != 0).ToList();
                if (enabled.Count != 1 || (enabled[0].Flags & 0x8) != 0 || enabled[0].Opacity != 1f) continue;
                var stack = enabled[0];
                if (stack.Keys.Count < 2) continue;
                int time = stack.Keys[0].Time;
                var local = evaluator.LocalTransforms(anim, time);
                foreach (var track in stack.Tracks)
                {
                    if (track.Target is not { } target || track.Channels != (AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Scale)) continue;
                    if (track.Keys[0] is not TransformKey key || key.Space != null) continue; // a space key is re-parented
                    int i = evaluator.IndexOf(target);
                    if (i < 0) continue;
                    var expected = PoseEvaluator.Compose(evaluator.RestTransforms[i], key.Value);
                    Assert.True(AktMath.MaxDifference(expected.ToMatrix(), local[i].ToMatrix()) < 1e-4f, $"{anim.Name} '{target.Name}'");
                }
            }
        }
    }
}

using System.Numerics;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>Posing reverse-foot legs through <see cref="KeyEditor.Pose"/>.</summary>
public class ReverseFootEditingTests
{
    [SceneTheory, SceneSamples]
    public void DraggingTheController_PullsTheLegAndKeepsTheShoeOnTheAnkle(string path)
    {        foreach (bool ikKey in new[] { true, false })
            foreach (var c in Candidates(path, ikKey).Take(3))
            {
                var refsBefore = c.Chain.Key.ChainTransforms.ToArray();
                var (world, _) = Evaluate(c);
                var target = Vector3.Lerp(world[c.ControllerIndex].Translation, world[c.JointIndices[0]].Translation, 0.05f);

                c.Editor.Pose(c.Stack, c.KeyIndex, c.Chain.Controller, MovedTo(c, target));

                var (after, local) = Evaluate(c);
                float length = LegLength(c, after);
                Assert.True(Vector3.Distance(after[c.ControllerIndex].Translation, target) < 1e-3f * length,
                    $"{c}: the controller misses the target by {Vector3.Distance(after[c.ControllerIndex].Translation, target)} (leg {length})");
                AssertConsistent(c, after, length);
                var refs = c.Chain.Key.ChainTransforms;
                for (int j = 0; j < 4; j++)
                    // A reached IK key keeps its string; an FK key stores the leg's locals.
                    AssertClose(ikKey ? refsBefore[j] : local[c.JointIndices[j]], refs[j], c.ToString());
            }
    }

    [SceneTheory, SceneSamples]
    public void MovingAJointAboveTheLeg_KeepsEveryReverseFootKeyConsistent(string path)
    {
        foreach (var c in Candidates(path, ikKey: null).Take(4))
        {
            if (c.Chain.Joints[0].Parent3D is not { } above || c.Stack.TrackFor(above) is not { } track
                || (track.Channels & AnimChannels.Rotate) == 0)
                continue;
            int ai = c.Editor.Evaluator.IndexOf(above);
            if (ai < 0) continue;
            var (world, local) = Evaluate(c);
            var ctrlBefore = world[c.ControllerIndex];
            var posed = local[ai] with
            {
                Rotation = Quaternion.Normalize(local[ai].Rotation * Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 3)), 0.05f)),
            };

            c.Editor.Pose(c.Stack, c.KeyIndex, above, posed);

            var (after, _) = Evaluate(c);
            float length = LegLength(c, after);
            AssertConsistent(c, after, length);
            if (c.IKKey)
                // The controller stays put and the leg follows the shoe.
                AssertSameWorld(ctrlBefore, after[c.ControllerIndex], length, $"{c}: the reverse-foot controller moved");
        }
    }

    [SceneTheory, SceneSamples]
    public void ReverseFootDrag_SurvivesSaveAndReload(string path)
    {
        if (Candidates(path, ikKey: false).FirstOrDefault() is not { } c) return;
        var (world, _) = Evaluate(c);
        var target = Vector3.Lerp(world[c.ControllerIndex].Translation, world[c.JointIndices[0]].Translation, 0.05f);
        c.Editor.Pose(c.Stack, c.KeyIndex, c.Chain.Controller, MovedTo(c, target));
        var (after, _) = Evaluate(c);
        var refs = c.Chain.Key.ChainTransforms.ToArray();

        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-rf-edit-{Guid.NewGuid():N}.akt");
        try
        {
            c.Stack.Model.Save(temp);
            var model = TestFiles.LoadScene(temp);
            var character = model.Scene.Characters.Single(ch => ch.Name == c.Editor.Character.Name);
            var animation = character.Animations.First(a => a.Name == c.Animation.Name);
            var stack = animation.Stacks.First(s => s.IsEnabled);
            var controller = character.IKControllers.First(ik => ik.Name == c.Chain.Controller.Name);
            var key = (ReverseFootKey)stack.TrackFor(controller)!.Keys[c.KeyIndex]!;
            for (int j = 0; j < refs.Length; j++) AssertClose(refs[j], key.ChainTransforms[j], c.ToString());
            var evaluator = new PoseEvaluator(character);
            var reloaded = evaluator.WorldMatrices(animation, c.Time);
            AssertSameWorld(after[c.ControllerIndex], reloaded[evaluator.IndexOf(controller)], LegLength(c, after), c.ToString());
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private sealed record Case(KeyEditor Editor, Animation Animation, AnimStack Stack, int KeyIndex, KeyEditor.FootKeyChain Chain,
        int ControllerIndex, int[] JointIndices)
    {
        public bool IKKey => (Chain.Key.Flags & 1) != 0;
        public float Time => Stack.Keys[KeyIndex].Time;
        public override string ToString() => $"{Animation.Name} {Chain.Controller.Name} key {KeyIndex}";
    }

    /// <summary>
    /// Reverse-foot legs keyed at key 1 of animations with a single enabled stack, already
    /// consistent there (the shoe's ankle on the leg's ankle), with a translated controller track.
    /// Legs with a non-uniformly scaled joint are left out: their ankle's world is sheared, so it
    /// cannot take the shoe's orientation exactly.
    /// Each case edits its own freshly loaded copy of the scene.
    /// </summary>
    private static IEnumerable<Case> Candidates(string path, bool? ikKey)
    {
        int count = AllCases(TestFiles.LoadScene(path), ikKey).Count();
        for (int i = 0; i < count; i++)
            yield return AllCases(TestFiles.LoadScene(path), ikKey).ElementAt(i);
    }

    private static IEnumerable<Case> AllCases(SceneModel model, bool? ikKey)
    {
        foreach (var character in model.Scene.Characters)
        {
            var editor = new KeyEditor(character);
            foreach (var animation in character.Animations)
            {
                if (animation.Stacks.Count(s => s.IsEnabled) != 1) continue;
                var stack = animation.Stacks.First(s => s.IsEnabled);
                if (stack.Keys.Count < 3 || stack.IsAdditive || stack.Opacity < 0.99f) continue;
                foreach (var rf in character.IKControllers.OfType<ControllerReverseFoot>())
                {
                    if (editor.FootChain(stack, 1, rf) is not { } chain) continue;
                    if (ikKey is { } wanted && ((chain.Key.Flags & 1) != 0) != wanted) continue;
                    if ((stack.TrackFor(rf)!.Channels & AnimChannels.Translate) == 0) continue;
                    var c = new Case(editor, animation, stack, 1, chain, editor.Evaluator.IndexOf(rf),
                        chain.Joints.Select(editor.Evaluator.IndexOf).ToArray());
                    var (world, local) = Evaluate(c);
                    if (c.JointIndices.Take(3).Any(j => !IsUniform(local[j].Scale))) continue;
                    if (Vector3.Distance(ShoeAnkle(c, world).Translation, world[c.JointIndices[2]].Translation) > 1e-4f * LegLength(c, world))
                        continue;
                    yield return c;
                }
            }
        }
    }

    private static bool IsUniform(Vector3 s) =>
        MathF.Abs(s.X - s.Y) < 1e-4f * MathF.Abs(s.X) && MathF.Abs(s.X - s.Z) < 1e-4f * MathF.Abs(s.X);

    private static (Matrix4x4[] World, Transform[] Local) Evaluate(Case c) =>
        (c.Editor.Evaluator.WorldMatrices(c.Animation, c.Time), c.Editor.Evaluator.LocalTransforms(c.Animation, c.Time));

    /// <summary>The world of the shoe's SkeletonAnkle posed by the key's angles under the controller.</summary>
    private static Matrix4x4 ShoeAnkle(Case c, Matrix4x4[] world)
    {
        var ev = c.Editor.Evaluator;
        var rf = c.Chain.Controller;
        var rest = c.Chain.Shoe.Select(s => ev.RestTransforms[ev.IndexOf(s)]).ToArray();
        var shoe = ReverseFootSolver.ShoeLocals(rest, c.Chain.Key.RollAngle, c.Chain.Key.TwistAngle, c.Chain.Key.BankAngle,
            ReverseFootLimits.FromScene(c.Editor.Character.Model.Scene), new Vector3(rf.RollAxisPlant.X, rf.RollAxisPlant.Y, rf.RollAxisPlant.Z));
        var p = Matrix4x4.Identity;
        for (int j = 0; j <= ReverseFootSolver.SkeletonAnkle; j++) p = shoe[j].ToMatrix() * p;
        return p * world[c.ControllerIndex];
    }

    /// <summary>The shoe's ankle on the leg's ankle, and <c>RefArray[4]</c> the controller relative to the ball.</summary>
    private static void AssertConsistent(Case c, Matrix4x4[] world, float length)
    {
        AssertSameWorld(ShoeAnkle(c, world), world[c.JointIndices[2]], length, $"{c}: shoe ankle vs leg ankle");
        AssertSameWorld(c.Chain.Key.ChainTransforms[4].ToMatrix() * world[c.JointIndices[3]], world[c.ControllerIndex], length,
            $"{c}: controller vs ball · RefArray[4]");
    }

    /// <summary>The controller's local transform that puts it at world position <paramref name="target"/>.</summary>
    private static Transform MovedTo(Case c, Vector3 target)
    {
        var (world, local) = Evaluate(c);
        var controllerLocal = local[c.ControllerIndex];
        Matrix4x4.Invert(controllerLocal.ToMatrix(), out var inverseLocal);
        var parent = inverseLocal * world[c.ControllerIndex];
        var wanted = world[c.ControllerIndex];
        wanted.Translation = target;
        Matrix4x4.Invert(parent, out var inverseParent);
        return Transform.FromMatrixLoose(wanted * inverseParent) with { Scale = controllerLocal.Scale };
    }

    private static float LegLength(Case c, Matrix4x4[] world)
    {
        float length = 0;
        for (int j = 1; j < c.JointIndices.Length; j++)
            length += Vector3.Distance(world[c.JointIndices[j]].Translation, world[c.JointIndices[j - 1]].Translation);
        return length;
    }

    private static void AssertSameWorld(Matrix4x4 expected, Matrix4x4 actual, float length, string what)
    {
        Assert.True(Vector3.Distance(expected.Translation, actual.Translation) < 1e-3f * length,
            $"{what}: {expected.Translation} vs {actual.Translation}");
        var a = Transform.FromMatrixLoose(expected).Rotation;
        var b = Transform.FromMatrixLoose(actual).Rotation;
        Assert.True(MathF.Abs(Quaternion.Dot(a, b)) > 1 - 1e-4f, $"{what}: rotation {a} vs {b}");
    }

    private static void AssertClose(Transform expected, Transform actual, string what)
    {
        Assert.True(Vector3.Distance(expected.Translation, actual.Translation) <= 1e-3f * MathF.Max(1, expected.Translation.Length()),
            $"{what}: translation {expected.Translation} vs {actual.Translation}");
        Assert.True(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected.Rotation), Quaternion.Normalize(actual.Rotation))) > 1 - 1e-5f,
            $"{what}: rotation {expected.Rotation} vs {actual.Rotation}");
    }
}

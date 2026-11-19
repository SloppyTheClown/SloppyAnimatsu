using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>Adding controllers to a stack.</summary>
public class TrackCreationTests
{
    [SceneTheory, SceneSamples]
    public void AddToStack_AppendsAnIdentityMRSTrack(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, animation, stack, controller)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        int before = stack.Tracks.Count();
        var times = new[] { stack.Keys[0].Time, (stack.Keys[0].Time + stack.Keys[^1].Time) / 2, stack.Keys[^1].Time };
        var posedBefore = times.Select(t => Mixed(session, animation, controller, t)).ToArray();

        Assert.Equal(1, session.AddToStack(stack, [controller]));

        var tracks = stack.Tracks.ToList();
        Assert.Equal(before + 1, tracks.Count);
        var track = tracks[^1];
        Assert.Same(track, stack.TrackFor(controller));
        Assert.Matches(@"^Track(_\d{2,})?$", track.Name);
        Assert.Single(tracks, t => t.Name == track.Name);
        Assert.Equal("CAnimTrackMRS", track.ClassName);
        Assert.Equal((AnimChannels)0x1FF, track.Channels);
        int n = stack.Keys.Count;
        Assert.Equal(n, track.Keys.Count);
        Assert.Equal(n, track.TangentsIn.Count);
        Assert.Equal(n, track.TangentsOut.Count);
        Assert.All(track.Keys, k =>
        {
            var key = Assert.IsType<TransformKey>(k);
            Assert.Equal(Transform.Identity, key.Value);
            Assert.Same(track, key.Track);
        });
        Assert.All(track.TangentsIn.Concat(track.TangentsOut), k =>
        {
            var key = Assert.IsType<TransformKey>(k);
            Assert.Null(key.Track);
            Assert.Null(key.Space);
        });
        Assert.Equal(n, track.TangentModes.Count);
        Assert.All(track.TangentModes, modes =>
        {
            Assert.Equal(9, modes.Length);
            Assert.All(modes, m => Assert.Equal(0x100u, m.Raw));
        });

        // The controller had no track in the animation, so it was at rest, and identity offsets keep it there.
        for (int i = 0; i < times.Length; i++)
            AssertClose(posedBefore[i], Mixed(session, animation, controller, times[i]));

        // The new track can be keyed right away.
        if (stack.IsEnabled && stack.Opacity > 1e-6f && PoseEditSession.EditableStack(animation, controller) == stack)
            Assert.NotNull(session.Begin(animation, controller, stack.Keys[0].Time, out _));
    }

    [SceneTheory, SceneSamples]
    public void AddToStack_IsUndoable_AndAddsNothingTwice(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, _, stack, controller)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var original = Tracks(stack);

        Assert.Equal(1, session.AddToStack(stack, [controller]));
        var edited = Tracks(stack);
        var undoable = session.History.NextUndo;
        Assert.Equal(0, session.AddToStack(stack, [controller]));
        Assert.Same(undoable, session.History.NextUndo);

        Assert.True(session.Undo());
        Assert.Equal(original, Tracks(stack));
        Assert.Null(stack.TrackFor(controller));
        Assert.True(session.Redo());
        Assert.Equal(edited, Tracks(stack));
        Assert.NotNull(stack.TrackFor(controller));
    }

    [SceneTheory, SceneSamples]
    public void AddedTrack_SurvivesSaveAndReload(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, animation, stack, controller)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        int stackIndex = animation.Stacks.ToList().IndexOf(stack);
        session.AddToStack(stack, [controller]);
        var expected = Tracks(stack);

        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-add-track-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(temp);
            var reloaded = TestFiles.LoadScene(temp);
            var c2 = reloaded.Scene.Characters.Single(c => c.Name == character.Name && c.Controllers.Any(o => o.Name == controller.Name));
            var a2 = c2.Animations.First(a => a.Name == animation.Name && a.FolderPath.SequenceEqual(animation.FolderPath));
            var s2 = a2.Stacks[stackIndex];
            var o2 = c2.Controllers.First(o => o.Name == controller.Name);
            Assert.NotNull(s2.TrackFor(o2));
            Assert.Equal(expected, Tracks(s2));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [SceneTheory, SceneSamples]
    public void AddToStack_AddsAnIKChainAndItsController_WithANewKeysDefaults(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (UntrackedChain(model, reverseFoot: false) is not var (character, animation, stack, ik)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var chain = ik.Chain;
        int before = stack.Tracks.Count();
        int n = stack.Keys.Count;

        Assert.Equal(chain.Count + 1, session.AddToStack(stack, [ik]));

        Assert.Equal(before + chain.Count + 1, stack.Tracks.Count());
        Assert.All(chain, j => Assert.NotNull(stack.TrackFor(j)));
        var track = stack.TrackFor(ik)!;
        Assert.Equal("CAnimTrackIK_VK", track.ClassName);
        Assert.Equal((AnimChannels)0x23F, track.Channels);
        Assert.Equal(n, track.Keys.Count);
        Assert.Equal(n, track.TangentsIn.Count);
        Assert.Equal(n, track.TangentsOut.Count);
        Assert.Equal(n, track.TangentModes.Count);
        Assert.All(track.TangentModes, modes =>
        {
            Assert.Equal(10, modes.Length);
            Assert.All(modes.Take(9), m => Assert.Equal(0x144u, m.Raw));
            Assert.Equal(0x133u, modes[9].Raw);
        });
        foreach (var (key, index) in track.Keys.Select((k, i) => (Assert.IsType<IKKey>(k), i)))
        {
            Assert.Equal(Transform.Identity, key.Value);
            Assert.Equal(0u, key.Flags & 1);
            Assert.Equal(0f, key.PoleAngle);
            Assert.Equal(0f, key.RefPoleAngle);
            Assert.Equal(Transform.Identity, key.RenderTransform);
            Assert.Same(ik, key.Controller);
            Assert.Equal(chain.Count, key.ChainTransforms.Count);
        }
        // The FK keys hold the chain they go with, from the evaluated locals at each key time, and
        // the end key replays key 0.
        for (int i = 0; i < n - 1; i++)
        {
            var local = session.Evaluator.LocalTransforms(animation, stack.Keys[i].Time);
            var refs = ((IKKey)track.Keys[i]!).ChainTransforms;
            for (int j = 0; j < chain.Count; j++) AssertClose(local[session.Evaluator.IndexOf(chain[j])], refs[j]);
        }
        Assert.Equal(((IKKey)track.Keys[0]!).ChainTransforms, ((IKKey)track.Keys[n - 1]!).ChainTransforms);

        // Undo and redo the whole group.
        var edited = Tracks(stack);
        Assert.True(session.Undo());
        Assert.Null(stack.TrackFor(ik));
        Assert.All(chain, j => Assert.Null(stack.TrackFor(j)));
        Assert.True(session.Redo());
        Assert.Equal(edited, Tracks(stack));

        // The new keys are usable: the chain helper finds every joint keyed, and a drag poses it.
        if (stack.IsEnabled && stack.Opacity > 1e-6f && PoseEditSession.EditableStack(animation, ik) == stack)
        {
            Assert.NotNull(session.Editor.IKChain(stack, 0, ik));
            var drag = session.Begin(animation, ik, stack.Keys[0].Time, out var reason);
            Assert.NotNull(drag);
            Assert.Null(reason);
            drag.Rotate(1, 0.15f);
            drag.Commit();
            var world = session.Evaluator.WorldMatrices(animation, stack.Keys[0].Time);
            float length = 0;
            for (int j = 1; j < chain.Count; j++)
                length += Vector3.Distance(world[session.Editor.Evaluator.IndexOf(chain[j])].Translation,
                    world[session.Editor.Evaluator.IndexOf(chain[j - 1])].Translation);
            Assert.True(Vector3.Distance(world[session.Editor.Evaluator.IndexOf(chain[^1])].Translation,
                world[session.Editor.Evaluator.IndexOf(ik)].Translation) < 1e-3f * length);
        }
    }

    [SceneTheory, SceneSamples]
    public void AddToStack_AddingAChainJointBringsTheWholeChain(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (UntrackedChain(model, reverseFoot: false) is not var (character, _, stack, ik)) return;
        var session = new PoseEditSession(new ScenePose(model), character);

        Assert.Equal(ik.Chain.Count + 1, session.AddToStack(stack, [ik.Chain[^1]]));

        Assert.NotNull(stack.TrackFor(ik));
        Assert.All(ik.Chain, j => Assert.NotNull(stack.TrackFor(j)));
    }

    [SceneTheory, SceneSamples]
    public void AddToStack_AddsAReverseFootShoeAndLeg_WithTheKeysDefaults(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (UntrackedChain(model, reverseFoot: true) is not var (character, animation, stack, ik)) return;
        var foot = (ControllerReverseFoot)ik;
        var session = new PoseEditSession(new ScenePose(model), character);
        int expected = foot.Shoe.Count + foot.Chain.Count + 1;
        int n = stack.Keys.Count;

        Assert.Equal(expected, session.AddToStack(stack, [foot]));

        Assert.All(foot.Shoe.Concat(foot.Chain), o => Assert.NotNull(stack.TrackFor(o)));
        var track = stack.TrackFor(foot)!;
        Assert.Equal("CAnimTrackReverseFoot_VK", track.ClassName);
        Assert.Equal((AnimChannels)0x1FFF, track.Channels);
        Assert.All(track.TangentModes, modes =>
        {
            Assert.Equal(13, modes.Length);
            Assert.All(modes.Take(9), m => Assert.Equal(0x144u, m.Raw));
            Assert.All(modes.Skip(9), m => Assert.Equal(0x133u, m.Raw));
        });
        for (int i = 0; i < n; i++)
        {
            var key = Assert.IsType<ReverseFootKey>(track.Keys[i]);
            Assert.Equal(Transform.Identity, key.Value);
            Assert.Equal(0u, key.Flags & 1);
            Assert.Equal((0f, 0f, 0f), (key.RollAngle, key.TwistAngle, key.BankAngle));
            Assert.Equal(foot.Chain.Count + 1, key.ChainTransforms.Count);
            if (i == n - 1) continue;
            // Entries 0..3 are the leg's locals; entry 4 is the controller relative to the ball.
            var local = session.Evaluator.LocalTransforms(animation, stack.Keys[i].Time);
            var world = session.Evaluator.WorldMatrices(animation, stack.Keys[i].Time);
            var refs = key.ChainTransforms;
            for (int j = 0; j < foot.Chain.Count; j++)
                AssertClose(local[session.Evaluator.IndexOf(foot.Chain[j])], refs[j]);
            Matrix4x4.Invert(world[session.Evaluator.IndexOf(foot.Chain[^1])], out var inverseBall);
            AssertClose(Transform.FromMatrixLoose(world[session.Evaluator.IndexOf(foot)] * inverseBall), refs[4]);
        }
        if (stack.IsEnabled && stack.Opacity > 1e-6f && PoseEditSession.EditableStack(animation, foot) == stack)
            Assert.NotNull(session.Editor.FootChain(stack, 0, foot));
    }

    [SceneTheory, SceneSamples]
    public void AddedIKTrack_SurvivesSaveAndReload(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (UntrackedChain(model, reverseFoot: false) is not var (character, animation, stack, ik)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        int stackIndex = animation.Stacks.ToList().IndexOf(stack);
        session.AddToStack(stack, [ik]);
        var expected = Tracks(stack);

        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-add-ik-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(temp);
            var reloaded = TestFiles.LoadScene(temp);
            var c2 = reloaded.Scene.Characters.Single(c => c.Name == character.Name);
            var a2 = c2.Animations.First(a => a.Name == animation.Name);
            var s2 = a2.Stacks[stackIndex];
            var ik2 = c2.IKControllers.First(o => o.Name == ik.Name);
            Assert.NotNull(s2.TrackFor(ik2));
            Assert.Equal(expected, Tracks(s2));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [SceneTheory, SceneSamples]
    public void RemoveFromStack_UndoesAnAdd_AndIsUndoable(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, _, stack, controller)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var original = Tracks(stack);
        session.AddToStack(stack, [controller]);
        var added = Tracks(stack);

        Assert.Equal(1, session.RemoveFromStack(stack, [controller], controller));
        Assert.Null(stack.TrackFor(controller));
        Assert.Equal(original, Tracks(stack));
        var undoable = session.History.NextUndo;
        Assert.Equal(0, session.RemoveFromStack(stack, [controller], controller));
        Assert.Same(undoable, session.History.NextUndo);

        Assert.True(session.Undo());
        Assert.Equal(added, Tracks(stack));
        Assert.True(session.Redo());
        Assert.Equal(original, Tracks(stack));
    }

    [SceneTheory, SceneSamples]
    public void RemovingAChainJoint_AlsoRemovesItsIKController_ButNotTheOtherJoints(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (KeyedChain(model, reverseFoot: false) is not var (character, stack, ik)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var joint = ik.Chain[^1];
        int before = stack.Tracks.Count();

        Assert.Equal(2, session.RemoveFromStack(stack, [joint], joint));
        Assert.Null(stack.TrackFor(joint));
        Assert.Null(stack.TrackFor(ik));
        Assert.All(ik.Chain.Take(ik.Chain.Count - 1), j => Assert.NotNull(stack.TrackFor(j)));
        Assert.Equal(before - 2, stack.Tracks.Count());
    }

    [SceneTheory, SceneSamples]
    public void RemovingAnIKController_RemovesItsShoe_ButNotItsChain(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (bool reverseFoot in new[] { false, true })
        {
            if (KeyedChain(model, reverseFoot) is not var (character, stack, ik)) continue;
            var session = new PoseEditSession(new ScenePose(model), character);
            var shoe = ik is ControllerReverseFoot rf ? rf.Shoe.Where(l => stack.TrackFor(l) != null).ToArray() : [];

            Assert.Equal(1 + shoe.Length, session.RemoveFromStack(stack, [ik], ik));
            Assert.Null(stack.TrackFor(ik));
            Assert.All(shoe, l => Assert.Null(stack.TrackFor(l)));
            Assert.All(ik.Chain, j => Assert.NotNull(stack.TrackFor(j)));
        }
    }

    [SceneTheory, SceneSamples]
    public void RemovingAnAncestorOfAChain_SnapsTheControllerAtFKKeys(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
            {
                if (animation.Stacks.Count != 1 || animation.Stacks[0] is not { IsEnabled: true } stack) continue;
                foreach (var ik in character.IKControllers)
                {
                    if (ik is ControllerReverseFoot || ik.Chain.Count < 2 || stack.TrackFor(ik) is not { } track) continue;
                    if (ik.Chain.Any(j => stack.TrackFor(j) == null) || IsAncestor(ik.Chain[0], ik)) continue;
                    // A keyed ancestor of the chain that does not also carry the controller.
                    var ancestor = Ancestors(ik.Chain[0]).FirstOrDefault(a =>
                        stack.TrackFor(a) != null && !IsAncestor(a, ik) && a.Constraint == null && a is not ControllerIK);
                    if (ancestor == null) continue;
                    int n = stack.Keys.Count;
                    var fk = Enumerable.Range(0, Math.Max(1, n - 1)).Where(k => track.Keys[k] is IKKey key && (key.Flags & 1) == 0).ToArray();
                    if (fk.Length == 0) continue;

                    var session = new PoseEditSession(new ScenePose(model), character);
                    Assert.True(session.RemoveFromStack(stack, [ancestor], ancestor) >= 1);
                    Assert.NotNull(stack.TrackFor(ik));
                    var ev = session.Evaluator;
                    foreach (int k in fk)
                    {
                        var world = ev.WorldMatrices(animation, stack.Keys[k].Time);
                        var end = world[ev.IndexOf(ik.Chain[^1])].Translation;
                        var at = world[ev.IndexOf(ik)].Translation;
                        Assert.True(Vector3.Distance(end, at) < 1e-3f * Math.Max(1, end.Length()),
                            $"{animation.Name} {ik.Name} key {k}: controller at {at}, chain end at {end}");
                    }
                    return;
                }
            }
    }

    /// <summary>A stack that keys an IK (or reverse-foot) controller, its whole chain and, for reverse foot, a shoe locator.</summary>
    private static (Character, AnimStack, ControllerIK)? KeyedChain(SceneModel model, bool reverseFoot)
    {
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
                foreach (var stack in animation.Stacks)
                    foreach (var ik in character.IKControllers)
                    {
                        if ((ik is ControllerReverseFoot) != reverseFoot || ik.Chain.Count < 2) continue;
                        if (stack.TrackFor(ik) == null || ik.Chain.Any(j => stack.TrackFor(j) == null)) continue;
                        if (ik is ControllerReverseFoot rf && !rf.Shoe.Any(l => stack.TrackFor(l) != null)) continue;
                        return (character, stack, ik);
                    }
        return null;
    }

    private static IEnumerable<Object3D> Ancestors(Object3D o)
    {
        for (var a = o.Parent3D; a != null; a = a.Parent3D) yield return a;
    }

    private static bool IsAncestor(Object3D ancestor, Object3D o) => Ancestors(o).Contains(ancestor);

    [Fact]
    public void Schema_NewObject_HasEveryMemberOfTheChain_WithZeroValues()
    {
        var schema = new AktSchema();
        var root = new AktClassDef("class CDataObject");
        root.Members.Add(new AktMember("Name", "class CAtom"));
        var derived = new AktClassDef("class CThing");
        derived.Members.Add(new AktMember("Count", "uint"));
        derived.Members.Add(new AktMember("Target", "class Reference<class CObject3D>"));
        derived.Members.Add(new AktMember("Pos", "class Vec3f"));
        derived.Members.Add(new AktMember("List", "class std::vector<class CPair>"));
        derived.Members.Add(new AktMember("Pair", "class CPair"));
        schema.AddClass(root, null);
        schema.AddClass(derived, root);
        var pair = new AktStructDef("class CPair");
        pair.Members.Add(new AktMember("A", "float"));
        pair.Members.Add(new AktMember("B", "bool"));
        schema.AddStruct(pair);

        var obj = schema.NewObject("CThing");
        Assert.Equal("class CThing", obj.TypeName);
        Assert.Equal(["Name", "Count", "Target", "Pos", "List", "Pair"], obj.Fields.Select(f => f.Name));
        Assert.Equal("class CDataObject", obj.Fields[0].Owner);
        Assert.Null(obj["Name"]);
        Assert.Equal(0u, obj["Count"]);
        Assert.True(((AktReference)obj["Target"]!).IsNull);
        Assert.Equal(new float[3], (float[])obj["Pos"]!);
        Assert.Empty((List<object?>)obj["List"]!);
        var s = (AktStruct)obj["Pair"]!;
        Assert.Equal(0f, s["A"]);
        Assert.Equal(false, s["B"]);
        Assert.Throws<InvalidOperationException>(() => schema.NewObject("CMissing"));
    }

    /// <summary>
    /// A single enabled override stack of an animation in which an IK (or reverse-foot) controller
    /// and every member of its add group have no track.
    /// </summary>
    private static (Character, Animation, AnimStack, ControllerIK)? UntrackedChain(SceneModel model, bool reverseFoot)
    {
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
            {
                if (animation.Stacks.Count(s => s.IsEnabled) != 1) continue;
                var stack = animation.Stacks.First(s => s.IsEnabled);
                if (stack.Keys.Count < 2) continue;
                foreach (var ik in character.IKControllers)
                {
                    if ((ik is ControllerReverseFoot) != reverseFoot || ik.Chain.Count < 2) continue;
                    if (KeyEditor.AddGroup(ik).Any(o => stack.TrackFor(o) != null)) continue;
                    return (character, animation, stack, ik);
                }
            }
        return null;
    }

    /// <summary>
    /// An enabled override stack at full opacity and a plain controller (not IK, no constraint, not
    /// in an IK chain) that no stack of the animation animates.
    /// </summary>
    private static (Character, Animation, AnimStack, Object3D)? Candidate(SceneModel model)
    {
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
            {
                var stack = animation.Stacks.FirstOrDefault(s =>
                    s.IsEnabled && !s.IsAdditive && MathF.Abs(s.Opacity - 1) < 1e-6f && s.Keys.Count >= 2);
                if (stack == null) continue;
                foreach (var controller in character.Controllers)
                {
                    if (controller is ControllerIK or ReverseFootLocator) continue;
                    if (!KeyEditor.CanAddToStack(controller, out _)) continue;
                    if (character.IKControllers.Any(ik => ik.Chain.Contains(controller))) continue;
                    if (animation.Stacks.Any(s => s.TrackFor(controller) != null)) continue;
                    return (character, animation, stack, controller);
                }
            }
        return null;
    }

    private static Transform Mixed(PoseEditSession session, Animation animation, Object3D controller, int frame) =>
        PoseEvaluator.Compose(session.Evaluator.RestTransforms[session.Evaluator.IndexOf(controller)],
            session.Evaluator.MixDelta(animation, controller, frame));

    /// <summary>Each track's target, mask, key values, spaces, IK fields and tangent modes, as comparable text.</summary>
    private static string Tracks(AnimStack stack) =>
        string.Join("|", stack.Tracks.Select(t =>
            $"{t.Target?.Name}:{t.Channels}:" +
            string.Join(";", t.Keys.Select(k => k is TransformKey tk ? KeyText(tk) : "-")) + ":" +
            string.Join(";", t.TangentModes.Select(m => string.Join(",", m)))));

    private static string KeyText(TransformKey key) => key switch
    {
        ReverseFootKey rf => $"{rf.Value}@{rf.Space?.Name}[{rf.PoleAngle},{rf.RollAngle},{rf.TwistAngle},{rf.BankAngle},{rf.Flags}]{{{Chain(rf)}}}",
        IKKey ik => $"{ik.Value}@{ik.Space?.Name}[{ik.PoleAngle},{ik.Flags}]{{{Chain(ik)}}}",
        _ => $"{key.Value}@{key.Space?.Name}",
    };

    private static string Chain(IKKey key) => string.Join(",", key.ChainTransforms);

    private static void AssertClose(Transform expected, Transform actual)
    {
        Assert.True(Vector3.Distance(expected.Translation, actual.Translation) < 1e-4f * Math.Max(1, expected.Translation.Length()),
            $"translation {expected.Translation} != {actual.Translation}");
        Assert.True(Vector3.Distance(expected.Scale, actual.Scale) < 1e-4f, $"scale {expected.Scale} != {actual.Scale}");
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected.Rotation), Quaternion.Normalize(actual.Rotation)));
        Assert.True(dot > 1 - 1e-6f, $"rotation {expected.Rotation} != {actual.Rotation}");
    }
}

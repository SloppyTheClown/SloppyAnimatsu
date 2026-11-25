using System.Numerics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

public class PoseEditingTests
{
    [Fact]
    public void Gizmo_DragDistance_IsInParentUnits()
    {
        var camera = new OrbitCamera { Target = Vector3.Zero, Distance = 5, Yaw = 0, Pitch = 0 };
        var viewport = new Vector2(800, 600);
        var gizmo = new TransformGizmo { Visible = true, Mode = GizmoMode.Translate, CanTranslate = true,
            ParentFrame = Matrix4x4.CreateScale(2) };
        var a = camera.Project(Vector3.Zero, viewport)!.Value;
        var b = camera.Project(new Vector3(1, 0, 0), viewport)!.Value;

        // One world unit along X is half a unit of a parent scaled by 2.
        Assert.Equal(0.5f, gizmo.DragDistance(camera, viewport, 0, b - a), 3);
        // Moving across the axis does not move along it.
        Assert.Equal(0f, gizmo.DragDistance(camera, viewport, 0, new Vector2(0, 40)), 3);
    }

    [Fact]
    public void Gizmo_DragAngle_IsRightHandedAboutTheAxis()
    {
        // The camera looks down -Z, so +Z points at the viewer, X right and Y up on screen.
        var camera = new OrbitCamera { Target = Vector3.Zero, Distance = 5, Yaw = 0, Pitch = 0 };
        var viewport = new Vector2(800, 600);
        var gizmo = new TransformGizmo { Visible = true };
        var c = camera.Project(Vector3.Zero, viewport)!.Value;

        // From the right to the top of the centre: a quarter turn from +X to +Y, positive about +Z.
        float angle = gizmo.DragAngle(camera, viewport, 2, c + new Vector2(100, 0), c + new Vector2(0, -100));
        Assert.Equal(MathF.PI / 2, angle, 3);
        // The same drag about an axis pointing away (a mirrored frame) turns the other way.
        gizmo.Frame = Matrix4x4.CreateScale(1, 1, -1);
        Assert.Equal(-MathF.PI / 2, gizmo.DragAngle(camera, viewport, 2, c + new Vector2(100, 0), c + new Vector2(0, -100)), 3);
    }

    [Fact]
    public void Gizmo_HitTest_FindsTheHandleUnderThePointer()
    {
        var camera = new OrbitCamera { Target = Vector3.Zero, Distance = 5, Yaw = 0, Pitch = 0 };
        var viewport = new Vector2(800, 600);
        var gizmo = new TransformGizmo { Visible = true, Mode = GizmoMode.Translate, CanTranslate = true };
        float size = gizmo.Size(camera);
        var onX = camera.Project(new Vector3(size * 0.5f, 0, 0), viewport)!.Value;
        var onY = camera.Project(new Vector3(0, size * 0.5f, 0), viewport)!.Value;

        Assert.Equal(0, gizmo.HitTest(camera, viewport, onX + new Vector2(0, 3), 6));
        Assert.Equal(1, gizmo.HitTest(camera, viewport, onY + new Vector2(3, 0), 6));
        Assert.Equal(-1, gizmo.HitTest(camera, viewport, new Vector2(10, 10), 6));

        gizmo.CanTranslate = false;
        Assert.Equal(-1, gizmo.HitTest(camera, viewport, onX, 6));
    }

    [Fact]
    public void History_DropsTheOldestPastCapacity_AndANewEditClearsRedo()
    {
        var history = new EditHistory { Capacity = 2 };
        EditRecord R(string name) => new(name, null!, null!);
        history.Push(R("a"));
        history.Push(R("b"));
        history.Push(R("c"));
        Assert.Equal("c", history.Undo()!.Description);
        Assert.Equal("b", history.Undo()!.Description);
        Assert.Null(history.Undo());
        Assert.True(history.CanRedo);
        history.Push(R("d"));
        Assert.False(history.CanRedo);
    }

    [SceneTheory, SceneSamples]
    public void History_SpillsPastItsMemoryBudget_AndDropsOnlyPastItsDiskBudget(string path)
    {
        var model = TestFiles.LoadScene(path);
        // Spilling moves a snapshot's bytes, so every record gets its own snapshot. The records carry
        // a Before side only, so Push has nothing to trim.
        EditSnapshot Take() => EditSnapshot.TakeScene(null, model.Scene);
        long one = Take().EstimatedBytes;
        Assert.True(one > 0);

        // Within the budget nothing moves.
        using (var roomy = new EditHistory { MemoryBudget = 10 * one })
        {
            foreach (var name in new[] { "a", "b", "c" })
                roomy.Push(new EditRecord(name, Take(), null!));
            Assert.Equal(3, roomy.Count);
            Assert.Equal(3 * one, roomy.EstimatedBytes);
            Assert.Equal(0, roomy.SpilledBytes);
        }

        // Past the memory budget the older steps go to the spill file and stay undoable; the newest
        // step stays in memory.
        using (var tight = new EditHistory { MemoryBudget = one + one / 2 })
        {
            foreach (var name in new[] { "a", "b", "c" })
                tight.Push(new EditRecord(name, Take(), null!));
            Assert.Equal(3, tight.Count);
            Assert.True(tight.SpilledBytes > 0);
            // A spilled step keeps its entries and reference tables in memory, only its bytes move.
            Assert.True(tight.EstimatedBytes < 3 * one);
            Assert.False(tight.NextUndo!.Before.IsReleased);
            Assert.Equal(0, tight.NextUndo.Before.SpilledBytes);
            long fileBytes = tight.SpillFileBytes;
            Assert.True(fileBytes >= tight.SpilledBytes);

            // A further push spills one more step and does not write the spilled ones again.
            long spilledBefore = tight.SpilledBytes;
            tight.Push(new EditRecord("d", Take(), null!));
            Assert.Equal(tight.SpillFileBytes - fileBytes, tight.SpilledBytes - spilledBefore);

            // Every step comes back, the spilled ones read from the file.
            foreach (var name in new[] { "d", "c", "b", "a" })
            {
                var record = tight.Undo()!;
                Assert.Equal(name, record.Description);
                record.Before.Restore();
            }
            Assert.Null(tight.Undo());
        }

        // A record larger than the whole budget is still kept in memory: the newest step can always be undone.
        using (var tiny = new EditHistory { MemoryBudget = 1 })
        {
            tiny.Push(new EditRecord("big", Take(), null!));
            Assert.Equal(1, tiny.Count);
            Assert.Equal(0, tiny.SpilledBytes);
        }

        // Past the disk budget the oldest steps are dropped, the newest kept.
        using (var full = new EditHistory { MemoryBudget = 1, DiskBudget = 1 })
        {
            foreach (var name in new[] { "a", "b", "c" })
                full.Push(new EditRecord(name, Take(), null!));
            Assert.Equal(1, full.Count);
            Assert.Equal("c", full.Undo()!.Description);
        }
    }

    [SceneTheory, SceneSamples]
    public void Drag_BetweenKeys_CreatesAKeyAndMixesToTheDraggedPose(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, animation, controller, frame)) return;
        var session = new PoseEditSession(new ScenePose(model), character);

        var drag = session.Begin(animation, controller, frame, out var reason);
        Assert.NotNull(drag);
        Assert.Null(reason);
        Assert.True(drag.CreatedKey);
        Assert.Equal(drag.KeyIndex, PoseEditSession.KeyAt(drag.Stack, frame));
        // Creating the key keeps the pose.
        AssertClose(drag.BaseLocal, Mixed(session, animation, controller, frame));

        drag.Rotate(1, 0.4f);
        var expected = drag.BaseLocal.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f);
        AssertClose(drag.BaseLocal with { Rotation = expected }, Mixed(session, animation, controller, frame));

        // A later move replaces the earlier one rather than adding to it.
        drag.Rotate(0, -0.25f);
        expected = drag.BaseLocal.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.25f);
        AssertClose(drag.BaseLocal with { Rotation = expected }, Mixed(session, animation, controller, frame));
    }

    [SceneTheory, SceneSamples]
    public void Cancel_And_Undo_PutTheStackBack_Redo_ReappliesTheEdit(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, animation, controller, frame)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var stack = PoseEditSession.EditableStack(animation, controller)!;
        var original = Values(stack);

        var drag = session.Begin(animation, controller, frame, out _)!;
        drag.Rotate(2, 0.5f);
        drag.Cancel();
        Assert.Equal(original, Values(stack));
        Assert.False(session.History.CanUndo);

        drag = session.Begin(animation, controller, frame, out _)!;
        drag.Rotate(2, 0.5f);
        if (drag.CanTranslate) drag.Translate(0, 1.5f);
        var posed = drag.Posed;
        drag.Commit();
        var edited = Values(stack);
        Assert.NotEqual(original, edited);

        Assert.True(session.Undo());
        Assert.Equal(original, Values(stack));
        Assert.Equal(-1, PoseEditSession.KeyAt(stack, frame));

        Assert.True(session.Redo());
        Assert.Equal(edited, Values(stack));
        AssertClose(posed, Mixed(session, animation, controller, frame), rotationOnly: !drag.CanTranslate);
    }

    [SceneTheory, SceneSamples]
    public void TimelineKeyEdits_AreRecorded_AndUndoRestoresTheStack(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, animation, controller, frame)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var stack = PoseEditSession.EditableStack(animation, controller)!;
        var original = Values(stack);
        var times = stack.Keys.Select(k => k.Time).ToArray();
        int n = times.Length;

        // Create between keys 0 and 1, then undo.
        int created = session.CreateKey(stack, frame);
        Assert.Equal(1, created);
        Assert.Equal(n + 1, stack.Keys.Count);
        Assert.Equal(frame, stack.Keys[1].Time);
        Assert.True(session.Undo());
        Assert.Equal(original, Values(stack));

        // Timeline delete keeps the other times; the end key is never deleted.
        Assert.Equal(1, session.DeleteKeys(stack, [1, n - 1], closeGaps: false));
        Assert.Equal(times.Where((_, i) => i != 1), stack.Keys.Select(k => k.Time));
        Assert.True(session.Undo());
        Assert.Equal(original, Values(stack));

        // A move onto key 0's time steps back; key 1 lands one frame after it.
        var moved = session.MoveKeys(stack, [1], times[0] - times[1]);
        Assert.Equal(1, moved[1]);
        Assert.Equal(times[0] + 1, stack.Keys[1].Time);
        var edited = Values(stack);
        Assert.True(session.Undo());
        Assert.Equal(original, Values(stack));
        Assert.True(session.Redo());
        Assert.Equal(edited, Values(stack));
        Assert.True(session.Undo());

        // Edits that change nothing are not recorded.
        var undoable = session.History.NextUndo;
        session.MoveKeys(stack, [1], 0);
        Assert.Equal(0, session.DeleteKeys(stack, [n - 1], closeGaps: true));
        Assert.Same(undoable, session.History.NextUndo);
        Assert.Equal(original, Values(stack));
    }

    [SceneTheory, SceneSamples]
    public void CommittedKey_SurvivesSaveAndReload(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, animation, controller, frame)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var drag = session.Begin(animation, controller, frame, out _)!;
        drag.Rotate(0, 0.3f);
        drag.Commit();
        var posed = drag.Posed;

        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-viewer-edit-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(temp);
            var reloaded = TestFiles.LoadScene(temp);
            var c2 = reloaded.Scene.Characters.Single(c => c.Name == character.Name && c.Controllers.Any(o => o.Name == controller.Name));
            var a2 = c2.Animations.First(a => a.Name == animation.Name);
            var o2 = c2.Controllers.First(o => o.Name == controller.Name);
            var stack2 = PoseEditSession.EditableStack(a2, o2)!;
            Assert.True(PoseEditSession.KeyAt(stack2, frame) >= 0);
            var evaluator = new PoseEvaluator(c2);
            var mixed = PoseEvaluator.Compose(evaluator.RestTransforms[evaluator.IndexOf(o2)], evaluator.MixDelta(a2, o2, frame));
            AssertClose(posed, mixed, rotationOnly: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>
    /// A controller keyed in a stack whose first two keys are at least two frames apart, with the
    /// frame just after the first key (where no key exists and stack time equals animation time).
    /// </summary>
    private static (Character, Animation, Object3D, int)? Candidate(SceneModel model)
    {
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
                foreach (var controller in character.Controllers)
                {
                    if (controller is ControllerIK) continue; // IK posing is not covered by the key editor
                    if (PoseEditSession.EditableStack(animation, controller) is not { } stack) continue;
                    var keys = stack.Keys;
                    if (keys.Count < 3 || keys[1].Time - keys[0].Time < 2) continue;
                    if (stack.TrackFor(controller)!.Keys.Take(keys.Count).Any(k => k is not TransformKey)) continue;
                    int frame = keys[0].Time + 1;
                    if (IsInIKChain(character, controller)) continue;
                    return (character, animation, controller, frame);
                }
        return null;
    }

    /// <summary>Joints driven by an IK controller show the solved pose, not the mixed one.</summary>
    private static bool IsInIKChain(Character character, Object3D controller) =>
        character.IKControllers.Any(ik => ik.Chain.Contains(controller));

    private static Transform Mixed(PoseEditSession session, Animation animation, Object3D controller, int frame) =>
        PoseEvaluator.Compose(session.Evaluator.RestTransforms[session.Evaluator.IndexOf(controller)],
            session.Evaluator.MixDelta(animation, controller, frame));

    /// <summary>The stack's key times and every track's key values, as comparable text.</summary>
    private static string Values(AnimStack stack) =>
        string.Join(";", stack.Keys) + "|" + string.Join("|", stack.Tracks.Select(t =>
            string.Join(";", t.Keys.Select(k => k is TransformKey tk ? tk.Value.ToString() : "-"))));

    private static void AssertClose(Transform expected, Transform actual, bool rotationOnly = false)
    {
        if (!rotationOnly)
        {
            Assert.True(Vector3.Distance(expected.Translation, actual.Translation) < 1e-3f * Math.Max(1, expected.Translation.Length()),
                $"translation {expected.Translation} != {actual.Translation}");
            Assert.True(Vector3.Distance(expected.Scale, actual.Scale) < 1e-3f, $"scale {expected.Scale} != {actual.Scale}");
        }
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected.Rotation), Quaternion.Normalize(actual.Rotation)));
        Assert.True(dot > 1 - 1e-4f, $"rotation {expected.Rotation} != {actual.Rotation}");
    }
}

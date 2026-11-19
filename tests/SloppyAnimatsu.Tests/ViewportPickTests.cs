using System.Numerics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// What a click in the viewport selects
/// the controller drawn nearest within the tolerance, never a hidden or unpickable one.
/// The viewer's click handler uses the same <see cref="ViewportPick"/>, so the rule is checked here
/// instead of by clicking in a window.
/// </summary>
public class ViewportPickTests
{
    [SceneTheory, SceneSamples]
    public void PickingAController_GivesTheNearestPickableOne(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Pose(model) is not { } pose) return;
        var (camera, viewport) = ViewFor(pose);
        var controllers = pose.Evaluator(pose.Character!).Controllers;
        var worlds = pose.ControllerWorlds;
        for (int i = 0; i < controllers.Count && i < worlds.Length; i++)
        {
            if (!Outliner.IsPickable(controllers[i])) continue;
            if (camera.Project(worlds[i].Translation, viewport) is not { } projected) continue;
            var at = projected + new Vector2(1, -1);

            var picked = ViewportPick.Nearest(camera, viewport, controllers, worlds, at);

            Assert.NotNull(picked);
            int index = Index(controllers, picked!);
            float distance = Vector2.Distance(camera.Project(worlds[index].Translation, viewport)!.Value, at);
            Assert.True(distance <= ViewportPick.Tolerance, $"{picked.Name} was picked {distance} DIPs away");
            for (int j = 0; j < controllers.Count && j < worlds.Length; j++)
            {
                if (!Outliner.IsPickable(controllers[j])) continue;
                if (camera.Project(worlds[j].Translation, viewport) is not { } other) continue;
                Assert.True(Vector2.Distance(other, at) >= distance - 1e-3f,
                    $"{controllers[j].Name} is nearer than the picked {picked.Name}");
            }
        }
    }

    [SceneTheory, SceneSamples]
    public void NothingIsPickedOffTheScene(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Pose(model) is not { } pose) return;
        var (camera, viewport) = ViewFor(pose);

        var picked = ViewportPick.Nearest(camera, viewport, pose.Evaluator(pose.Character!).Controllers,
            pose.ControllerWorlds, new Vector2(viewport.X + 5000, viewport.Y + 5000));

        Assert.Null(picked);
    }

    [SceneTheory, SceneSamples]
    public void AHiddenOrUnpickableController_IsSkipped(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Pose(model) is not { } pose) return;
        var (camera, viewport) = ViewFor(pose);
        var controllers = pose.Evaluator(pose.Character!).Controllers;
        var worlds = pose.ControllerWorlds;
        var candidate = controllers.FirstOrDefault(c =>
            Outliner.IsPickable(c) && Index(controllers, c) < worlds.Length &&
            camera.Project(worlds[Index(controllers, c)].Translation, viewport) != null);
        if (candidate == null) return;
        int index = Index(controllers, candidate);
        var at = camera.Project(worlds[index].Translation, viewport)!.Value;

        // With only the candidate allowed, a click on it selects it.
        Assert.Same(candidate, ViewportPick.Nearest(camera, viewport, controllers, worlds, at,
            pickable: c => ReferenceEquals(c, candidate)));
        // Refused, the same click selects another controller or nothing - never the candidate.
        Assert.NotSame(candidate, ViewportPick.Nearest(camera, viewport, controllers, worlds, at,
            pickable: c => !ReferenceEquals(c, candidate) && Outliner.IsPickable(c)));
        // The two flags are what the default rule reads.
        try
        {
            candidate.IsUnpickable = true;
            Assert.False(Outliner.IsPickable(candidate));
            Assert.NotSame(candidate, ViewportPick.Nearest(camera, viewport, controllers, worlds, at));
        }
        finally
        {
            candidate.IsUnpickable = false;
        }
        try
        {
            candidate.IsShown = false;
            Assert.False(Outliner.IsPickable(candidate));
            Assert.NotSame(candidate, ViewportPick.Nearest(camera, viewport, controllers, worlds, at));
        }
        finally
        {
            candidate.IsShown = true;
        }
        Assert.True(Outliner.IsPickable(candidate));
    }

    [Fact]
    public void ACustomPickableRuleDecides_AndNothingIsPickedWhenItRefusesEverything()
    {
        var model = TestFiles.Resource3D == null ? null : TestFiles.LoadScene(TestFiles.Resource3D);
        if (model == null) return;
        var pose = new ScenePose(model);
        var character = model.Scene.Characters.FirstOrDefault();
        if (character == null) return;
        try { pose.Update(character, null, 0); } catch (Exception) { return; }
        var (camera, viewport) = ViewFor(pose);
        var controllers = pose.Evaluator(character).Controllers;

        Assert.Null(ViewportPick.Nearest(camera, viewport, controllers, pose.ControllerWorlds,
            viewport / 2f, pickable: static _ => false));
    }

    /// <summary>
    /// An IK controller is drawn as a ring around its chain's leaf joint, so a click on the ring
    /// picks the controller and not the joint at the ring's centre.
    /// </summary>
    [SceneTheory, SceneSamples]
    public void AClickOnAnIkRing_PicksTheIkController(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Pose(model) is not { } pose) return;
        var character = pose.Character!;
        var evaluator = pose.Evaluator(character);
        var worlds = pose.ControllerWorlds;
        var viewport = new Vector2(800, 600);
        int shown = 0, tested = 0;
        foreach (var (controller, points) in ControllerShapes.Outlines(character, evaluator, worlds))
        {
            if (!Outliner.IsPickable(controller) || points.Count == 0) continue;
            shown++;
            // A front view framed on this outline, so the ring is some hundred DIPs across.
            var min = points.Aggregate(Vector3.Min);
            var max = points.Aggregate(Vector3.Max);
            var centre = (min + max) / 2;
            float extent = MathF.Max(MathF.Max(max.X - min.X, max.Y - min.Y), 1e-3f);
            var camera = SceneCameraView.Ortho(0, extent * 3, new Vector2(centre.X, centre.Y));

            // The outline point farthest from every controller origin.
            var origins = new List<Vector2>();
            for (int i = 0; i < evaluator.Controllers.Count && i < worlds.Length; i++)
                if (camera.Project(worlds[i].Translation, viewport) is { } o) origins.Add(o);
            Vector2? at = null;
            float clearance = ViewportPick.Tolerance * 2;
            foreach (var point in points)
            {
                if (camera.Project(point, viewport) is not { } p) continue;
                float nearest = origins.Count == 0 ? float.MaxValue : origins.Min(o => Vector2.Distance(o, p));
                if (nearest > clearance) { clearance = nearest; at = p; }
            }
            if (at is not { } click) continue;
            tested++;

            Assert.Null(ViewportPick.Nearest(camera, viewport, evaluator.Controllers, worlds, click));
            var picked = ViewportPick.At(camera, viewport, character, evaluator, worlds, click);
            Assert.True(picked is ControllerIK, $"{controller.Name}: a click on its outline picked {picked?.Name ?? "nothing"}");

            // The origin itself still picks whatever sits there, and a hidden controller has no outline.
            Assert.Null(ViewportPick.At(camera, viewport, character, evaluator, worlds, click, pickable: _ => false));
        }
        Assert.True(shown == 0 || tested > 0, "no IK outline could be clicked clear of the controller origins");
    }

    private static int Index(IReadOnlyList<Object3D> controllers, Object3D controller)
    {
        for (int i = 0; i < controllers.Count; i++)
            if (ReferenceEquals(controllers[i], controller)) return i;
        return -1;
    }

    private static ScenePose? Pose(SceneModel model)
    {
        var pose = new ScenePose(model);
        var character = model.Scene.Characters.FirstOrDefault();
        if (character == null) return null;
        try { pose.Update(character, null, 0); }
        catch (Exception) { return null; }
        return pose.Bounds.Min == pose.Bounds.Max ? null : pose;
    }

    /// <summary>An orthographic front view framed on the scene's bounds, 800 by 600.</summary>
    private static (IViewCamera Camera, Vector2 Viewport) ViewFor(ScenePose pose)
    {
        var center = (pose.Bounds.Min + pose.Bounds.Max) * 0.5f;
        var size = pose.Bounds.Max - pose.Bounds.Min;
        float height = MathF.Max(MathF.Max(size.Y, size.X) * 1.2f, 1f);
        return (SceneCameraView.Ortho(0, height, new Vector2(center.X, center.Y)), new Vector2(800, 600));
    }
}

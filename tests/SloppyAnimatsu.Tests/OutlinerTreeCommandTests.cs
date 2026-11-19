using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The hierarchy panel's three tree commands
/// The Select hierarchy, Toggle hierarchy selection and Delete tree object.
/// </summary>
public class OutlinerTreeCommandTests
{
    private static Joint? JointWithChildren(SceneModel model) =>
        model.Scene.Descendants<Joint>().FirstOrDefault(j => j.Descendants<Joint>().Any());

    private static Character? RiggedCharacter(SceneModel model) =>
        model.Scene.Characters.FirstOrDefault(c => c.Joints.Count > 0);

    [SceneTheory, SceneSamples]
    public void SelectHierarchy_AddsTheObjectAndItsSubtreeOnce(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (JointWithChildren(model) is not { } root) return;
        var expected = Outliner.Subtree(root).ToList();

        var selection = new List<Object3D>();
        int added = Outliner.SelectHierarchy(selection, root);

        Assert.Equal(expected.Count, added);
        Assert.Equal(expected.Count, selection.Count);
        Assert.Contains(root, selection);
        Assert.All(expected, o => Assert.Contains(o, selection));
        // Selecting the same hierarchy again adds nobody: the app replaces the marker in place.
        Assert.Equal(0, Outliner.SelectHierarchy(selection, root));
        Assert.Equal(expected.Count, selection.Count);
    }

    [SceneTheory, SceneSamples]
    public void SelectHierarchy_FilterKeepsTheControllersTheViewerCanSelect(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (RiggedCharacter(model) is not { } character || JointWithChildren(model) is not { } root) return;
        var evaluator = new PoseEvaluator(character);
        var controllers = Outliner.Subtree(root).Where(o => evaluator.IndexOf(o) >= 0).ToList();
        Assert.NotEmpty(controllers);

        var selection = new List<Object3D>();
        int added = Outliner.SelectHierarchy(selection, root, o => evaluator.IndexOf(o) >= 0);

        Assert.Equal(controllers.Count, added);
        Assert.All(selection, o => Assert.Contains(o, controllers));
    }

    [SceneTheory, SceneSamples]
    public void ToggleHierarchySelection_LeavesWhenTheRootIsSelectedAndJoinsOtherwise(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (JointWithChildren(model) is not { } root) return;
        int size = Outliner.Subtree(root).Count();

        // The hierarchy is selected, so the toggle takes it all out again.
        var selection = new List<Object3D>();
        Assert.Equal(size, Outliner.SelectHierarchy(selection, root));
        int removed = Outliner.ToggleHierarchySelection(selection, root);
        Assert.Equal(-size, removed);
        Assert.Empty(selection);

        int back = Outliner.ToggleHierarchySelection(selection, root);
        Assert.Equal(size, back);
        Assert.Equal(size, selection.Count);
    }

    [SceneTheory, SceneSamples]
    public void ToggleHierarchySelection_DecidesOnTheClickedObjectNotOnADescendant(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (JointWithChildren(model) is not { } root) return;
        var child = root.Descendants<Joint>().First();
        // Only a descendant is selected: the app looks for the clicked object's own marker, so the
        // hierarchy still joins the selection (and the already selected descendant stays once).
        var selection = new List<Object3D> { child };
        int changed = Outliner.ToggleHierarchySelection(selection, root);
        Assert.Equal(Outliner.Subtree(root).Count() - 1, changed);
        Assert.Contains(root, selection);
        Assert.Contains(child, selection);
    }

    [SceneTheory, SceneSamples]
    public void TreeJoints_KeepsTheJointSubtreeAndStopsAtANonJointSelection(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (JointWithChildren(model) is not { } root) return;
        var joints = Outliner.TreeJoints([root]);
        Assert.Equal(Outliner.Subtree(root).OfType<Joint>().Count(), joints.Count);
        Assert.Contains(root, joints);

        // A selected mesh is not a joint, and the walk does not descend through it.
        if (model.Scene.Meshes.FirstOrDefault() is { } mesh) Assert.Empty(Outliner.TreeJoints([mesh]));

        // A joint and a joint below it are not collected twice.
        var child = root.Descendants<Joint>().First();
        Assert.Equal(joints.Count, Outliner.TreeJoints([root, child]).Count);
    }

    [SceneTheory, SceneSamples]
    public void DeleteTreeObjects_RemovesTheJointSubtree(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (RiggedCharacter(model) is not { } character) return;
        var target = character.Joints.FirstOrDefault(j => j.Constraint == null && !j.Descendants<Joint>().Any());
        if (target == null) return;
        var editor = new RigEditor(character);
        int before = character.Joints.Count;
        string name = target.Name!;

        var result = Outliner.DeleteTreeObjects(editor, [target]);

        Assert.True(result.Ok, result.Refusal);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(before - 1, character.Joints.Count);
        Assert.DoesNotContain(character.Joints, j => j.Name == name);
    }

    [SceneTheory, SceneSamples]
    public void DeleteTreeObjects_TakesTheControllerAndItsShoesWithTheJoint(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (RiggedCharacter(model) is not { } character) return;
        // A constrained joint without joints below it keeps the counts simple.
        var target = character.Joints.FirstOrDefault(j => j.Constraint != null && !j.Descendants<Joint>().Any());
        if (target == null) return;
        var controller = target.Constraint!;
        var shoes = Outliner.Subtree(controller).Where(o => !ReferenceEquals(o, controller)).ToList();
        var chain = character.Joints.Where(j => ReferenceEquals(j.Constraint, controller)).ToList();
        var editor = new RigEditor(character);
        int joints = character.Joints.Count;
        int slots = character.Controllers.Count;
        var slotSet = character.Controllers.ToHashSet(ReferenceEqualityComparer.Instance);
        int removedSlots = 2 + shoes.Count(slotSet.Contains);   // the joint and its controller, plus its shoes

        var result = Outliner.DeleteTreeObjects(editor, [target]);

        // The app takes the constraining controller with the joint, its shoe
        // locators with the controller, and clears the Constraint of the joints that stay.
        Assert.True(result.Ok, result.Refusal);
        Assert.Equal(2, result.Deleted);
        Assert.Equal(joints - 1, character.Joints.Count);
        Assert.DoesNotContain(target, character.Joints);
        Assert.Equal(slots - removedSlots, character.Controllers.Count);
        Assert.DoesNotContain(controller, character.Controllers);
        Assert.All(shoes, shoe => Assert.DoesNotContain(shoe, character.Controllers));
        foreach (var joint in chain.Where(j => !ReferenceEquals(j, target)))
            Assert.Null(joint.Constraint);
        // The pose matrices stay one per controller.
        Assert.All(editor.Poses(), pose => Assert.Equal(character.Controllers.Count, pose.Count));
        foreach (var animation in character.Animations.Append(character.RigAnimation).OfType<Animation>())
            foreach (var stack in animation.Stacks)
                Assert.DoesNotContain(stack.Tracks, t =>
                    ReferenceEquals(t.Target, controller) || shoes.Any(s => ReferenceEquals(t.Target, s)));
    }

    [SceneTheory, SceneSamples]
    public void DeleteTreeObjects_RemovesASelectedControllerAndClearsTheConstraints(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (RiggedCharacter(model) is not { } character) return;
        var controller = character.Controllers.OfType<Controller3D>()
            .FirstOrDefault(c => character.Joints.Any(j => ReferenceEquals(j.Constraint, c)));
        if (controller == null) return;
        var chain = character.Joints.Where(j => ReferenceEquals(j.Constraint, controller)).ToList();
        var shoes = Outliner.Subtree(controller).Where(o => !ReferenceEquals(o, controller)).ToList();
        var editor = new RigEditor(character);
        int joints = character.Joints.Count;
        int slots = character.Controllers.Count;
        var slotSet = character.Controllers.ToHashSet(ReferenceEqualityComparer.Instance);
        int removedSlots = 1 + shoes.Count(slotSet.Contains);

        // A controller row selected on its own is what the DELETE removes
        var result = Outliner.DeleteTreeObjects(editor, [controller]);

        Assert.True(result.Ok, result.Refusal);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(joints, character.Joints.Count);
        Assert.Equal(slots - removedSlots, character.Controllers.Count);
        Assert.All(chain, joint => Assert.Null(joint.Constraint));
        Assert.All(shoes, shoe => Assert.DoesNotContain(shoe, character.Controllers));
    }

    [SceneTheory, SceneSamples]
    public void DeleteTreeObjects_RefusesAJointWithALockedSkinnedMeshUnderIt(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (RiggedCharacter(model) is not { } character) return;
        var mesh = character.Meshes.FirstOrDefault(m => m.Skinned);
        if (mesh == null) return;
        // The joint the mesh leans on most, when no controller constrains it (a constraint is refused
        // first, and that rule has its own test).
        var bone = mesh.SkinWeights
            .SelectMany(vertex => vertex.Where(w => w.Weight > 0))
            .GroupBy(w => w.Bone)
            .OrderByDescending(g => g.Sum(w => w.Weight))
            .Select(g => g.Key)
            .FirstOrDefault(-1);
        if (bone < 0 || bone >= character.Joints.Count) return;
        var target = character.Joints[bone];
        if (target.Constraint != null) return;
        Outliner.SetMeshesLocked([mesh], true);
        var editor = new RigEditor(character);
        int before = character.Joints.Count;

        var result = Outliner.DeleteTreeObjects(editor, [target]);

        Assert.False(result.Ok);
        Assert.Contains("locked skinned mesh", result.Refusal!);
        Assert.Contains(mesh.Name!, result.Refusal!);
        Assert.Equal(before, character.Joints.Count);
        // The refusal is the same sentence the plan gives, so a caller can refuse without an edit.
        Assert.Equal(result.Refusal, Outliner.PlanTreeDelete(editor, [target]).Refusal);
    }

    [SceneTheory, SceneSamples]
    public void DeleteTreeObjects_SaysSoWhenTheSelectionHoldsNoJointOrController(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (RiggedCharacter(model) is not { } character) return;
        var editor = new RigEditor(character);
        var mesh = model.Scene.Meshes.FirstOrDefault();
        if (mesh == null) return;
        var result = Outliner.DeleteTreeObjects(editor, [mesh]);
        Assert.False(result.Ok);
        Assert.Contains("no joint or controller", result.Refusal!);
        Assert.Contains("no joint or controller", Outliner.PlanTreeDelete(editor, []).Refusal!);
        Assert.Empty(Outliner.PlanTreeDelete(editor, []).Joints);
        Assert.Empty(Outliner.PlanTreeDelete(editor, []).Controllers);
    }

    [Fact]
    public void TreeSceneObjects_TakesMeshesAndCharactersButNotRigRowsOrFixedCameras()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        Assert.Equal([character], Outliner.TreeSceneObjects([character]));
        Assert.Empty(Outliner.TreeSceneObjects([model.Scene.Cameras.First()]));
        Assert.Empty(Outliner.TreeSceneObjects(character.Joints.Take(1).Cast<Object3D>()));
    }

    [SceneTheory, SceneSamples]
    public void DeleteSceneObjects_RemovesAMeshAndEveryReferenceToIt(string path)
    {
        var model = TestFiles.LoadScene(path);
        var mesh = model.Scene.Meshes.FirstOrDefault();
        if (mesh == null) return;
        var gone = mesh.Source.PreOrder().ToHashSet(ReferenceEqualityComparer.Instance);
        int meshes = model.Scene.Meshes.Count();

        Assert.Equal([mesh], Outliner.TreeSceneObjects([mesh]));
        Assert.Equal(1, Outliner.DeleteSceneObjects(model.Scene, [mesh]));

        Assert.Equal(meshes - 1, model.Scene.Meshes.Count());
        Assert.DoesNotContain(model.Scene.Meshes, m => ReferenceEquals(m.Source, mesh.Source));
        Assert.DoesNotContain(ReferenceTargets(model), gone.Contains);
    }

    [SceneTheory, SceneSamples]
    public void DeleteSceneObjects_RemovesACharacterWithItsAnimationsAndBoundMeshes(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (RiggedCharacter(model) is not { } character) return;
        var bound = character.Meshes.ToList();
        var gone = character.Source.PreOrder().Concat(bound.SelectMany(m => m.Source.PreOrder()))
            .ToHashSet(ReferenceEqualityComparer.Instance);
        int characters = model.Scene.Characters.Count();

        // The character plus each bound mesh that does not already hang under it.
        Assert.Equal(1 + bound.Count(m => !IsUnder(m.Source, character.Source)),
            Outliner.DeleteSceneObjects(model.Scene, [character]));

        Assert.Equal(characters - 1, model.Scene.Characters.Count());
        Assert.DoesNotContain(model.Scene.Meshes, m => gone.Contains(m.Source));
        Assert.DoesNotContain(model.Document.AllObjects(), gone.Contains);
        Assert.DoesNotContain(ReferenceTargets(model), gone.Contains);
        // The current character falls back to a remaining one.
        Assert.Equal(model.Scene.Characters.Any(), model.Scene.CurrentCharacter != null);
    }

    /// <summary>
    /// The viewer's undo of a whole-object delete restores a <see cref="EditSnapshot.TakeScene"/>
    /// snapshot with no owner, which has to bring back even the last character, byte for byte,
    /// and redo has to take it away again.
    /// </summary>
    [SceneTheory, SceneSamples]
    public void DeleteSceneObjects_OfEveryCharacterIsUndoneByTheSceneSnapshot(string path)
    {
        var model = TestFiles.LoadScene(path);
        var characters = model.Scene.Characters.ToList();
        if (characters.Count == 0) return;
        byte[] original = AktWriter.Write(model.Document);

        var before = EditSnapshot.TakeScene(null, model.Scene);
        Outliner.DeleteSceneObjects(model.Scene, characters);
        var after = EditSnapshot.TakeScene(null, model.Scene);
        Assert.Empty(model.Scene.Characters);
        byte[] deleted = AktWriter.Write(model.Document);

        before.Restore();
        Assert.Equal(characters.Count, model.Scene.Characters.Count());
        Assert.NotNull(model.Scene.CurrentCharacter);
        Assert.Equal(original, AktWriter.Write(model.Document));

        after.Restore();
        Assert.Empty(model.Scene.Characters);
        Assert.Equal(deleted, AktWriter.Write(model.Document));
    }

    private static bool IsUnder(AktObject node, AktObject ancestor)
    {
        for (var p = node.Parent; p != null; p = p.Parent)
            if (ReferenceEquals(p, ancestor)) return true;
        return false;
    }

    /// <summary>Every object a reference anywhere in the document points at.</summary>
    private static List<AktObject> ReferenceTargets(SceneModel model)
    {
        var targets = new List<AktObject>();
        void Walk(object? value)
        {
            switch (value)
            {
                case AktReference { Target: { } t }: targets.Add(t); break;
                case List<AktReference> refs: foreach (var r in refs) Walk(r); break;
                case AktStruct s: foreach (var f in s.Fields) Walk(f.Value); break;
                case List<object?> list: foreach (var v in list) Walk(v); break;
                case object?[] tuple: foreach (var v in tuple) Walk(v); break;
                case AktMap map: foreach (var e in map.Entries) { Walk(e.Key); Walk(e.Value); } break;
            }
        }
        foreach (var node in model.Document.AllObjects())
            foreach (var f in node.Fields) Walk(f.Value);
        return targets;
    }

    [Fact]
    public void DeleteTreeObjects_TakesACamerasWholeRig()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var session = new PoseEditSession(new ScenePose(model), character);
        var camera = session.AddAnimatedCamera("Rig Camera");
        var target = Assert.IsType<CameraTarget>(camera.Target);
        var hide = Assert.IsType<Joint>(camera.HideJoint);
        var animation = session.CreateAnimation();
        var stack = Assert.Single(animation.Stacks);
        var editor = new RigEditor(character);
        int controllers = character.Controllers.Count;

        var result = Outliner.DeleteTreeObjects(editor, [camera]);

        // One selected row, three deleted objects: the delete takes the constrained target with
        // the camera and the camera's child hidden joint with it.
        Assert.True(result.Ok, result.Refusal);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(controllers - 3, character.Controllers.Count);
        Assert.DoesNotContain(target, character.Controllers);
        Assert.DoesNotContain(hide, character.Controllers);
        Assert.DoesNotContain(hide, character.Joints);
        Assert.Empty(stack.Tracks);
    }

    [Fact]
    public void DeleteTreeObjects_RefusesACameraTargetOnItsOwn()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var session = new PoseEditSession(new ScenePose(model), character);
        var camera = session.AddAnimatedCamera("Rig Camera");
        var target = Assert.IsType<CameraTarget>(camera.Target);
        var editor = new RigEditor(character);
        int controllers = character.Controllers.Count;

        // CCameraTarget::CanDelete: the chain root (the camera) is not in the selection.
        var plan = Outliner.PlanTreeDelete(editor, [target]);
        Assert.False(plan.Ok);
        Assert.Contains("delete the camera", plan.Refusal!);
        Assert.Contains(camera.Name, plan.Refusal!);
        var result = Outliner.DeleteTreeObjects(editor, [target]);
        Assert.False(result.Ok);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(controllers, character.Controllers.Count);
        Assert.Contains(target, character.Controllers);
    }

    [Fact]
    public void DeleteTreeObjects_RefusesAFixedSceneCamera()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        var editor = new RigEditor(character);
        // Only an animated camera is a controller row; the scene's eight fixed cameras are not
        // delete targets (the walk refuses them and a fixed camera's class is not deletable).
        var plan = Outliner.PlanTreeDelete(editor, [model.Scene.Cameras.First()]);
        Assert.False(plan.Ok);
        Assert.Contains("no joint or controller", plan.Refusal!);
    }

    [Fact]
    public void OutlinerMenu_OffersTheExpectedRowsInOrder()
    {
        var state = new MenuState { HasScene = true, HasTreeRow = true };
        var rows = MenuModel.OutlinerItems(state);
        Assert.Equal(
            new[]
            {
                MenuCommand.RenameTreeObject, MenuCommand.RenameTool,
                MenuCommand.ShowHierarchy, MenuCommand.HideHierarchy,
                MenuCommand.SelectHierarchy,
                MenuCommand.ExpandHierarchy, MenuCommand.CollapseHierarchy,
                MenuCommand.DeleteTreeObject,
            },
            rows.Where(r => r.Kind == MenuEntryKind.Item).Select(r => r.Command!.Value));
        Assert.All(rows.Where(r => r.Kind == MenuEntryKind.Item), r => Assert.True(r.Enabled));

        // On the panel's blank space the menu acts on the whole tree instead.
        var empty = MenuModel.OutlinerItems(new MenuState { HasScene = true });
        Assert.Equal(
            new[]
            {
                MenuCommand.ShowAllObjects, MenuCommand.HideAllObjects, MenuCommand.SelectAllTreeRows,
                MenuCommand.ExpandAllTreeRows, MenuCommand.CollapseAllTreeRows,
            },
            empty.Where(r => r.Kind == MenuEntryKind.Item).Select(r => r.Command!.Value));
        Assert.All(empty.Where(r => r.Kind == MenuEntryKind.Item), r => Assert.True(r.Enabled));
        Assert.Equal(2, empty.Count(r => r.Kind == MenuEntryKind.Separator));

        // Every command the panel's menu names is a command the menu bar's walk knows about.
        Assert.All(MenuModel.Commands(state), command => Assert.True(Enum.IsDefined(command)));
        Assert.Contains(MenuCommand.DeleteTreeObject, MenuModel.Commands(state));
    }

    [Fact]
    public void OutlinerMenu_OffersTheSkinLockOnlyOnASkinnedMesh()
    {
        Assert.DoesNotContain(MenuModel.OutlinerItems(new MenuState { HasScene = true, HasTreeRow = true }),
            r => r.Command == MenuCommand.ToggleSkinLock);
        var unlocked = MenuModel.OutlinerItems(new MenuState { HasScene = true, HasTreeRow = true, TreeRowSkinLocked = false });
        var row = Assert.Single(unlocked, r => r.Command == MenuCommand.ToggleSkinLock);
        Assert.Equal("_Lock skin", row.Header);
        Assert.Equal(MenuEntryKind.Separator, unlocked[^2].Kind);
        var locked = MenuModel.OutlinerItems(new MenuState { HasScene = true, HasTreeRow = true, TreeRowSkinLocked = true });
        Assert.Equal("_Unlock skin", Assert.Single(locked, r => r.Command == MenuCommand.ToggleSkinLock).Header);
    }
}

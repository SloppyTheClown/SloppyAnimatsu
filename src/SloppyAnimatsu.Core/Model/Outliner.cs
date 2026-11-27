using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The top-level interface context
/// the value stored in <c>CScene.InterfaceContextIndex</c>.
/// </summary>
public enum InterfaceContext : byte
{
    Rigging = 0,
    Animation = 1,
}

/// <summary>How a viewport click combines with the selection: plain, Ctrl, add-only, or Alt.</summary>
public enum PickCombine
{
    /// <summary>No modifier: the click's objects become the selection.</summary>
    Replace,

    /// <summary>Ctrl: added, or removed when they were already selected.</summary>
    Toggle,

    /// <summary>Added, never removed.</summary>
    Add,

    /// <summary>Alt: removed from the selection.</summary>
    Remove,
}

/// <summary>One row of the hierarchy panel: a 3D object and its indentation depth.</summary>
public readonly record struct OutlinerRow(Object3D Object, int Depth);

/// <summary>
/// The outcome of the Delete tree object: how many joints and controllers went, or the
/// sentence that says why nothing did. A refusal deletes nothing, like the, which checks the
/// whole selection before removing anything.
/// </summary>
public readonly record struct TreeDeleteResult(int Deleted, string? Refusal)
{
    public bool Ok => Refusal == null;

    /// <summary>The sentence the log gets: the count, or the refusal.</summary>
    public string Message => Refusal ?? $"{Deleted} object(s) deleted";
}

/// <summary>
/// What a Delete tree object removes: every selected joint with its joint subtree, the controller
/// that constrains one of those joints (the selection gathers it - so a chain
/// joint takes its IK or reverse foot with it), and any selected controller with its subtree. An
/// animated camera is a controller row too (it is in <c>Controllers</c>): its rig - the hidden joint
/// and the target - goes with it.
/// </summary>
public readonly record struct TreeDeletePlan(
    IReadOnlyList<Joint> Joints, IReadOnlyList<Object3D> Controllers, string? Refusal)
{
    public bool Ok => Refusal == null;

    /// <summary>Joints plus controllers; the shoe locators go with their controller.</summary>
    public int Count => Joints.Count + Controllers.Count;
}

/// <summary>
/// The hierarchy panel and its Show/Hide and Lock commands.
/// These are plain edits of the stored <c>Flags</c> and <c>SkinnedMeshIsLocked</c> values; recording
/// them for undo is the caller's job (the viewer uses <c>PoseEditSession</c>).
/// </summary>
public static class Outliner
{
    /// <summary><c>Flags</c> bit 6: hidden in the hierarchy panel while it has no children.</summary>
    public const uint HiddenInTreeFlag = 0x40;

    /// <summary>
    /// The panel's rows in file pre-order. Folders (<c>CDummy</c>) take no row and are flattened
    /// (their 3D children get the folder's depth); non-3D nodes never appear, and internal helpers
    /// (a <c>__</c> name, or bit 6 while childless) are left out.
    /// </summary>
    public static IReadOnlyList<OutlinerRow> Rows(Scene scene)
    {
        var rows = new List<OutlinerRow>();
        void Walk(SceneObject parent, int depth)
        {
            foreach (var child in parent.Children)
            {
                if (child is Folder)
                {
                    Walk(child, depth);
                    continue;
                }
                if (child is not Object3D o || !IsListed(o)) continue;
                rows.Add(new OutlinerRow(o, depth));
                Walk(o, depth + 1);
            }
        }
        Walk(scene, 0);
        return rows;
    }

    /// <summary>Whether the panel lists <paramref name="o"/> itself (its children are walked regardless).</summary>
    /// The reverse foot's locators are engine pivots: like the Picker, the panel lists only the
    /// reverse foot controller itself (controller-display.md "Shoe locators").
    public static bool IsListed(Object3D o) =>
        o is not ReverseFootLocator && !o.IsInternal && !IsViewCamera(o) && !IsCameraRigJoint(o) && ((o.Flags & HiddenInTreeFlag) == 0 || o.Children.Any());

    /// <summary>
    /// A scene's fixed view cameras (Perspective, Front, Top...) are chosen from the camera label
    /// of a view, not listed as objects; an animated camera is a rig object and stays listed.
    /// </summary>
    public static bool IsViewCamera(Object3D o) => o is Camera && o is not CameraAnimated;

    /// <summary>
    /// An animated camera's <c>CHideJointForLooker</c>: the effector of its orbit rig, kept up by
    /// the camera's keys and never edited by hand, so neither the tree nor the viewport shows it
    /// </summary>
    public static bool IsCameraRigJoint(Object3D o) => o.ClassName == Akt.CameraSchema.HideJointClass;

    /// <summary>The object and every 3D object below it, through folders.</summary>
    public static IEnumerable<Object3D> Subtree(Object3D o) =>
        new[] { o }.Concat(o.Descendants<Object3D>());

    /// <summary>
    /// Hides <paramref name="objects"/>. The Hide clears the bit on the whole subtree
    /// (<paramref name="walkSubtree"/>, kept for parity tests); SloppyAnimatsu hides only the given objects
    /// and the viewport hides what is under a hidden ancestor (<see cref="Object3D.IsShownInScene"/>),
    /// so a child's own state survives a hide/show of its parent. Returns how many objects changed.
    /// </summary>
    public static int Hide(IEnumerable<Object3D> objects, bool walkSubtree = false) =>
        SetShown(walkSubtree ? objects.SelectMany(Subtree) : objects, false);

    /// <summary>
    /// Shows the given objects only; like the Show it does not walk the subtree (hide's
    /// descendants keep their own bit). Returns how many objects changed.
    /// </summary>
    public static int Show(IEnumerable<Object3D> objects) => SetShown(objects, true);

    /// <summary>Flips each object's own shown state; returns how many were flipped.</summary>
    public static int ToggleShown(IEnumerable<Object3D> objects)
    {
        int changed = 0;
        foreach (var o in Distinct(objects))
        {
            o.IsShown = !o.IsShown;
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Sets or clears each object's own unpickable bit (the Set as unpickable / Set as pickable;
    /// the object stays drawn but the viewport cannot click or select it). Returns how many changed.
    /// </summary>
    public static int SetPickable(IEnumerable<Object3D> objects, bool pickable)
    {
        int changed = 0;
        foreach (var o in Distinct(objects))
        {
            if (o.IsUnpickable == !pickable) continue;
            o.IsUnpickable = !pickable;
            changed++;
        }
        return changed;
    }

    /// <summary>Shows every 3D object of the scene (the Show All).</summary>
    public static int ShowAll(Scene scene) => SetShown(scene.Descendants<Object3D>(), true);

    /// <summary>Hides every 3D object of the scene (the Hide All).</summary>
    public static int HideAll(Scene scene) => SetShown(scene.Descendants<Object3D>(), false);

    /// <summary>
    /// Sets <c>SkinnedMeshIsLocked</c> on the given skinned meshes (the Lock/Unlock
    /// Skinned Meshes only touches meshes whose <c>Skinned</c> is set). Returns how many changed.
    /// </summary>
    public static int SetMeshesLocked(IEnumerable<Mesh> meshes, bool locked)
    {
        int changed = 0;
        foreach (var mesh in Distinct(meshes))
        {
            if (!mesh.Skinned || mesh.SkinnedMeshIsLocked == locked) continue;
            mesh.SkinnedMeshIsLocked = locked;
            changed++;
        }
        return changed;
    }

    /// <summary>Locks or unlocks every skinned mesh of <paramref name="character"/>.</summary>
    public static int SetCharacterMeshesLocked(Character character, bool locked) =>
        SetMeshesLocked(character.Meshes, locked);

    /// <summary>
    /// Whether the viewport may pick <paramref name="o"/>: it is shown, and neither it nor an
    /// ancestor is unpickable (bit 9; on a character it covers all its descendants). A camera's
    /// hidden joint is never picked: SloppyAnimatsu does not show it.
    /// </summary>
    public static bool IsPickable(Object3D o)
    {
        if (IsCameraRigJoint(o)) return false;
        for (Object3D? p = o; p != null; p = p.Parent3D)
            if (!p.IsShown || p.IsUnpickable) return false;
        return true;
    }

    // ---- the panel's three tree commands --------------------------------------------------------
    // The Select hierarchy, Toggle hierarchy selection and Delete tree object commands.
    // "Hierarchy" is the clicked object and every 3D object
    // below it, folders included (every CObject3D child is collected recursively);
    // "selected" is the viewport selection for the first two and the tree's own selection for the
    // delete.

    /// <summary>
    /// The <b>Select hierarchy</b>: adds <paramref name="root"/> and every 3D object below it
    /// to <paramref name="selection"/> and returns how many were added. Re-select is idempotent
    /// (the app replaces the selection marker, which only changes the selection's order).
    /// <paramref name="filter"/> narrows what joins the selection - the viewer passes the current
    /// character's controllers, because a mesh or a folder cannot be selected there.
    /// </summary>
    public static int SelectHierarchy(ICollection<Object3D> selection, Object3D root, Func<Object3D, bool>? filter = null)
    {
        int added = 0;
        foreach (var o in Subtree(root))
        {
            if (filter != null && !filter(o)) continue;
            if (!selection.Contains(o))
            {
                selection.Add(o);
                added++;
            }
        }
        return added;
    }

    /// <summary>
    /// The <b>Toggle hierarchy selection</b>: when <paramref name="root"/> is selected the
    /// whole hierarchy leaves the selection (the return is minus what was removed), otherwise the
    /// whole hierarchy joins it (the return is what was added). The app decides on the clicked
    /// object alone - it looks for the clicked object's own selection marker, not for a descendant's.
    /// </summary>
    public static int ToggleHierarchySelection(ICollection<Object3D> selection, Object3D root, Func<Object3D, bool>? filter = null)
    {
        if (!selection.Contains(root)) return SelectHierarchy(selection, root, filter);
        int removed = 0;
        foreach (var o in Subtree(root))
        {
            if (filter != null && !filter(o)) continue;
            if (selection.Remove(o)) removed++;
        }
        return -removed;
    }

    /// <summary>
    /// A viewport hierarchy click: a right click takes <paramref name="root"/> and everything below
    /// it (<paramref name="includeRoot"/> true), a middle click only what is below it. With no
    /// modifier the result replaces the selection; Ctrl (<see cref="PickCombine.Toggle"/>) adds the
    /// members, or removes them when they were all selected already; <see cref="PickCombine.Add"/>
    /// only adds; Alt (<see cref="PickCombine.Remove"/>) removes them. The member the gizmo should sit
    /// on - the root for a right click, the first object below it for a middle click - is moved to the
    /// end of the selection, where the viewer reads its current object. Returns the members picked
    /// (after <paramref name="filter"/>), in tree order.
    /// </summary>
    public static IReadOnlyList<Object3D> PickHierarchy(IList<Object3D> selection, Object3D root, bool includeRoot,
        PickCombine combine, Func<Object3D, bool>? filter = null)
    {
        var members = Subtree(root)
            .Where(o => (includeRoot || !ReferenceEquals(o, root)) && (filter == null || filter(o)))
            .ToList();
        if (members.Count == 0) return members;
        bool remove = combine == PickCombine.Remove
            || (combine == PickCombine.Toggle && members.All(selection.Contains));
        if (remove)
        {
            foreach (var o in members) selection.Remove(o);
            return members;
        }
        if (combine == PickCombine.Replace) selection.Clear();
        foreach (var o in members)
            if (!selection.Contains(o)) selection.Add(o);
        var lead = members[0];
        selection.Remove(lead);
        selection.Add(lead);
        return members;
    }

    /// <summary>
    /// The joints the delete collects from the tree selection: a selected joint brings its whole
    /// joint subtree, and a selected object that is not a joint brings nothing and hides what is under
    /// it (the walk stops descending at a selected object and keeps only joints).
    /// </summary>
    public static IReadOnlyList<Joint> TreeJoints(IEnumerable<Object3D> treeSelection)
    {
        var joints = new List<Joint>();
        var seen = new HashSet<AktObject>(ReferenceEqualityComparer.Instance);
        foreach (var o in treeSelection.DistinctBy(o => (object)o.Source, ReferenceEqualityComparer.Instance))
        {
            if (o is not Joint) continue;
            foreach (var joint in Subtree(o).OfType<Joint>())
                if (seen.Add(joint.Source)) joints.Add(joint);
        }
        return joints;
    }

    /// <summary>
    /// The <b>Delete tree object</b>: removes the joints of the tree selection, with their
    /// subtrees, tracks and weight remapping (<see cref="RigEditor.DeleteJoints"/>), together with the
    /// controller a constrained joint carries (<see cref="RigEditor.DeleteControllers"/>) and any
    /// controller the selection names directly. Nothing is deleted when a locked skinned mesh hangs
    /// under one of the joints - the one refusal.
    /// </summary>
    public static TreeDeleteResult DeleteTreeObjects(RigEditor editor, IEnumerable<Object3D> treeSelection)
    {
        var plan = PlanTreeDelete(editor, treeSelection);
        if (!plan.Ok) return new TreeDeleteResult(0, plan.Refusal);
        int deleted = editor.DeleteControllers(plan.Controllers) + editor.DeleteJoints(plan.Joints);
        return new TreeDeleteResult(deleted, null);
    }

    /// <summary>
    /// What <see cref="DeleteTreeObjects"/> would delete, or why it would delete nothing. Split out so
    /// a caller can refuse without taking an undo step for an edit that did nothing. A selected
    /// controller is taken with its subtree; a selected joint brings its joint subtree and, when a
    /// controller constrains one of them, that controller with its shoe locators.
    /// </summary>
    public static TreeDeletePlan PlanTreeDelete(RigEditor editor, IEnumerable<Object3D> treeSelection)
    {
        var selection = treeSelection
            .DistinctBy(o => (object)o.Source, ReferenceEqualityComparer.Instance).ToList();
        var joints = TreeJoints(selection);
        var controllers = new List<Object3D>();
        void AddController(Object3D? controller)
        {
            if (controller != null && !controllers.Contains(controller)) controllers.Add(controller);
        }
        // A controller row and an animated camera row are both in Character.Controllers; a mesh, a
        // joint or one of the scene's fixed cameras is not a delete target of its own (the
        // walk refuses it, and a fixed camera's class is not deletable).
        foreach (var o in selection)
            if (o is Controller3D || o is CameraAnimated) AddController(o);
        foreach (var joint in joints) AddController(joint.Constraint);
        if (joints.Count == 0 && controllers.Count == 0)
            return new TreeDeletePlan(joints, controllers, "The tree selection holds no joint or controller to delete.");
        // The rule: the target's chain root (its camera) must be deleted
        // with it, so a target row on its own is refused.
        foreach (var target in controllers.OfType<CameraTarget>())
            if (target.Camera is { } camera &&
                !selection.Any(o => ReferenceEquals(o.Source, camera.Source)))
                return new TreeDeletePlan(joints, controllers,
                    $"'{target.Name}' is the target of camera '{camera.Name}'; delete the camera to remove its rig.");
        if (BlockedByLockedMesh(editor.Character, joints) is { } blocked)
            return new TreeDeletePlan(joints, controllers,
                $"'{blocked.Joint.Name}' has a locked skinned mesh under it ({string.Join(", ", blocked.Meshes)}); unlock it in the tree first.");
        return new TreeDeletePlan(joints, controllers, null);
    }

    /// <summary>
    /// The tree selection's whole objects, which Delete removes with everything under them
    /// (<see cref="DeleteSceneObjects"/>): a mesh or a character wherever it sits, and any other row
    /// that is neither part of a character's rig (a joint, a controller, an animated camera and its
    /// locators) nor one of the scene's fixed cameras. A selected object under another selected one
    /// is left to its ancestor. A SloppyAnimatsu choice.
    /// </summary>
    public static IReadOnlyList<Object3D> TreeSceneObjects(IEnumerable<Object3D> treeSelection)
    {
        var picked = treeSelection
            .DistinctBy(o => (object)o.Source, ReferenceEqualityComparer.Instance)
            .Where(IsWholeObject).ToList();
        var sources = picked.Select(o => o.Source).ToHashSet(ReferenceEqualityComparer.Instance);
        return picked.Where(o => !Ancestors(o.Source).Any(sources.Contains)).ToList();
    }

    private static bool IsWholeObject(Object3D o) => o switch
    {
        Mesh or Character => true,
        Joint or Controller3D or Camera or ReverseFootLocator => false,
        _ => !Ancestors(o.Source).Any(a => o.Model.Wrap(a) is Character),
    };

    private static IEnumerable<AktObject> Ancestors(AktObject node)
    {
        for (var p = node.Parent; p != null; p = p.Parent) yield return p;
    }

    /// <summary>
    /// Duplicate hierarchy for whole objects (<see cref="TreeSceneObjects"/>): copies each object
    /// with everything under it and puts the copy right after the original under the same parent,
    /// under a name no other object of the scene has. A character's copy takes its rig, poses, Anim
    /// Bank and rig animation (they live under it) and copies of the meshes bound to it, which are
    /// rebound to the copied rig. A track elsewhere that animates something inside a copied object
    /// (another character's animation of a loose mesh, say) gets a twin that animates the copy.
    /// Returns the copies of <paramref name="objects"/>, in order. A SloppyAnimatsu choice.
    /// </summary>
    public static IReadOnlyList<Object3D> DuplicateSceneObjects(Scene scene, IReadOnlyList<Object3D> objects)
    {
        var model = scene.Model;
        var picked = TreeSceneObjects(objects);
        if (picked.Count == 0) throw new InvalidOperationException("Select the objects to duplicate.");
        foreach (var o in picked)
            if (o.Source.Parent == null) throw new InvalidOperationException($"'{o.Name}' has no parent to copy it under.");
        var roots = picked.ToList();
        foreach (var character in picked.OfType<Character>())
            foreach (var mesh in character.Meshes)
                if (!roots.Any(r => ReferenceEquals(r.Source, mesh.Source))) roots.Add(mesh);
        var inside = new HashSet<AktObject>(ReferenceEqualityComparer.Instance);
        foreach (var o in roots)
            foreach (var node in o.Source.PreOrder()) inside.Add(node);
        // Keep only the top objects: a bound mesh may sit under its character already.
        roots = roots.Where(o => !Ancestors(o.Source).Any(inside.Contains)).ToList();
        var existingCharacters = scene.Characters.ToList();

        var copies = AktObject.CloneHierarchies(roots.Select(o => o.Source).ToList(), out var map);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in model.Document.AllObjects())
            if (node.Name is { } name) names.Add(name);
        for (int i = 0; i < roots.Count; i++)
        {
            var original = roots[i].Source;
            var parent = original.Parent!;
            string name = UniqueSceneName(names, original.Name ?? "Object");
            names.Add(name);
            copies[i].Set("Name", name);
            parent.InsertChild(parent.Children.IndexOf(original) + 1, copies[i]);
        }

        foreach (var character in existingCharacters)
        {
            var animations = character.Animations.ToList();
            if (character.RigAnimation is { } rig) animations.Add(rig);
            foreach (var animation in animations.Distinct())
                foreach (var stack in animation.Stacks)
                    foreach (var track in stack.Tracks.ToList())
                    {
                        // A track inside a copied character was copied with it.
                        if (inside.Contains(track.Source) || track.Target is not { } target || !map.ContainsKey(target.Source)) continue;
                        var twin = track.Source.CloneHierarchy();
                        foreach (var node in twin.PreOrder())
                            for (int f = 0; f < node.FieldCount; f++) AktObject.Remap(node.ValueAt(f), map);
                        stack.Source.InsertChild(stack.Source.Children.IndexOf(track.Source) + 1, twin);
                    }
        }
        return picked.Select(o => (Object3D)model.Wrap(map[o.Source])).ToList();
    }

    /// <summary><paramref name="requested"/> when free, else its stem with the next free "_NN" index (Mesh, Mesh_02, ...).</summary>
    private static string UniqueSceneName(HashSet<string> names, string requested)
    {
        if (!names.Contains(requested)) return requested;
        string stem = System.Text.RegularExpressions.Regex.Replace(requested, "_[0-9]+$", "");
        for (int n = 2; ; n++)
        {
            string candidate = stem + "_" + n.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            if (!names.Contains(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Deletes whole objects with their subtrees. A character takes its rig, its poses, its Anim
    /// Bank and rig animation (they live under it) and the meshes bound to it; a deleted object's
    /// tracks leave every surviving character's animations, a surviving character's joints or
    /// controllers that hung under a deleted object go through <see cref="RigEditor"/>, deleted
    /// cameras leave <c>CameraList</c>, and every other reference into the deleted set is dropped
    /// (a single reference becomes null, a reference list loses the entry). When the current
    /// character goes, the first remaining character becomes current. Returns the number of
    /// objects removed from the tree (the selected ones plus a character's bound meshes).
    /// </summary>
    public static int DeleteSceneObjects(Scene scene, IReadOnlyList<Object3D> objects)
    {
        if (objects.Count == 0) return 0;
        var model = scene.Model;
        var roots = objects.DistinctBy(o => (object)o.Source, ReferenceEqualityComparer.Instance).ToList();
        foreach (var character in roots.OfType<Character>().ToList())
            foreach (var mesh in character.Meshes.ToList())
                if (!roots.Any(r => ReferenceEquals(r.Source, mesh.Source))) roots.Add(mesh);
        var gone = new HashSet<AktObject>(ReferenceEqualityComparer.Instance);
        foreach (var o in roots)
            foreach (var node in o.Source.PreOrder()) gone.Add(node);
        // Keep only the top objects: a bound mesh may already sit under a deleted character.
        roots = roots.Where(o => !Ancestors(o.Source).Any(gone.Contains)).ToList();

        var survivors = scene.Characters.Where(c => !gone.Contains(c.Source)).ToList();
        foreach (var character in survivors)
        {
            var editor = new RigEditor(character);
            var controllers = character.Controllers.Where(c => c is not Joint && gone.Contains(c.Source)).ToList();
            if (controllers.Count > 0) editor.DeleteControllers(controllers);
            var joints = character.Joints.Where(j => gone.Contains(j.Source)).ToList();
            if (joints.Count > 0) editor.DeleteJoints(joints);
            var animations = character.Animations.ToList();
            if (character.RigAnimation is { } rig) animations.Add(rig);
            foreach (var animation in animations)
                foreach (var stack in animation.Stacks)
                    foreach (var track in stack.Tracks.ToList())
                        if (track.Target is { } target && gone.Contains(target.Source))
                            stack.Source.RemoveChild(track.Source);
        }
        foreach (var node in gone)
            if (model.Wrap(node) is Camera camera) scene.RemoveCamera(camera);

        foreach (var o in roots) o.Source.Parent?.RemoveChild(o.Source);
        foreach (var node in model.Document.AllObjects())
            for (int fi = 0; fi < node.FieldCount; fi++)
            {
                var value = node.ValueAt(fi);
                var dropped = DropReferences(value, gone);
                if (!ReferenceEquals(value, dropped)) node.SetValueAt(fi, dropped);
            }

        if (scene.CurrentCharacter == null)
            scene.CurrentCharacter = scene.Characters.FirstOrDefault();
        return roots.Count;
    }

    /// <summary>
    /// Nulls a reference into <paramref name="gone"/> and takes such entries out of reference lists.
    /// Copy-on-write: a value holding such a reference is replaced by a changed copy and the original
    /// is left untouched, because the undo snapshot of a scene or rig edit shares the field values of
    /// animation tracks with the document instead of copying them (EditSnapshot.TakeRig/TakeScene).
    /// Returns <paramref name="value"/> itself when nothing in it pointed into <paramref name="gone"/>.
    /// </summary>
    private static object? DropReferences(object? value, HashSet<AktObject> gone)
    {
        switch (value)
        {
            case AktReference { Target: { } target } when gone.Contains(target):
                return new AktReference(null);
            case List<AktReference> refs:
                return refs.Any(r => r.Target is { } t && gone.Contains(t))
                    ? refs.Where(r => !(r.Target is { } t && gone.Contains(t))).ToList()
                    : refs;
            case AktStruct s:
            {
                AktStruct? copy = null;
                for (int fi = 0; fi < s.FieldCount; fi++)
                {
                    var v = s.ValueAt(fi);
                    var nv = DropReferences(v, gone);
                    if (ReferenceEquals(v, nv)) continue;
                    if (copy == null)
                    {
                        var (layout, values) = s.CopyFields(deep: false, shareLargeArrays: true);
                        copy = new AktStruct(s.TypeName, layout, values);
                    }
                    copy.SetValueAt(fi, nv);
                }
                return copy ?? s;
            }
            case AktMrsKeyColumn { IsPacked: true } keys:
            {
                if (!keys.AnyReference(t => t != null && gone.Contains(t))) return keys;
                var copy = (AktMrsKeyColumn)keys.CloneList(shareLargeArrays: false);
                copy.MapReferences((t, raw) => t != null && gone.Contains(t) ? (null, -1) : (t, raw));
                return copy;
            }
            case AktPackedList { IsPacked: true } packed:
                return packed; // tangent modes: no references
            case IList<object?> list and not object?[]:
            {
                List<object?>? copy = null;
                for (int i = 0; i < list.Count; i++)
                {
                    var nv = DropReferences(list[i], gone);
                    if (ReferenceEquals(list[i], nv)) continue;
                    copy ??= new List<object?>(list);
                    copy[i] = nv;
                }
                return copy ?? list;
            }
            case object?[] tuple:
            {
                object?[]? copy = null;
                for (int i = 0; i < tuple.Length; i++)
                {
                    var nv = DropReferences(tuple[i], gone);
                    if (ReferenceEquals(tuple[i], nv)) continue;
                    copy ??= (object?[])tuple.Clone();
                    copy[i] = nv;
                }
                return copy ?? tuple;
            }
            case AktMap map:
            {
                AktMap? copy = null;
                for (int i = 0; i < map.Entries.Count; i++)
                {
                    var e = map.Entries[i];
                    var nk = DropReferences(e.Key, gone);
                    var nv = DropReferences(e.Value, gone);
                    if (ReferenceEquals(nk, e.Key) && ReferenceEquals(nv, e.Value)) continue;
                    if (copy == null)
                    {
                        copy = new AktMap();
                        foreach (var x in map.Entries) copy.Add(x.Key, x.Value);
                    }
                    copy.Entries[i] = new AktMapEntry(nk, nv);
                }
                return copy ?? map;
            }
            default:
                return value;
        }
    }

    /// <summary>
    /// The first joint of <paramref name="joints"/> that carries a locked skinned mesh: a mesh bound
    /// to the character whose tree ancestor is in the joint's subtree, or whose skin weights name one
    /// of those joints. A tree test alone
    /// would miss a mesh bound from outside the joint (the usual shape here).
    /// </summary>
    private static (Joint Joint, List<string> Meshes)? BlockedByLockedMesh(Character character, IReadOnlyList<Joint> joints)
    {
        var locked = character.Model.Scene.Meshes
            .Where(m => m.Skinned && m.SkinnedMeshIsLocked && m.BindPose?.Character == character)
            .ToList();
        if (locked.Count == 0) return null;
        var ordinal = new Dictionary<Joint, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < character.Joints.Count; i++) ordinal[character.Joints[i]] = i;
        // Each collected joint's bones, as character joint indices.
        var bones = new List<HashSet<int>>(joints.Count);
        foreach (var joint in joints)
        {
            var set = new HashSet<int>();
            foreach (var j in Subtree(joint).OfType<Joint>())
                if (ordinal.TryGetValue(j, out int index)) set.Add(index);
            bones.Add(set);
        }
        foreach (var mesh in locked)
        {
            var used = mesh.SkinWeights
                .SelectMany(vertex => vertex.Where(w => w.Weight > 0).Select(w => w.Bone))
                .ToHashSet();
            var owner = mesh.Ancestor<Joint>();
            for (int i = 0; i < joints.Count; i++)
            {
                bool under = owner != null && ordinal.TryGetValue(owner, out int at) && bones[i].Contains(at);
                if (under || used.Overlaps(bones[i])) return (joints[i], [mesh.Name ?? "(unnamed)"]);
            }
        }
        return null;
    }

    private static int SetShown(IEnumerable<Object3D> objects, bool shown)
    {
        int changed = 0;
        foreach (var o in Distinct(objects))
        {
            if (o.IsShown == shown) continue;
            o.IsShown = shown;
            changed++;
        }
        return changed;
    }

    private static IEnumerable<T> Distinct<T>(IEnumerable<T> items) where T : SceneObject =>
        items.DistinctBy(o => (object)o.Source, ReferenceEqualityComparer.Instance);
}

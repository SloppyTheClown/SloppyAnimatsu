using System.Numerics;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// What dropping a hierarchy row on another row would do
/// The drop is the reparent action, so the
/// panel asks this before it invites the drop and the window turns the verdict into a message;
/// <see cref="RigEditor.Reparent"/> enforces the same rules again when the edit runs.
/// </summary>
public enum OutlinerDropVerdict
{
    /// <summary>The target accepts the row: the object is reparented under it.</summary>
    Move,

    /// <summary>The row was dropped on itself; nothing happens.</summary>
    Self,

    /// <summary>A character stays at the scene root.</summary>
    Character,

    /// <summary>A row may only be dropped on a row of the same character.</summary>
    OtherCharacter,

    /// <summary>The target is below the dragged object, so the drop would make a cycle.</summary>
    Cycle,

    /// <summary>A constrained object, or one carrying <c>NoReparentFlag</c>, cannot be reparented.</summary>
    Constrained,

    /// <summary>A skinned mesh locked to its skin cannot leave its character (it would have to be unbound).</summary>
    LockedMesh,

    /// <summary>Only a mesh can be dropped at the scene root, out of every character.</summary>
    SceneRoot,
}


/// <summary>Where on a row a drop lands: above it, onto it (as its child) or below it.</summary>
public enum OutlinerDropPlace
{
    /// <summary>The upper band: the row is put just above the target, under the target's parent.</summary>
    Before,

    /// <summary>The middle: the row becomes the target's last child.</summary>
    Inside,

    /// <summary>The lower band: the row is put just below the target, under the target's parent.</summary>
    After,
}
/// <summary>
/// The hierarchy panel's row drag as a rule that is tested without a window
/// a press on a row's name stays a click until the pointer
/// has moved <see cref="Threshold"/> DIP, and a drop is refused on the row itself, on a character
/// row, across characters, on a descendant and for constrained objects. The viewer's
/// <c>OutlinerView</c> asks it for the drag threshold and the drop cursor, and <c>MainWindow</c>
/// turns the verdict into its status message. This is a viewer design, not the tree control.
/// </summary>
public static class OutlinerDrag
{
    /// <summary>
    /// The pointer distance, in device-independent pixels, that a press on a row's name must travel
    /// before it becomes a drag; below it the press stays the click that selects the row.
    /// </summary>
    public const double Threshold = 6;

    /// <summary>
    /// Whether a press that started at <paramref name="press"/> and is now at
    /// <paramref name="at"/> has left the click.
    /// </summary>
    public static bool StartsDrag(Vector2 press, Vector2 at) =>
        (at - press).LengthSquared() >= Threshold * Threshold;

    /// <summary>What dropping <paramref name="dragged"/> on <paramref name="target"/> would do.</summary>
    public static OutlinerDropVerdict Verdict(Object3D dragged, Object3D target)
    {
        if (ReferenceEquals(dragged, target)) return OutlinerDropVerdict.Self;
        // A character is the scene's own root: it has no hierarchy row to be given another parent.
        if (dragged is Character) return OutlinerDropVerdict.Character;
        // A joint moved between characters would leave its controller, pose slot and tracks behind:
        // a drag only rearranges the hierarchy of one character. A mesh has none of those, so it may
        // change character (it is unbound on the way out - SkinEditor.MoveMesh).
        if (!ReferenceEquals(OwnerOf(dragged), OwnerOf(target)))
        {
            if (dragged is not Mesh mesh) return OutlinerDropVerdict.OtherCharacter;
            if (LeavesLocked(mesh)) return OutlinerDropVerdict.LockedMesh;
        }
        if (IsDescendantOf(target, dragged)) return OutlinerDropVerdict.Cycle;
        if (dragged.Constraint != null || (dragged.Flags & RigEditor.NoReparentFlag) != 0)
            return OutlinerDropVerdict.Constrained;
        return OutlinerDropVerdict.Move;
    }

    /// <summary>Whether the target accepts the row: the only verdict that reparents.</summary>
    public static bool AllowsDrop(Object3D dragged, Object3D target) =>
        Verdict(dragged, target) == OutlinerDropVerdict.Move;

    /// <summary>The share of a row's height, at its top and at its bottom, that drops beside it rather than into it.</summary>
    public const double EdgeBand = 0.3;

    /// <summary>Where a drop at <paramref name="y"/> (from the row's top) on a row <paramref name="height"/> high lands.</summary>
    public static OutlinerDropPlace PlaceAt(double y, double height) =>
        y < height * EdgeBand ? OutlinerDropPlace.Before
        : y > height * (1 - EdgeBand) ? OutlinerDropPlace.After
        : OutlinerDropPlace.Inside;

    /// <summary>
    /// The parent a drop at <paramref name="place"/> on <paramref name="target"/> moves the row
    /// under: the target itself for <see cref="OutlinerDropPlace.Inside"/>, else the target's parent.
    /// A character has no parent row, so a drop beside it goes into it.
    /// </summary>
    public static Object3D NewParent(Object3D target, OutlinerDropPlace place) =>
        place == OutlinerDropPlace.Inside || target is Character || target.Parent is not Object3D parent
            ? target : parent;

    /// <summary>
    /// What dropping <paramref name="dragged"/> at <paramref name="place"/> on
    /// <paramref name="target"/> would do. A drop beside a sibling of the dragged row only reorders
    /// it, which even a constrained object may do; any other drop is a reparent under
    /// <see cref="NewParent"/> and follows <see cref="Verdict(Object3D, Object3D)"/>.
    /// </summary>
    public static OutlinerDropVerdict Verdict(Object3D dragged, Object3D target, OutlinerDropPlace place)
    {
        if (ReferenceEquals(dragged, target)) return OutlinerDropVerdict.Self;
        if (DropParent(dragged, target, place) == null) return RootVerdict(dragged);
        var parent = NewParent(target, place);
        if (ReferenceEquals(parent, target)) return Verdict(dragged, target);
        if (dragged is Character) return OutlinerDropVerdict.Character;
        if (ReferenceEquals(dragged.Parent, parent)) return OutlinerDropVerdict.Move;
        return Verdict(dragged, parent);
    }

    /// <summary>Whether a drop at <paramref name="place"/> is accepted.</summary>
    public static bool AllowsDrop(Object3D dragged, Object3D target, OutlinerDropPlace place) =>
        Verdict(dragged, target, place) == OutlinerDropVerdict.Move;

    /// <summary>
    /// Whether <paramref name="o"/>'s row is a top-level row: a character, or an object outside
    /// every character (a free mesh) whose parent is the scene or one of its folders.
    /// </summary>
    public static bool AtSceneRoot(Object3D o) => o is Character || (o.Parent is not Object3D && OwnerOf(o) == null);

    /// <summary>
    /// The parent a drop moves <paramref name="dragged"/> under, or null for the scene root. Only a
    /// mesh dropped beside a top-level row (above or below a character or a free mesh) goes to the
    /// scene root - that is how a mesh is dragged out of its character; every other drop is
    /// <see cref="NewParent"/>.
    /// </summary>
    public static Object3D? DropParent(Object3D dragged, Object3D target, OutlinerDropPlace place) =>
        dragged is Mesh && place != OutlinerDropPlace.Inside && AtSceneRoot(target) ? null : NewParent(target, place);

    /// <summary>
    /// What dropping <paramref name="dragged"/> at the scene root (beside a top-level row, or on the
    /// panel's empty space below the rows) would do: a mesh leaves its character, anything else is
    /// refused.
    /// </summary>
    public static OutlinerDropVerdict RootVerdict(Object3D dragged) => dragged switch
    {
        Character => OutlinerDropVerdict.Character,
        Mesh mesh when LeavesLocked(mesh) => OutlinerDropVerdict.LockedMesh,
        Mesh => OutlinerDropVerdict.Move,
        _ => OutlinerDropVerdict.SceneRoot,
    };

    /// <summary>
    /// Whether moving <paramref name="mesh"/> out of its character is refused: a skinned mesh whose
    /// skin is locked, which Unbind Skin also leaves attached.
    /// </summary>
    public static bool LeavesLocked(Mesh mesh) => mesh.Skinned && mesh.SkinnedMeshIsLocked && OwnerOf(mesh) != null;

    /// <summary>
    /// The character an object belongs to, or null for a scene-level object such as a mesh.
    /// </summary>
    public static Character? OwnerOf(Object3D o) => o as Character ?? o.Ancestor<Character>();

    private static bool IsDescendantOf(Object3D node, Object3D ancestor)
    {
        for (var parent = node.Parent; parent != null; parent = parent.Parent)
            if (ReferenceEquals(parent, ancestor)) return true;
        return false;
    }
}
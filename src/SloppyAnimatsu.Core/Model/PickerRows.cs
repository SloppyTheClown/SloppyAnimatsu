namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The Picker panel's rows: one **flat**
/// row per controller, filtered to the current mixer layer unless the layer-edit ("hammer") mode
/// is on, with the IK-driven bones marked for dimming and the layer's membership for the hammer.
/// The stamps' groups are <see cref="PickerGroups"/>.
/// </summary>
public static class PickerRows
{
    /// <summary>
    /// The controllers the list shows, in <paramref name="controllers"/> order: all of them when
    /// there is no current layer (the rigging context) or when <paramref name="everyController"/>
    /// (the hammer mode), otherwise only the controllers that have a track in
    /// <paramref name="layer"/> - what that layer can key.
    /// </summary>
    public static IReadOnlyList<Object3D> Rows(
        IReadOnlyList<Object3D> controllers, AnimStack? layer, bool everyController)
    {
        if (layer == null || everyController) return controllers;
        var targets = new HashSet<Object3D>(
            layer.Tracks.Select(t => t.Target).OfType<Object3D>(), ReferenceEqualityComparer.Instance);
        return controllers.Where(targets.Contains).ToList();
    }

    /// <summary>
    /// Whether <paramref name="layer"/> has a track for <paramref name="controller"/>: the row's
    /// hammer is lit when it is, and a click adds or removes that track.
    /// </summary>
    public static bool InLayer(AnimStack? layer, Object3D controller) => layer?.TrackFor(controller) != null;

    /// <summary>
    /// The bones an IK or reverse-foot controller solves: every joint of every IK controller's
    /// chain. The controllers themselves are not bones, and a camera's hidden-joint chain
    /// contributes nothing (its members are not <see cref="Joint"/>s).
    /// </summary>
    public static IReadOnlySet<Object3D> IkDriven(Character character) =>
        new HashSet<Object3D>(
            character.Controllers.OfType<ControllerIK>().SelectMany(c => c.Chain).OfType<Joint>(),
            ReferenceEqualityComparer.Instance);
}
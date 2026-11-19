namespace SloppyAnimatsu.Core.Model;

/// <summary>The keys the floating panels are named by, shared by the window and the tests.</summary>
public static class PanelKeys
{
    public const string Bank = "Bank";
    public const string Scene = "Scene";
    public const string Properties = "Properties";
    public const string Material = "Material";
    public const string Transform = "Transform";
    public const string Mixer = "Mixer";
    public const string Picker = "Picker";
    public const string SkinWorkshop = "Skin Workshop";
    public const string Blendshapes = "Blendshapes";
    public const string Stacker = "Stacker";
    public const string Cyclemaker = "Cyclemaker";
    public const string Timeline = "Timeline";
    public const string Painter = "Painter";
    public const string RigWorkshop = "Rig Workshop";
}

/// <summary>
/// The panels SloppyAnimatsu has, in the order they stack, and the .akt dialog
/// classes each stands for
/// The sizes are the fallback a panel gets when the saved layout does not size it; the
/// bank is <c>CCharacterBankDialog</c> in the Rigging context and <c>CAnimBankDialog</c> in the
/// Animation one, which is why both classes are listed.
/// </summary>
public static class PanelCatalogue
{
    private static readonly PanelSpec Bank =
        new(PanelKeys.Bank, "Bank", ["CAnimBankDialog", "CCharacterBankDialog"], 210, 300, PanelAnchor.Left);

    private static readonly PanelSpec Scene =
        new(PanelKeys.Scene, "Scene", ["CHierarchyDialog"], 220, 300, PanelAnchor.Left);

    // Tall enough for all four rows (Name, Visible, Compensate Scale, Target Size Multiplier).
    private static readonly PanelSpec Properties =
        new(PanelKeys.Properties, "Properties", ["CPropertiesDialog"], 210, 170, PanelAnchor.Left);

    // SloppyAnimatsu's own MATERIAL panel. The
    // dialog; the class name is SloppyAnimatsu's own, so its place, size and fold are saved as one more
    // CDialogPosition in SloppyAnimatsu's preferences.akt (FloatingLayout.WriteTo adds the entry).
    public const string MaterialDialogClass = "CMyAkMaterialDialog";

    private static readonly PanelSpec Material =
        new(PanelKeys.Material, "Material", [MaterialDialogClass], 210, 420, PanelAnchor.Left);

    private static readonly PanelSpec Transform =
        new(PanelKeys.Transform, "Transform", ["CTransformDialog"], 250, 600, PanelAnchor.Right);

    // Taller than a minimal panel: the stack rows need room above the toolbar and the flags
    private static readonly PanelSpec Mixer =
        new(PanelKeys.Mixer, "Mixer", ["CMixerDialog"], 210, 240, PanelAnchor.Right);

    private static readonly PanelSpec Picker =
        new(PanelKeys.Picker, "Picker", ["CPickerDialog"], 200, 250, PanelAnchor.Right);

    private static readonly PanelSpec SkinWorkshop =
        new(PanelKeys.SkinWorkshop, "Skin Workshop", ["CSkinWorkshopDialog"], 250, 300, PanelAnchor.Right);

    // The BLENDSHAPES panel is CFaceDialog. A profile
    // may have no entry for it in a context; FloatingLayout.WriteTo adds one, and until then it
    // hangs under the panel the default arrangement puts it below.
    private static readonly PanelSpec Blendshapes =
        new(PanelKeys.Blendshapes, "Blendshapes", ["CFaceDialog"], 250, 340, PanelAnchor.Right);

    // The STACKER toolbar (CStackerDialog) and the CYCLEMAKER's two buttons (CCycleDialog) of the
    // Animation context.
    private static readonly PanelSpec Stacker =
        new(PanelKeys.Stacker, "Stacker", ["CStackerDialog"], 210, 110, PanelAnchor.Right);

    private static readonly PanelSpec Cyclemaker =
        new(PanelKeys.Cyclemaker, "Cyclemaker", ["CCycleDialog"], 210, 70, PanelAnchor.Right);

    // The Rigging context's PAINTER (CPainterDialog, above SKIN ATELIER) and the RIG ATELIER
    // (CSkeletonDialog, above TRANSFORM) - SloppyAnimatsu fills
    // PAINTER with the brush settings and calls the other RIG WORKSHOP (as SKIN ATELIER became
    // the Skin Workshop), filled with the Rig menu's commands. Layouts are saved by dialog class,
    // so the name is free.
    private static readonly PanelSpec Painter =
        new(PanelKeys.Painter, "Painter", ["CPainterDialog"], 250, 150, PanelAnchor.Right);

    private static readonly PanelSpec RigWorkshop =
        new(PanelKeys.RigWorkshop, "Rig Workshop", ["CSkeletonDialog"], 250, 150, PanelAnchor.Right);

    private static readonly PanelSpec Timeline =
        new(PanelKeys.Timeline, "Timeline", ["CTimelineDialog"], 600, 175, PanelAnchor.Bottom);

    /// <summary>
    /// The panels of an interface context. The Mixer belongs to the Animation context, as it does in
    /// the usual layout, with the Stacker and the Cyclemaker;
    /// the Skin Workshop and the Blendshapes panel are Rigging's. The timeline stays in both, because
    /// SloppyAnimatsu keys the rest pose from it too.
    /// </summary>
    public static IReadOnlyList<PanelSpec> For(string context)
    {
        if (string.Equals(context, "Rigging", StringComparison.OrdinalIgnoreCase))
            return [Bank, Scene, Properties, Material, RigWorkshop, Transform, Picker, Painter, SkinWorkshop, Blendshapes, Timeline];
        return [Bank, Scene, Properties, Material, Stacker, Transform, Mixer, Picker, Cyclemaker, Timeline];
    }
}

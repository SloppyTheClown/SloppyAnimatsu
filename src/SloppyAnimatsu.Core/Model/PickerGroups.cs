namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The Picker window's selection stamps:
/// The eleven <c>Aux_Pick_Select_*</c> buttons, which pick whole groups of controllers -
/// everything, one side, the IK and reverse-foot controllers, the blendshape controllers and the
/// four face directions.
/// </summary>
public enum PickerGroup
{
    All,
    LeftSide,
    RightSide,
    IK,
    ReverseFoot,
    Blendshape,
    Face,
    FaceUp,
    FaceDown,
    FaceLeft,
    FaceRight,
}

/// <summary>
/// What each Picker stamp selects. The groups are filters over <see cref="Character.Controllers"/>;
/// the order of the character's controller list is kept, so the picked list matches the Picker's
/// rows. Sides follow the mirror naming (<c>_L</c>/<c>_R</c> and <c>.L</c>/<c>.R</c>,
///) and the face directions are the blendshape channel's
/// quadrants.
/// </summary>
public static class PickerGroups
{
    /// <summary>The controllers the stamp picks, in the character's order.</summary>
    public static IReadOnlyList<Object3D> Pick(Character character, PickerGroup group)
    {
        var controllers = character.Controllers;
        return group switch
        {
            PickerGroup.All => controllers,
            PickerGroup.LeftSide => controllers.Where(c => IsSide(c.Name, left: true)).ToList(),
            PickerGroup.RightSide => controllers.Where(c => IsSide(c.Name, left: false)).ToList(),
            PickerGroup.IK => controllers.OfType<ControllerIK>().Cast<Object3D>().ToList(),
            PickerGroup.ReverseFoot => controllers.OfType<ControllerReverseFoot>().Cast<Object3D>().ToList(),
            PickerGroup.Blendshape => controllers.OfType<BlendshapeController>().Cast<Object3D>().ToList(),
            PickerGroup.Face => controllers.OfType<BlendshapeFaceController>().Cast<Object3D>().ToList(),
            PickerGroup.FaceUp => FaceDirection(controllers, 0),
            PickerGroup.FaceDown => FaceDirection(controllers, 1),
            PickerGroup.FaceLeft => FaceDirection(controllers, 2),
            PickerGroup.FaceRight => FaceDirection(controllers, 3),
            _ => [],
        };
    }

    /// <summary>What the status line says after the stamp picked nothing useful.</summary>
    public static string Describe(PickerGroup group) => group switch
    {
        PickerGroup.All => "all controllers",
        PickerGroup.LeftSide => "the left-side controllers",
        PickerGroup.RightSide => "the right-side controllers",
        PickerGroup.IK => "the IK controllers",
        PickerGroup.ReverseFoot => "the reverse-foot controllers",
        PickerGroup.Blendshape => "the blendshape controllers",
        PickerGroup.Face => "the blendshape face controllers",
        PickerGroup.FaceUp => "the upward blendshape channels",
        PickerGroup.FaceDown => "the downward blendshape channels",
        PickerGroup.FaceLeft => "the leftward blendshape channels",
        PickerGroup.FaceRight => "the rightward blendshape channels",
        _ => "the controllers",
    };

    /// <summary>True when the name ends in the mirror suffix for that side.</summary>
    public static bool IsSide(string? name, bool left)
    {
        if (string.IsNullOrEmpty(name) || name.Length < 2) return false;
        char side = char.ToUpperInvariant(name[^1]);
        if (side != (left ? 'L' : 'R')) return false;
        char separator = name[^2];
        return separator is '_' or '.';
    }

    private static List<Object3D> FaceDirection(IReadOnlyList<Object3D> controllers, uint direction) =>
        controllers.OfType<BlendshapeController>()
            .Where(c => c.Channels.Any(channel => channel.Direction == direction))
            .Cast<Object3D>()
            .ToList();
}
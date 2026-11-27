namespace SloppyAnimatsu.Core.Model;

/// <summary>One button of the ref pose prompt: its label and the resolution it picks (null = Cancel).</summary>
public readonly record struct RestPoseChoice(string Label, RestPoseResolution? Resolution);

/// <summary>
/// The "REFERENCE POSE" prompt:
/// what it says, the three answers it offers, and which resolution a button picks. Closing the
/// dialog answers nothing, so leaving the rigging context is never resolved by accident.
/// </summary>
public static class RestPosePrompt
{
    /// <summary>The dialog title.</summary>
    public const string Title = "REFERENCE POSE";

    /// <summary>The three answer buttons, and a Cancel as the last one.</summary>
    public static IReadOnlyList<RestPoseChoice> Choices { get; } = new RestPoseChoice[]
    {
        new("Convert Animations", RestPoseResolution.ConvertAnimations),
        new("Do Not Convert Animations", RestPoseResolution.KeepAnimations),
        new("Return to Previous Ref Pose", RestPoseResolution.ReturnToPrevious),
        new("Cancel", null),
    };

    /// <summary>The index of the Cancel answer.</summary>
    public static int CancelChoice => Choices.Count - 1;

    /// <summary>
    /// A sentence about the change, then the count of changed controllers (the part the
    /// dialog already showed, and the only place the number appears).
    /// </summary>
    public static string Message(int changedSlots)
    {
        string controllers = changedSlots == 1 ? "1 controller": $"{changedSlots} controllers";
        return "You modified your Reference Pose, it may damage your current animations. " +
               "We recommend to convert your animations to let them preserved. " +
               $"({controllers} changed.)";
    }

    /// <summary>
    /// The extra paragraph for a character that shares its Anim Bank with <paramref name="peers"/>:
    /// the shared animations are keyed against every member's reference pose, so a change that the
    /// others do not follow makes the same keys pose them differently.
    /// </summary>
    public static string SharedBankWarning(IReadOnlyList<string> peers) =>
        $"This character shares its Anim Bank with {JoinNames(peers)}. The shared animations are keyed " +
        "against each character's reference pose: if the others keep their old pose, the converted " +
        "animations will pose them differently from now on (tilted or twisted joints), and any edit " +
        "you make to a shared animation is copied to them as it is.";

    /// <summary>The check box that makes the linked characters follow the change.</summary>
    public static string FollowLabel(IReadOnlyList<string> peers) =>
        $"Also change the reference pose of {JoinNames(peers)} to match";

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "no other character",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    /// <summary>The resolution a button picks; Cancel (or an index that is no button) is null.</summary>
    public static RestPoseResolution? Resolve(int choiceIndex) =>
        choiceIndex >= 0 && choiceIndex < Choices.Count ? Choices[choiceIndex].Resolution: null;
}

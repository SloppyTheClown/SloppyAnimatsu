namespace SloppyAnimatsu.Core.Model;

/// <summary>Which dialog a focus rule belongs to.</summary>
public enum DialogId
{
    Preferences,
    Shortcuts,
    RestPose,
    ChangeSpace,
    TextPrompt,
    Welcome,
    Retarget,
}

/// <summary>
/// One place a dialog can focus, grouped by dialog. The repeated values - <see cref="PageValue"/>
/// for a preferences page's boxes and <see cref="PoseChoice"/> for the reference-pose prompt's
/// answers - stand for every such control, in the order the dialog builds them.
/// </summary>
public enum DialogStop
{
    /// <summary>The preferences dialog's page list.</summary>
    PageList,

    /// <summary>One of the selected preferences page's value boxes.</summary>
    PageValue,

    PageSave,
    PageCancel,

    /// <summary>The shortcut dialog's action list.</summary>
    ActionList,

    ActionAssign,
    ActionUnbind,
    ActionReset,
    ActionResetAll,

    /// <summary>Save and close.</summary>
    ActionSave,

    /// <summary>One answer button of the reference-pose prompt, in <see cref="RestPosePrompt.Choices"/> order.</summary>
    PoseChoice,

    /// <summary>The Space Switch window's character drop-down: whose spaces the list shows.</summary>
    SpaceCharacter,

    /// <summary>The Change Space list, the parent row first.</summary>
    SpaceList,

    SpaceOk,
    SpaceCancel,

    /// <summary>The one-line text prompt's text box.</summary>
    PromptText,

    PromptOk,
    PromptCancel,

    /// <summary>One template row of the Welcome window, in <see cref="Welcome.Templates"/> order.</summary>
    WelcomeTemplate,

    /// <summary>The Welcome window's "skip at startup" box.</summary>
    WelcomeSkip,

    WelcomeClose,

    /// <summary>The retarget dialog's source skeleton combo.</summary>
    RetargetSource,

    /// <summary>The retarget dialog's target skeleton combo.</summary>
    RetargetTarget,

    RetargetRun,
    RetargetCancel,
}

/// <summary>The keys a dialog's layout gives a meaning to.</summary>
public enum DialogKey
{
    Tab,
    ShiftTab,
    Enter,
    Escape,
}

/// <summary>What a key does: nothing, move focus, invoke a control's action, close, or cancel a capture.</summary>
public enum DialogKeyKind
{
    None,
    Focus,
    Invoke,
    Close,
    CancelCapture,
}

/// <summary>What one key press means in a dialog: the kind, and the stop index for <see cref="DialogKeyKind.Focus"/> and <see cref="DialogKeyKind.Invoke"/>.</summary>
public readonly record struct DialogKeyAction(DialogKeyKind Kind, int Index)
{
    public static readonly DialogKeyAction None = new(DialogKeyKind.None, -1);
    public static readonly DialogKeyAction Close = new(DialogKeyKind.Close, -1);
    public static readonly DialogKeyAction CancelCapture = new(DialogKeyKind.CancelCapture, -1);

    /// <summary>Move focus to the stop at <paramref name="index"/>.</summary>
    public static DialogKeyAction Focus(int index) => new(DialogKeyKind.Focus, index);

    /// <summary>Run the control at <paramref name="index"/> as its button would be pressed.</summary>
    public static DialogKeyAction Invoke(int index) => new(DialogKeyKind.Invoke, index);
}

/// <summary>
/// The dialogs' layout and focus without a window
/// the order Tab walks - the list or field the dialog is about first,
/// then the values, then the buttons - which control each dialog focuses when it opens, what Enter
/// invokes and what Escape means. A window binds its controls in <see cref="Stops"/> order and routes
/// these keys through <see cref="Key"/>, so the order and the key meanings are testable without
/// Avalonia. The order is a SloppyAnimatsu
/// choice; the Enter and Escape meanings follow the notes for each dialog.
/// </summary>
public static class DialogLayout
{
    private static readonly DialogStop[] ShortcutStops =
    [
        DialogStop.ActionList,
        DialogStop.ActionAssign,
        DialogStop.ActionUnbind,
        DialogStop.ActionReset,
        DialogStop.ActionResetAll,
        DialogStop.ActionSave,
    ];

    /// <summary>One stop per answer button, in the prompt's own order, so adding an answer adds a stop.</summary>
    private static readonly DialogStop[] RestPoseStops =
        Enumerable.Repeat(DialogStop.PoseChoice, RestPosePrompt.Choices.Count).ToArray();

    private static readonly DialogStop[] SpaceStops =
    [
        DialogStop.SpaceCharacter,
        DialogStop.SpaceList,
        DialogStop.SpaceOk,
        DialogStop.SpaceCancel,
    ];

    private static readonly DialogStop[] PromptStops =
    [
        DialogStop.PromptText,
        DialogStop.PromptOk,
        DialogStop.PromptCancel,
    ];

    /// <summary>
    /// One stop per template row (<paramref name="templates"/>, the scenes the templates folder
    /// holds), then the "skip" box and Close.
    /// </summary>
    private static DialogStop[] WelcomeStops(int templates) =>
    [
        .. Enumerable.Repeat(DialogStop.WelcomeTemplate, Math.Max(templates, 0)),
        DialogStop.WelcomeSkip,
        DialogStop.WelcomeClose,
    ];

    private static readonly DialogStop[] RetargetStops =
    [
        DialogStop.RetargetSource,
        DialogStop.RetargetTarget,
        DialogStop.RetargetRun,
        DialogStop.RetargetCancel,
    ];

    /// <summary>
    /// The stops Tab walks. A preferences page contributes one <see cref="DialogStop.PageValue"/> per
    /// visible row (<paramref name="values"/>), so the order follows the page; a page without a
    /// writable value has only its list and buttons, because the "no editable values" line is text.
    /// </summary>
    public static IReadOnlyList<DialogStop> Stops(DialogId dialog, int values = 0)
    {
        if (dialog != DialogId.Preferences) return dialog switch
        {
            DialogId.Shortcuts => ShortcutStops,
            DialogId.RestPose => RestPoseStops,
            DialogId.ChangeSpace => SpaceStops,
            DialogId.TextPrompt => PromptStops,
            DialogId.Welcome => WelcomeStops(values),
            DialogId.Retarget => RetargetStops,
            _ => Array.Empty<DialogStop>(),
        };

        var stops = new List<DialogStop>(Math.Max(values, 0) + 3) { DialogStop.PageList };
        for (int i = 0; i < values; i++) stops.Add(DialogStop.PageValue);
        stops.Add(DialogStop.PageSave);
        stops.Add(DialogStop.PageCancel);
        return stops;
    }

    /// <summary>
    /// The stop a dialog focuses when it opens: the list or field the dialog is about, never a
    /// button. The reference-pose prompt is the one exception, because its only controls are answers.
    /// </summary>
    public static DialogStop Initial(DialogId dialog) => dialog switch
    {
        DialogId.Preferences => DialogStop.PageList,
        DialogId.Shortcuts => DialogStop.ActionList,
        DialogId.RestPose => DialogStop.PoseChoice,
        DialogId.ChangeSpace => DialogStop.SpaceCharacter,
        DialogId.TextPrompt => DialogStop.PromptText,
        DialogId.Welcome => DialogStop.WelcomeTemplate,
        DialogId.Retarget => DialogStop.RetargetSource,
        _ => DialogStop.PageList,
    };

    /// <summary>
    /// The next stop index, wrapping at both ends: forward from the last is the first, backward from
    /// the first is the last. A dialog with no focus (index -1) or a stale one enters at the edge the
    /// move comes from.
    /// </summary>
    public static int NextIndex(int count, int index, bool forward)
    {
        if (count <= 0) return -1;
        if (index < 0 || index >= count) return forward ? 0 : count - 1;
        return forward ? (index + 1) % count : (index + count - 1) % count;
    }

    /// <summary>
    /// What a key does in a dialog. Tab and Shift+Tab walk <paramref name="stops"/>; Enter invokes the
    /// dialog's default - Save in Preferences, Save and close in the shortcut dialog, OK in Change
    /// Space and the text prompt, and the focused answer of the reference-pose prompt, which has no
    /// default answer; in the Welcome window it presses the focused row, box or button, and the first
    /// template row when nothing in the window has focus. Escape invokes Cancel in Preferences, Change
    /// Space and the text prompt, closes the reference-pose prompt with no answer and the Welcome
    /// window, and cancels an armed capture in the shortcut dialog (with nothing armed the window
    /// closes).
    /// </summary>
    public static DialogKeyAction Key(DialogId dialog, IReadOnlyList<DialogStop> stops, int index, DialogKey key)
    {
        if (stops.Count == 0) return DialogKeyAction.None;
        switch (key)
        {
            case DialogKey.Tab:
                return DialogKeyAction.Focus(NextIndex(stops.Count, index, true));
            case DialogKey.ShiftTab:
                return DialogKeyAction.Focus(NextIndex(stops.Count, index, false));
            case DialogKey.Enter:
                return dialog switch
                {
                    DialogId.Preferences => InvokeStop(stops, DialogStop.PageSave),
                    DialogId.Shortcuts => InvokeStop(stops, DialogStop.ActionSave),
                    DialogId.RestPose => FocusedOrFirst(stops, index, DialogStop.PoseChoice),
                    DialogId.ChangeSpace => InvokeStop(stops, DialogStop.SpaceOk),
                    DialogId.TextPrompt => InvokeStop(stops, DialogStop.PromptOk),
                    DialogId.Welcome => WelcomeEnter(stops, index),
                    DialogId.Retarget => InvokeStop(stops, DialogStop.RetargetRun),
                    _ => DialogKeyAction.None,
                };
            case DialogKey.Escape:
                return dialog switch
                {
                    DialogId.Preferences => InvokeStop(stops, DialogStop.PageCancel),
                    DialogId.Shortcuts => DialogKeyAction.CancelCapture,
                    DialogId.RestPose => DialogKeyAction.Close,
                    DialogId.ChangeSpace => InvokeStop(stops, DialogStop.SpaceCancel),
                    DialogId.TextPrompt => InvokeStop(stops, DialogStop.PromptCancel),
                    DialogId.Welcome => DialogKeyAction.Close,
                    DialogId.Retarget => InvokeStop(stops, DialogStop.RetargetCancel),
                    _ => DialogKeyAction.None,
                };
            default:
                return DialogKeyAction.None;
        }
    }

    private static DialogKeyAction FocusedOrFirst(IReadOnlyList<DialogStop> stops, int index, DialogStop stop)
    {
        if (index >= 0 && index < stops.Count && stops[index] == stop) return DialogKeyAction.Invoke(index);
        return InvokeStop(stops, stop);
    }

    /// <summary>
    /// Enter in the Welcome window presses what has focus: a template row opens that scene, the
    /// "skip" box toggles, Close closes. With no focus it presses the first template row, the only
    /// default the window has; <see cref="DialogChrome"/> can press a check box as well as a button
    /// for this.
    /// </summary>
    private static DialogKeyAction WelcomeEnter(IReadOnlyList<DialogStop> stops, int index)
    {
        if (index >= 0 && index < stops.Count) return DialogKeyAction.Invoke(index);
        return InvokeStop(stops, DialogStop.WelcomeTemplate);
    }

    private static DialogKeyAction InvokeStop(IReadOnlyList<DialogStop> stops, DialogStop stop)
    {
        for (int i = 0; i < stops.Count; i++)
            if (stops[i] == stop) return DialogKeyAction.Invoke(i);
        return DialogKeyAction.None;
    }
}
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The six dialogs' layout and focus
/// the tab order each window binds its controls to, the control it focuses when it
/// opens, and what Enter and Escape mean. The viewer's <c>DialogChrome</c> walks exactly these lists
/// and refuses a binding in another order, so a window's keys are checked here without Avalonia.
/// </summary>
public class DialogLayoutTests
{
    [Fact]
    public void Preferences_WalksThePageListEveryValueThenSaveAndCancel()
    {
        Assert.Equal(
            new[]
            {
                DialogStop.PageList, DialogStop.PageValue, DialogStop.PageValue, DialogStop.PageValue,
                DialogStop.PageSave, DialogStop.PageCancel,
            },
            DialogLayout.Stops(DialogId.Preferences, 3));
        Assert.Equal(
            new[] { DialogStop.PageList, DialogStop.PageSave, DialogStop.PageCancel },
            DialogLayout.Stops(DialogId.Preferences));
        // A negative count has no value box, and one page's boxes always sit between the list and the
        // buttons, so Save and Cancel keep their places on every page.
        Assert.Equal(DialogLayout.Stops(DialogId.Preferences), DialogLayout.Stops(DialogId.Preferences, -4));
        for (int values = 0; values <= 4; values++)
        {
            var stops = DialogLayout.Stops(DialogId.Preferences, values);
            Assert.Equal(DialogStop.PageList, stops[0]);
            Assert.Equal(DialogStop.PageSave, stops[^2]);
            Assert.Equal(DialogStop.PageCancel, stops[^1]);
            Assert.Equal(values, stops.Count(stop => stop == DialogStop.PageValue));
        }
    }

    [Fact]
    public void Shortcuts_WalksTheActionListThenEveryButton()
    {
        Assert.Equal(
            new[]
            {
                DialogStop.ActionList, DialogStop.ActionAssign, DialogStop.ActionUnbind,
                DialogStop.ActionReset, DialogStop.ActionResetAll, DialogStop.ActionSave,
            },
            DialogLayout.Stops(DialogId.Shortcuts));
    }

    [Fact]
    public void RestPose_WalksOneStopPerAnswerButton()
    {
        var stops = DialogLayout.Stops(DialogId.RestPose);
        Assert.Equal(RestPosePrompt.Choices.Count, stops.Count);
        Assert.All(stops, stop => Assert.Equal(DialogStop.PoseChoice, stop));
    }

    [Fact]
    public void ChangeSpaceAndTheTextPrompt_WalkTheirFieldThenOkAndCancel()
    {
        Assert.Equal(
            new[] { DialogStop.SpaceCharacter, DialogStop.SpaceList, DialogStop.SpaceOk, DialogStop.SpaceCancel },
            DialogLayout.Stops(DialogId.ChangeSpace));
        Assert.Equal(
            new[] { DialogStop.PromptText, DialogStop.PromptOk, DialogStop.PromptCancel },
            DialogLayout.Stops(DialogId.TextPrompt));
    }

    [Fact]
    public void Retarget_WalksTheTwoSkeletonCombosThenRetargetAndClose()
    {
        var stops = DialogLayout.Stops(DialogId.Retarget);
        Assert.Equal(
            new[]
            {
                DialogStop.RetargetSource, DialogStop.RetargetTarget, DialogStop.RetargetRun,
                DialogStop.RetargetCancel,
            },
            stops);
        // Enter retargets from anywhere in the dialog; Escape closes it without running.
        Assert.Equal(DialogKeyAction.Invoke(2), DialogLayout.Key(DialogId.Retarget, stops, 0, DialogKey.Enter));
        Assert.Equal(DialogKeyAction.Invoke(3), DialogLayout.Key(DialogId.Retarget, stops, 0, DialogKey.Escape));
    }

    [Fact]
    public void Welcome_WalksTheTemplateRowsThenSkipAndClose()
    {
        var stops = DialogLayout.Stops(DialogId.Welcome, 3);
        Assert.Equal(3 + 2, stops.Count);
        Assert.All(stops.Take(3), stop => Assert.Equal(DialogStop.WelcomeTemplate, stop));
        Assert.Equal(DialogStop.WelcomeSkip, stops[^2]);
        Assert.Equal(DialogStop.WelcomeClose, stops[^1]);
    }

    [Fact]
    public void Welcome_EnterPressesTheFocusedRowAndEscapeCloses()
    {
        var stops = DialogLayout.Stops(DialogId.Welcome, 3);
        for (int index = 0; index < stops.Count; index++)
            Assert.Equal(DialogKeyAction.Invoke(index), DialogLayout.Key(DialogId.Welcome, stops, index, DialogKey.Enter));
        // Nothing in the window has focus: the first template row is the default action.
        Assert.Equal(DialogKeyAction.Invoke(0), DialogLayout.Key(DialogId.Welcome, stops, -1, DialogKey.Enter));
        Assert.Equal(DialogKeyAction.Invoke(0), DialogLayout.Key(DialogId.Welcome, stops, stops.Count, DialogKey.Enter));
        Assert.Equal(DialogKeyAction.Close, DialogLayout.Key(DialogId.Welcome, stops, 0, DialogKey.Escape));
    }

    [Fact]
    public void EveryDialog_OpensOnItsFirstStop()
    {
        var dialogs = Enum.GetValues<DialogId>();
        Assert.Equal(7, dialogs.Length);
        foreach (var dialog in dialogs)
        {
            var stops = DialogLayout.Stops(dialog, 2);
            Assert.NotEmpty(stops);
            Assert.Equal(stops[0], DialogLayout.Initial(dialog));
        }
        // The first stop is the list or field the dialog is about; the prompt's only controls are
        // its answers, so its first answer is its initial focus, and the Welcome window opens on its
        // first template row.
        Assert.Equal(DialogStop.PageList, DialogLayout.Initial(DialogId.Preferences));
        Assert.Equal(DialogStop.ActionList, DialogLayout.Initial(DialogId.Shortcuts));
        Assert.Equal(DialogStop.PoseChoice, DialogLayout.Initial(DialogId.RestPose));
        Assert.Equal(DialogStop.SpaceCharacter, DialogLayout.Initial(DialogId.ChangeSpace));
        Assert.Equal(DialogStop.PromptText, DialogLayout.Initial(DialogId.TextPrompt));
        Assert.Equal(DialogStop.WelcomeTemplate, DialogLayout.Initial(DialogId.Welcome));
        Assert.Equal(DialogStop.RetargetSource, DialogLayout.Initial(DialogId.Retarget));
    }

    [Fact]
    public void Tab_WalksTheWholeOrderAndWrapsBothWays()
    {
        foreach (var dialog in Enum.GetValues<DialogId>())
        {
            var stops = DialogLayout.Stops(dialog, 2);
            var walked = new List<int> { 0 };
            int index = 0;
            for (int i = 1; i < stops.Count; i++)
            {
                var action = DialogLayout.Key(dialog, stops, index, DialogKey.Tab);
                Assert.Equal(DialogKeyKind.Focus, action.Kind);
                index = action.Index;
                walked.Add(index);
            }
            Assert.Equal(Enumerable.Range(0, stops.Count).ToList(), walked);
            // Forward from the last is the first; backward from the first is the last.
            Assert.Equal(0, DialogLayout.Key(dialog, stops, stops.Count - 1, DialogKey.Tab).Index);
            Assert.Equal(stops.Count - 1, DialogLayout.Key(dialog, stops, 0, DialogKey.ShiftTab).Index);
            Assert.Equal(stops.Count - 2, DialogLayout.Key(dialog, stops, stops.Count - 1, DialogKey.ShiftTab).Index);
        }
    }

    [Fact]
    public void Tab_WithoutAFocusStartsAtTheEdgeTheMoveComesFrom()
    {
        foreach (var dialog in Enum.GetValues<DialogId>())
        {
            var stops = DialogLayout.Stops(dialog, 2);
            Assert.Equal(0, DialogLayout.Key(dialog, stops, -1, DialogKey.Tab).Index);
            Assert.Equal(0, DialogLayout.Key(dialog, stops, stops.Count, DialogKey.Tab).Index);
            Assert.Equal(stops.Count - 1, DialogLayout.Key(dialog, stops, -1, DialogKey.ShiftTab).Index);
            Assert.Equal(stops.Count - 1, DialogLayout.Key(dialog, stops, stops.Count, DialogKey.ShiftTab).Index);
        }
        Assert.Equal(-1, DialogLayout.NextIndex(0, 0, true));
        Assert.Equal(-1, DialogLayout.NextIndex(0, -1, false));
    }

    [Fact]
    public void Enter_InvokesEachDialogsDefault()
    {
        var preferences = DialogLayout.Stops(DialogId.Preferences, 2);
        // Two value boxes put Save at 3; Enter saves from a box and from no focus at all.
        Assert.Equal(DialogKeyAction.Invoke(3), DialogLayout.Key(DialogId.Preferences, preferences, 1, DialogKey.Enter));
        Assert.Equal(DialogKeyAction.Invoke(3), DialogLayout.Key(DialogId.Preferences, preferences, -1, DialogKey.Enter));

        var shortcuts = DialogLayout.Stops(DialogId.Shortcuts);
        Assert.Equal(DialogKeyAction.Invoke(5), DialogLayout.Key(DialogId.Shortcuts, shortcuts, 0, DialogKey.Enter));

        var spaces = DialogLayout.Stops(DialogId.ChangeSpace);
        Assert.Equal(DialogKeyAction.Invoke(2), DialogLayout.Key(DialogId.ChangeSpace, spaces, 0, DialogKey.Enter));

        var prompt = DialogLayout.Stops(DialogId.TextPrompt);
        Assert.Equal(DialogKeyAction.Invoke(1), DialogLayout.Key(DialogId.TextPrompt, prompt, 0, DialogKey.Enter));

        var retarget = DialogLayout.Stops(DialogId.Retarget);
        Assert.Equal(DialogKeyAction.Invoke(2), DialogLayout.Key(DialogId.Retarget, retarget, 1, DialogKey.Enter));
    }

    [Fact]
    public void Enter_OnTheReferencePosePromptPressesTheFocusedAnswer()
    {
        var stops = DialogLayout.Stops(DialogId.RestPose);
        for (int index = 0; index < stops.Count; index++)
            Assert.Equal(DialogKeyAction.Invoke(index), DialogLayout.Key(DialogId.RestPose, stops, index, DialogKey.Enter));
        // No answer has focus yet (or the focus left the dialog): the first answer is pressed.
        Assert.Equal(DialogKeyAction.Invoke(0), DialogLayout.Key(DialogId.RestPose, stops, -1, DialogKey.Enter));
        Assert.Equal(DialogKeyAction.Invoke(0), DialogLayout.Key(DialogId.RestPose, stops, stops.Count, DialogKey.Enter));
    }

    [Fact]
    public void Escape_CancelsOrClosesEachDialog()
    {
        var preferences = DialogLayout.Stops(DialogId.Preferences, 2);
        // Cancel is the last stop: 0 list, 1-2 values, 3 save, 4 cancel.
        Assert.Equal(DialogKeyAction.Invoke(4), DialogLayout.Key(DialogId.Preferences, preferences, 1, DialogKey.Escape));
        // The shortcut dialog's Escape is its armed capture first; with nothing armed the window closes.
        Assert.Equal(
            DialogKeyAction.CancelCapture,
            DialogLayout.Key(DialogId.Shortcuts, DialogLayout.Stops(DialogId.Shortcuts), 0, DialogKey.Escape));
        // The prompt has no cancel button: Escape closes it with no answer.
        Assert.Equal(
            DialogKeyAction.Close,
            DialogLayout.Key(DialogId.RestPose, DialogLayout.Stops(DialogId.RestPose), 0, DialogKey.Escape));
        Assert.Equal(
            DialogKeyAction.Invoke(3),
            DialogLayout.Key(DialogId.ChangeSpace, DialogLayout.Stops(DialogId.ChangeSpace), 0, DialogKey.Escape));
        Assert.Equal(
            DialogKeyAction.Invoke(2),
            DialogLayout.Key(DialogId.TextPrompt, DialogLayout.Stops(DialogId.TextPrompt), 0, DialogKey.Escape));
    }

    [Fact]
    public void EveryStop_BelongsToOneDialogAndEveryKeyOnAValidStopDoesSomething()
    {
        var dialogs = Enum.GetValues<DialogId>().ToList();
        var stopsByDialog = dialogs.ToDictionary(
            dialog => dialog,
            dialog => DialogLayout.Stops(dialog, 1).Distinct().ToList());
        foreach (var stop in Enum.GetValues<DialogStop>())
            Assert.Single(dialogs, dialog => stopsByDialog[dialog].Contains(stop));
        Assert.Equal(
            Enum.GetValues<DialogStop>().OrderBy(stop => stop).ToList(),
            stopsByDialog.SelectMany(pair => pair.Value).Distinct().OrderBy(stop => stop).ToList());

        foreach (var dialog in dialogs)
        {
            var stops = DialogLayout.Stops(dialog, 2);
            for (int index = -1; index <= stops.Count; index++)
                foreach (var key in Enum.GetValues<DialogKey>())
                {
                    var action = DialogLayout.Key(dialog, stops, index, key);
                    Assert.NotEqual(DialogKeyKind.None, action.Kind);
                    if (action.Kind is DialogKeyKind.Focus or DialogKeyKind.Invoke)
                        Assert.InRange(action.Index, 0, stops.Count - 1);
                }
        }
    }

    [Fact]
    public void NoStops_MeansNoKeyAction()
    {
        foreach (var key in Enum.GetValues<DialogKey>())
            Assert.Equal(DialogKeyAction.None, DialogLayout.Key(DialogId.TextPrompt, [], 0, key));
    }
}
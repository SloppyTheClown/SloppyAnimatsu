using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// Asks what to do with the animations after the reference pose changed
/// What it says and the buttons it
/// offers are <see cref="RestPosePrompt"/>; closing it answers nothing.
/// </summary>
public sealed class RestPoseDialog : Window
{
    private RestPoseResolution? _choice;
    private readonly List<Button> _buttons = [];

    private readonly CheckBox? _follow;

    private RestPoseDialog(int changedSlots, IReadOnlyList<string> peers)
    {
        Title = RestPosePrompt.Title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < RestPosePrompt.Choices.Count; i++)
        {
            int index = i;
            var button = new Button { Content = RestPosePrompt.Choices[i].Label };
            button.Click += (_, _) =>
            {
                _choice = RestPosePrompt.Resolve(index);
                Close();
            };
            buttons.Children.Add(button);
            _buttons.Add(button);
        }

        var body = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 14,
            MaxWidth = 560,
            Children =
            {
                new TextBlock
                {
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Text = RestPosePrompt.Message(changedSlots),
                },
            },
        };
        if (peers.Count > 0)
        {
            // A character with a shared Anim Bank: say what the change does to the others, and
            // offer to give them the same change (on by default, which keeps the bank valid for all).
            body.Children.Add(new TextBlock
            {
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = RestPosePrompt.SharedBankWarning(peers),
            });
            _follow = new CheckBox { Content = RestPosePrompt.FollowLabel(peers), IsChecked = true };
            body.Children.Add(_follow);
        }
        body.Children.Add(buttons);
        Content = body;

        // The prompt has no default answer: Enter presses the answer that has focus, and Escape
        // closes with no answer (DialogLayout's Close).
        var chrome = new DialogChrome(this, DialogId.RestPose);
        chrome.Bind(_buttons.Select(button => (DialogStop.PoseChoice, (Control)button)).ToArray());
    }

    /// <summary>The user's choice, or null if they cancelled.</summary>
    public static async Task<RestPoseResolution?> Ask(Window owner, int changedSlots) =>
        (await Ask(owner, changedSlots, [])).Choice;

    /// <summary>
    /// The user's choice (null if they cancelled), and for a character sharing its Anim Bank with
    /// <paramref name="peers"/>, whether those take the same ref-pose change.
    /// </summary>
    public static async Task<(RestPoseResolution? Choice, bool Follow)> Ask(Window owner, int changedSlots, IReadOnlyList<string> peers)
    {
        var dialog = new RestPoseDialog(changedSlots, peers);
        await dialog.ShowDialog(owner);
        return (dialog._choice, dialog._follow?.IsChecked == true);
    }
}

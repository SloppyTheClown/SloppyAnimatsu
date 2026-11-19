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

    private RestPoseDialog(int changedSlots)
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

        Content = new StackPanel
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
                buttons,
            },
        };

        // The prompt has no default answer: Enter presses the answer that has focus, and Escape
        // closes with no answer (DialogLayout's Close).
        var chrome = new DialogChrome(this, DialogId.RestPose);
        chrome.Bind(_buttons.Select(button => (DialogStop.PoseChoice, (Control)button)).ToArray());
    }

    /// <summary>The user's choice, or null if they cancelled.</summary>
    public static async Task<RestPoseResolution?> Ask(Window owner, int changedSlots)
    {
        var dialog = new RestPoseDialog(changedSlots);
        await dialog.ShowDialog(owner);
        return dialog._choice;
    }
}

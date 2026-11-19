using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// Asks for one line of text (a new name); <see cref="TextPrompt"/> decides what an answer is (blank
/// is no answer, and an answer is trimmed). Closing it or Escape answers nothing.
/// </summary>
public sealed class TextPromptDialog : Window
{
    private string? _answer;

    private TextPromptDialog(string title, string label, string initial)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var box = new TextBox { Text = initial, Width = 320 };
        var ok = new Button { Content = "OK" };
        var cancel = new Button { Content = "Cancel" };
        ok.Click += (_, _) =>
        {
            _answer = TextPrompt.Answer(box.Text);
            Close();
        };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = label },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };

        // The box is the model's initial stop, so the chrome focuses it when the window opens; the
        // selection is the prompt's own touch.
        var chrome = new DialogChrome(this, DialogId.TextPrompt);
        chrome.Bind(
            (DialogStop.PromptText, box),
            (DialogStop.PromptOk, ok),
            (DialogStop.PromptCancel, cancel));
        Opened += (_, _) => box.SelectAll();
    }

    /// <summary>The text entered, or null if the user cancelled or left it blank.</summary>
    public static async Task<string?> Ask(Window owner, string title, string label, string initial)
    {
        var dialog = new TextPromptDialog(title, label, initial);
        await dialog.ShowDialog(owner);
        return dialog._answer;
    }
}

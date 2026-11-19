using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The "RIGID AND SMOOTH MODIFIED" prompt, shown by the AutoRigger when the
/// character's rigid and smooth weight versions were both edited. Its two answers are the weight
/// versions; <see cref="AutoRigSmoothAgainPrompt"/> holds what it says, and closing it answers
/// nothing, so a build never silently replaces the user's weights.
/// </summary>
public sealed class SmoothAgainDialog : Window
{
    private AutoRigWeightVersion? _answer;
    private readonly List<Button> _buttons = [];

    private SmoothAgainDialog()
    {
        Title = AutoRigSmoothAgainPrompt.Title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        foreach (AutoRigWeightChoice choice in AutoRigSmoothAgainPrompt.Choices)
        {
            AutoRigWeightVersion version = choice.Version;
            var button = new Button { Content = choice.Label, Padding = new Thickness(14, 4) };
            button.Click += (_, _) =>
            {
                _answer = version;
                Close();
            };
            buttons.Children.Add(button);
            _buttons.Add(button);
        }

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 14,
            MaxWidth = 520,
            Children =
            {
                new TextBlock
                {
                    Text = AutoRigSmoothAgainPrompt.Message,
                    TextWrapping = TextWrapping.Wrap,
                },
                buttons,
            },
        };

        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
        Opened += (_, _) => _buttons.FirstOrDefault()?.Focus();
    }

    /// <summary>The user's answer, or null when they closed the prompt without one.</summary>
    public static async Task<AutoRigWeightVersion?> Ask(Window owner)
    {
        var dialog = new SmoothAgainDialog();
        await dialog.ShowDialog(owner);
        return dialog._answer;
    }
}
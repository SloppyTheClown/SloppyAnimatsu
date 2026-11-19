using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// Asked before a scene is saved as <c>.akt</c>: that file is written in the plain .akt scheme, so
/// what only SloppyAnimatsu keeps (<see cref="AkeytsuCompatibility.LostFeatures"/>) is left out.
/// Closing the prompt or Escape cancels the save.
/// </summary>
public sealed class AkeytsuSaveDialog : Window
{
    private bool _confirmed;
    private readonly Button _save;

    private AkeytsuSaveDialog(string fileName)
    {
        Title = "Save for akeytsu";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _save = new Button { Content = "Save as .akt", Padding = new Thickness(14, 4) };
        _save.Click += (_, _) =>
        {
            _confirmed = true;
            Close();
        };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4) };
        cancel.Click += (_, _) => Close();

        var lost = new StackPanel { Spacing = 2, Margin = new Thickness(12, 0, 0, 0) };
        foreach (string item in AkeytsuCompatibility.LostFeatures)
            lost.Children.Add(new TextBlock { Text = "• " + item, TextWrapping = TextWrapping.Wrap });

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            MaxWidth = 540,
            Children =
            {
                new TextBlock
                {
                    Text = $"{fileName} will be saved as an akeytsu scene, in akeytsu's own format, so akeytsu " +
                           "can open it. Information only Sloppy Animatsu keeps will be lost from this file:",
                    TextWrapping = TextWrapping.Wrap,
                },
                lost,
                new TextBlock
                {
                    Text = "To keep everything, save as .slopa instead (File > Save As).",
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { _save, cancel },
                },
            },
        };

        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
        Opened += (_, _) => _save.Focus();
    }

    /// <summary>True when the user chose to save as <c>.akt</c>; false when they cancelled.</summary>
    public static async Task<bool> Ask(Window owner, string fileName)
    {
        var dialog = new AkeytsuSaveDialog(fileName);
        await dialog.ShowDialog(owner);
        return dialog._confirmed;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The Welcome window: the startup dialog with
/// one row per template scene and its "Skip welcome window at startup" box. A row opens its scene
/// through the ordinary File &gt; Open path; a template removed since the window opened is
/// reported and leaves the window open. The box writes <c>CGeneralPreferences.SkipWelcomeWindow</c>
/// at once, the way the recent list is written (the app keeps it until the application exits).
/// </summary>
public sealed class WelcomeDialog : Window
{
    private readonly Preferences? _preferences;
    private readonly string _folder;
    private readonly TextBlock _message = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.IndianRed,
    };
    private string? _opened;

    private WelcomeDialog(Preferences? preferences, string? folder)
    {
        _preferences = preferences;
        _folder = folder ?? Welcome.DefaultFolder;

        Title = "Welcome";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var rows = new StackPanel { Spacing = 6 };
        var controls = new List<(DialogStop Stop, Control Control)>();
        foreach (var template in Welcome.Find(_folder))
        {
            var button = new Button
            {
                Content = new TextBlock { Text = template.Name }, // a bare string loses its first '_' (access key)
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.Click += (_, _) => Choose(template);
            rows.Children.Add(button);
            controls.Add((DialogStop.WelcomeTemplate, button));
        }

        var skip = new CheckBox
        {
            Content = $"Skip {Welcome.Heading.ToLowerInvariant()} window at startup",
            IsChecked = Welcome.IsSkipped(preferences),
            IsEnabled = preferences != null,
        };
        skip.IsCheckedChanged += (_, _) => Remember(skip.IsChecked == true);
        controls.Add((DialogStop.WelcomeSkip, skip));

        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        controls.Add((DialogStop.WelcomeClose, close));

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            MaxWidth = 560,
            Children =
            {
                new TextBlock { Text = Welcome.Heading, FontSize = 22, FontWeight = FontWeight.Bold },
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "Open a template scene. Put scenes (.slopa or .akt) in the templates folder below to list them here.",
                },
                rows,
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.7,
                    FontSize = 11,
                    Text = $"Templates folder: {_folder}",
                },
                _message,
                skip,
                close,
            },
        };

        var chrome = new DialogChrome(this, DialogId.Welcome);
        chrome.Bind(controls.ToArray());
    }

    /// <summary>
    /// Shows the window and returns the template scene the user picked, or null when they closed it.
    /// </summary>
    public static async Task<string?> Show(Window owner, Preferences? preferences, string? folder = null)
    {
        var dialog = new WelcomeDialog(preferences, folder);
        await dialog.ShowDialog(owner);
        return dialog._opened;
    }

    private void Choose(WelcomeTemplate template)
    {
        string path = Welcome.TemplatePath(template, _folder);
        if (File.Exists(path))
        {
            _opened = path;
            Close();
        }
        else
        {
            _message.Text = $"'{template.Name}' is no longer in {_folder}.";
        }
    }

    /// <summary>
    /// Writes the box to the preferences. The app binds the box to the preference live and saves the
    /// file on exit; SloppyAnimatsu saves a preference as soon as it changes, as it does for the recent list.
    /// </summary>
    private void Remember(bool skip)
    {
        if (_preferences == null) return;
        _preferences.SkipWelcomeWindow = skip;
        try
        {
            _preferences.Save();
        }
        catch (Exception e)
        {
            _message.Text = $"Could not save the preferences: {e.Message}";
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>What the link dialog asks for: the reference, the characters to link to it, the duplicates to drop.</summary>
public sealed record SharedBankLinkRequest(Character Reference, IReadOnlyList<Character> Others, IReadOnlyList<BankDuplicate> Drop);

/// <summary>
/// "Link Anim Banks": picks a reference character and the characters that will share its Anim Bank.
/// Each candidate shows whether its skeleton matches the reference's; a character that does not can
/// still be picked, after a warning. The duplicates the merge would hold twice are listed with a
/// box each (checked: dropped). Closing it or Escape links nothing.
/// </summary>
public sealed class SharedBankDialog : Window
{
    private readonly ComboBox _reference;
    private readonly StackPanel _candidates = new() { Spacing = 4 };
    private readonly StackPanel _duplicates = new() { Spacing = 2 };
    private readonly TextBlock _duplicateInfo = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly List<(Character Character, CheckBox Box, SharedBankEligibility? Eligibility)> _rows = [];
    private readonly List<(BankDuplicate Duplicate, CheckBox Box)> _duplicateRows = [];
    // Above the duplicate rows: checks or clears them all; shows a dash while only some are checked.
    private readonly CheckBox _selectAll = new() { Content = "Select all", IsThreeState = true, IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
    private bool _settingAll;
    private readonly IReadOnlyList<Character> _characters;
    private SharedBankLinkRequest? _answer;
    private int _scanGeneration;
    private bool _scanned;

    private SharedBankDialog(IReadOnlyList<Character> characters, Character? current)
    {
        _characters = characters;
        Title = "Link Anim Banks";
        Width = 620;
        Height = 620;
        MinWidth = 460;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _reference = new ComboBox
        {
            ItemsSource = characters.Select(c => c.Name ?? "(unnamed)").ToList(),
            SelectedIndex = Math.Max(0, current == null ? 0 : IndexOf(characters, current)),
            MinWidth = 260,
        };
        _reference.SelectionChanged += (_, _) => BuildCandidates();

        var link = new Button { Content = "Link", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        link.Click += async (_, _) => await Confirm();
        cancel.Click += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        var top = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "Linked characters share one Anim Bank: every animation is in each of their banks, " +
                           "and an edit in one is copied to the others. Linking merges the banks and gives every " +
                           "character the reference's joint rotations in its reference pose (each keeps its own " +
                           "bone lengths; its animations are converted). Unlinking later leaves each character " +
                           "its own full copy.",
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { new TextBlock { Text = "Reference character:", VerticalAlignment = VerticalAlignment.Center }, _reference },
                },
                new TextBlock { Text = "Link with:", FontWeight = FontWeight.SemiBold },
                _candidates,
                new TextBlock { Text = "Duplicate animations (checked ones are left out):", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                _duplicateInfo,
                _selectAll,
            },
        };
        _selectAll.IsCheckedChanged += (_, _) =>
        {
            if (_settingAll) return;
            // A click clears the rows when every one is checked, and otherwise checks them all.
            bool check = !_duplicateRows.All(r => r.Box.IsChecked == true);
            _settingAll = true;
            foreach (var row in _duplicateRows) row.Box.IsChecked = check;
            _selectAll.IsChecked = check;
            _settingAll = false;
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { link, cancel },
        };
        var root = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(buttons);
        // The duplicate rows scroll inside an outlined box, so the scrolling area is plain to see.
        root.Children.Add(new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 4, 0, 0),
            Child = new ScrollViewer
            {
                Content = _duplicates,
                Padding = new Thickness(6, 4),
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            },
        });
        Content = root;
        BuildCandidates();
    }

    private static int IndexOf(IReadOnlyList<Character> list, Character c)
    {
        for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i].Source, c.Source)) return i;
        return -1;
    }

    private Character? Reference => _reference.SelectedIndex is int i && i >= 0 && i < _characters.Count ? _characters[i] : null;

    /// <summary>One row per other character: a box, its name and whether its skeleton matches.</summary>
    private void BuildCandidates()
    {
        _rows.Clear();
        _candidates.Children.Clear();
        if (Reference is not { } reference) return;
        var group = SharedAnimBanks.GroupOf(reference);
        foreach (var character in _characters)
        {
            if (ReferenceEquals(character.Source, reference.Source)) continue;
            bool linked = group.Any(g => ReferenceEquals(g.Source, character.Source));
            var eligibility = linked ? null : SharedAnimBanks.Eligibility(reference, character);
            var box = new CheckBox
            {
                Content = character.Name ?? "(unnamed)",
                IsChecked = linked,
                IsEnabled = !linked,
                MinWidth = 180,
            };
            box.IsCheckedChanged += (_, _) => ScanSoon();
            string note = linked ? "already linked"
                : eligibility!.Eligible ? "compatible skeleton"
                : "not compatible: " + eligibility.Summary;
            var info = new TextBlock
            {
                Text = note,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Foreground = eligibility is { Eligible: false } ? Brushes.Orange : Brushes.Gray,
            };
            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(box, Dock.Left);
            row.Children.Add(box);
            row.Children.Add(info);
            _candidates.Children.Add(row);
            _rows.Add((character, box, eligibility));
        }
        ScanSoon();
    }

    private List<Character> Picked() =>
        _rows.Where(r => r.Box.IsChecked == true && r.Box.IsEnabled).Select(r => r.Character).ToList();

    /// <summary>Scans for duplicates once the dialog is idle; a newer change cancels an older scan.</summary>
    private void ScanSoon()
    {
        int generation = ++_scanGeneration;
        _scanned = false;
        _duplicateRows.Clear();
        _duplicates.Children.Clear();
        _duplicateInfo.Text = "Looking for duplicates...";
        UpdateSelectAll();
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _scanGeneration) return;
            Scan();
        }, DispatcherPriority.Background);
    }

    private void Scan()
    {
        _duplicateRows.Clear();
        _duplicates.Children.Clear();
        _scanned = true;
        var picked = Picked();
        if (Reference is not { } reference || picked.Count == 0)
        {
            _duplicateInfo.Text = "Pick at least one character to link.";
            return;
        }
        IReadOnlyList<BankDuplicate> found;
        try
        {
            found = SharedAnimBanks.FindDuplicates(reference, picked);
        }
        catch (Exception ex)
        {
            _duplicateInfo.Text = "Could not compare the banks: " + ex.Message;
            return;
        }
        _duplicateInfo.Text = found.Count == 0
            ? "No duplicates: every animation is kept."
            : $"{found.Count} animation(s) hold the same motion as one in another bank. The copy outside a " +
              "\"Retargeted From\" folder is kept.";
        foreach (var d in found)
        {
            var box = new CheckBox
            {
                IsChecked = true,
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"Drop {d.DroppedOwner.Name}: {d.DroppedPath}   (same as {d.KeptOwner.Name}: {d.KeptPath})",
                },
            };
            box.IsCheckedChanged += (_, _) => UpdateSelectAll();
            _duplicates.Children.Add(box);
            _duplicateRows.Add((d, box));
        }
        UpdateSelectAll();
    }

    /// <summary>Select all follows the rows: checked when all are, clear when none are, a dash between.</summary>
    private void UpdateSelectAll()
    {
        if (_settingAll) return;
        _settingAll = true;
        _selectAll.IsVisible = _duplicateRows.Count > 0;
        int checkedRows = _duplicateRows.Count(r => r.Box.IsChecked == true);
        _selectAll.IsChecked = checkedRows == _duplicateRows.Count ? true : checkedRows == 0 ? false : null;
        _settingAll = false;
    }

    private async Task Confirm()
    {
        if (Reference is not { } reference) return;
        var picked = Picked();
        if (picked.Count == 0)
        {
            _duplicateInfo.Text = "Pick at least one character to link.";
            return;
        }
        if (!_scanned) Scan();
        var ineligible = _rows.Where(r => r.Box.IsChecked == true && r.Box.IsEnabled && r.Eligibility is { Eligible: false }).ToList();
        if (ineligible.Count > 0)
        {
            string message = "These characters do not have the same skeleton as " + reference.Name + ":\n\n" +
                             string.Join("\n", ineligible.Select(r => $"• {r.Character.Name}: {r.Eligibility!.Summary}")) +
                             "\n\nA shared animation only moves the controllers both characters have; the others " +
                             "stay in their reference pose, and joints with different rotations or proportions " +
                             "can pose badly. Link them anyway?";
            if (!await WarningDialog.Ask(this, "Characters do not match", message, "Link anyway")) return;
        }
        _answer = new SharedBankLinkRequest(reference, picked,
            _duplicateRows.Where(r => r.Box.IsChecked == true).Select(r => r.Duplicate).ToList());
        Close();
    }

    /// <summary>The link to make, or null if the user cancelled.</summary>
    public static async Task<SharedBankLinkRequest?> Ask(Window owner, IReadOnlyList<Character> characters, Character? current)
    {
        var dialog = new SharedBankDialog(characters, current);
        await dialog.ShowDialog(owner);
        return dialog._answer;
    }
}

/// <summary>A warning with a go-ahead button and Cancel; closing it is Cancel.</summary>
public sealed class WarningDialog : Window
{
    private bool _accepted;

    private WarningDialog(string title, string message, string accept, string decline)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var ok = new Button { Content = accept };
        var cancel = new Button { Content = decline, IsCancel = true, IsDefault = true };
        ok.Click += (_, _) =>
        {
            _accepted = true;
            Close();
        };
        cancel.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 14,
            MaxWidth = 520,
            Children =
            {
                new TextBlock { TextWrapping = TextWrapping.Wrap, Text = message },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
    }

    public static async Task<bool> Ask(Window owner, string title, string message, string accept, string decline = "Cancel")
    {
        var dialog = new WarningDialog(title, message, accept, decline);
        await dialog.ShowDialog(owner);
        return dialog._accepted;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The Space Switch window: a drop-down
/// of the scene's characters, the list of the picked character's spaces (the parent row first, the
/// space in force selected), and Apply. It opens from Keys > Change Space and from the TRANSFORM
/// window's Space row. The rows and what a chosen row means are <see cref="SpaceSwitchChoice"/>;
/// closing it or Escape answers nothing.
/// </summary>
public sealed class SpaceSwitchDialog : Window
{
    private bool _ok;
    private IReadOnlyList<SpaceSwitchItem> _items = [];
    private readonly ListBox _list;

    private SpaceSwitchDialog(IReadOnlyList<Character> characters, IReadOnlyList<Object3D> spaceable,
        Object3D? current, Object3D? target, string heading)
    {
        Title = "Space Switch";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var combo = new ComboBox
        {
            ItemsSource = characters.Select(c => c.Name ?? "?").ToList(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _list = new ListBox { Width = 320, MinHeight = 120, MaxHeight = 360 };
        void Fill()
        {
            Character? character = combo.SelectedIndex >= 0 && combo.SelectedIndex < characters.Count ? characters[combo.SelectedIndex] : null;
            _items = SpaceSwitchChoice.ItemsFor(character, spaceable);
            _list.ItemsSource = _items.Select(i => i.Label).ToList();
            _list.SelectedIndex = SpaceSwitchChoice.IndexOf(_items, current);
        }
        combo.SelectionChanged += (_, _) => Fill();
        combo.SelectedIndex = SpaceSwitchChoice.DefaultCharacter(characters, current, target);
        Fill();

        var ok = new Button { Content = "Apply" };
        var cancel = new Button { Content = "Cancel" };
        ToolTip.SetTip(ok, "Switch the selected controllers to the selected space at this key, keeping their pose.");
        ok.Click += (_, _) => { _ok = true; Close(); };
        cancel.Click += (_, _) => Close();
        _list.DoubleTapped += (_, _) => { _ok = true; Close(); };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = heading },
                new TextBlock { Text = "Character", FontSize = 11, Opacity = 0.7 },
                combo,
                new TextBlock { Text = "Spaces", FontSize = 11, Opacity = 0.7 },
                _list,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };

        var chrome = new DialogChrome(this, DialogId.ChangeSpace);
        chrome.Bind(
            (DialogStop.SpaceCharacter, combo),
            (DialogStop.SpaceList, _list),
            (DialogStop.SpaceOk, ok),
            (DialogStop.SpaceCancel, cancel));
    }

    /// <summary>
    /// The chosen space: <c>(true, null)</c> for the parent, <c>(true, space)</c>, or
    /// <c>(false, null)</c> when the user cancelled. <paramref name="current"/> is the space in
    /// force (null = the parent), selected when the window opens; <paramref name="target"/> picks
    /// the drop-down's character when there is no current space.
    /// </summary>
    public static async Task<(bool Chosen, Object3D? Space)> Ask(Window owner, IReadOnlyList<Character> characters,
        IReadOnlyList<Object3D> spaceable, Object3D? current, Object3D? target, string heading)
    {
        var dialog = new SpaceSwitchDialog(characters, spaceable, current, target, heading);
        await dialog.ShowDialog(owner);
        return SpaceSwitchChoice.Choose(dialog._items, dialog._list.SelectedIndex, dialog._ok);
    }
}

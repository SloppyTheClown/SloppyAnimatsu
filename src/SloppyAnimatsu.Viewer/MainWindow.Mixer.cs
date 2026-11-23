// The Mixer window: the stack list
// with its eye/type/name/opacity rows, the six-button toolbar (New, Copy, Merge, Extract, Audio,
// Delete) and the selection written to the animation's __SelectedStackList.
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SloppyAnimatsu.Core.Audio;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    // The Mixer's controls live for the window's lifetime: ApplyDockLayout may rebuild the panel
    // bodies (one per context), and the list and buttons are detached and re-parented like the
    // bank's (MainWindow.Panels.cs SharedControls/ReleaseDockBodies).
    private readonly ListBox _mixerList = new ListBox
    {
        SelectionMode = SelectionMode.Multiple,
        Margin = new Thickness(2.0),
        ItemsPanel = new FuncTemplate<Panel>(() => new StackPanel())
    };
    // The texture layer's row: texture keys belong
    // to the animation, not to a stack, so it is a fixed line under the stacks, not a stack row.
    private readonly TextBlock _mixerTextures = new TextBlock
    {
        FontSize = 10.0,
        Margin = new Thickness(6.0, 1.0, 4.0, 2.0),
        TextTrimming = TextTrimming.CharacterEllipsis,
        IsVisible = false
    };
    private Button _mixerNew = null!;
    private Button _mixerDuplicate = null!;
    private Button _mixerMerge = null!;
    private Button _mixerExtract = null!;
    private Button _mixerAudio = null!;
    private Button _mixerDelete = null!;
    private List<MixerItem> _mixerItems = [];
    private bool _mixerReady;

    /// <summary>Creates the Mixer's buttons and template once; the dock re-parents them on every relayout.</summary>
    private void EnsureMixerControls()
    {
        if (_mixerReady) return;
        _mixerReady = true;
        _mixerNew = MixerButton("New", "Create a stack from the selected objects", NewStack);
        _mixerDuplicate = MixerButton("Copy", "Duplicate the selected stacks", DuplicateSelectedStacks);
        _mixerMerge = MixerButton("Merge", "Bake the selected stacks into one (one stack merges with the one below it)", () => MergeSelectedStacks());
        _mixerExtract = MixerButton("Extract", "Move the selected objects' channels into a new stack", () => ExtractSelectedChannels());
        _mixerAudio = MixerButton("Audio…", "Add a sound file as a new audio stack", AddAudioStackFromFile);
        _mixerDelete = MixerButton("Delete", "Delete the selected stacks", DeleteSelectedStacks);
        _mixerList.SelectionChanged += delegate
        {
            MixerSelectionChanged();
        };
        // A row is picked by clicking it, and its opacity cell is the only thing that takes the
        // keyboard: a focusable row would steal the focus the inline field asks for.
        _mixerList.Styles.Add(new Avalonia.Styling.Style(x => Avalonia.Styling.Selectors.OfType<ListBoxItem>(x))
        {
            Setters =
            {
                new Avalonia.Styling.Setter(InputElement.FocusableProperty, false),
                new Avalonia.Styling.Setter(ListBoxItem.MinHeightProperty, 0.0)
            }
        });
        _mixerList.ItemTemplate = new FuncDataTemplate<MixerItem>((item, _) => MixerRowView(item), supportsRecycling: false);
    }

    /// <summary>One row of the Mixer: the stack and what the row shows.</summary>
    private sealed record MixerItem(AnimStack Stack, string Label, bool Enabled, bool Audio, float Opacity);

    private static Button MixerButton(string text, string tip, Action action)
    {
        Button button = new Button
        {
            Content = text,
            FontSize = 9.0,
            Height = 18.0,
            MinHeight = 0.0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(1.0),
            Padding = new Thickness(1.0, 0.0)
        };
        ToolTip.SetTip(button, tip);
        button.Click += delegate
        {
            action();
        };
        return button;
    }

    /// <summary>One Mixer row: the enabled eye, the stack-type glyph, the name and the opacity.</summary>
    private Control MixerRowView(MixerItem? item)
    {
        // A recycled row's content is cleared before it is rebuilt, which builds the template with
        // a null item (the same guard as BankRow and PickerRowView).
        if (item == null) return new TextBlock();
        Button eye = new Button
        {
            Content = item.Enabled ? "◉" : "○",
            Width = 20.0,
            Height = 18.0,
            Padding = new Thickness(0.0),
            MinWidth = 0.0,
            FontSize = 11.0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0.0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(eye, item.Enabled ? "Enabled: click to turn the stack off" : "Disabled: click to turn the stack on");
        eye.Click += delegate
        {
            if (_syncing) return;
            ToggleMixerStack(item.Stack, !item.Stack.IsEnabled);
        };
        TextBlock kind = new TextBlock
        {
            Text = item.Audio ? "♪" : "↗",
            FontSize = 12.0,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(kind, item.Audio ? "Audio stack" : "Pose stack");
        TextBlock name = new TextBlock
        {
            Text = item.Label,
            FontSize = 11.0,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var opacity = MixerOpacityEditor(item.Stack, item.Opacity);
        // The hammer next to the layer name: edit this layer's joints in the Picker.
        bool layerMode = _pickerLayerMode && ReferenceEquals(PickerLayer, item.Stack);
        Button hammer = new Button
        {
            Content = PickerHammer(10.0),
            Width = 16.0,
            Height = 16.0,
            MinWidth = 0.0,
            MinHeight = 0.0,
            Padding = new Thickness(0.0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0.0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Opacity = layerMode ? 0.95 : 0.35
        };
        ToolTip.SetTip(hammer, layerMode
            ? "The Picker is editing this layer's joints: click to leave"
            : "Edit this layer's joints in the Picker (list every controller and add or remove it)");
        hammer.Click += delegate
        {
            if (_syncing) return;
            if (layerMode) SetPickerLayerMode(false);
            else EditLayerJoints(item.Stack);
        };
        Grid row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("22,18,*,18,46"),
            Margin = new Thickness(2.0, 1.0)
        };
        Grid.SetColumn(eye, 0);
        row.Children.Add(eye);
        Grid.SetColumn(kind, 1);
        row.Children.Add(kind);
        Grid.SetColumn(name, 2);
        row.Children.Add(name);
        Grid.SetColumn(hammer, 3);
        row.Children.Add(hammer);
        Grid.SetColumn(opacity, 4);
        row.Children.Add(opacity);
        return row;
    }

    /// <summary>
    /// The row's opacity cell: the number the app prints, edited in place. A click on the number
    /// turns it into a field (the row's own value, so an edit reaches that stack and not the current
    /// one); Enter or leaving the field applies it as one undo step, Escape puts the number back.
    /// </summary>
    private Control MixerOpacityEditor(AnimStack stack, float opacity)
    {
        var value = new TextBlock
        {
            Text = opacity.ToString("0.00", CultureInfo.InvariantCulture),
            FontSize = 10.0,
            Opacity = 0.8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(value, "Opacity: click to edit");
        var field = new TextBox
        {
            Text = opacity.ToString("0.##", CultureInfo.InvariantCulture),
            FontSize = 10.0,
            Height = 18.0,
            MinHeight = 0.0,
            Width = 44.0,
            Padding = new Thickness(1.0, 0.0),
            TextAlignment = TextAlignment.Right,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsVisible = false
        };
        Grid cell = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        cell.Children.Add(value);
        cell.Children.Add(field);
        bool applying = false;
        bool focused = false;
        void Show()
        {
            if (applying) return;
            field.Text = stack.Opacity.ToString("0.##", CultureInfo.InvariantCulture);
            value.IsVisible = false;
            field.IsVisible = true;
            // The row takes the focus as the click finishes, so the field's own focus comes after it.
            Dispatcher.UIThread.Post(delegate
            {
                if (!field.IsVisible) return;
                field.SelectAll();
                field.Focus();
            }, DispatcherPriority.Input);
        }
        void Hide()
        {
            focused = false;
            field.IsVisible = false;
            value.IsVisible = true;
        }
        void Apply(bool commit)
        {
            if (applying || !field.IsVisible) return;
            if (commit
                && float.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float typed))
            {
                applying = true;
                var current = _mixerItems.FirstOrDefault(i => i.Stack == stack)?.Opacity ?? stack.Opacity;
                float wanted = Math.Clamp(typed, 0f, 1f);
                if (MathF.Abs(wanted - current) > 0.0001f)
                    RunBankEdit(delegate(PoseEditSession session)
                    {
                        session.SetStackOpacity(stack, wanted);
                    });
                applying = false;
            }
            Hide();
        }
        value.PointerPressed += delegate(object? _, PointerPressedEventArgs e)
        {
            if (_syncing || _drag != null) return;
            Show();
            e.Handled = true;
        };
        cell.PointerPressed += delegate(object? _, PointerPressedEventArgs e)
        {
            if (_syncing || _drag != null) return;
            Show();
            e.Handled = true;
        };
        field.GotFocus += delegate
        {
            focused = true;
        };
        field.KeyDown += delegate(object? _, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    Apply(commit: true);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Apply(commit: false);
                    e.Handled = true;
                    break;
                case Key.Tab:
                    Apply(commit: true);
                    break;
            }
        };
        field.LostFocus += delegate
        {
            if (focused) Apply(commit: true);
        };
        return cell;
    }

    /// <summary>Re-reads the stack rows and their selection after any edit or animation change.</summary>
    private void RefreshMixer()
    {
        if (_mixerList == null) return;
        Animation? animation = _animation;
        var rows = animation == null
            ? []
            : animation.Stacks
                .Select(s => new MixerItem(s, s.Name ?? "(unnamed)", s.IsEnabled, s is AudioStack, s.Opacity))
                .ToList();
        var textured = animation == null ? [] : Material.Keyed(animation);
        _mixerTextures.IsVisible = textured.Count > 0;
        _mixerTextures.Text = textured.Count == 0 ? "" :
            $"Textures layer: {textured.Sum(m => m.TextureKeys(animation!).Count)} keys on {string.Join(", ", textured.Select(m => m.Name))}";
        ToolTip.SetTip(_mixerTextures, "Texture keys of this animation, shared by every stack; edit them on the timeline's TX rows or in the Material panel.");
        _syncing = true;
        if (!_mixerItems.SequenceEqual(rows))
        {
            _mixerItems = rows;
            _mixerList.ItemsSource = rows;
        }

        List<AnimStack> selected = (animation?.SelectedStacks ?? []).ToList();
        if (selected.Count == 0 && _timeline.ActiveStack is { } active && animation != null && active.Animation == animation)
            selected = [active];
        _mixerList.SelectedItems?.Clear();
        foreach (var row in rows)
            if (selected.Contains(row.Stack)) _mixerList.SelectedItems?.Add(row);
        _syncing = false;

        bool hasAnimation = animation != null;
        int count = SelectedStacks().Count;
        _mixerNew.IsEnabled = hasAnimation;
        _mixerAudio.IsEnabled = hasAnimation;
        _mixerDuplicate.IsEnabled = count > 0;
        _mixerDelete.IsEnabled = count > 0;
        _mixerMerge.IsEnabled = count > 0;
        _mixerExtract.IsEnabled = count > 0;
    }

    /// <summary>The stacks the Mixer's actions work on: the selected rows, else the timeline's row.</summary>
    private List<AnimStack> SelectedStacks()
    {
        if (_animation == null) return [];
        var chosen = _mixerList?.SelectedItems?.OfType<MixerItem>().Select(i => i.Stack).ToList() ?? [];
        if (chosen.Count == 0 && _timeline.ActiveStack is { } active && active.Animation == _animation) chosen.Add(active);
        return chosen;
    }

    private void MixerSelectionChanged()
    {
        if (_syncing || _animation == null || _timeline.IsDragging) return;
        var stacks = _mixerList.SelectedItems?.OfType<MixerItem>().Select(i => i.Stack).ToList() ?? [];
        _animation.SelectStacks(stacks);
        if (stacks.Count == 1)
        {
            Session?.SetCurrentStack(stacks[0]);
            _timeline.Select(stacks[0], Array.Empty<int>());
        }
        else
        {
            UpdateKeyCommands();
        }
    }

    private void ToggleMixerStack(AnimStack stack, bool enabled)
    {
        if ((stack.IsEnabled) == enabled) return;
        RunBankEdit(delegate(PoseEditSession session)
        {
            session.ToggleStackFlag(stack, AnimationBankEditor.EnabledFlag);
        });
    }

    private void DuplicateSelectedStacks()
    {
        Animation? animation = _animation;
        var stacks = SelectedStacks();
        PoseEditSession? session = Session;
        if (animation == null || session == null || stacks.Count == 0) return;
        var copies = new List<AnimStack>();
        RunBankEdit(delegate(PoseEditSession s)
        {
            foreach (var stack in stacks) copies.Add(s.DuplicateStack(stack));
        }, () => animation);
        if (copies.Count > 0)
        {
            animation.SelectStacks(copies);
            _timeline.Select(copies[^1], Array.Empty<int>());
        }
    }

    private void DeleteSelectedStacks()
    {
        var stacks = SelectedStacks();
        if (stacks.Count == 0) return;
        int deleted = 0;
        RunBankEdit(delegate(PoseEditSession s)
        {
            foreach (var stack in stacks) if (s.DeleteStack(stack)) deleted++;
        });
        if (deleted == 0) _status.Text = "An animation keeps at least one stack.";
        else _timeline.Select(_animation?.CurrentStack, Array.Empty<int>());
    }

    /// <summary>
    /// The Mixer's Merge button and the <c>--bank merge</c> scripted action. Returns whether a stack
    /// was baked, so a scripted run stops instead of saving a scene the merge never touched.
    /// </summary>
    private bool MergeSelectedStacks()
    {
        Animation? animation = _animation;
        var stacks = SelectedStacks();
        PoseEditSession? session = Session;
        if (animation == null || session == null || stacks.Count == 0) return false;
        AnimStack? merged = null;
        RunBankEdit(delegate(PoseEditSession s)
        {
            merged = s.MergeStacks(animation, stacks);
        });
        if (merged == null) return false;
        _timeline.Select(merged, Array.Empty<int>());
        _status.Text = $"Merged {stacks.Count} stack(s) into '{merged.Name}'.";
        return true;
    }

    private bool ExtractSelectedChannels()
    {
        AnimStack? stack = _timeline.ActiveStack ?? SelectedStacks().FirstOrDefault();
        PoseEditSession? session = Session;
        if (stack == null || session == null) return false;
        var targets = _selectedAll.Where(o => stack.TrackFor(o) != null).ToList();
        if (targets.Count == 0)
        {
            _status.Text = "Select the objects whose tracks to extract: their channels move into the new stack.";
            return false;
        }
        var tracks = stack.Tracks.Where(t => t.Target != null && targets.Contains(t.Target)).ToList();
        AnimChannels channels = 0;
        foreach (var track in tracks) channels |= track.Channels;
        AnimStack? created = null;
        RunBankEdit(delegate(PoseEditSession s)
        {
            created = s.ExtractChannels(stack, channels, tracks);
        });
        if (created == null) return false;
        _timeline.Select(created, Array.Empty<int>());
        _status.Text = $"Extracted {tracks.Count} track(s) into '{created.Name}'.";
        return true;
    }

    private async void AddAudioStackFromFile()
    {
        Animation? animation = _animation;
        if (animation == null || Session == null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add an audio stack",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Sound") { Patterns = ["*.wav"] }]
        });
        string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path != null) AddAudioStack(path);
    }

    /// <summary>
    /// Adds <paramref name="path"/> as an audio stack (the Audio button and <c>--bank audio:&lt;file&gt;</c>).
    /// Returns false when the file cannot be read or is not a PCM WAV, after saying why in the status line.
    /// </summary>
    private bool AddAudioStack(string path)
    {
        Animation? animation = _animation;
        PoseEditSession? session = Session;
        if (animation == null || session == null) return false;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = "Could not read " + Path.GetFileName(path) + ": " + ex.Message;
            return false;
        }
        WaveFile? sound = WaveFile.TryDecode(bytes);
        if (sound == null)
        {
            _status.Text = "That file is not a PCM WAV sound.";
            return false;
        }
        AudioStack? created = null;
        RunBankEdit(delegate(PoseEditSession s)
        {
            created = s.AddAudioStack(animation, path, bytes, sound.Duration);
        });
        if (created == null) return false;
        _timeline.Select(created, Array.Empty<int>());
        _status.Text = $"Added '{Path.GetFileName(path)}' as audio stack '{created.Name}' ({sound.Duration:0.###} s).";
        return true;
    }
}

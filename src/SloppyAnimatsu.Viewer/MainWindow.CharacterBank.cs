// The CHAR BANK (CCharacterBankDialog): what the left bank panel shows in the Rigging context, in
// place of the Anim Bank. Create Character / Create Ref Pose / Delete Ref Pose at the top, then the
// scene's characters, each with its pose rows and Paint & Check. The row decides what the Rigging
// tools do:
// - Bind Pose / Build Pose: TRANSFORM edits and gizmo drags write that pose; Bind Skin, painting and
//   The skin-weight tint are off (the app hides the weight decorator in Bind Pose only).
// - Ref Pose: rest edits write the custom reference pose; painting works.
// - Paint & Check (CurrentPose null): Bind Skin, painting and the tint work; rest edits are refused.
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    /// <summary>Undo steps of the CHAR BANK start with this, so undo reloads the rest poses.</summary>
    private const string CharBankStep = "CHAR BANK: ";

    private DockPanel? _charBankPanel;
    private StackPanel? _charBankList;
    private Button? _createCharacterButton;
    private Button? _createRefPoseButton;
    private Button? _deleteRefPoseButton;
    /// <summary>Names of the characters whose rows the CHAR BANK has collapsed.</summary>
    private readonly HashSet<string> _charBankCollapsed = new();
    /// <summary>The bank panel's header text, renamed CHAR BANK / ANIM BANK with the context.</summary>
    private TextBlock? _bankTitle;

    private static readonly IBrush CurrentRowBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x3C, 0x82, 0xC8));
    private static readonly IBrush CurrentCharacterBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));

    /// <summary>The bank panel shows the CHAR BANK: a scene is open in the Rigging context.</summary>
    private bool CharBankShown => _pose != null && _pose.Model.Scene.Context == InterfaceContext.Rigging;

    private Control CharBankBody()
    {
        if (_charBankPanel != null) return _charBankPanel;
        _createCharacterButton = CharBankButton("Create Character", PanelIcons.CreateCharacter(), "Create Character: add a new, empty character to the scene.", CreateCharacter);
        _createRefPoseButton = CharBankButton("Create Ref Pose", PanelIcons.CreateRefPose(),
            "Create Ref Pose: a reference pose of its own, built from the character's current state (pose it in Paint & Check first). The animations follow it after the REFERENCE POSE dialog.",
            CreateRefPose);
        _deleteRefPoseButton = CharBankButton("Delete Ref Pose", PanelIcons.DeleteRefPose(), "Delete Ref Pose: the Bind Pose acts as the reference pose again.", DeleteRefPose);
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), Margin = new Thickness(0, 0, 0, 4) };
        Grid.SetColumn(_createRefPoseButton, 1);
        Grid.SetColumn(_deleteRefPoseButton, 2);
        buttons.Children.Add(_createCharacterButton);
        buttons.Children.Add(_createRefPoseButton);
        buttons.Children.Add(_deleteRefPoseButton);
        DockPanel.SetDock(buttons, Dock.Top);
        _charBankList = new StackPanel { Orientation = Orientation.Vertical };
        _charBankPanel = new DockPanel { LastChildFill = true, IsVisible = false };
        _charBankPanel.Children.Add(buttons);
        _charBankPanel.Children.Add(new ScrollViewer
        {
            Content = _charBankList,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        });
        return _charBankPanel;
    }

    private static Button CharBankButton(string text, Control icon, string tip, Action action)
    {
        var button = new Button
        {
            MinHeight = 0.0,
            Height = 30.0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1.0),
            Padding = new Thickness(1.0, 0.0),
        };
        ToolTip.SetTip(button, tip);
        // 20 px: the drawing scaled up to fill the 30 px high button.
        PanelIcons.Apply(button, text, icon, 20.0);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>Shows the CHAR BANK or the Anim Bank for the context and rebuilds the character rows.</summary>
    private void RefreshCharBank()
    {
        bool shown = CharBankShown;
        if (_bankTitle != null) _bankTitle.Text = shown ? "CHAR BANK" : "ANIM BANK";
        _animations.IsVisible = !shown;
        UpdateSharedBankMarks();
        if (_charBankPanel == null || _charBankList == null) return;
        _charBankPanel.IsVisible = shown;
        if (!shown) return;
        _charBankList.Children.Clear();
        var characters = _pose!.Characters;
        foreach (var character in characters)
        {
            bool active = ReferenceEquals(character, _character);
            string key = character.Name ?? "";
            bool collapsed = _charBankCollapsed.Contains(key);
            var header = new Button
            {
                Content = new TextBlock { Text = character.Name ?? "(unnamed)", FontWeight = FontWeight.DemiBold, FontSize = 10.0, TextTrimming = TextTrimming.CharacterEllipsis },
                Background = active ? CurrentCharacterBrush : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                MinHeight = 0, Height = 20, Padding = new Thickness(4, 0), Margin = new Thickness(0, 2, 0, 0),
            };
            ToolTip.SetTip(header, "Make this the character the Rigging tools work on.");
            var picked = character;
            header.Click += (_, _) => PickBankCharacter(picked);
            var arrow = new Button
            {
                Content = new TextBlock { Text = collapsed ? "▶" : "▼", FontSize = 8.0 },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                MinHeight = 0, MinWidth = 0, Width = 16, Height = 20, Padding = new Thickness(0),
                Margin = new Thickness(0, 2, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(arrow, collapsed ? "Expand this character's rows." : "Collapse this character's rows.");
            arrow.Click += (_, _) =>
            {
                if (!_charBankCollapsed.Remove(key)) _charBankCollapsed.Add(key);
                RefreshCharBank();
            };
            var headerRow = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(arrow, Dock.Left);
            headerRow.Children.Add(arrow);
            headerRow.Children.Add(header);
            _charBankList.Children.Add(headerRow);
            if (collapsed) continue;
            foreach (var row in CharacterBank.Rows(character))
            {
                string glyph = row.IsCurrent ? "◉ " : "○ ";
                string suffix = row.ActsAsRefPose && row.Kind == CharacterBankRowKind.BindPose ? "  (Ref Pose)" : "";
                var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
                label.Children.Add(new TextBlock { Text = glyph, FontSize = 10.0, VerticalAlignment = VerticalAlignment.Center });
                foreach (var icon in RowIcons(row)) label.Children.Add(icon);
                label.Children.Add(new TextBlock { Text = row.Label + suffix, FontSize = 10.0, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var button = new Button
                {
                    Content = label,
                    Background = row.IsCurrent ? CurrentRowBrush : Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    MinHeight = 0, Height = 19, Padding = new Thickness(18, 0, 4, 0),
                };
                ToolTip.SetTip(button, RowTip(row));
                var chosen = row;
                button.Click += (_, _) => SelectBankRow(picked, chosen);
                _charBankList.Children.Add(button);
            }
        }
        var current = _character;
        if (_createCharacterButton != null) DisabledHint.SetEnabled(_createCharacterButton, _pose != null, "an open scene");
        if (_createRefPoseButton != null) DisabledHint.SetEnabled(_createRefPoseButton, current != null && CharacterBank.CanCreateRefPose(current), "a bound character whose bind pose is still its reference pose");
        if (_deleteRefPoseButton != null) DisabledHint.SetEnabled(_deleteRefPoseButton, current != null && CharacterBank.CanDeleteRefPose(current), "a bound character with a reference pose of its own");
    }

    /// <summary>
    /// Whether the current CHAR BANK row forbids moving, rotating or scaling the rig: Paint &amp; Check
    /// (SloppyAnimatsu does not keep a runtime pose there) and a Bind Pose row (the
    /// pose the skin was bound in stays fixed; edit the Build Pose before binding, or a Ref Pose).
    /// </summary>
    private bool InPaintAndCheck =>
        _animation == null && _character != null && _pose != null
        && CharacterBank.CurrentRow(_character).Kind == CharacterBankRowKind.PaintAndCheck;

    private bool BankRowRefusesRestEdit(out string reason)
    {
        reason = "";
        if (_character == null) return false;
        switch (CharacterBank.CurrentRow(_character).Kind)
        {
            case CharacterBankRowKind.PaintAndCheck:
                reason = "Paint & Check: pick the Build Pose or a Ref Pose in the CHAR BANK to edit the rest pose.";
                return true;
            case CharacterBankRowKind.BindPose:
                reason = "Bind Pose: joints cannot be moved, rotated or scaled here. Create a Ref Pose in the CHAR BANK to edit the rest pose.";
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The row's icons: a red bone on a Bind Pose, a
    /// blue figure on whichever row acts as the reference pose, a yellow paintbrush on Paint & Check.
    /// </summary>
    private static IEnumerable<Control> RowIcons(CharacterBankRow row)
    {
        if (row.Kind == CharacterBankRowKind.BindPose) yield return BoneIcon();
        if (row.ActsAsRefPose || row.Kind == CharacterBankRowKind.RefPose) yield return RefPoseIcon();
        if (row.Kind == CharacterBankRowKind.PaintAndCheck) yield return PaintbrushIcon();
    }

    private static readonly IBrush RefPoseIconBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x9C, 0xF0));
    private static readonly IBrush BoneIconBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x48, 0x40));
    private static readonly IBrush BrushIconBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xC8, 0x30));
    private static readonly IBrush BrushHandleBrush = new SolidColorBrush(Color.FromRgb(0xB8, 0x86, 0x3C));

    private static Control IconPath(string data, IBrush fill) => new Avalonia.Controls.Shapes.Path
    {
        Data = Geometry.Parse(data),
        Fill = fill,
        Width = 12, Height = 12,
        Stretch = Stretch.None,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A standing figure: head, body, arms and legs, in a 12 px box.</summary>
    private static Control RefPoseIcon() => IconPath(
        "M6,0.5 A1.8,1.8 0 1 1 5.99,0.5 Z M3,4 L9,4 L9,5.2 L7.2,5.2 L7.2,7.5 L8,11.5 L6.8,11.5 L6,8 L5.2,11.5 L4,11.5 L4.8,7.5 L4.8,5.2 L3,5.2 Z",
        RefPoseIconBrush);

    /// <summary>A diagonal bone: a shaft with two knuckles at each end.</summary>
    private static Control BoneIcon() => IconPath(
        "M3.2,4.6 L7.4,8.8 L8.4,7.8 L4.2,3.6 Z " +
        "M3,1 A1.3,1.3 0 1 1 2.99,1 Z M1,3 A1.3,1.3 0 1 1 0.99,3 Z " +
        "M10,8 A1.3,1.3 0 1 1 9.99,8 Z M8,10 A1.3,1.3 0 1 1 7.99,10 Z",
        BoneIconBrush);

    /// <summary>A paintbrush leaning to the left: handle, ferrule and a pointed tip.</summary>
    private static Control PaintbrushIcon()
    {
        var canvas = new Canvas { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M11.5,0.5 L12,1 L6.5,6.8 L5.2,5.5 Z"), Fill = BrushHandleBrush });
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M4.8,5.4 L6.6,7.2 C6,9.5 3.5,11.5 0.5,11.5 C1.2,10.5 1.2,8.6 2.6,7 C3.2,6.3 4,5.8 4.8,5.4 Z"), Fill = BrushIconBrush });
        return canvas;
    }

    private static string RowTip(CharacterBankRow row) => row.Kind switch
    {
        CharacterBankRowKind.BuildPose => "Build Pose: the pose the rig is built in until a mesh is bound. Rest edits write it; the skin weights are hidden.",
        CharacterBankRowKind.BindPose => "Bind Pose: rest edits write the pose the meshes are bound in; the skin weights are hidden and Bind Skin is off."
            + (row.ActsAsRefPose ? " It also acts as the reference pose." : ""),
        CharacterBankRowKind.RefPose => "Ref Pose: rest edits write the reference pose the animations are built on; painting works.",
        _ => "Paint & Check: bind, paint and check the skin by posing the rig. Rest edits are off here.",
    };

    private void PickBankCharacter(Character character)
    {
        if (ReferenceEquals(character, _character)) return;
        SelectCharacter(character, "(rest pose)", null);
        RefreshCharBank();
    }

    /// <summary>Makes a CHAR BANK row the character's mode, as one undo step.</summary>
    private void SelectBankRow(Character character, CharacterBankRow row)
    {
        if (!ReferenceEquals(character, _character)) SelectCharacter(character, "(rest pose)", null);
        if (row.IsCurrent && ReferenceEquals(character.CurrentPose, row.Pose))
        {
            _status.Text = row.Label + " is already current.";
            return;
        }
        // Paint & Check's pose stays on the character while another row is current (and is saved
        // with the scene); the step keeps a copy so Undo puts back exactly what was there.
        var checkPose = _pose?.Evaluator(character) is { } evaluator ? (evaluator, evaluator.CheckPoseValues()) : default;
        RunRigEdit(CharBankStep + row.Label, _ =>
        {
            CharacterBank.Select(character, row);
            return null;
        }, "pick a CHAR BANK row");
        if (!_rigEditFailed && checkPose.evaluator != null && checkPose.Item2 != null
            && _history.NextUndo is { } step && step.Description == CharBankStep + row.Label)
            step.Before.CheckPose = checkPose;
        // Paint & Check opens on a custom reference pose, not the bind pose, unless it kept a pose.
        if (!_rigEditFailed && row.Kind == CharacterBankRowKind.PaintAndCheck && _pose != null
            && !_pose.Evaluator(character).HasCheckPose
            && CharacterBank.CanDeleteRefPose(character) && character.RefPose is { } reference)
        {
            _pose.Evaluator(character).StartCheckPoseFrom(reference);
            Apply();
        }
        if (!_rigEditFailed) _status.Text = CharBankModeStatus(character);
        AfterCharBankChange();
    }

    private static string CharBankModeStatus(Character character) => CharacterBank.CurrentRow(character).Kind switch
    {
        CharacterBankRowKind.PaintAndCheck => "Paint & Check: Bind Skin, painting and posing to check the skin.",
        CharacterBankRowKind.RefPose => "Ref Pose: rest edits write the reference pose.",
        _ => CharacterBank.CurrentRow(character).Label + ": rest edits write this pose; the skin weights are hidden.",
    };

    private void CreateCharacter()
    {
        if (_pose == null || _document == null)
        {
            _status.Text = "Open a scene first.";
            return;
        }
        if (!CharBankShown)
        {
            _status.Text = "Switch to the Rigging context to create a character.";
            return;
        }
        var model = _pose.Model;
        var owner = _character ?? model.Scene.Characters.FirstOrDefault();
        Character created;
        EditSnapshot? before = owner != null ? EditSnapshot.TakeRig(owner, model.Scene) : null;
        try
        {
            created = CharacterBank.CreateCharacter(model);
        }
        catch (InvalidOperationException ex)
        {
            _status.Text = "Create Character failed: " + ex.Message;
            return;
        }
        before ??= EditSnapshot.TakeRig(created, model.Scene);
        _history.Push(new EditRecord(CharBankStep + "Create Character", before, EditSnapshot.TakeRig(created, model.Scene)));
        _document.Document.Dirty = true;
        ShowDocument(_document.Document);
        SelectCharacter(created, "(rest pose)", null);
        _dirty = true;
        UpdateTitle();
        _outliner.Build(model.Scene);
        AfterCharBankChange();
        _status.Text = "Created " + created.Name + ".";
    }

    private void CreateRefPose()
    {
        var character = _character;
        if (character == null || !CharacterBank.CanCreateRefPose(character))
        {
            _status.Text = "Create Ref Pose needs a bound character whose Bind Pose acts as the reference pose.";
            return;
        }
        // In Paint & Check the new reference pose is exactly the pose shown there, IK chains
        // included, and Paint & Check stays current with that pose.
        var check = InPaintAndCheck && _pose != null ? _pose.Evaluator(character) : null;
        bool fromCheck = check != null && check.CheckPoseChanges().Count > 0;
        RunRigEdit(CharBankStep + "Create Ref Pose", _ =>
        {
            var created = fromCheck ? CharacterBank.CreateRefPoseFromCheckPose(character, check!) : CharacterBank.CreateRefPose(character);
            if (created == null)
                throw new InvalidOperationException("the scene's schema cannot hold a reference pose.");
            return null;
        }, "create a reference pose");
        if (!_rigEditFailed)
        {
            _status.Text = fromCheck
                ? "Created a Ref Pose for " + character.Name + " from the Paint & Check pose; pick the Ref Pose row to edit it."
                : "Created a Ref Pose for " + character.Name + "; rest edits now write it.";
        }
        AfterCharBankChange();
    }

    private void DeleteRefPose()
    {
        var character = _character;
        if (character == null || !CharacterBank.CanDeleteRefPose(character))
        {
            _status.Text = "Delete Ref Pose needs a bound character with a reference pose of its own.";
            return;
        }
        RunRigEdit(CharBankStep + "Delete Ref Pose", _ => CharacterBank.DeleteRefPose(character) ? null
            : throw new InvalidOperationException("the reference pose could not be removed."), "delete the reference pose");
        if (!_rigEditFailed) _status.Text = "Deleted the Ref Pose of " + character.Name + "; the Bind Pose acts as the reference pose again.";
        AfterCharBankChange();
    }

    /// <summary>After a CHAR BANK change: the rows, the menu (Bind Skin) and the tools that follow the mode.</summary>
    private void AfterCharBankChange()
    {
        RefreshCharBank();
        RefreshMenu();
        RefreshRigAtelier();
        UpdateSelection(forceSpinner: true);
        Apply();
    }

    /// <summary>
    /// Undo/redo of a CHAR BANK step: a created character comes and goes (the pose is rebuilt, as on
    /// opening), and a changed <c>CurrentPose</c>/<c>RefPose</c> needs the rest poses reloaded.
    /// </summary>
    private void AfterCharBankHistoryStep(EditRecord? step)
    {
        if (_pose == null || step?.Description.StartsWith(CharBankStep, StringComparison.Ordinal) != true) return;
        if (_document != null && _pose.Characters.Count != _pose.Model.Scene.Characters.Count())
        {
            string? keep = _character?.Name;
            ShowDocument(_document.Document);
            var again = _pose.Characters.FirstOrDefault(c => c.Name == keep) ?? _pose.Characters.FirstOrDefault();
            if (again != null && !ReferenceEquals(again, _character)) SelectCharacter(again, "(rest pose)", null);
            _outliner.Build(_pose.Model.Scene);
        }
        else
        {
            foreach (var character in _pose.Characters) _pose.ReloadRestPose(character);
        }
        AfterCharBankChange();
    }

    /// <summary>
    /// Weight edits (painter strokes, SKIN WORKSHOP) are refused while a Bind or Build Pose row is
    /// current: the app hides the skin decorator there, so the weights are worked on in Paint &amp;
    /// Check or a Ref Pose. Sets the status line when it refuses.
    /// </summary>
    private bool SkinEditsBlockedByBindPose()
    {
        if (_animation != null || _character == null || !CharacterBank.IsBindPoseCurrent(_character)) return false;
        _status.Text = "The skin weights are hidden in " + CharacterBank.CurrentRow(_character).Label
            + ": pick Paint & Check in the CHAR BANK to paint.";
        return true;
    }
}

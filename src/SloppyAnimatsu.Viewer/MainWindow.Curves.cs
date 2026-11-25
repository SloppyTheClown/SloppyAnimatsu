// The curveboard panel: building it, running a curve edit, refreshing the display and the info line,
// and the window-level Undo/Redo (which restore a whole EditSnapshot).
//
// Curve edits go through RunCurveEdit, the curve counterpart of RunKeyEdit.
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SloppyAnimatsu.Core;
using SloppyAnimatsu.Core.Aknm;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    private Control CurvePanel()
    {
        string[] array = new string[13]
        {
            "TX", "TY", "TZ", "RX", "RY", "RZ", "SX", "SY", "SZ", "Pole",
            "Roll", "Twist", "Bank"
        };
        WrapPanel wrapPanel = new WrapPanel
        {
            Margin = new Thickness(0.0, 0.0, 0.0, 4.0)
        };
        for (int i = 0; i < 13; i++)
        {
            int channel = i;
            ToggleButton toggle = new ToggleButton
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4.0,
                    Children = 
                    {
                        (Control)new Border
                        {
                            Width = 8.0,
                            Height = 8.0,
                            VerticalAlignment = VerticalAlignment.Center,
                            Background = new SolidColorBrush(CurveboardView.ChannelColors[i])
                        },
                        (Control)new TextBlock
                        {
                            Text = array[i]
                        }
                    }
                },
                IsChecked = true,
                Padding = new Thickness(6.0, 2.0),
                Margin = new Thickness(0.0, 0.0, 2.0, 2.0)
            };
            ToolTip.SetTip(toggle, Curves.ChannelNames[i]);
            toggle.IsCheckedChanged += delegate
            {
                AnimChannels animChannels = Curves.Bit(channel);
                _curveboard.Displayed = ((toggle.IsChecked == true) ? (_curveboard.Displayed | animChannels) : (_curveboard.Displayed & ~animChannels));
                _curveboard.Refresh();
            };
            wrapPanel.Children.Add(toggle);
        }
        wrapPanel.Children.Add(new Border
        {
            Width = 12.0
        });
        uint[] settable = TangentTypes.Settable;
        foreach (uint type in settable)
        {
            Button button = new Button
            {
                Content = TangentTypes.Name(type),
                Padding = new Thickness(8.0, 2.0),
                Margin = new Thickness(0.0, 0.0, 2.0, 2.0)
            };
            button.Click += delegate
            {
                RunCurveEdit((PoseEditSession session, AnimStack stack, IReadOnlyCollection<CurveKeySelection> selection) => session.ChangeTangentType(stack, selection, type, _curveboard.Displayed));
            };
            _curveButtons.Add(button);
            wrapPanel.Children.Add(button);
        }
        wrapPanel.Children.Add(new Border
        {
            Width = 12.0
        });
        bool[] array2 = new bool[2] { true, false };
        foreach (bool unified in array2)
        {
            Button button2 = new Button
            {
                Content = (unified ? "Unify" : "Break"),
                Padding = new Thickness(8.0, 2.0),
                Margin = new Thickness(0.0, 0.0, 2.0, 2.0)
            };
            button2.Click += delegate
            {
                RunCurveEdit((PoseEditSession session, AnimStack stack, IReadOnlyCollection<CurveKeySelection> selection) => session.SetTangentsUnified(stack, selection, unified, _curveboard.Displayed));
            };
            _curveButtons.Add(button2);
            wrapPanel.Children.Add(button2);
        }
        Button button3 = new Button
        {
            Content = "Fit (F)",
            Padding = new Thickness(8.0, 2.0),
            Margin = new Thickness(12.0, 0.0, 8.0, 2.0)
        };
        button3.Click += delegate
        {
            _curveboard.Fit();
        };
        wrapPanel.Children.Add(button3);
        wrapPanel.Children.Add(_curveInfo);
        DockPanel dockPanel = new DockPanel
        {
            Margin = new Thickness(10.0, 6.0, 10.0, 0.0),
            Height = 280.0
        };
        DockPanel.SetDock(wrapPanel, Dock.Top);
        dockPanel.Children.Add(wrapPanel);
        dockPanel.Children.Add(_curveboard);
        return dockPanel;
    }

    /// <summary>
    /// Runs a curveboard edit against the selected curve keys as one undo step. Ignored while a curve
    /// drag is in progress or when no key is selected.
    /// </summary>
    private void RunCurveEdit(Func<PoseEditSession, AnimStack, IReadOnlyCollection<CurveKeySelection>, int> edit)
    {
        if (_curveDrag != null)
        {
            return;
        }
        PoseEditSession session = Session;
        if (session == null)
        {
            return;
        }
        AnimStack stack = _curveboard.Stack;
        if (stack == null)
        {
            return;
        }
        IReadOnlyCollection<CurveKeySelection> selection = _curveboard.Selection;
        if (selection.Count == 0)
        {
            _status.Text = "Select keys on the Curveboard first.";
            return;
        }
        RunKeyEdit(delegate
        {
            edit(session, stack, selection);
        }, "Curve edit failed: ");
    }

    private void UpdateCurveboard()
    {
        Control curvePanel = _curvePanel;
        if (curvePanel != null && curvePanel.IsVisible && _curveDrag == null)
        {
            AnimStack animStack = _timeline.ActiveStack ?? _animation?.CurrentStack;
            _curveboard.Evaluator = ((_pose != null && _character != null) ? _pose.Evaluator(_character) : null);
            List<AnimTrack> tracks = ((animStack == null) ? new List<AnimTrack>() : _selectedAll.Select(animStack.TrackFor).OfType<AnimTrack>().ToList());
            _curveboard.Show(animStack, tracks);
            UpdateCurveInfo();
        }
    }

    private void UpdateCurveInfo()
    {
        IReadOnlyCollection<CurveKeySelection> selection = _curveboard.Selection;
        foreach (Button curveButton in _curveButtons)
        {
            DisabledHint.SetEnabled(curveButton, selection.Count > 0, "selected keys in the Curveboard");
        }
        if (selection.Count != 1)
        {
            _curveInfo.Text = ((selection.Count == 0) ? "" : $"{selection.Count} keys");
            return;
        }
        CurveKeySelection curveKeySelection = selection.First();
        int num = Curves.Channels(curveKeySelection.Any).FirstOrDefault();
        IReadOnlyList<TangentMode[]> tangentModes = curveKeySelection.Track.TangentModes;
        string text = ((curveKeySelection.Key < tangentModes.Count && num < tangentModes[curveKeySelection.Key].Length) ? TangentTypes.Describe(tangentModes[curveKeySelection.Key][num]) : "");
        float? num2 = Curves.KeyValue(curveKeySelection.Track.Keys.ElementAtOrDefault(curveKeySelection.Key), num);
        _curveInfo.Text = $"{curveKeySelection.Track.Target?.Name} key {curveKeySelection.Key}, {Curves.ChannelNames[num]}" + ((!num2.HasValue) ? "" : (" = " + num2.GetValueOrDefault().ToString("0.###", CultureInfo.InvariantCulture))) + ": " + text;
    }

    private void Undo()
    {
        if (_drag != null || StepSceneDelete(undo: true))
        {
            return;
        }
        PoseEditSession session = Session;
        if (session == null)
        {
            _status.Text = "Nothing to undo.";
            return;
        }
        EditRecord nextUndo = _history.NextUndo;
        if (nextUndo == null || !session.Undo())
        {
            _status.Text = "Nothing to undo.";
            return;
        }
        _status.Text = "Undid: " + nextUndo.Description;
        AfterHistoryStep(nextUndo);
        _dirty = true;
        UpdateTitle();
        AfterKeyChange();
        if (RigStructureChanged())
        {
            RefreshRigStructure();
        }
        else
        {
            RebuildSkinJoints();
        }
        MemoryLog.Mark(_status.Text, _history.Count);
    }

    private void Redo()
    {
        if (_drag != null || StepSceneDelete(undo: false))
        {
            return;
        }
        PoseEditSession session = Session;
        if (session == null)
        {
            _status.Text = "Nothing to redo.";
            return;
        }
        EditRecord nextRedo = _history.NextRedo;
        if (nextRedo == null || !session.Redo())
        {
            _status.Text = "Nothing to redo.";
            return;
        }
        _status.Text = "Redid: " + nextRedo.Description;
        AfterHistoryStep(nextRedo);
        _dirty = true;
        UpdateTitle();
        AfterKeyChange();
        if (RigStructureChanged())
        {
            RefreshRigStructure();
        }
        else
        {
            RebuildSkinJoints();
        }
    }
}

// Blendshape editing, panel and sight: the controller rows and the quadrant pad, the viewport zone
// hit-test and overlay, and the Picker face cells.
//
// A blendshape key is a pair of values (X/Y of the active direction). The sight and the Picker write
// through the same ApplyBlendshapeValue/ApplyBlendshapeFace helpers (MainWindow.Blendshapes.Editing.cs)
// so a pad click and a face click cannot drift apart.
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
    private void BuildBlendshapeControllerRows(BlendshapeSession session)
    {
        if (_blendshapeControllers == null)
        {
            return;
        }
        _blendshapeFields.Clear();
        _blendshapeControllers.Children.Clear();
        foreach (BlendshapeControllerRow controllerRow in session.ControllerRows)
        {
            BlendshapeControllerRow row = controllerRow;
            NumericUpDown x = new NumericUpDown
            {
                Width = 114.0,
                Increment = 0.05m,
                FormatString = "0.00",
                Minimum = -1m,
                Maximum = 1m
            };
            NumericUpDown y = new NumericUpDown
            {
                Width = 114.0,
                Increment = 0.05m,
                FormatString = "0.00",
                Minimum = -1m,
                Maximum = 1m
            };
            TextBlock textBlock = new TextBlock
            {
                FontSize = 10.0,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            };
            x.ValueChanged += delegate
            {
                Commit();
            };
            y.ValueChanged += delegate
            {
                Commit();
            };
            StackPanel stackPanel = new StackPanel
            {
                Spacing = 2.0
            };
            stackPanel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4.0,
                Children = 
                {
                    (Control)new TextBlock
                    {
                        Text = "X",
                        Width = 10.0,
                        FontSize = 11.0,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    (Control)x
                }
            });
            stackPanel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4.0,
                Children = 
                {
                    (Control)new TextBlock
                    {
                        Text = "Y",
                        Width = 10.0,
                        FontSize = 11.0,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    (Control)y
                }
            });
            StackPanel stackPanel2 = new StackPanel
            {
                Spacing = 2.0,
                Children = 
                {
                    (Control)new TextBlock
                    {
                        Text = row.Controller.Name + " — " + row.MeshName,
                        FontWeight = FontWeight.DemiBold,
                        FontSize = 11.0
                    },
                    (Control)stackPanel,
                    QuadrantPad(row.Controller),
                    (Control)textBlock
                }
            };
            foreach (BlendshapeChannelRow channel in row.Channels)
            {
                stackPanel2.Children.Add(new TextBlock
                {
                    Text = "  " + channel.Label,
                    FontSize = 10.0,
                    Opacity = 0.65,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            _blendshapeControllers.Children.Add(stackPanel2);
            _blendshapeFields.Add((row.Controller, x, y, textBlock));
            void Commit()
            {
                if (!_syncing)
                {
                    ApplyBlendshapeValue(row.Controller, (float)x.Value.GetValueOrDefault(), (float)y.Value.GetValueOrDefault());
                }
            }
        }
        if (_blendshapeFields.Count == 0)
        {
            _blendshapeControllers.Children.Add(new TextBlock
            {
                Text = "No blendshape controller on this character.",
                FontSize = 11.0,
                Opacity = 0.7
            });
        }
    }

    private Control QuadrantPad(BlendshapeController controller)
    {
        UniformGrid uniformGrid = new UniformGrid
        {
            Rows = 2,
            Columns = 2,
            IsHitTestVisible = false
        };
        ReadOnlySpan<string> readOnlySpan = ["▲", "▼", "◀", "▶"];
        for (int i = 0; i < readOnlySpan.Length; i++)
        {
            string text = readOnlySpan[i];
            uniformGrid.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 10.0,
                Opacity = 0.8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        Border pad = new Border
        {
            Width = 46.0,
            Height = 46.0,
            Margin = new Thickness(0.0, 2.0),
            CornerRadius = new CornerRadius(3.0),
            Background = new SolidColorBrush(Color.FromArgb(28, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
            BorderThickness = new Thickness(1.0),
            Child = uniformGrid
        };
        ToolTip.SetTip(pad, "Press a quadrant to write '" + controller.Name + "'s channel extreme at the playhead (up +Y, down -Y, left -X, right +X).");
        pad.PointerPressed += delegate(object? _, PointerPressedEventArgs e)
        {
            Point position = e.GetPosition(pad);
            Vector2 size = new Vector2((float)pad.Bounds.Width, (float)pad.Bounds.Height);
            Vector2 local = new Vector2((float)position.X, (float)position.Y);
            ApplyBlendshapeQuadrant(controller, BlendshapeSession.Quadrant(local, size));
        };
        return pad;
    }

    private void ApplyBlendshapeQuadrant(BlendshapeController controller, int quadrant)
    {
        BlendshapeSession blendshapes = _blendshapes;
        if (blendshapes == null || _pose == null)
        {
            return;
        }
        blendshapes.Animation = _animation;
        blendshapes.Stack = _timeline.ActiveStack;
        blendshapes.Time = _time;
        AnimStack currentStack = blendshapes.CurrentStack;
        if (currentStack == null)
        {
            _status.Text = "Pick an animation and a stack on the timeline before setting a blendshape value.";
            RefreshBlendshapePanel();
            return;
        }
        uint selection = _preferences?.BlendshapeQuadrantSelection ?? 0;
        (int Group, float Sign) tuple = BlendshapeSession.QuadrantDirection(quadrant, selection);
        int item = tuple.Group;
        float item2 = tuple.Sign;
        EditSnapshot editSnapshot = EditSnapshot.Take(currentStack);
        BlendshapeValueWrite blendshapeValueWrite;
        try
        {
            blendshapeValueWrite = blendshapes.SetQuadrant(controller, quadrant, selection);
        }
        catch (Exception ex)
        {
            editSnapshot.Restore();
            _status.Text = "Could not write the blendshape quadrant: " + ex.Message;
            RefreshBlendshapePanel();
            return;
        }
        _history.Push(new EditRecord($"Set blendshape '{controller.Name}' {((item == 0) ? "X" : "Y")} to {((item2 > 0f) ? "+" : "-")}", editSnapshot, EditSnapshot.Take(currentStack)));
        _dirty = true;
        UpdateTitle();
        _status.Text = $"Blendshape '{controller.Name}' quadrant {quadrant} -> {((item == 0) ? "X" : "Y")} {((item2 > 0f) ? "+1" : "-1")}, value {blendshapeValueWrite.X:0.##}/{blendshapeValueWrite.Y:0.##} at key {blendshapeValueWrite.KeyIndex} of '{currentStack.Name}'" + (blendshapeValueWrite.Created ? " (new key)." : ".");
        AfterKeyChange();
    }

    private bool ApplyBlendshapeZoneAt(Vector2 at)
    {
        if (_blendshapeStatus == null || _pose == null || _character == null)
        {
            return false;
        }
        ClearBlendshapeZone();
        IViewCamera activeView = _viewport.Renderer.ActiveView;
        Vector2 editSize = _viewport.EditSize;
        SceneHit? sceneHit = ScenePicking.Pick(_pose, activeView, editSize, at);
        if (sceneHit.HasValue)
        {
            SceneHit valueOrDefault = sceneHit.GetValueOrDefault();
            if (valueOrDefault.Triangle >= 0)
            {
                if (_blendshapes == null || _blendshapes.Character != _character)
                {
                    _blendshapes = new BlendshapeSession(_character);
                }
                BlendshapeSession blendshapes = _blendshapes;
                blendshapes.Animation = _animation;
                blendshapes.Stack = _timeline.ActiveStack;
                blendshapes.Time = _time;
                BlendshapeZoneHit blendshapeZoneHit = blendshapes.PickZone(valueOrDefault.Mesh, valueOrDefault.Triangle);
                if ((object)blendshapeZoneHit == null)
                {
                    return false;
                }
                AnimStack currentStack = blendshapes.CurrentStack;
                if (currentStack == null)
                {
                    _status.Text = $"Zone '{blendshapeZoneHit.Controller.Name}' at triangle {blendshapeZoneHit.Triangle}: pick an animation and a stack to apply its face.";
                    return true;
                }
                DrawBlendshapeZone(blendshapeZoneHit);
                EditSnapshot editSnapshot = EditSnapshot.Take(currentStack);
                int num;
                try
                {
                    num = blendshapes.ApplyFaceAt(valueOrDefault.Mesh, valueOrDefault.Triangle);
                }
                catch (Exception ex)
                {
                    editSnapshot.Restore();
                    _status.Text = "Could not apply the blendshape zone: " + ex.Message;
                    RefreshBlendshapePanel();
                    return true;
                }
                if (num == 0)
                {
                    editSnapshot.Restore();
                    _status.Text = $"Zone '{blendshapeZoneHit.Controller.Name}' (triangle {blendshapeZoneHit.Triangle}) of '{valueOrDefault.Mesh.Name}': " + "no CBlendshapeFace stores values for this controller.";
                    RefreshBlendshapePanel();
                    return true;
                }
                _history.Push(new EditRecord("Apply blendshape zone '" + blendshapeZoneHit.Controller.Name + "'", editSnapshot, EditSnapshot.Take(currentStack)));
                _dirty = true;
                UpdateTitle();
                _status.Text = $"Zone '{blendshapeZoneHit.Controller.Name}' (triangle {blendshapeZoneHit.Triangle} of '{valueOrDefault.Mesh.Name}'): applied face '{blendshapeZoneHit.Face?.Name ?? "(none)"}' to {num} controller(s) at the keys of '{currentStack.Name}'.";
                RefreshBlendshapePanel();
                AfterKeyChange();
                return true;
            }
        }
        return false;
    }

    private void DrawBlendshapeZone(BlendshapeZoneHit zone)
    {
        if (_pose == null)
        {
            return;
        }
        List<float> vertices = new List<float>();
        Vector3 colour = new Vector3(0.3f, 0.9f, 1f);
        uint[] interactiveFaces = zone.Controller.InteractiveFaces;
        foreach (uint triangle in interactiveFaces)
        {
            if (_pose.TryTriangle(zone.Mesh, (int)triangle, out var a, out var b, out var c))
            {
                Add(a);
                Add(b);
                Add(b);
                Add(c);
                Add(c);
                Add(a);
                if (vertices.Count >= 147456)
                {
                    break;
                }
            }
        }
        _viewport.Renderer.ZoneTriangles = vertices.ToArray();
        _viewport.RequestNextFrameRendering();
        void Add(Vector3 point)
        {
            vertices.AddRange([point.X, point.Y, point.Z, colour.X, colour.Y, colour.Z]);
        }
    }

    private void ClearBlendshapeZone()
    {
        if (_viewport.Renderer.ZoneTriangles.Length != 0)
        {
            _viewport.Renderer.ZoneTriangles = Array.Empty<float>();
            _viewport.RequestNextFrameRendering();
        }
    }

    private void BuildBlendshapeFaceCells(BlendshapeSession session)
    {
        if (_blendshapeFaces == null)
        {
            return;
        }
        _blendshapeFaces.Children.Clear();
        foreach (BlendshapeFaceRow face in session.Faces)
        {
            StackPanel stackPanel = new StackPanel
            {
                Spacing = 1.0,
                Children = 
                {
                    (Control)new TextBlock
                    {
                        Text = face.Label,
                        FontSize = 11.0,
                        FontWeight = FontWeight.DemiBold
                    },
                    (Control)new TextBlock
                    {
                        Text = ((face.Controllers.Count == 0) ? "no controller here" : string.Join(", ", face.Controllers)),
                        FontSize = 9.0,
                        Opacity = 0.8
                    },
                    (Control)new TextBlock
                    {
                        Text = ((face.Values.Count == 0) ? "no stored values" : string.Join("  ", face.Values.Select((float[] v) => $"{v[0]:0.##}/{v[1]:0.##}"))),
                        FontSize = 9.0,
                        Opacity = 0.7
                    },
                    (Control)new TextBlock
                    {
                        Text = "no thumbnail picture",
                        FontSize = 9.0,
                        Opacity = 0.5
                    }
                }
            };
            if (face.Ignored.Count > 0)
            {
                stackPanel.Children.Add(new TextBlock
                {
                    Text = "ignored: " + string.Join(", ", face.Ignored),
                    FontSize = 9.0,
                    Opacity = 0.6
                });
            }
            Button button = new Button
            {
                Padding = new Thickness(6.0, 4.0),
                Margin = new Thickness(0.0, 0.0, 4.0, 4.0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = stackPanel
            };
            ToolTip.SetTip(button, $"Apply {face.Label} of '{face.Mesh.Name}' to {string.Join(", ", face.Controllers)} at the current stack's keys.");
            button.Click += delegate
            {
                ApplyBlendshapeFace(face.Face);
            };
            _blendshapeFaces.Children.Add(button);
        }
        if (session.Faces.Count == 0)
        {
            _blendshapeFaces.Children.Add(new TextBlock
            {
                Text = "No CBlendshapeFace on the character's meshes.",
                FontSize = 11.0,
                Opacity = 0.7
            });
        }
    }
}

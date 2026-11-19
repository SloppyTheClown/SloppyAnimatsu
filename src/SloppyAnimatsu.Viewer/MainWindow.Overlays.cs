using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

// The windows that float over the view area beside the panels: the curveboard, its Curves tab and
// the spinner dial. They live in the panels' canvas so that whichever window was pressed last is
// drawn in front of the others (a viewer design; FloatingLayout.NextOrder hands out the order).
public sealed partial class MainWindow
{
    /// <summary>The curveboard's height when the view area has room for it.</summary>
    private const double CurveboardHeight = 300.0;

    /// <summary>The gap between the curveboard (or the Curves tab) and the timeline under it.</summary>
    private const double CurveboardGap = 4.0;

    /// <summary>How far right of the timeline's left edge the closed Curves tab sits: just past its title.</summary>
    private const double CurvesTabTitleOffset = 96.0;

    /// <summary>Whether the overlays' press handlers are attached (once per control).</summary>
    private bool _overlaysWired;

    /// <summary>Puts the curveboard, the Curves tab and the spinner dial back into the canvas after it was cleared.</summary>
    private void AddOverlays()
    {
        if (_floatHost == null) return;
        WireOverlays();
        foreach (Control c in new Control?[] { _curveHost, _showCurves, _spinnerDial }.OfType<Control>())
        {
            (c.Parent as Panel)?.Children.Remove(c);
            _floatHost.Children.Add(c);
        }
    }

    /// <summary>Puts the dial (and an open curveboard) in front of the panels of a freshly built layout.</summary>
    private void RaiseOverlays()
    {
        if (ActiveLayout is not { } layout) return;
        if (_spinnerDial != null) _spinnerDial.ZIndex = layout.NextOrder();
        if (_curveHost is { IsVisible: true }) _curveHost.ZIndex = layout.NextOrder();
        PlaceCurveboard();
    }

    private void WireOverlays()
    {
        if (_overlaysWired) return;
        _overlaysWired = true;
        _showCurves.Content = "Curves ▲";
        _showCurves.FontSize = 11.0;
        _showCurves.Padding = new Thickness(10.0, 0.0);
        _showCurves.MinHeight = 0.0;
        _showCurves.Height = 17.0;
        _showCurves.CornerRadius = new CornerRadius(3.0, 3.0, 0.0, 0.0);
        _showCurves.ZIndex = int.MaxValue - 1;
        ToolTip.SetTip(_showCurves, "Open or close the curveboard (C)");
        foreach (Control c in new Control?[] { _curveHost, _spinnerDial }.OfType<Control>())
        {
            Control window = c;
            window.AddHandler(InputElement.PointerPressedEvent, delegate
            {
                if (ActiveLayout is { } layout) window.ZIndex = layout.NextOrder();
            }, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
    }

    /// <summary>
    /// Places the curveboard over the view area, as wide as the timeline and just above it (85% of
    /// the width, centred, when there is no timeline panel at the bottom), and the Curves tab on its
    /// top edge - or, closed, on the timeline's top edge above the Play button.
    /// </summary>
    private void PlaceCurveboard()
    {
        if (_floatHost == null || _curveHost == null) return;
        double hostWidth = _floatHost.Bounds.Width, hostHeight = _floatHost.Bounds.Height;
        if (hostWidth < 2.0 || hostHeight < 2.0) return;
        var timeline = ActiveLayout?.Panel(PanelKeys.Timeline);
        bool docked = timeline != null && timeline.Anchor == PanelAnchor.Bottom;
        double x = docked ? timeline!.X : FloatingLayout.BottomX(hostWidth);
        double width = docked ? timeline!.Width : FloatingLayout.BottomWidth(hostWidth);
        double floor = docked ? timeline!.Y - CurveboardGap : hostHeight - FloatingLayout.Margin;
        double tabHeight = _showCurves.Height;
        bool open = _showCurves.IsChecked == true;
        double height = Math.Max(120.0, Math.Min(CurveboardHeight, floor - tabHeight - FloatingLayout.Margin));
        double top = floor - height;
        _curveHost.IsVisible = open;
        Canvas.SetLeft(_curveHost, x);
        Canvas.SetTop(_curveHost, top);
        _curveHost.Width = width;
        _curveHost.Height = height;
        _showCurves.Content = open ? "Curves ▼" : "Curves ▲";
        // Open, the tab sits on the curveboard's top edge; closed, in the timeline's title bar
        // beside its title, so it does not cover a side panel that ends just above the timeline.
        if (open || !docked)
        {
            Canvas.SetLeft(_showCurves, x + 8.0);
            Canvas.SetTop(_showCurves, (open ? top : floor + CurveboardGap) - tabHeight);
        }
        else
        {
            Canvas.SetLeft(_showCurves, x + CurvesTabTitleOffset);
            Canvas.SetTop(_showCurves, timeline!.Y + 1.0);
        }
        if (_curvePanel != null)
        {
            _curvePanel.Height = double.NaN;
            _curvePanel.VerticalAlignment = VerticalAlignment.Stretch;
        }
    }

    /// <summary>The curveboard's window frame, styled like a panel's.</summary>
    private Border CurveboardFrame(Control content) => new Border
    {
        Child = content,
        IsVisible = false,
        Background = PanelBackground,
        BorderBrush = PanelBorderBrush,
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(3.0),
        BoxShadow = PanelShadow,
        ClipToBounds = true,
        Padding = new Thickness(0.0, 0.0, 0.0, 6.0),
    };
}

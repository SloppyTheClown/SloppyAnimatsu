using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// One numeric field of the viewer's Spinner
/// the value is typed as text and applied on Enter or when the field loses
/// focus. A drag strip left of the text box scrubs the value: dragging right raises it, left
/// lowers it, one <see cref="Step"/> per <see cref="PixelsPerStep"/> pixels (Ctrl: a tenth of
/// that). The text shows the value live while dragging and the value is applied once, on release,
/// so a drag is one undo step. Rotation values are Euler degrees.
/// This is a viewer design, not the widget;
/// The field is a circular slider with the same per-object value.
/// </summary>
public sealed class SpinnerField : Grid
{
    /// <summary>Pixels of drag per <see cref="Step"/> (default 4).</summary>
    public double PixelsPerStep { get; set; } = 4;

    private readonly TextBox _text = new()
    {
        FontSize = 11,
        Padding = new Thickness(3, 1),
        MinHeight = 19,
        BorderThickness = new Thickness(1),
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly Border _scrub = new()
    {
        Width = 14,
        MinHeight = 19,
        Background = Brushes.Transparent,
        Cursor = new Cursor(StandardCursorType.SizeWestEast),
        Child = new TextBlock
        {
            Text = "⋮⋮",
            FontSize = 9,
            Opacity = 0.55,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };
    private bool _syncing;
    private float _value;
    private Point? _dragStart;
    private float _dragFrom;
    private float _dragValue;

    public SpinnerField()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*");
        ColumnSpacing = 2;
        ToolTip.SetTip(_scrub, "Drag left or right to change the value (Ctrl: finer).");
        Children.Add(_scrub);
        SetColumn(_text, 1);
        Children.Add(_text);

        _text.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitText();
            e.Handled = true;
        };
        _text.LostFocus += (_, _) => CommitText();
        _scrub.PointerPressed += ScrubPressed;
        _scrub.PointerMoved += ScrubMoved;
        _scrub.PointerReleased += ScrubReleased;
        _scrub.PointerCaptureLost += (_, _) => EndScrub();
    }

    /// <summary>Whether the field shows a value that can be edited.</summary>
    public bool IsEditable => _text.IsEnabled;

    /// <summary>What the field needs to be editable, shown in its hover text while it is greyed out.</summary>
    public string Requires { get; set; } = "a selected object that can be edited here";

    private void SetTextEnabled(bool enabled) => DisabledHint.SetEnabled(_text, enabled, Requires);

    private void SyncScrub()
    {
        _scrub.IsEnabled = _text.IsEnabled;
        _scrub.Opacity = _scrub.IsEnabled ? 1 : 0.35;
    }

    /// <summary>The amount one drag increment (<see cref="PixelsPerStep"/> pixels) adds or removes.</summary>
    public float Step { get; set; } = 1;

    /// <summary>Raised when the user applies a value (Enter, focus lost, or the end of a drag).</summary>
    public event Action<float>? Committed;

    /// <summary>
    /// Raised on every step of a drag with the value under the pointer, so the owner can show it
    /// live; the drag still ends in one <see cref="Committed"/> (or none when it returns to where it
    /// started), always preceded by <see cref="DragEnded"/>.
    /// </summary>
    public event Action<float>? Previewed;

    /// <summary>Raised when a drag ends, before its <see cref="Committed"/>: the owner drops its live preview.</summary>
    public event Action? DragEnded;

    /// <summary>The value the field last showed or committed, in model units.</summary>
    public float Value => _value;

    /// <summary>
    /// Display units per model unit: the text shows <c>value * DisplayScale</c> and typed text is
    /// divided by it, while <see cref="Value"/>, <see cref="Step"/> and every event stay in model
    /// units. 1 by default; 0.01 shows centimetre scene units as metres.
    /// </summary>
    public float DisplayScale
    {
        get => _displayScale;
        set
        {
            if (!(value > 0) || value == _displayScale) return;
            _displayScale = value;
            // Three decimals of the model unit stay visible at any scale (0.01 -> five decimals).
            int decimals = 3 + Math.Max(0, (int)Math.Round(-Math.Log10(value)));
            _format = "0." + new string('#', decimals);
            if (_text.IsEnabled && _dragStart == null) Show(_value, enabled: true, force: true);
        }
    }

    private float _displayScale = 1;
    private string _format = "0.###";

    /// <summary>
    /// Text shown in front of the number (the unit, e.g. "m" for metres), or empty. Typed text may
    /// carry it or leave it out; it is stripped before parsing.
    /// </summary>
    public string Prefix
    {
        get => _prefix;
        set
        {
            value ??= "";
            if (value == _prefix) return;
            _prefix = value;
            if (_text.IsEnabled && _dragStart == null) Show(_value, enabled: true, force: true);
        }
    }

    private string _prefix = "";

    /// <summary>
    /// Shows <paramref name="value"/>, or a blank, disabled field when it is null. A focused field
    /// keeps text the user is still editing unless <paramref name="force"/> puts the model value
    /// back; a drag in progress keeps its live value.
    /// </summary>
    public void Show(float? value, bool enabled = true, bool force = false)
    {
        if (_dragStart != null && !force) return;
        _syncing = true;
        if (value is { } v)
        {
            // Text still reading what the field last showed is not an edit, even when the shown
            // digits round the model value (a metre display drops sub-millimetre digits).
            bool editing = !force && _text.IsFocused
                           && _text.Text != Format(_value)
                           && TryParseText(out float typed)
                           && MathF.Abs(typed - v) >= 1e-6f;
            _value = v;
            if (!editing) _text.Text = Format(v);
            SetTextEnabled(enabled);
        }
        else
        {
            _value = 0;
            _text.Text = "";
            SetTextEnabled(false);
        }
        SyncScrub();
        _syncing = false;
    }

    private void CommitText()
    {
        if (_syncing) return;
        // Untouched text (what Format showed) commits nothing: parsing it back would turn the
        // rounding of the display into a real edit when the field merely loses focus.
        if (_text.Text == Format(_value)) return;
        if (!TryParseText(out float value))
        {
            _text.Text = Format(_value);
            return;
        }
        if (MathF.Abs(value - _value) < 1e-6f)
        {
            _text.Text = Format(_value);
            return;
        }
        _value = value;
        _text.Text = Format(value);
        Committed?.Invoke(value);
    }

    private void ScrubPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_text.IsEnabled || !e.GetCurrentPoint(_scrub).Properties.IsLeftButtonPressed) return;
        CommitText();
        _dragStart = e.GetPosition(this);
        _dragFrom = _dragValue = _value;
        e.Pointer.Capture(_scrub);
        e.Handled = true;
    }

    private void ScrubMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start) return;
        double dx = e.GetPosition(this).X - start.X;
        float step = Step > 0 ? Step : 1;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) step *= 0.1f;
        float value = _dragFrom + (float)Math.Round(dx / PixelsPerStep) * step;
        float previous = _dragValue;
        _dragValue = MathF.Round(value / step, MidpointRounding.AwayFromZero) * step;
        if (MathF.Abs(_dragValue - previous) >= 1e-6f) Previewed?.Invoke(_dragValue);
        _syncing = true;
        _text.Text = Format(_dragValue);
        _syncing = false;
        e.Handled = true;
    }

    private void ScrubReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragStart == null) return;
        EndScrub();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void EndScrub()
    {
        if (_dragStart == null) return;
        _dragStart = null;
        DragEnded?.Invoke();
        if (MathF.Abs(_dragValue - _dragFrom) >= 1e-6f)
        {
            _value = _dragValue;
            _text.Text = Format(_dragValue);
            Committed?.Invoke(_dragValue);
        }
        else
        {
            _text.Text = Format(_value);
        }
    }

    /// <summary>The field's text in model units (the typed display value divided by <see cref="DisplayScale"/>).</summary>
    private bool TryParseText(out float value)
    {
        string text = (_text.Text ?? "").Trim();
        // The unit may be typed in front of the number (as shown) or behind it, or left out.
        if (_prefix.Length > 0)
        {
            if (text.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)) text = text[_prefix.Length..].Trim();
            else if (text.EndsWith(_prefix, StringComparison.OrdinalIgnoreCase)) text = text[..^_prefix.Length].Trim();
        }
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float shown))
        {
            value = 0;
            return false;
        }
        value = shown / _displayScale;
        return true;
    }

    /// <summary>A model value as the field shows it, in display units.</summary>
    private string Format(float value)
    {
        string number = (value * _displayScale).ToString(_format, CultureInfo.InvariantCulture);
        // A space between the unit and the number ("m 1.25"); parsing trims it again.
        return _prefix.Length > 0 ? _prefix + " " + number : number;
    }
}

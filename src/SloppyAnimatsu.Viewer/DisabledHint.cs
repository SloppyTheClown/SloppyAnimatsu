using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// Enables or greys out a control and keeps its hover text useful either way: a disabled control
/// still shows its own tip (tooltips show on disabled controls app-wide, see <see cref="Install"/>)
/// followed by what it needs to work again ("Needs a selected joint.").
/// </summary>
internal static class DisabledHint
{
    private sealed class State
    {
        /// <summary>The control's own tip, without the requirement line.</summary>
        public object? BaseTip;

        /// <summary>The tip this helper last set, to notice when other code replaced it.</summary>
        public object? Shown;
    }

    private static readonly ConditionalWeakTable<Control, State> States = new();

    /// <summary>Lets every control show its tooltip while it is disabled.</summary>
    /// <remarks>
    /// The property is registered on <see cref="Control"/> itself, so overriding its default for
    /// <see cref="Control"/> throws "Metadata is already set" at start-up. Overriding the default on
    /// <see cref="TopLevel"/> does not help either: an inherited property only flows down from a
    /// value that is actually set on an ancestor, never from a default. So an application style sets
    /// it on every top level (windows and popup roots), and every control inside inherits it.
    /// </remarks>
    public static void Install(Application app) => app.Styles.Add(new Style(x => x.Is<TopLevel>())
    {
        Setters = { new Setter(ToolTip.ShowOnDisabledProperty, true) },
    });

    /// <summary>
    /// Sets <paramref name="control"/>'s <c>IsEnabled</c>; while disabled its tooltip gains
    /// "Needs <paramref name="requires"/>." under its own text.
    /// </summary>
    public static void SetEnabled(Control control, bool enabled, string requires) =>
        Apply(control, enabled, tip => MenuEntry.WithRequirement(tip, requires));

    /// <summary>
    /// As <see cref="SetEnabled"/>, but while disabled the tooltip gains <paramref name="reason"/>,
    /// a whole sentence (e.g. a dialog's own refusal message), instead of a "Needs ..." line.
    /// </summary>
    public static void SetEnabledBecause(Control control, bool enabled, string? reason) =>
        Apply(control, enabled, tip => string.IsNullOrEmpty(reason) ? tip : string.IsNullOrEmpty(tip) ? reason : tip + "\n\n" + reason);

    private static void Apply(Control control, bool enabled, Func<string?, string?> withRequirement)
    {
        control.IsEnabled = enabled;
        State state = States.GetOrCreateValue(control);
        object? current = ToolTip.GetTip(control);
        // A tip set elsewhere since our last change is the new base text.
        if (!ReferenceEquals(current, state.Shown)) state.BaseTip = current;
        object? tip = enabled || state.BaseTip is not (null or string)
            ? state.BaseTip
            : withRequirement((string?)state.BaseTip);
        ToolTip.SetTip(control, tip);
        state.Shown = tip;
    }
}

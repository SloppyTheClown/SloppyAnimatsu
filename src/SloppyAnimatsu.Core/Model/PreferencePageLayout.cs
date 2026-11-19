using System.Globalization;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The widget a preferences row is drawn with: the dialog
/// draws a check box for a bool - or for one bit of a flags member - a numeric field, a colour swatch
/// beside its text, a combo box whose options carry hand-written names, an editable text field, and one
/// hyperlink.
/// </summary>
public enum PreferenceRowWidget
{
    Text,
    Check,
    Number,
    Color,
    Combo,
    Link,
}

/// <summary>One option of a combo box: the name the page draws and the value it stores.</summary>
/// <param name="Name">The option's drawn name.</param>
/// <param name="Value">Its stored value.</param>
public sealed record PreferenceOption(string Name, int Value);

/// <summary>
/// One row of a preferences page as 2020-3-13 draws it: the hand-written label, the member behind it and
/// the widget. A null <paramref name="Member"/> is a row the page draws with nothing to edit (Timebar's
/// scene frame rate, Spinner's untraced "Show axis hints"), or one whose value is not a plain member
/// (the privacy-policy link); <paramref name="Note"/> says why.
/// </summary>
/// <param name="Decimals">How many decimals a numeric field draws.</param>
/// <param name="Metres">The field draws a length in metres; the file stores centimetres.</param>
/// <param name="Default">The value drawn for a stored ~0 (the tool default; its profile can hold denormal garbage rather than 0), or null.</param>
/// <param name="BitMask">The bit of a flags member this check box edits, or null for a whole bool.</param>
/// <param name="EnabledBy">The label of the check box this row is only enabled by (Stacker's dimmed second box).</param>
/// <param name="Note">What the measurement could not settle, for the row's tooltip.</param>
/// <param name="Scene">
/// The app draws this row from the open scene's <c>CScene</c> member of the same name, not from the
/// profile. The
/// dialog binds it to the scene when one is open and falls back to the profile without one.
/// </param>
public sealed record PreferenceRowSpec(
    string Label,
    string? Member = null,
    string? Class = null,
    PreferenceRowWidget Widget = PreferenceRowWidget.Text,
    IReadOnlyList<PreferenceOption>? Options = null,
    int Decimals = 3,
    bool Metres = false,
    double? Default = null,
    int? BitMask = null,
    string? EnabledBy = null,
    string? Note = null,
    bool Scene = false);

/// <summary>
/// One row of the preferences dialog as it is drawn now: a <see cref="PreferenceRowSpec"/> bound to the
/// file's member. It owns the drawing rules
/// a stored ~0 is drawn as the tool's default where one is known (the
/// profile holds denormal garbage such as 1.1E-33 where the page draws 3.000), a length is drawn in
/// metres (the file stores centimetres), a flags member's bit is a check box, and a combo box draws the
/// option name of its stored value. <see cref="Text"/> is both what the page shows and what
/// <see cref="TrySetFromText"/> reads back.
/// </summary>
public sealed class PreferenceRow
{
    private readonly IReadOnlyList<PreferenceOption> _options;
    private readonly int? _sceneFrameRate;

    internal PreferenceRow(
        PreferenceRowSpec spec, PreferenceField? field, string? className, int? sceneFrameRate,
        bool sceneBacked = false)
    {
        Label = spec.Label;
        Member = spec.Member;
        ClassName = className ?? spec.Class;
        Widget = spec.Widget;
        Field = field;
        IsSceneBacked = sceneBacked;
        _options = spec.Options ?? Array.Empty<PreferenceOption>();
        OptionNames = _options.Select(option => option.Name).ToList();
        Decimals = spec.Decimals;
        Metres = spec.Metres;
        Default = spec.Default;
        BitMask = spec.BitMask;
        EnabledBy = spec.EnabledBy;
        Note = spec.Note;
        _sceneFrameRate = sceneFrameRate;
    }

    /// <summary>The label the page draws.</summary>
    public string Label { get; }

    /// <summary>The member path the row edits, or null for a row with nothing to edit.</summary>
    public string? Member { get; }

    /// <summary>The page class the row was bound to (for Rig Atelier's merged rows, the page that has the member).</summary>
    public string? ClassName { get; }

    public PreferenceRowWidget Widget { get; }

    /// <summary>The bound member, or null for a row the page draws without an editable value.</summary>
    public PreferenceField? Field { get; }

    /// <summary>
    /// Whether <see cref="Field"/> is the open scene's <c>CScene</c> member rather than the profile's:
    /// The app draws these rows from the scene (open-questions 27), so a save writes the scene.
    /// </summary>
    public bool IsSceneBacked { get; }

    /// <summary>A combo box's option names, in the order the drop-down draws them.</summary>
    public IReadOnlyList<string> OptionNames { get; }

    public int Decimals { get; }
    public bool Metres { get; }
    public double? Default { get; }
    public int? BitMask { get; }

    /// <summary>The label of the check box this row's enabled state follows, or null.</summary>
    public string? EnabledBy { get; }

    /// <summary>What the measurement could not settle, for the tooltip.</summary>
    public string? Note { get; }

    public string TypeName => Field?.TypeName ?? "";

    /// <summary>Whether the row writes a member (a link or a row with no member does not).</summary>
    public bool Editable => Field != null && Widget != PreferenceRowWidget.Link;

    /// <summary>Whether the drawn text is a substituted default (a value the page draws as a real value).</summary>
    public bool IsDefault =>
        Widget == PreferenceRowWidget.Number && Field != null && Default is not null &&
        NumberValue() is double number && IsUnset(number);

    /// <summary>The text the page draws in the row, in the form <see cref="TrySetFromText"/> reads back.</summary>
    public string Text
    {
        get
        {
            switch (Widget)
            {
                case PreferenceRowWidget.Check:
                    return CheckValue() ? "True" : "False";
                case PreferenceRowWidget.Number:
                    if (Field == null) return _sceneFrameRate?.ToString(CultureInfo.InvariantCulture) ?? "";
                    if (NumberValue() is not double number) return Field.Format();
                    double shown = IsUnset(number) && Default is double fallback ? fallback
                        : Metres ? number / 100 : number;
                    string digits = shown.ToString("F" + Decimals.ToString(CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture);
                    return Metres ? "m " + digits : digits;
                case PreferenceRowWidget.Combo:
                {
                    int? stored = StoredNumber();
                    if (stored is null) return "";
                    foreach (var option in _options)
                        if (option.Value == stored) return option.Name;
                    return stored.Value.ToString(CultureInfo.InvariantCulture);
                }
                case PreferenceRowWidget.Link:
                    return Label;
                default:
                    return Field?.Format() ?? "";
            }
        }
    }

    /// <summary>The option a combo box selects, or -1 when no listed option has the stored value.</summary>
    public int OptionIndex
    {
        get
        {
            int? stored = StoredNumber();
            if (stored is null) return -1;
            for (int i = 0; i < _options.Count; i++)
                if (_options[i].Value == stored) return i;
            return -1;
        }
    }

    /// <summary>Whether <paramref name="text"/> is still what the row draws, so a commit must not write it.</summary>
    public bool IsUnchanged(string? text) => string.Equals((text ?? "").Trim(), Text, StringComparison.Ordinal);

    /// <summary>
    /// Whether a stored number counts as unset, so the page draws its default. The profile holds
    /// denormal garbage for some defaults (the live file's four size factors are 1.1E-33, 9E-43, 0, 0)
    /// while the page draws 2.000/1.000/3.000/0.400, so the rule cannot be an exact <c>== 0</c>; a
    /// magnitude below this is drawn as the default.
    /// </summary>
    private const double UnsetThreshold = 1e-6;

    private static bool IsUnset(double number) => Math.Abs(number) < UnsetThreshold;

    /// <summary>
    /// Writes the text a row draws back into its member. False (and a reason) when the text is not a
    /// value of the member's type, a combo option whose stored value is not listed, or the row has
    /// nothing to edit.
    /// </summary>
    public bool TrySetFromText(string? text, out string? error)
    {
        error = null;
        if (Field is not { } field)
        {
            error = $"{Label}: this row has nothing to edit.";
            return false;
        }
        if (Widget == PreferenceRowWidget.Link)
        {
            error = $"{Label}: this row is a link, not a value.";
            return false;
        }
        string input = (text ?? "").Trim();
        switch (Widget)
        {
            case PreferenceRowWidget.Check:
            {
                if (!TryParseFlag(input, out bool flagged))
                {
                    error = $"{Name}: '{text}' is not a bool.";
                    return false;
                }
                if (BitMask is int mask)
                {
                    uint bits = Bits();
                    bits = flagged ? bits | (uint)mask : bits & ~(uint)mask;
                    return field.TrySetFromString(bits.ToString(CultureInfo.InvariantCulture), out error);
                }
                return field.TrySetFromString(flagged ? "True" : "False", out error);
            }
            case PreferenceRowWidget.Number:
                return TrySetNumber(field, input, text, out error);
            case PreferenceRowWidget.Combo:
                return TrySetCombo(field, input, text, out error);
            default:
                return field.TrySetFromString(text ?? "", out error);
        }
    }

    private bool TrySetNumber(PreferenceField field, string input, string? text, out string? error)
    {
        error = null;
        string body = input;
        if (Metres && body.StartsWith("m ", StringComparison.OrdinalIgnoreCase)) body = body[2..].Trim();
        if (!double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            error = $"{Name}: '{text}' is not a number.";
            return false;
        }
        if (Metres) number *= 100;
        if (Decimals == 0)
        {
            // A whole-number field (a count, a rate, a threshold in pixels) takes "12" and "12.0" but
            // not "12.5": rounding silently would write a value the user did not type.
            double rounded = Math.Round(number);
            if (Math.Abs(number - rounded) > 1e-9)
            {
                error = $"{Name}: '{text}' is not a whole number.";
                return false;
            }
            number = rounded;
        }
        else
        {
            number = Math.Round(number, 6);
        }
        return field.TrySetFromString(number.ToString("0.######", CultureInfo.InvariantCulture), out error);
    }

    private bool TrySetCombo(PreferenceField field, string input, string? text, out string? error)
    {
        error = null;
        foreach (var option in _options)
        {
            if (!string.Equals(option.Name, input, StringComparison.OrdinalIgnoreCase)) continue;
            return field.TrySetFromString(option.Value.ToString(CultureInfo.InvariantCulture), out error);
        }
        if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return field.TrySetFromString(input, out error);
        error = $"{Name}: '{text}' is not one of its options ({string.Join(", ", OptionNames)}).";
        return false;
    }

    /// <summary>The member a status line names, or the label for a row with no member.</summary>
    private string Name => Member ?? Label;

    private object? Value => Field?.Value;

    private bool CheckValue()
    {
        if (Value is null) return false;
        if (BitMask is int mask) return (Bits() & (uint)mask) != 0;
        return Value switch
        {
            bool flag => flag,
            byte raw => raw != 0,
            sbyte raw => raw != 0,
            _ => false,
        };
    }

    private uint Bits() => Value switch
    {
        uint value => value,
        int value => (uint)value,
        ushort value => value,
        short value => (uint)value,
        byte value => value,
        sbyte value => (uint)value,
        _ => 0u,
    };

    private double? NumberValue() => Value switch
    {
        float value => value,
        double value => value,
        int value => value,
        uint value => value,
        ushort value => value,
        short value => value,
        byte value => value,
        sbyte value => value,
        long value => value,
        ulong value => value,
        _ => null,
    };

    private int? StoredNumber() => Value switch
    {
        int value => value,
        uint value => (int)value,
        ushort value => value,
        short value => value,
        byte value => value,
        sbyte value => value,
        _ => null,
    };

    private static bool TryParseFlag(string text, out bool value)
    {
        value = false;
        if (text is "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }
        return text is "0" || text.Equals("false", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => $"{Label} ({Member ?? "no member"}) = {Text}";
}

/// <summary>
/// Binds a <see cref="PreferenceCatalogue.Entry"/> to a file: its curated rows in catalogue order,
/// then a plain row for every member the page draws no row for - so a page class the catalogue does not
/// cover, or a member 2020-3-13's page hides, keeps its value reachable (the dialog's old rule).
/// </summary>
public static class PreferenceLayout
{
    /// <summary>
    /// The rows of one dialog entry: the curated rows whose member this file (or the open scene) has,
    /// then the remaining members. <paramref name="sceneFrameRate"/> feeds Timebar's scene-frame-rate
    /// row and <paramref name="scene"/> the rows the app draws from <c>CScene</c> - both follow the open
    /// scene rather than the profile (open-questions 27).
    /// </summary>
    public static IReadOnlyList<PreferenceRow> Build(
        PreferenceCatalogue.Entry entry, IReadOnlyList<PreferencePage> pages, int? sceneFrameRate = null,
        Scene? scene = null)
    {
        var byClass = new Dictionary<string, PreferencePage>(StringComparer.Ordinal);
        foreach (var page in pages) byClass[page.ClassName] = page;
        var fieldsByClass = new Dictionary<string, List<PreferenceField>>(StringComparer.Ordinal);
        foreach (string className in entry.Classes)
            if (byClass.TryGetValue(className, out var page)) fieldsByClass[className] = page.Fields.ToList();
        // The open scene as a preference page: its fields have the types and text forms the rows need,
        // and writing one edits the scene in place (the document saves it with the scene).
        PreferencePage? scenePage = scene is null ? null : new PreferencePage(scene.Source);
        IReadOnlyList<PreferenceField>? sceneFields = scenePage?.Fields;

        var rows = new List<PreferenceRow>();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in entry.Rows ?? Array.Empty<PreferenceRowSpec>())
        {
            if (spec.Member is not { } member)
            {
                rows.Add(new PreferenceRow(spec, null, spec.Class, sceneFrameRate));
                continue;
            }
            PreferenceField? field = null;
            string? owner = null;
            bool sceneBacked = false;
            if (spec.Scene && scenePage is not null)
            {
                field = sceneFields!.FirstOrDefault(candidate =>
                    string.Equals(candidate.Path, member, StringComparison.Ordinal));
                if (field != null)
                {
                    owner = scenePage.ClassName;
                    sceneBacked = true;
                }
            }
            if (field == null)
            {
                foreach (string className in Candidates(spec, entry))
                {
                    if (!fieldsByClass.TryGetValue(className, out var fields)) continue;
                    field = fields.FirstOrDefault(candidate =>
                        string.Equals(candidate.Path, member, StringComparison.Ordinal));
                    if (field != null)
                    {
                        owner = className;
                        break;
                    }
                }
            }
            // A member neither file nor scene has (an older preferences.akt, an old scene) draws no row.
            if (field == null) continue;
            covered.Add(member);
            rows.Add(new PreferenceRow(spec, field, owner, sceneFrameRate, sceneBacked));
        }

        foreach (string className in entry.Classes)
        {
            if (!fieldsByClass.TryGetValue(className, out var fields)) continue;
            foreach (var field in fields)
            {
                // The node's own Name is not a value the dialog edits, and a member another class of the
                // row draws (Rig Atelier's skeleton page inherits the IK members) is drawn once.
                if (field.Path == "Name" || Preferences.HiddenMembers.Contains(field.Path) || !covered.Add(field.Path)) continue;
                rows.Add(new PreferenceRow(Appended(field), field, className, sceneFrameRate));
            }
        }
        return rows;
    }

    /// <summary>The classes a row may bind to: the curated one first, then the entry's others.</summary>
    private static IEnumerable<string> Candidates(PreferenceRowSpec spec, PreferenceCatalogue.Entry entry)
    {
        if (spec.Class is { } declared) yield return declared;
        foreach (string className in entry.Classes)
            if (!string.Equals(className, spec.Class, StringComparison.Ordinal)) yield return className;
    }

    /// <summary>The row a member the page does not draw keeps: its own name, and the widget its type allows.</summary>
    private static PreferenceRowSpec Appended(PreferenceField field) => new(
        field.Path,
        field.Path,
        null,
        field.Widget switch
        {
            PreferenceWidget.Check => PreferenceRowWidget.Check,
            PreferenceWidget.Color => PreferenceRowWidget.Color,
            _ => PreferenceRowWidget.Text,
        });
}

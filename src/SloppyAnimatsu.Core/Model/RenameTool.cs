using System.Globalization;
using System.Text.RegularExpressions;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The five fields of the Rename Tool
/// search for, replace with, add prefix, add suffix and the number suffix setting.
/// </summary>
public sealed record RenameToolOptions(
    string Search = "",
    string Replace = "",
    string Prefix = "",
    string Suffix = "",
    string Number = "")
{
    /// <summary>Whether applying these options can change a name at all.</summary>
    public bool ChangesAnything =>
        Replace.Length > 0 || Prefix.Length > 0 || Suffix.Length > 0 || Number.Length > 0;
}

/// <summary>
/// The Rename Tool's naming rule. It is pure: the caller applies the result through the panel's own
/// unique-name rule, which is what numbers names automatically when no number suffix is given.
/// </summary>
public static class RenameTool
{
    private static readonly Regex Digits = new("[0-9]+", RegexOptions.CultureInvariant);

    /// <summary>
    /// The new name of the <paramref name="index"/>-th (0-based) object of the selection:
    /// <list type="bullet">
    /// <item>An empty <i>search for</i> targets every character, so <i>replace with</i> replaces the
    /// whole name; a non-empty one replaces each occurrence (ordinal, case-sensitive).</item>
    /// <item>An empty <i>replace with</i> keeps the original name ("kept safe"), whatever the search.</item>
    /// <item>The prefix goes in front, the suffix after, and the number suffix after the suffix.</item>
    /// <item>The number suffix's first block of digits counts up from its own value by the object's
    /// place in the selection, keeping its width (<c>_01</c> gives <c>_01</c>, <c>_02</c>, ...); a
    /// setting without digits is appended as it is.</item>
    /// </list>
    /// </summary>
    public static string Apply(string original, RenameToolOptions options, int index)
    {
        string core = original;
        if (options.Replace.Length > 0)
            core = options.Search.Length == 0
                ? options.Replace
                : original.Replace(options.Search, options.Replace, StringComparison.Ordinal);
        return options.Prefix + core + options.Suffix + NumberSuffix(options.Number, index);
    }

    /// <summary>The number suffix setting for the <paramref name="index"/>-th object.</summary>
    public static string NumberSuffix(string setting, int index)
    {
        if (setting.Length == 0) return "";
        var match = Digits.Match(setting);
        if (!match.Success) return setting;
        long start = long.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long n) ? n : 0;
        string number = (start + index).ToString(new string('0', match.Length), CultureInfo.InvariantCulture);
        return setting[..match.Index] + number + setting[(match.Index + match.Length)..];
    }

    /// <summary>
    /// The new names of <paramref name="originals"/> in selection order, before the unique-name rule.
    /// </summary>
    public static IReadOnlyList<string> Preview(IReadOnlyList<string> originals, RenameToolOptions options) =>
        originals.Select((name, i) => Apply(name, options, i)).ToList();

    /// <summary>
    /// The name a scene-tree object gets for <paramref name="requested"/>: a character's objects
    /// share one name space, anything
    /// else is unique among its siblings. The object's own current name never counts as taken.
    /// </summary>
    public static string UniqueTreeName(Object3D o, string requested)
    {
        var character = o as Character ?? o.Ancestor<Character>();
        if (character == null || o is Character)
            return UniqueNames.For(o.Source.Parent, requested, o.Source);
        var taken = character.Descendants<Object3D>()
            .Where(x => !ReferenceEquals(x, o))
            .Select(x => x.Name)
            .Where(x => x != null)
            .ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(requested)) return requested;
        string stem = Regex.Replace(requested, "_[0-9]+$", "");
        var numbered = new Regex("^" + Regex.Escape(stem) + "_([0-9]+)$", RegexOptions.CultureInvariant);
        long highest = 1;
        foreach (string? name in taken)
            if (name != null && numbered.Match(name) is { Success: true } m && long.TryParse(m.Groups[1].Value, out long k))
                highest = Math.Max(highest, k);
        return $"{stem}_{Math.Max(2, highest + 1).ToString("00", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Renames a scene-tree object through <see cref="UniqueTreeName"/>; returns the name it got.</summary>
    public static string RenameTreeObject(Object3D o, string requested)
    {
        string name = UniqueTreeName(o, requested);
        o.Name = name;
        return name;
    }
}

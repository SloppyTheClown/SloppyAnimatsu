namespace SloppyAnimatsu.Core.Model;

/// <summary>One row of the Change Space list: what it says and the space it picks (null = the parent).</summary>
public readonly record struct SpaceSwitchItem(string Label, Object3D? Space)
{
    /// <summary>The "(parent)" row: the one that clears the space.</summary>
    public bool IsParent => Space == null;
}

/// <summary>
/// The Change Space dialog: its message
/// for a character with nothing spaceable, the rows it lists - the parent first, then one per
/// spaceable object, named after its character when that is a different object - and what a chosen
/// row means.
/// </summary>
public static class SpaceSwitchChoice
{
    /// <summary>The first row, which clears the space instead of setting one.</summary>
    public const string ParentLabel = "(parent)";

    /// <summary>The message when no object is spaceable.</summary>
    public const string NoSpacesMessage =
        "Foremost you need to set some joints as available spaces in Rigging mode. " +
        "Please turn on the space switch icons displayed into the Picker.";

    /// <summary>
    /// A space's row: its name, and its character's name when the space belongs to a character that
    /// is not itself (so two characters' joints of the same name read apart).
    /// </summary>
    public static string Label(Object3D space)
    {
        string name = space.Name ?? "?";
        return space.Ancestor<Character>() is { } character && !ReferenceEquals(character, space)
            ? $"{name}  ({character.Name})"
            : name;
    }

    /// <summary>The list the dialog shows: the parent row, then one row per space in the caller's order.</summary>
    public static IReadOnlyList<SpaceSwitchItem> Items(IReadOnlyList<Object3D> spaces)
    {
        var items = new List<SpaceSwitchItem>(spaces.Count + 1) { new(ParentLabel, null) };
        foreach (var space in spaces) items.Add(new SpaceSwitchItem(Label(space), space));
        return items;
    }

    /// <summary>
    /// The character a space belongs to: the character itself, or the character it hangs below
    /// (null for an object outside every character).
    /// </summary>
    public static Character? OwnerOf(Object3D space) => space as Character ?? space.Ancestor<Character>();

    /// <summary>
    /// The Space Switch window's list for the character picked in its drop-down: the parent row,
    /// then <paramref name="character"/>'s spaces among <paramref name="spaceable"/> by their own
    /// names (the drop-down already says whose they are).
    /// </summary>
    public static IReadOnlyList<SpaceSwitchItem> ItemsFor(Character? character, IEnumerable<Object3D> spaceable)
    {
        var items = new List<SpaceSwitchItem> { new(ParentLabel, null) };
        if (character == null) return items;
        foreach (var space in spaceable)
            if (ReferenceEquals(OwnerOf(space), character)) items.Add(new SpaceSwitchItem(space.Name ?? "?", space));
        return items;
    }

    /// <summary>The row of <paramref name="current"/> (the space in force; null = the parent), or 0 when it is not listed.</summary>
    public static int IndexOf(IReadOnlyList<SpaceSwitchItem> items, Object3D? current)
    {
        if (current == null) return 0;
        for (int i = 1; i < items.Count; i++)
            if (ReferenceEquals(items[i].Space, current)) return i;
        return 0;
    }

    /// <summary>
    /// The drop-down's first choice: the character of the current space when there is one, else
    /// the edited object's own character, else the first character.
    /// </summary>
    public static int DefaultCharacter(IReadOnlyList<Character> characters, Object3D? current, Object3D? target)
    {
        foreach (var o in new[] { current, target })
        {
            if (o == null || OwnerOf(o) is not { } owner) continue;
            for (int i = 0; i < characters.Count; i++)
                if (ReferenceEquals(characters[i], owner)) return i;
        }
        return characters.Count > 0 ? 0 : -1;
    }

    /// <summary>
    /// What OK (or a double-click) on <paramref name="index"/> means: <c>Chosen</c> with the space -
    /// null for the parent - or <c>(false, null)</c> for Cancel and for no selection.
    /// </summary>
    public static (bool Chosen, Object3D? Space) Choose(IReadOnlyList<SpaceSwitchItem> items, int index, bool accepted)
    {
        if (!accepted || index < 0 || index >= items.Count) return (false, null);
        return (true, items[index].Space);
    }
}

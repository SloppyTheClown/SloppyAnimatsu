namespace SloppyAnimatsu.Core.Model;

/// <summary>How the Anim Bank panel orders its rows.</summary>
public enum AnimBankSort
{
    /// <summary>Folders first, then animations, each by name (natural: "run2" before "run10"). The default.</summary>
    Alphabetical,

    /// <summary>The bank's own order (the scene file's child order), which the user arranges by dragging. New items go last.</summary>
    Free,

    /// <summary>Folders first, then animations, newest first (<see cref="AnimBankView.AddedKey"/>); a folder by its newest animation.</summary>
    Added,
}

/// <summary>
/// The Anim Bank panel's view of a bank folder: the order its rows are listed in and which rows a
/// search leaves (a SloppyAnimatsu feature).
/// Neither changes the scene: the alphabetical order is only a view, so switching back to free sort
/// finds the manual order as it was.
/// </summary>
public static class AnimBankView
{
    /// <summary>
    /// <paramref name="children"/> in the order <paramref name="sort"/> lists them. Folders always come
    /// first. <paramref name="reversed"/> turns A-Z into Z-A and Added (newest first) into oldest
    /// first; the free order is never reversed.
    /// </summary>
    public static IEnumerable<SceneObject> Order(IEnumerable<SceneObject> children, AnimBankSort sort, bool reversed = false)
    {
        switch (sort)
        {
            case AnimBankSort.Free:
                return children;
            case AnimBankSort.Added:
            {
                // Unnumbered items are ranked by their place in the bank's pre-order, so an animation
                // appended inside a folder compares with one appended beside it.
                var list = children.ToList();
                var place = new Dictionary<SceneObject, int>();
                foreach (var item in list)
                {
                    place[item] = place.Count;
                    foreach (var inner in item.Descendants<SceneObject>()) place.TryAdd(inner, place.Count);
                }
                var keyed = list.Select(c => (c, Key: c is Folder f ? FolderAddedKey(f, place) : AddedKey(c, place[c]))).ToList();
                IEnumerable<(SceneObject c, (int, int) Key)> Sorted(IEnumerable<(SceneObject c, (int, int) Key)> rows) =>
                    reversed ? rows.OrderBy(p => p.Key) : rows.OrderByDescending(p => p.Key);
                return Sorted(keyed.Where(p => p.c is Folder)).Concat(Sorted(keyed.Where(p => p.c is not Folder)))
                    .Select(p => p.c).ToList();
            }
            default:
            {
                var byKind = children.OrderBy(c => c is Folder ? 0 : 1);
                return reversed
                    ? byKind.ThenByDescending(c => c.Name ?? "", NaturalComparer.Instance)
                    : byKind.ThenBy(c => c.Name ?? "", NaturalComparer.Instance);
            }
        }
    }

    /// <summary>An animation's number in the Added sort; 0 when it has none yet.</summary>
    public static int AddedNumber(SceneObject item) =>
        item.Source[Akt.MaterialSchema.AddedOrderMember] is int n && n > 0 ? n : 0;

    /// <summary>
    /// When <paramref name="item"/> was added, oldest smallest: numbered animations by their number,
    /// then the unnumbered ones (added since the bank was last rearranged) by their place
    /// <paramref name="siblingIndex"/> in the bank, which is the order they were appended in.
    /// </summary>
    public static (int Unnumbered, int Order) AddedKey(SceneObject item, int siblingIndex) =>
        AddedNumber(item) is var n && n > 0 ? (0, n) : (1, siblingIndex);

    /// <summary>
    /// A folder's place in the Added sort: that of the newest animation anywhere inside it. An empty
    /// folder counts as added where it stands in the bank, like an unnumbered animation.
    /// </summary>
    private static (int Unnumbered, int Order) FolderAddedKey(Folder folder, IReadOnlyDictionary<SceneObject, int> place)
    {
        var keys = folder.Descendants<Animation>().Select(a => AddedKey(a, place[a])).ToList();
        return keys.Count > 0 ? keys.Max() : (1, place[folder]);
    }

    /// <summary>
    /// Numbers the bank's unnumbered animations, in bank order, after the highest number, so the Added
    /// sort keeps them in the order they were added once the bank order changes. Called before any
    /// rearrangement; returns whether anything was numbered.
    /// </summary>
    public static bool NumberAdded(SceneObject bank)
    {
        var animations = bank.Descendants<Animation>().ToList();
        if (animations.All(a => AddedNumber(a) > 0)) return false;
        Akt.MaterialSchema.EnsureFields(bank.Model.Document.Schema, bank.Model.Document.Roots);
        int next = animations.Select(AddedNumber).DefaultIfEmpty(0).Max() + 1;
        foreach (var animation in animations)
            if (AddedNumber(animation) == 0)
                animation.Source.Set(Akt.MaterialSchema.AddedOrderMember, next++);
        return true;
    }

    /// <summary>Whether <paramref name="name"/> holds <paramref name="query"/> (case-insensitive); a blank query matches everything.</summary>
    public static bool Matches(string? name, string? query) =>
        string.IsNullOrWhiteSpace(query) || (name ?? "").Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a search shows <paramref name="item"/>: its name matches, or something inside it does
    /// (a folder). A row inside a matching folder is shown by the caller.
    /// </summary>
    public static bool Shows(SceneObject item, string? query) =>
        Matches(item.Name, query) || (item is Folder && item.Children.Any(c => (c is Animation or Folder) && Shows(c, query)));

    /// <summary>Case-insensitive name order that compares runs of digits by value.</summary>
    public sealed class NaturalComparer : IComparer<string>
    {
        public static NaturalComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            x ??= "";
            y ??= "";
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    string a = x[si..i].TrimStart('0'), b = y[sj..j].TrimStart('0');
                    int c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                    if (c != 0) return c;
                    continue;
                }
                int d = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
                if (d != 0) return d;
                i++;
                j++;
            }
            int rest = (x.Length - i).CompareTo(y.Length - j);
            return rest != 0 ? rest : string.CompareOrdinal(x, y);
        }
    }
}

using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The retargeting name-pair cache: one
/// <c>CRetargetingPreferences</c> root whose <c>MatchingMetaString</c> lists the pairs of a
/// character pair as
/// <c>$Source$&lt;source name&gt;$Target$&lt;target name&gt;</c>, sorted by
/// the source name.
/// The file is <c>&lt;Documents&gt;/SloppyAnimatsu/Retargeting/&lt;source&gt;_&lt;target&gt;.slopa</c>.
/// The dialog restores a pair's mapping from it by name when the scene has no usable
/// <see cref="RetargetingMatching"/>; a transfer writes it.
/// </summary>
public sealed class RetargetingPreferences
{
    /// <summary>The object class of the cache file.</summary>
    public const string Class = "CRetargetingPreferences";
    private const string FirstMarker = "$Source$";
    private const string SecondMarker = "$Target$";
    private const string RootName = "Default name";

    private readonly AktDocument _document;
    private readonly AktObject _root;
    private readonly List<(string Source, string Target)> _pairs;

    private RetargetingPreferences(AktDocument document, AktObject root, List<(string, string)> pairs, string? path)
    {
        _document = document;
        _root = root;
        _pairs = pairs;
        Path = path;
    }

    /// <summary>The file this was loaded from or saved to; null for a document not yet written.</summary>
    public string? Path { get; private set; }

    /// <summary>The pairs in file order (normally sorted by the source name).</summary>
    public IReadOnlyList<(string Source, string Target)> Pairs => _pairs;

    /// <summary>The cache directory: <c>&lt;Documents&gt;/SloppyAnimatsu/Retargeting</c>.</summary>
    public static string DefaultDirectory => UserFolders.Retargeting;

    /// <summary>The cache file name of a character pair.</summary>
    public static string FileName(string source, string target) => $"{source}_{target}{SceneFiles.Extension}";

    /// <summary>The cache file of a character pair under <paramref name="directory"/> (or the default one).</summary>
    public static string PathFor(string source, string target, string? directory = null) =>
        System.IO.Path.Combine(directory ?? DefaultDirectory, FileName(source, target));

    /// <summary>A cache document for a pair and its name pairs.</summary>
    public static RetargetingPreferences Create(string source, string target,
        IEnumerable<(string Source, string Target)> pairs)
    {
        var schema = new AktSchema();
        var dataObject = new AktClassDef("class CDataObject");
        dataObject.Members.Add(new AktMember("Name", "CAtom"));
        schema.AddClass(dataObject, null);
        var preferenceNode = new AktClassDef("class CPreferenceNode");
        schema.AddClass(preferenceNode, dataObject);
        var preferences = new AktClassDef("class CRetargetingPreferences");
        preferences.Members.Add(new AktMember("MatchingMetaString", "QString"));
        schema.AddClass(preferences, preferenceNode);

        var root = schema.NewObject("class CRetargetingPreferences");
        root.Set("Name", RootName);
        root.Set("MatchingMetaString", Format(pairs));
        var document = new AktDocument(schema, new[] { root });
        var parsed = Parse(Format(pairs));
        var cache = new RetargetingPreferences(document, root, parsed, null);
        cache.SourceName = source;
        cache.TargetName = target;
        return cache;
    }

    /// <summary>The character names the document was created for (not stored in the file).</summary>
    public string? SourceName { get; private set; }
    public string? TargetName { get; private set; }

    /// <summary>Loads a cache file. Throws when it is not a retargeting-preferences document.</summary>
    public static RetargetingPreferences Load(string path)
    {
        var document = AktDocument.Load(path);
        var root = document.Roots.FirstOrDefault(o => o.ClassKey == AktNames.Key(Class))
                   ?? throw new InvalidDataException($"'{path}' is not a retargeting cache: it has no CRetargetingPreferences.");
        string? meta = root["MatchingMetaString"] as string;
        return new RetargetingPreferences(document, root, Parse(meta ?? ""), path);
    }

    /// <summary>The pair's cache, or null when the file does not exist.</summary>
    public static RetargetingPreferences? LoadFor(string source, string target, string? directory = null)
    {
        string path = PathFor(source, target, directory);
        return File.Exists(path) ? Load(path): null;
    }

    /// <summary>Writes the document to <paramref name="path"/>, else to <see cref="Path"/>.</summary>
    public void Save(string? path = null)
    {
        string target = path ?? Path ?? throw new InvalidOperationException("This cache has no path to save to.");
        string? directory = System.IO.Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _root.Set("MatchingMetaString", Format(_pairs));
        _document.Save(target, SettingsFiles.FormatFor(target));
        Path = target;
    }

    /// <summary>
    /// The name pairs a grid gives, sorted by the source name.
    /// Rows without a target ("none") are not stored.
    /// </summary>
    public static IReadOnlyList<(string Source, string Target)> PairsOf(RetargetingMap map) =>
        map.Selection.Where(row => row.Target != null && row.Source.Name is not null && row.Target!.Name is not null).Select(row => (row.Source.Name!, row.Target!.Name!)).OrderBy(pair => pair.Item1, StringComparer.Ordinal).ToList();

    /// <summary>The meta string of the pairs (the <c>$Source$</c> / <c>$Target$</c> form).</summary>
    public static string Format(IEnumerable<(string Source, string Target)> pairs) =>
        string.Concat(pairs.OrderBy(pair => pair.Source, StringComparer.Ordinal).Select(pair => $"{FirstMarker}{pair.Source}{SecondMarker}{pair.Target}"));

    /// <summary>Reads the meta string back into pairs; an unterminated entry is dropped.</summary>
    public static List<(string Source, string Target)> Parse(string meta)
    {
        var pairs = new List<(string, string)>();
        int at = 0;
        while (at < meta.Length)
        {
            int first = meta.IndexOf(FirstMarker, at, StringComparison.Ordinal);
            if (first < 0) break;
            int sourceStart = first + FirstMarker.Length;
            int second = meta.IndexOf(SecondMarker, sourceStart, StringComparison.Ordinal);
            if (second < 0) break;
            int targetStart = second + SecondMarker.Length;
            int next = meta.IndexOf(FirstMarker, targetStart, StringComparison.Ordinal);
            int targetEnd = next < 0 ? meta.Length: next;
            string source = meta[sourceStart..second];
            string target = meta[targetStart..targetEnd];
            if (source.Length > 0 && target.Length > 0) pairs.Add((source, target));
            at = targetEnd;
        }
        return pairs;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// SloppyAnimatsu's saved retargeting mappings:
/// one entry per pair of skeletons, keyed by <see cref="RetargetNames.Signature"/>, so a grid set up
/// between two rigs comes back for every pair of characters made from them, in either direction.
/// An entry holds name links between the two skeletons (a further link from a source that already
/// has one is a copy row) and the names each side set to "none" while it was the source. The file is
/// JSON beside SloppyAnimatsu's preferences; not part of any .akt file.
/// </summary>
public sealed class RetargetMappingStore
{
    public const string FileName = "retarget-mappings.json";

    /// <summary>The share of skeleton names two rigs must have in common to reuse a mapping saved for the other.</summary>
    public const double SimilarSkeleton = 0.9;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly FileData _data;

    public RetargetMappingStore(string path)
    {
        Path = path;
        _data = Read(path);
    }

    public string Path { get; }

    /// <summary><c>%APPDATA%\SloppyAnimatsu\retarget-mappings.json</c>.</summary>
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SloppyAnimatsu", FileName);

    /// <summary>The last save error, or null after a successful save.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// The rows and copy rows saved for this pair's skeletons, resolved by skeleton name; null when no
    /// entry applies. A row the entry says nothing about keeps its <paramref name="fallback"/> target
    /// when no saved link already holds it.
    /// </summary>
    public (IReadOnlyList<RetargetSelection> Rows, IReadOnlyList<RetargetSelection> Copies)? Load(
        Character source, Character target, IReadOnlyList<RetargetSelection> fallback)
    {
        var (entry, forward) = Find(source, target);
        if (entry == null) return null;

        var sourceByName = ByName(source);
        var targetByName = ByName(target);
        var none = new HashSet<string>(forward ? entry.NoneA : entry.NoneB, StringComparer.Ordinal);
        var main = new Dictionary<Object3D, Object3D>(ReferenceEqualityComparer.Instance);
        var copies = new List<RetargetSelection>();
        var usedTargets = new HashSet<Object3D>(ReferenceEqualityComparer.Instance);
        int resolved = 0;
        foreach (var link in entry.Links)
        {
            if (link.Length != 2) continue;
            string from = forward ? link[0] : link[1];
            string to = forward ? link[1] : link[0];
            if (!sourceByName.TryGetValue(from, out var s) || !targetByName.TryGetValue(to, out var t)) continue;
            if (!usedTargets.Add(t)) continue;
            resolved++;
            if (!main.TryAdd(s, t)) copies.Add(new RetargetSelection(s, t));
        }
        if (resolved == 0 && none.Count == 0) return null;

        var rows = new List<RetargetSelection>(fallback.Count);
        foreach (var row in fallback)
        {
            if (main.TryGetValue(row.Source, out var t)) rows.Add(row with { Target = t });
            else if (none.Contains(RetargetNames.SkeletonName(source, row.Source))) rows.Add(row with { Target = null });
            else if (row.Target is { } auto && usedTargets.Add(auto)) rows.Add(row);
            else rows.Add(row with { Target = null });
        }
        return (rows, copies);
    }

    /// <summary>Records the grid of the pair (its rows and copy rows) and writes the file.</summary>
    public bool Save(Character source, Character target, IReadOnlyList<RetargetSelection> rows,
        IReadOnlyList<RetargetSelection> copies)
    {
        string sourceSig = RetargetNames.Signature(source);
        string targetSig = RetargetNames.Signature(target);
        bool forward = string.CompareOrdinal(sourceSig, targetSig) <= 0;
        string key = forward ? $"{sourceSig}|{targetSig}" : $"{targetSig}|{sourceSig}";
        _data.Skeletons[sourceSig] = Names(source);
        _data.Skeletons[targetSig] = Names(target);
        if (!_data.Mappings.TryGetValue(key, out var entry)) _data.Mappings[key] = entry = new Entry();

        var links = new List<string[]>();
        var noneNames = new List<string>();
        foreach (var row in rows)
        {
            string from = RetargetNames.SkeletonName(source, row.Source);
            if (row.Target is { } t) links.Add(Link(from, RetargetNames.SkeletonName(target, t)));
            else noneNames.Add(from);
        }
        foreach (var copy in copies)
            if (copy.Target is { } t)
                links.Add(Link(RetargetNames.SkeletonName(source, copy.Source), RetargetNames.SkeletonName(target, t)));
        entry.Links = links;
        if (forward) entry.NoneA = noneNames;
        else entry.NoneB = noneNames;
        return Write();

        string[] Link(string from, string to) => forward ? [from, to] : [to, from];
    }

    /// <summary>The Retarget window's Mirror toggle, kept in the same file.</summary>
    public bool Mirror => _data.Mirror;

    /// <summary>Records the Mirror toggle and writes the file.</summary>
    public bool SaveMirror(bool mirror)
    {
        _data.Mirror = mirror;
        return Write();
    }

    private bool Write()
    {
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(Path, JsonSerializer.Serialize(_data, Json));
            LastError = null;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LastError = e.Message;
            return false;
        }
    }

    private (Entry? Entry, bool Forward) Find(Character source, Character target)
    {
        string sourceSig = RetargetNames.Signature(source);
        string targetSig = RetargetNames.Signature(target);
        if (string.CompareOrdinal(sourceSig, targetSig) <= 0)
        {
            if (_data.Mappings.TryGetValue($"{sourceSig}|{targetSig}", out var forward)) return (forward, true);
        }
        else if (_data.Mappings.TryGetValue($"{targetSig}|{sourceSig}", out var backward)) return (backward, false);

        // No entry for these exact rigs: the best one whose two skeletons nearly match ours.
        var sourceNames = new HashSet<string>(Names(source), StringComparer.Ordinal);
        var targetNames = new HashSet<string>(Names(target), StringComparer.Ordinal);
        Entry? best = null;
        bool bestForward = true;
        double bestScore = 0;
        foreach (var (key, entry) in _data.Mappings)
        {
            int bar = key.IndexOf('|');
            if (bar < 0) continue;
            if (!_data.Skeletons.TryGetValue(key[..bar], out var a)
                || !_data.Skeletons.TryGetValue(key[(bar + 1)..], out var b)) continue;
            double forwardScore = Math.Min(Similarity(sourceNames, a), Similarity(targetNames, b));
            double backwardScore = Math.Min(Similarity(sourceNames, b), Similarity(targetNames, a));
            double score = Math.Max(forwardScore, backwardScore);
            if (score < SimilarSkeleton || score <= bestScore) continue;
            best = entry;
            bestForward = forwardScore >= backwardScore;
            bestScore = score;
        }
        return (best, bestForward);
    }

    private static double Similarity(HashSet<string> ours, List<string> theirs)
    {
        if (ours.Count == 0 || theirs.Count == 0) return 0;
        int common = theirs.Count(ours.Contains);
        return (double)common / (ours.Count + theirs.Count - common);
    }

    private static List<string> Names(Character character) =>
        RetargetingMap.Candidates(character)
            .Select(candidate => RetargetNames.SkeletonName(character, candidate))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    private static Dictionary<string, Object3D> ByName(Character character)
    {
        var byName = new Dictionary<string, Object3D>(StringComparer.Ordinal);
        foreach (var candidate in RetargetingMap.Candidates(character))
            byName.TryAdd(RetargetNames.SkeletonName(character, candidate), candidate);
        return byName;
    }

    private static FileData Read(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<FileData>(File.ReadAllText(path), Json) ?? new FileData();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new FileData();
    }

    private sealed class FileData
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        /// <summary>Each skeleton signature's sorted names, for the near-match lookup.</summary>
        [JsonPropertyName("skeletons")] public Dictionary<string, List<string>> Skeletons { get; set; } = new();
        /// <summary>"signatureA|signatureB" (A sorts first) to its entry, links oriented A to B.</summary>
        [JsonPropertyName("mappings")] public Dictionary<string, Entry> Mappings { get; set; } = new();
        /// <summary>The Retarget window's Mirror toggle.</summary>
        [JsonPropertyName("mirror")] public bool Mirror { get; set; }
    }

    private sealed class Entry
    {
        [JsonPropertyName("links")] public List<string[]> Links { get; set; } = [];
        [JsonPropertyName("noneA")] public List<string> NoneA { get; set; } = [];
        [JsonPropertyName("noneB")] public List<string> NoneB { get; set; } = [];
    }
}

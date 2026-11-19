// Which tests a change can affect, for `TestRunner --changed`.
//
// There is no version control in this workspace, so "changed" means "written after the last
// finished run", compared by file time. Tests are chosen by a small map of features to test
// classes (tests/test-map.json); a changed source file that the map does not know runs everything,
// because a wrong guess that skips tests is worse than a slow run. The last run's failed classes
// are always added, so a fix is re-run even when no file changed.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestRunner;

internal sealed class TestMap
{
    public List<TestFeature> Features { get; set; } = new();
}

internal sealed class TestFeature
{
    public string Name { get; set; } = "";
    /// <summary>Repo-relative globs, `/` separated, `**` for any depth and `*` within a segment.</summary>
    public List<string> Sources { get; set; } = new();
    public List<string> Tests { get; set; } = new();
}

/// <summary>What the last finished run leaves behind for the next `--changed`.</summary>
internal sealed class RunBaseline
{
    public DateTimeOffset FinishedAt { get; set; }
    /// <summary>The SLOPPYANIMATSU_SAMPLES value the run used: a different sample set makes it unusable.</summary>
    public string? Samples { get; set; }
    /// <summary>Classes with a failing test in the last completed run.</summary>
    public List<string> Failed { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static RunBaseline? Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<RunBaseline>(File.ReadAllText(path), Json)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Write(string path, DateTimeOffset finishedAt, IEnumerable<string> failed)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new RunBaseline
            {
                FinishedAt = finishedAt,
                Samples = Environment.GetEnvironmentVariable("SLOPPYANIMATSU_SAMPLES"),
                Failed = failed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            }, Json));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"warning: cannot write the run baseline '{path}': {e.Message}");
        }
    }
}

internal sealed class ChangedScope
{
    /// <summary>Source trees whose changes can affect the tests (the viewer and CLI are not referenced by them).</summary>
    private static readonly string[] Trees = ["src/SloppyAnimatsu.Core", "src/SloppyAnimatsu.Rendering", "tests/SloppyAnimatsu.Tests"];

    public static string DefaultMapPath(string root) => Path.Combine(root, "tests", "test-map.json");

    public static string DefaultBaselinePath(string root) => Path.Combine(root, ".tmp-run", "test-baseline.json");

    /// <summary>
    /// The classes to run, or null for "all tests". <paramref name="explain"/> is printed for the
    /// user: which files changed, which test classes they select, and why an unknown file (or a
    /// missing baseline/map) turns the run into everything.
    /// </summary>
    public static HashSet<string>? Resolve(
        string root,
        string mapPath,
        string baselinePath,
        out List<string> explain)
    {
        explain = new List<string>();

        RunBaseline? baseline = RunBaseline.Read(baselinePath);
        if (baseline is null)
        {
            explain.Add($"no baseline at {Relative(root, baselinePath)} - every test runs; a green run records one");
            return null;
        }

        DateTime since = baseline.FinishedAt.UtcDateTime;
        string? samples = Environment.GetEnvironmentVariable("SLOPPYANIMATSU_SAMPLES");
        if (!string.Equals(baseline.Samples ?? "", samples ?? "", StringComparison.OrdinalIgnoreCase))
        {
            explain.Add("the last run used a different SLOPPYANIMATSU_SAMPLES - every test runs");
            return null;
        }
        List<string> changed = Trees
            .SelectMany(t => CSharpFiles(Path.Combine(root, t)))
            .Where(f => File.GetLastWriteTimeUtc(f) > since)
            .Select(f => Relative(root, f))
            .Order(StringComparer.Ordinal)
            .ToList();

        // A changed test class is always its own scope. TestFiles.cs is the shared sample loader,
        // so a change there can move every scene test.
        var classes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in changed.Where(f => f.StartsWith("tests/", StringComparison.Ordinal)))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name is "TestFiles")
            {
                explain.Add($"{file} is shared by every scene test - every test runs");
                return null;
            }
            classes.Add(name);
        }

        List<string> sources = changed.Where(f => !f.StartsWith("tests/", StringComparison.Ordinal)).ToList();
        if (sources.Count > 0)
        {
            TestMap? map = ReadMap(mapPath);
            if (map is null)
            {
                explain.Add($"no test map at {Relative(root, mapPath)} - every test runs");
                return null;
            }

            var features = map.Features.Select(f => (Feature: f, Globs: f.Sources.Select(Glob).ToList())).ToList();
            var unmatched = new List<string>();
            foreach (string source in sources)
            {
                var hits = features.Where(f => f.Globs.Any(g => g.IsMatch(source))).ToList();
                if (hits.Count == 0)
                {
                    unmatched.Add(source);
                    continue;
                }
                foreach (var hit in hits)
                {
                    if (hit.Feature.Tests.Contains("*"))
                    {
                        explain.Add($"{source} -> {hit.Feature.Name}: everything");
                        return null;
                    }
                    classes.UnionWith(hit.Feature.Tests);
                    explain.Add($"{source} -> {hit.Feature.Name}: {string.Join(", ", hit.Feature.Tests)}");
                }
            }

            if (unmatched.Count > 0)
            {
                explain.Add($"{string.Join(", ", unmatched)} match no feature in {Relative(root, mapPath)} - every test runs");
                return null;
            }
        }

        // A class that failed last time runs again even if no file is newer than the baseline.
        foreach (string failed in baseline.Failed)
        {
            string name = failed.Split('.')[0];
            if (name.Length > 0)
            {
                classes.Add(name);
                explain.Add($"{name} failed last run");
            }
        }

        if (classes.Count == 0)
            explain.Add($"nothing changed since {baseline.FinishedAt:yyyy-MM-dd HH:mm:ss zzz}");

        return classes;
    }

    private static TestMap? ReadMap(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<TestMap>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                : null;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"warning: cannot read the test map '{path}': {e.Message}");
            return null;
        }
    }

    private static IEnumerable<string> CSharpFiles(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (string file in Directory.EnumerateFiles(current, "*.cs"))
                yield return file;
            foreach (string child in Directory.EnumerateDirectories(current))
            {
                string name = Path.GetFileName(child);
                if (name is "bin" or "obj") continue;
                pending.Push(child);
            }
        }
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static readonly Dictionary<string, System.Text.RegularExpressions.Regex> s_globs = new(StringComparer.Ordinal);

    private static System.Text.RegularExpressions.Regex Glob(string pattern)
    {
        if (s_globs.TryGetValue(pattern, out var cached)) return cached;
        string escaped = System.Text.RegularExpressions.Regex.Escape(pattern.Replace('\\', '/'));
        string regex = "^" + escaped.Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*") + "$";
        var compiled = new System.Text.RegularExpressions.Regex(regex,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        s_globs[pattern] = compiled;
        return compiled;
    }
}

using System.Reflection;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using Xunit.Sdk;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// A theory over <see cref="TestFiles.SceneSamples"/>, skipped (instead of failing with "No data
/// found") when SLOPPYANIMATSU_SAMPLES names no scene.
/// </summary>
public sealed class SceneTheoryAttribute : TheoryAttribute
{
    public SceneTheoryAttribute()
    {
        if (TestFiles.SceneSamples.Count == 0)
            Skip = "Set SLOPPYANIMATSU_SAMPLES to one or more scene .akt files or folders.";
    }
}

/// <summary>Supplies <see cref="TestFiles.SceneSamples"/>; pair with <see cref="SceneTheoryAttribute"/>.</summary>
public sealed class SceneSamplesAttribute : DataAttribute
{
    public override IEnumerable<object[]> GetData(MethodInfo testMethod) =>
        TestFiles.SceneSamples.Select(f => new object[] { f });
}

/// <summary>
/// A theory over <see cref="TestFiles.AllSceneSamples"/> - every scene the user supplied - for the
/// checks that must see all of them (the rig undo snapshot), skipped when there is no scene.
/// </summary>
public sealed class AllSceneTheoryAttribute : TheoryAttribute
{
    public AllSceneTheoryAttribute()
    {
        if (TestFiles.AllSceneSamples.Count == 0)
            Skip = "Set SLOPPYANIMATSU_SAMPLES to one or more scene .akt files or folders.";
    }
}

/// <summary>Supplies <see cref="TestFiles.AllSceneSamples"/>; pair with <see cref="AllSceneTheoryAttribute"/>.</summary>
public sealed class AllSceneSamplesAttribute : DataAttribute
{
    public override IEnumerable<object[]> GetData(MethodInfo testMethod) =>
        TestFiles.AllSceneSamples.Select(f => new object[] { f });
}

/// <summary>
/// Locates sample files. Nothing is copied into the repository: the shipped Resource3D.akt is read
/// from the reference install next to SloppyAnimatsu, and extra user files come from the
/// SLOPPYANIMATSU_SAMPLES environment variable (';'-separated files or folders).
/// </summary>
public static class TestFiles
{
    public static string? Resource3D { get; } = FindResource3D();

    /// <summary>
    /// The <c>preferences.akt</c> the tests read: SLOPPYANIMATSU_PREFERENCES, else the frozen copy in
    /// <c>tests/SloppyAnimatsu.Tests/TestData</c>. Never
    /// The live settings file: the app and the viewer both rewrite that, and the dock-layout tests
    /// assert one fixed arrangement.
    /// </summary>
    public static string? Preferences { get; } = FirstExisting(
        Environment.GetEnvironmentVariable("SLOPPYANIMATSU_PREFERENCES"),
        FindTestData("preferences.akt"));

    private static string? FindTestData(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "SloppyAnimatsu.Tests", "TestData", name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// akeytsu's own shortcuts file (a compatibility input): from SLOPPYANIMATSU_SHORTCUTS, else akeytsu's
    /// install location. Null when this machine has none.
    /// </summary>
    public static string? Shortcuts { get; } = FirstExisting(
        Environment.GetEnvironmentVariable("SLOPPYANIMATSU_SHORTCUTS"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "akeytsu", "akeytsuShortcuts.akt"));

    /// <summary>
    /// The autosave of a brand-new scene, used as the reference output of File > New Scene
    /// Null when the machine has no autosaves.
    /// </summary>
    public static string? NewSceneAutosave { get; } = FirstExisting(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "akeytsu", "Autosave", "New_1_autosave_01.akt"));

    private static string? FirstExisting(params string?[] paths) =>
        paths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));

    private static string? FindResource3D()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "samples", "Resource3D.akt");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    public static IEnumerable<string> UserSamples()
    {
        string? env = Environment.GetEnvironmentVariable("SLOPPYANIMATSU_SAMPLES");
        if (string.IsNullOrWhiteSpace(env))
            yield break;
        foreach (var p in env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Directory.Exists(p))
                foreach (var f in SceneFiles.Enumerate(p))
                    yield return f;
            else if (File.Exists(p))
                yield return p;
        }
    }

    private static readonly Lazy<IReadOnlyList<string>> s_allSceneSamples = new(() =>
        UserSamples().Where(f => AktReader.ProbeRootClass(File.ReadAllBytes(f)) == "CScene").ToList());

    /// <summary>
    /// Every scene the user supplied (root <c>CScene</c>; preferences files and the like are left
    /// out). Only the checks that must see every file use this - format round-trips, the schema and
    /// the rig undo snapshot - because it reads all of them.
    /// </summary>
    public static IReadOnlyList<string> AllSceneSamples => s_allSceneSamples.Value;

    /// <summary>
    /// The scenes the feature theories run on: one per shape of test data instead of all of them.
    /// Running every scene test on every sample was 4,657 cases and 64 minutes; a
    /// feature needs a small rig with IK and reverse feet (<c>7_AddingIK</c>), the biggest animation
    /// bank (<c>19_run</c>, 107 animations) and a second character with more UV layers and a
    /// non-uniform leg scale. The format and schema theories and
    /// <see cref="AllSceneSamples"/> still read every file. Set <c>SLOPPYANIMATSU_ALL_SAMPLES=1</c> to
    /// run the feature theories on every scene again.
    /// </summary>
    public static IReadOnlyList<string> SceneSamples => s_sceneSamples.Value;

    private static readonly string[] s_featureSampleNames =
    [
        "7_AddingIK.akt",
        "19_run.akt",
        "DragonPrince_74.akt",
    ];

    private static readonly Lazy<IReadOnlyList<string>> s_sceneSamples = new(FeatureSamples);

    private static IReadOnlyList<string> FeatureSamples()
    {
        if (Environment.GetEnvironmentVariable("SLOPPYANIMATSU_ALL_SAMPLES") is { Length: > 0 } all && all != "0")
            return AllSceneSamples;
        var picked = s_featureSampleNames
            .Select(name => AllSceneSamples.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)))
            .OfType<string>()
            .ToList();
        // An unfamiliar sample set gets every scene: a feature test skipped for want of data is
        // worse than a slow run.
        return picked.Count >= 2 ? picked : AllSceneSamples;
    }

    public static TheoryData<string> AllSamples()
    {
        var data = new TheoryData<string>();
        if (Resource3D != null) data.Add(Resource3D);
        foreach (var f in UserSamples()) data.Add(f);
        return data;
    }

    /// <summary>
    /// Loads a scene for a test. Each test parses the file itself. Caching one parsed document and
    /// handing out deep copies was measured on this machine: on the 90 MB samples a copy costs
    /// about as much as a parse (10.3 s vs 7.0 s per test), and the cached documents kept ~10 GB
    /// alive per process, which made three parallel shards slower per test instead of faster.
    /// </summary>
    public static SceneModel LoadScene(string path) => SceneModel.Load(Path.GetFullPath(path));
}

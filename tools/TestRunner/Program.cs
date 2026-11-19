// In-process runner for the xUnit tests in tests/SloppyAnimatsu.Tests.
//
// VSTest normally starts a testhost.exe process per test assembly. In some environments that host
// dies before running a single test while opening its parent process ("Win32Exception (5): Access
// is denied", exception 0xe0434352), so `dotnet test` cannot be used. This tool loads the test
// assembly into its own process, discovers the [Fact]/[Theory] methods and invokes them the way
// xUnit does, so the same tests run where VSTest's host cannot start.
//
// It is a fallback, not a replacement: tests run one at a time, there are no class or collection
// fixtures (only IAsyncLifetime and IDisposable), and no TRX or coverage files are produced.

using System.Diagnostics;
using System.Reflection;
using TestRunner;
using Xunit;
using Xunit.Sdk;

internal static class Program
{
    private static int Main(string[] args)
    {
        string? filter = null;
        string? statusPath = null;
        string? mapPath = null;
        string? baselinePath = null;
        bool list = false;
        bool verbose = false;
        bool trace = false;
        bool noStatus = false;
        bool noBaseline = false;
        bool allowConcurrent = false;
        bool changed = false;
        (int Index, int Count)? shard = null;

        foreach (string arg in args)
        {
            if (arg is "--help" or "-h" or "/?")
            {
                PrintUsage();
                return 0;
            }
            if (arg == "--list")
                list = true;
            else if (arg is "--verbose" or "-v")
                verbose = true;
            else if (arg == "--trace")
                trace = true;
            else if (arg == "--no-status")
                noStatus = true;
            else if (arg == "--no-baseline")
                noBaseline = true;
            else if (arg == "--changed")
                changed = true;
            else if (arg == "--allow-concurrent")
                allowConcurrent = true;
            else if (arg == "--status")
                statusPath = DefaultStatusPath();
            else if (arg.StartsWith("--status=", StringComparison.Ordinal))
                statusPath = arg["--status=".Length..];
            else if (arg == "--map")
                mapPath = DefaultMapPath();
            else if (arg.StartsWith("--map=", StringComparison.Ordinal))
                mapPath = arg["--map=".Length..];
            else if (arg == "--baseline")
                baselinePath = DefaultBaselinePath();
            else if (arg.StartsWith("--baseline=", StringComparison.Ordinal))
                baselinePath = arg["--baseline=".Length..];
            else if (arg.StartsWith("--shard=", StringComparison.Ordinal))
            {
                if (!TryParseShard(arg["--shard=".Length..], out shard))
                {
                    Console.Error.WriteLine($"Invalid --shard value '{arg["--shard=".Length..]}'; use i/n, for example --shard=2/4.");
                    return 2;
                }
            }
            else if (arg.StartsWith("--filter=", StringComparison.Ordinal))
                filter = arg["--filter=".Length..];
            else
            {
                Console.Error.WriteLine($"Unknown argument '{arg}'.");
                Console.Error.WriteLine();
                PrintUsage();
                return 2;
            }
        }

        string root = RepositoryRoot();
        string defaultStatus = DefaultStatusPath();
        string defaultBaseline = baselinePath ?? ChangedScope.DefaultBaselinePath(root);

        HashSet<string>? classes = null;
        if (changed)
        {
            classes = ChangedScope.Resolve(root, mapPath ?? ChangedScope.DefaultMapPath(root), defaultBaseline, out List<string> explain);
            Console.WriteLine($"Changed scope from {Path.GetRelativePath(root, defaultBaseline).Replace('\\', '/')}: " +
                (classes is null ? "every test" : $"{classes.Count} test classes"));
            foreach (string line in explain.Take(25))
                Console.WriteLine($"  {line}");
            if (explain.Count > 25)
                Console.WriteLine($"  ... and {explain.Count - 25} more");
            if (classes is not null && classes.Count == 0)
            {
                Console.WriteLine("Nothing changed and the last run had no failures - nothing to run.");
                return 0;
            }
        }

        Assembly assembly = typeof(SloppyAnimatsu.Tests.TestFiles).Assembly;
        // A shard gets its own file beside the normal one, unless the caller names one: the
        // parallel driver passes one per shard.
        string? statusFile = noStatus ? null : statusPath ?? ShardStatusPath(defaultStatus, shard);

        if (statusFile != null && !allowConcurrent)
        {
            foreach (string candidate in new[] { defaultStatus, statusFile }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                TestRunStatus? previous = TestRunStatus.Read(candidate);
                if (previous is not { State: "running" })
                    continue;

                if (IsAlive(previous.Pid))
                {
                    Console.Error.WriteLine(
                        $"Another TestRunner is still running: pid {previous.Pid}, started {previous.StartedAt}, " +
                        $"current {previous.Current ?? "(between tests)"}, last update {previous.UpdatedAt}.");
                    Console.Error.WriteLine(
                        $"Its status file is '{candidate}'. Stop that process, or pass --allow-concurrent.");
                    return 3;
                }

                Console.WriteLine($"note: ignoring the stale status of pid {previous.Pid} in '{candidate}' (it is no longer running).");
            }
        }

        int filtered;
        if (list)
        {
            List<TestCase> listed = OrderByScene(CollectCases(assembly, filter, classes, null, out filtered));
            if (shard is { } listedShard)
                listed = Partition(listed, listedShard.Index, listedShard.Count);
            foreach (TestCase test in listed)
                Console.WriteLine(test.Name);

            Console.WriteLine();
            Console.WriteLine($"{listed.Count} tests" +
                (filtered > 0 ? $", {filtered} filtered out" : ""));
            return listed.Count > 0 ? 0 : 1;
        }

        // Create the status before discovering the tests: a sample folder can take a long time to
        // enumerate, and that is otherwise a silent gap with no process output and no file.
        TestRunStatusWriter? status = null;
        if (statusFile != null)
        {
            var writer = new TestRunStatusWriter(statusFile, filter, trace);
            status = writer;
            Console.WriteLine($"Progress: {writer.FilePath}");
            Console.CancelKeyPress += (_, _) => writer.Finish(130, "aborted", "interrupted (Ctrl+C)");
        }

        List<TestCase> cases = CollectCases(assembly, filter, classes,
            (found, methods) => status?.Discovering(found, methods), out filtered);
        // One scene at a time: every scene test on a sample runs together, so the sample cache in
        // TestFiles holds one parsed document instead of re-parsing it for each test class.
        cases = OrderByScene(cases);
        if (shard is { } partition)
        {
            cases = Partition(cases, partition.Index, partition.Count);
            Console.WriteLine($"Shard {partition.Index + 1}/{partition.Count}: {cases.Count} tests.");
        }
        status?.Discovered(cases.Count, filtered);

        int passed = 0, failed = 0, skipped = 0;
        var failures = new List<string>();
        var failedNames = new List<string>();
        var clock = Stopwatch.StartNew();

        try
        {
            foreach (TestCase test in cases)
            {
                if (test.Fact.Skip is { } reason)
                {
                    skipped++;
                    status?.Skipped(test.Name);
                    Console.WriteLine($"SKIP {test.Name} ({reason})");
                    continue;
                }

                if (trace)
                {
                    Console.WriteLine($"RUN {test.Name}");
                    Console.Out.Flush();
                }
                status?.Started(test.Name);

                Exception? failure = Run(test.Type, test.Method, test.Data);
                if (failure is null)
                {
                    passed++;
                    status?.Passed(test.Name);
                    continue;
                }

                failed++;
                failedNames.Add(test.Name);
                status?.Failed(test.Name);
                failures.Add(Describe(test.Name, failure));
                Console.WriteLine($"FAIL {test.Name}");
                Console.WriteLine(verbose ? failure.ToString() : $"  {FirstLine(failure)}");
            }

            Console.WriteLine();
            foreach (string failure in failures)
                Console.WriteLine(failure + "\n");
            Console.WriteLine($"{passed} passed, {failed} failed, {skipped} skipped" +
                (filtered > 0 ? $", {filtered} filtered out" : "") +
                $" in {clock.Elapsed.TotalSeconds:0.0}s");

            int exitCode;
            if (passed + failed + skipped == 0)
            {
                Console.Error.WriteLine(filter == null ? "No tests found." : $"No tests match filter '{filter}'.");
                Console.Error.WriteLine("Theories fed by SLOPPYANIMATSU_SAMPLES only produce tests when samples are set.");
                exitCode = 1;
                status?.Finish(exitCode, "error", "no tests were selected");
            }
            else
            {
                exitCode = failed == 0 ? 0 : 1;
                status?.Finish(exitCode, exitCode == 0 ? "passed" : "failed");
            }

            // Remember when a whole run last finished, so `--changed` knows what is new. Only a
            // run that covered everything may move the baseline: a filtered, listed or sharded
            // run says nothing about the tests it did not run.
            bool completed = passed + failed + skipped == cases.Count;
            if (completed && cases.Count > 0 && filter == null && shard is null && !noBaseline)
            {
                RunBaseline.Write(defaultBaseline, DateTimeOffset.Now,
                    failedNames.Select(n => n.Split('.')[0]));
            }
            return exitCode;
        }
        catch (Exception e)
        {
            status?.Finish(1, "crashed", e.ToString());
            throw;
        }
        finally
        {
            status?.Dispose();
        }
    }

    /// <summary>Discovers the tests to run; reports progress so a slow sample-folder scan is visible.</summary>
    private static List<TestCase> CollectCases(Assembly assembly, string? filter, HashSet<string>? classes,
        Action<int, int>? progress, out int filtered)
    {
        var cases = new List<TestCase>();
        int filteredOut = 0, methodsSeen = 0;
        foreach (Type type in Types(assembly))
        {
            foreach (MethodInfo method in Methods(type))
            {
                methodsSeen++;
                if (!Matches(type, method, filter, classes))
                {
                    filteredOut++;
                    continue;
                }

                var fact = method.GetCustomAttributes().OfType<FactAttribute>().First();
                foreach (object[] data in Rows(method, fact))
                    cases.Add(new TestCase(type, method, data, fact, Name(type, method, data)));
                progress?.Invoke(cases.Count, methodsSeen);
            }
        }

        filtered = filteredOut;
        return cases;
    }

    /// <summary>The scene a test row runs on, or "" for a test that does not take a scene file.</summary>
    private static string SceneKey(TestCase test) =>
        test.Data.Length == 1 && test.Data[0] is string path &&
        (path.EndsWith(".akt", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slopa", StringComparison.OrdinalIgnoreCase))
            ? path
            : "";

    /// <summary>
    /// The run order: one sample at a time (non-scene tests first). Each test parses the file it
    /// needs, so this is not a cache; keeping a scene's cases together lets the shard partition
    /// hand every group's cases evenly to all shards.
    /// </summary>
    private static List<TestCase> OrderByScene(List<TestCase> cases)
    {
        var groups = new SortedDictionary<string, List<TestCase>>(StringComparer.OrdinalIgnoreCase);
        foreach (TestCase test in cases)
        {
            string key = SceneKey(test);
            if (!groups.TryGetValue(key, out List<TestCase>? group))
                groups[key] = group = new List<TestCase>();
            group.Add(test);
        }
        return groups.Values.SelectMany(g => g).ToList();
    }

    /// <summary>
    /// One shard's share of the cases. Every case parses its own scene
    /// (<c>TestFiles.LoadScene</c>), so cases do not have to stay together: dealing them
    /// round-robin gives each shard an equal share of every scene group. That matters on the
    /// 17-90 MB samples, where one group alone would otherwise own a shard. Whole groups were
    /// dealt out while a parsed sample was cached; that cache is gone (see TestFiles.LoadScene).
    /// </summary>
    private static List<TestCase> Partition(List<TestCase> cases, int index, int count)
    {
        var share = new List<TestCase>((cases.Count + count - 1) / count);
        for (int i = index; i < cases.Count; i += count)
            share.Add(cases[i]);
        return share;
    }

    private static bool TryParseShard(string text, out (int Index, int Count)? shard)
    {
        shard = null;
        string[] parts = text.Split('/');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out int number) ||
            !int.TryParse(parts[1], out int count) ||
            count < 1 || number < 1 || number > count)
            return false;
        shard = (number - 1, count);
        return true;
    }

    /// <summary>A shard writes its own status file beside the normal one, so shards do not overwrite each other.</summary>
    private static string? ShardStatusPath(string? path, (int Index, int Count)? shard)
    {
        if (path == null || shard is not { } partition)
            return path;
        string extension = Path.GetExtension(path);
        string stem = path[..^extension.Length];
        return $"{stem}.s{partition.Index + 1}of{partition.Count}{extension}";
    }

    /// <summary>The folder holding SloppyAnimatsu.sln, from the binary or the current directory upward.</summary>
    private static string RepositoryRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            DirectoryInfo? directory = new(start);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SloppyAnimatsu.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        return Environment.CurrentDirectory;
    }

    private static string DefaultMapPath() => ChangedScope.DefaultMapPath(RepositoryRoot());

    private static string DefaultBaselinePath() => ChangedScope.DefaultBaselinePath(RepositoryRoot());

    /// <summary>Concrete classes holding tests, in a stable order.</summary>
    private static IEnumerable<Type> Types(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

    private static IEnumerable<MethodInfo> Methods(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttributes().Any(a => a is FactAttribute))
            .OrderBy(m => m.Name, StringComparer.Ordinal);

    private static bool Matches(Type type, MethodInfo method, string? filter, HashSet<string>? classes) =>
        (classes is null || classes.Contains(type.Name)) &&
        (filter is null || filter.Length == 0 ||
         type.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
         method.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

    /// <summary>One row per test: a single empty row for a fact, one per theory data item.</summary>
    private static IEnumerable<object[]> Rows(MethodInfo method, FactAttribute fact)
    {
        if (fact is not TheoryAttribute)
            return [[]];

        List<DataAttribute> attributes = method.GetCustomAttributes().OfType<DataAttribute>().ToList();
        // xUnit reports a theory without data as an error; with no row at all it would silently
        // disappear here, so invoke it with no arguments and let the missing parameters fail it.
        if (attributes.Count == 0)
            return [[]];

        return attributes.SelectMany(a => a.GetData(method));
    }

    /// <summary>Invokes one test; returns the exception it threw, or null when it passed.</summary>
    private static Exception? Run(Type type, MethodInfo method, object[] data)
    {
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(type);
            if (instance is IAsyncLifetime lifetime)
                lifetime.InitializeAsync().GetAwaiter().GetResult();

            object? result = method.Invoke(instance, data);
            if (result is Task task)
                task.GetAwaiter().GetResult();
            return null;
        }
        catch (Exception e)
        {
            return e is TargetInvocationException { InnerException: { } inner } ? inner : e;
        }
        finally
        {
            Release(instance);
        }
    }

    /// <summary>Runs the test class's own teardown, keeping a failure there from hiding the result.</summary>
    private static void Release(object? instance)
    {
        try
        {
            switch (instance)
            {
                case IAsyncLifetime lifetime:
                    lifetime.DisposeAsync().GetAwaiter().GetResult();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"  dispose failed: {FirstLine(e)}");
        }
    }

    private static string Describe(string name, Exception failure)
    {
        string message = $"{name}\n  {FirstLine(failure)}";
        StackFrame? frame = new StackTrace(failure, true).GetFrames()
            .FirstOrDefault(f => f.GetMethod()?.DeclaringType?.Namespace?.StartsWith("SloppyAnimatsu.Tests") == true);
        if (frame != null)
            message += $"\n  at {frame.GetMethod()!.DeclaringType!.Name}.{frame.GetMethod()!.Name} line {frame.GetFileLineNumber()}";
        return message;
    }

    private static string FirstLine(Exception e) => $"{e.GetType().Name}: {e.Message.Split('\n')[0]}";

    private static string Name(Type type, MethodInfo method, object[] data) =>
        $"{type.Name}.{method.Name}" + (data.Length > 0 ? $"({string.Join(", ", data.Select(Short))})" : "");

    /// <summary>Where testrunner-status.json lives when --status is not given.</summary>
    private static string DefaultStatusPath()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("SLOPPYANIMATSU_TEST_STATUS");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return Environment.ExpandEnvironmentVariables(fromEnvironment);

        // Find the repository by its solution file, so the status is in the same place no matter
        // which directory the runner was started from.
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            DirectoryInfo? directory = new(start);
            while (directory != null)
            {
                if (File.Exists(System.IO.Path.Combine(directory.FullName, "SloppyAnimatsu.sln")))
                    return System.IO.Path.Combine(directory.FullName, "testrunner-status.json");
                directory = directory.Parent;
            }
        }

        return System.IO.Path.Combine(Environment.CurrentDirectory, "testrunner-status.json");
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private sealed record TestCase(Type Type, MethodInfo Method, object[] Data, FactAttribute Fact, string Name);

    private static string Short(object? o) =>
        o is string s ? (s.Length > 60 ? "..." + s[^57..] : s) : o?.ToString() ?? "null";

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Runs the SloppyAnimatsu xUnit tests in-process, for machines where VSTest's testhost
            cannot start (it crashes with "Win32Exception (5): Access is denied").

            Usage:
              dotnet run --project tools/TestRunner -- [options]

            Options:
              --filter=text  Run only tests whose class or method name contains text
                             (case-insensitive), for example --filter=KeyCopyPaste.
              --changed      Run only the test classes that the files written since the last
                             finished run can affect (tests/test-map.json), plus last run's
                             failures. A changed file with no feature in the map, a different
                             sample set or no baseline runs everything. A completed run records
                             a baseline in .tmp-run/test-baseline.json.
              --map[=file]   The change map (default: tests/test-map.json).
              --baseline[=file]  The run baseline (default: .tmp-run/test-baseline.json).
              --no-baseline  Do not write the baseline after this run.
              --shard=i/n    Run only shard i of n (1-based; the cases are dealt round-robin so
                             every shard gets an equal share of each scene). Writes its own status
                             file and never moves the baseline; run-parallel.ps1 drives it.
              --list         List the tests that would run instead of running them.
              --trace        Print each test's name as it starts (finding a hanging test).
              --verbose, -v  Print the full stack trace of each failure.
              --status[=file]  Write live progress to file (default: testrunner-status.json
                             beside SloppyAnimatsu.sln, or $SLOPPYANIMATSU_TEST_STATUS). On by default;
                             rewritten after every test and every 5 seconds.
              --no-status    Do not write a status file.
              --allow-concurrent
                             Start even when the status file belongs to a live TestRunner.
              -h, --help     Show this help.

            Tests run one sample at a time (all tests of one scene together), because a scene is
            parsed once and copied per test; that is what makes the full suite fast.

            A second run refuses to start (exit code 3) while the status file names a live
            process, so a detached old run cannot silently hold tools/TestRunner/bin. The file
            is what tools/TestRunner/status.ps1 prints.

            Environment:
              SLOPPYANIMATSU_SAMPLES  ';'-separated scene files or folders, as for `dotnet test`.
            """);
    }
}

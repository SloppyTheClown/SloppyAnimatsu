// Live progress of a TestRunner process, kept in one JSON file.
//
// The console is not a reliable place to watch a long run from: the shell can return while the
// runner continues, and a detached process has no visible output at all. This file is rewritten
// after every test and by a five-second heartbeat, so "still running", "hung" and "finished" can
// be told apart from outside the process (tools/TestRunner/status.ps1 prints it).

using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>The on-disk shape of testrunner-status.json.</summary>
internal sealed class TestRunStatus
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Tool { get; set; } = "SloppyAnimatsu TestRunner";
    public string State { get; set; } = "running";
    public int Pid { get; set; }
    public string StartedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string? LastProgressAt { get; set; }
    public double ElapsedSeconds { get; set; }
    public double WorkingSetMB { get; set; }
    public double ManagedMB { get; set; }
    public double PeakWorkingSetMB { get; set; }
    public string? Filter { get; set; }
    public string? Current { get; set; }
    public string? Last { get; set; }
    public string? LastResult { get; set; }
    public int Total { get; set; }
    public int Completed { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int FilteredOut { get; set; }
    public bool Trace { get; set; }
    public string? Samples { get; set; }
    public string? WorkingDirectory { get; set; }
    public int? ExitCode { get; set; }
    public string? Message { get; set; }

    public static TestRunStatus? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<TestRunStatus>(File.ReadAllText(path), Json);
        }
        catch
        {
            // A half-written or hand-edited file is not worth failing a test run over.
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>Writes the status file after each change and on a heartbeat until the run ends.</summary>
internal sealed class TestRunStatusWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TestRunStatus _status;
    private readonly Timer? _heartbeat;
    private bool _finished;
    private bool _warned;

    public TestRunStatusWriter(string path, string? filter, bool trace)
    {
        string startedAt = DateTimeOffset.Now.ToString("O");
        _path = Path.GetFullPath(path);
        _status = new TestRunStatus
        {
            Pid = Environment.ProcessId,
            StartedAt = startedAt,
            LastProgressAt = startedAt,
            Filter = filter,
            Current = "discovering tests",
            Trace = trace,
            Samples = Environment.GetEnvironmentVariable("SLOPPYANIMATSU_SAMPLES"),
            WorkingDirectory = Environment.CurrentDirectory,
        };
        Stamp();
        WriteLocked();
        _heartbeat = new Timer(_ => Beat(), null, 5000, 5000);
    }

    public string FilePath => _path;

    public void Discovering(int found, int methodsSeen) => Update(status =>
    {
        status.Total = found;
        status.Current = $"discovering tests ({found} found, {methodsSeen} methods checked)";
        Touch(status);
    });

    public void Discovered(int total, int filteredOut) => Update(status =>
    {
        status.Total = total;
        status.FilteredOut = filteredOut;
        status.Current = null;
        Touch(status);
    });

    public void Started(string name) => Update(status =>
    {
        status.Current = name;
        Touch(status);
    });

    public void Passed(string name) => Update(status =>
    {
        status.Current = null;
        status.Completed++;
        status.Passed++;
        status.Last = name;
        status.LastResult = "passed";
        Touch(status);
    });

    public void Failed(string name) => Update(status =>
    {
        status.Current = null;
        status.Completed++;
        status.Failed++;
        status.Last = name;
        status.LastResult = "failed";
        Touch(status);
    });

    public void Skipped(string name) => Update(status =>
    {
        status.Current = null;
        status.Completed++;
        status.Skipped++;
        status.Last = name;
        status.LastResult = "skipped";
        Touch(status);
    });

    public void Finish(int exitCode, string state, string? message = null)
    {
        lock (_gate)
        {
            if (_finished)
                return;
            _finished = true;
            _status.State = state;
            _status.ExitCode = exitCode;
            _status.Message = message;
            _status.Current = null;
            Stamp();
            WriteLocked();
        }
        _heartbeat?.Dispose();
    }

    public void Dispose() => Finish(1, "aborted", "the runner stopped without reporting a result");

    /// <summary>Marks test progress. The heartbeat does not call this, so a stopped count stands out.</summary>
    private static void Touch(TestRunStatus status) =>
        status.LastProgressAt = DateTimeOffset.Now.ToString("O");

    private void Beat()
    {
        lock (_gate)
        {
            if (_finished)
                return;
            Stamp();
            WriteLocked();
        }
    }

    private void Update(Action<TestRunStatus> change)
    {
        lock (_gate)
        {
            if (_finished)
                return;
            change(_status);
            Stamp();
            WriteLocked();
        }
    }

    private void Stamp()
    {
        _status.UpdatedAt = DateTimeOffset.Now.ToString("O");
        _status.ElapsedSeconds = Math.Round(_clock.Elapsed.TotalSeconds, 1);
        _status.WorkingSetMB = Math.Round(Environment.WorkingSet / (1024.0 * 1024.0), 1);
        _status.ManagedMB = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 1);
        _status.PeakWorkingSetMB = Math.Max(_status.PeakWorkingSetMB, _status.WorkingSetMB);
    }

    private void WriteLocked()
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // A watcher (status.ps1 / watch.ps1) may be reading the file at the instant it is replaced,
        // and a reader that opened it without FileShare.Delete makes File.Move fail. That collision
        // is transient (the watcher refreshes every few seconds), so retry instead of warning; a
        // watcher left open must not fill the runner's stderr with "cannot write the status file".
        string temporary = _path + "." + Environment.ProcessId + ".tmp";
        Exception? last = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                File.WriteAllText(temporary, _status.ToJson());
                File.Move(temporary, _path, overwrite: true);
                _warned = false;
                return;
            }
            catch (Exception e)
            {
                last = e;
                try { File.Delete(temporary); } catch { }
                if (attempt < 5)
                    Thread.Sleep(25);
            }
        }
        if (_warned)
            return;
        _warned = true;
        Console.Error.WriteLine($"warning: cannot write the status file '{_path}': {last?.Message}");
    }
}
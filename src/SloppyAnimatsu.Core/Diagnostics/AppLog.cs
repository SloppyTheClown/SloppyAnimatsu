using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace SloppyAnimatsu.Core.Diagnostics;

/// <summary>
/// The run's log file. Every process start creates a new file, the folder keeps the last
/// <see cref="KeepFiles"/> of them, and one file never grows past <see cref="MaxFileBytes"/>.
///
/// A crash is always written with the full <see cref="Exception.ToString"/> - every frame and
/// every inner exception - because that text is what a bug report can carry. The file keeps a
/// reserve for it (an eighth of the limit, up to 512 KiB), so a crash is recorded even when the
/// ordinary lines got there first. Standard output and standard error are copied in as well, so a
/// viewer started from Explorer (whose console is hidden) still records what it printed.
///
/// The logger never throws and never ends a run that could have continued: a machine with no
/// writable folder simply gets no log.
/// </summary>
public static class AppLog
{
    /// <summary>The most one log file may hold: 4 MiB, tens of thousands of lines.</summary>
    public const long MaxFileBytes = 4L * 1024 * 1024;

    /// <summary>
    /// How many run logs of one role the folder keeps, the current one included: this run's and the
    /// previous run's. A viewer start never deletes a CLI log, and the other way round.
    /// </summary>
    public const int KeepFiles = 2;

    /// <summary>
    /// A folder to use instead of the default one. It is tried first; if it cannot be written the
    /// usual fallbacks are still tried, so a bad value never leaves the run without a log.
    /// </summary>
    public const string FolderVariable = "SLOPPYANIMATSU_LOG_DIR";

    /// <summary>Set to <c>0</c> to keep standard output and standard error out of the log.</summary>
    public const string CaptureVariable = "SLOPPYANIMATSU_LOG_CONSOLE";

    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly object Gate = new();

    private static StreamWriter? _writer;
    private static long _bytes;
    private static long _limit = MaxFileBytes;
    private static long _sequence;
    private static bool _full;
    private static bool _hooked;
    private static Exception? _crashed;

    /// <summary>The folder of this run's log, or null when the run has no log.</summary>
    public static string? FolderPath { get; private set; }

    /// <summary>The file of this run's log, or null when the run has no log.</summary>
    public static string? FilePath { get; private set; }

    /// <summary>The previous run's log of the same role, or null when there is none.</summary>
    public static string? PreviousFilePath { get; private set; }

    /// <summary>What kind of run this is ("viewer", "cli"), for the header and the file name.</summary>
    public static string Role { get; private set; } = "app";

    /// <summary>Whether a log file is open.</summary>
    public static bool IsActive
    {
        get
        {
            lock (Gate)
            {
                return _writer != null;
            }
        }
    }

    /// <summary>
    /// Opens this run's log. Call it once, first thing in <c>Main</c>, so a crash during start-up is
    /// recorded too. Calling it again starts a new file (that is what a fresh in-process run means).
    /// </summary>
    public static void Start(string role, IReadOnlyList<string> args)
        => Start(role, args, CandidateFolders(), CaptureConsole(), MaxFileBytes);

    /// <summary>
    /// The test entry point: one explicit folder, no console capture. The size limit is small in
    /// tests so the cap can be reached without writing megabytes.
    /// </summary>
    internal static void StartForTesting(string folder, string role, IReadOnlyList<string> args,
        long limit = MaxFileBytes)
        => Start(role, args, [folder], false, limit);

    private static void Start(string role, IReadOnlyList<string> args, IEnumerable<string> folders,
        bool captureConsole, long limit)
    {
        lock (Gate)
        {
            try
            {
                CloseWriter();
                Role = string.IsNullOrWhiteSpace(role) ? "app" : role.Trim();
                _bytes = 0;
                _limit = Math.Max(1024L, limit);
                _full = false;
                _crashed = null;
                FilePath = null;
                FolderPath = null;
                PreviousFilePath = null;
                foreach (string folder in folders)
                {
                    if (!TryOpen(folder))
                    {
                        continue;
                    }

                    Header(args);
                    break;
                }
            }
            catch (Exception)
            {
                // Logging must never take the program down with it.
                Disable();
            }

            if (captureConsole)
            {
                TeeConsole();
            }

            Hook();
        }
    }

    /// <summary>An informational line.</summary>
    public static void Info(string message) => WriteLine("info", message);

    /// <summary>Something the user did: an edit, an undo or redo, a command, a file opened or saved.</summary>
    public static void Action(string message) => WriteLine("action", message);

    /// <summary>Something the user should know about but that did not stop the run.</summary>
    public static void Warn(string message) => WriteLine("warn", message);

    /// <summary>Something that failed.</summary>
    public static void Error(string message) => WriteLine("error", message);

    /// <summary>
    /// A crash: the full stack trace of <paramref name="exception"/>, inner exceptions included.
    /// Call this from every handler that ends the run; ordinary lines stop before the reserve, so
    /// the trace still has room in a log the run had already filled.
    /// </summary>
    public static void Crash(Exception? exception, string what)
    {
        try
        {
            lock (Gate)
            {
                if (_writer == null) return;
                if (exception != null && ReferenceEquals(_crashed, exception)) return;
                _crashed = exception;
                Append(Line("crash", what) + Environment.NewLine, force: true);
                Append(exception == null
                    ? "    (no exception object was supplied)" + Environment.NewLine
                    : exception + Environment.NewLine + Environment.NewLine, force: true);
                _writer.Flush();
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Writes a footer and closes the file. Safe to call more than once.</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            try
            {
                CloseWriter();
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// The text of a log file, read while this run (or another) still writes it. Only the last
    /// <paramref name="maxChars"/> characters are returned, cut at a line start; null when the file
    /// cannot be read.
    /// </summary>
    public static string? ReadShared(string? path, int maxChars = 512 * 1024)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            Flush();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Utf8);
            string text = reader.ReadToEnd();
            if (text.Length <= maxChars) return text;
            int start = text.Length - maxChars;
            int line = text.IndexOf('\n', start);
            return "[... earlier lines omitted ...]" + Environment.NewLine
                   + text[(line >= 0 ? line + 1 : start)..];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Flushes what has been written so far.</summary>
    public static void Flush()
    {
        lock (Gate)
        {
            try
            {
                _writer?.Flush();
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>A console line, copied into the log without its own timestamp.</summary>
    private static void ConsoleLine(string line)
    {
        try
        {
            lock (Gate)
            {
                if (_writer == null || _full) return;
                Append("        | " + line + Environment.NewLine, force: false);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void WriteLine(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                if (_writer == null) return;
                Append(Line(level, message) + Environment.NewLine, force: false);
            }
        }
        catch (Exception)
        {
        }
    }

    private static string Line(string level, string message)
        => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
           + " [" + level + "] " + message;

    /// <summary>Room kept free of ordinary lines for a crash's stack trace.</summary>
    private static long Reserve => Math.Min(512L * 1024, Math.Max(1024L, _limit / 8));

    /// <summary>Where ordinary lines stop; <see cref="_limit"/> stays the hard cap.</summary>
    private static long NormalLimit => Math.Max(0L, _limit - Reserve);

    /// <summary>Appends text, keeping the file inside the size limit. The caller holds the gate.</summary>
    private static void Append(string text, bool force)
    {
        if (_writer == null) return;
        if (_full && !force) return;

        long room = (force ? _limit : NormalLimit) - _bytes;
        int size = Utf8.GetByteCount(text);
        if (size <= room)
        {
            WriteChunk(text, size);
            return;
        }

        if (_full)
        {
            // Ordinary lines already filled the file: a crash still gets what the hard limit
            // leaves, with the part that did not fit marked as cut.
            string tail = Environment.NewLine + "[... the rest of the crash report did not fit ...]"
                          + Environment.NewLine;
            long textRoom = room - Utf8.GetByteCount(tail);
            if (textRoom <= 0) return;
            WriteChunk(text, textRoom);
            WriteChunk(tail, Utf8.GetByteCount(tail));
            return;
        }

        _full = true;

        string marker = Environment.NewLine
                        + $"[... the rest was dropped: the log reached its {_limit:N0}-byte limit ...]"
                        + Environment.NewLine;
        long markerSize = Utf8.GetByteCount(marker);
        long available = room - markerSize;
        if (available <= 0)
        {
            WriteChunk(marker, room);
            return;
        }

        WriteChunk(text, available);
        WriteChunk(marker, markerSize);
    }

    /// <summary>Appends at most <paramref name="maxBytes"/> of <paramref name="text"/>.</summary>
    private static void WriteChunk(string text, long maxBytes)
    {
        if (_writer == null || maxBytes <= 0 || text.Length == 0) return;
        int chars = text.Length;
        if (Utf8.GetByteCount(text) > maxBytes)
        {
            int low = 0;
            int high = text.Length;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (Utf8.GetByteCount(text.AsSpan(0, middle)) <= maxBytes) low = middle;
                else high = middle - 1;
            }

            chars = low;
            if (chars > 0 && char.IsHighSurrogate(text[chars - 1])) chars--;
        }

        if (chars <= 0) return;
        try
        {
            _writer.Write(text.AsSpan(0, chars));
            _bytes += Utf8.GetByteCount(text.AsSpan(0, chars));
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            Disable();
        }
    }

    private static void Header(IReadOnlyList<string> args)
    {
        WriteLine("start", $"{BuildInfo.Name} {Role}, process {Environment.ProcessId}, {args.Count} argument(s)");
        WriteLine("start", $"{RuntimeInformation.OSDescription}; {RuntimeInformation.FrameworkDescription}; "
                            + $"{RuntimeInformation.ProcessArchitecture}");
        WriteLine("start", "command: " + Environment.CommandLine);
        WriteLine("start", $"folder: {AppContext.BaseDirectory}");
        WriteLine("start", $"working: {Environment.CurrentDirectory}");
        WriteLine("start", $"log: {FilePath} (limit {_limit:N0} bytes, keeping {KeepFiles} run logs)");
    }

    private static void CloseWriter()
    {
        if (_writer == null) return;
        try
        {
            Append(Line("exit", "run ended") + Environment.NewLine, force: true);
            _writer.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
        }

        try
        {
            _writer.Dispose();
        }
        catch (Exception)
        {
        }

        _writer = null;
        _full = false;
    }

    private static void Disable()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception)
        {
        }

        _writer = null;
    }

    /// <summary>Creates the folder if needed, opens the file and deletes the runs that fell out.</summary>
    private static bool TryOpen(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Rotate(folder);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string stem = $"{FileName(Role)}-{stamp}-{Environment.ProcessId}";
            // The counter keeps two runs started in the same millisecond in order, so the name
            // still sorts oldest first (rotation keeps the names at the end).
            string path = Path.Combine(folder, $"{stem}-{++_sequence:D3}.log");
            while (File.Exists(path))
            {
                path = Path.Combine(folder, $"{stem}-{++_sequence:D3}.log");
            }

            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(stream, Utf8) { AutoFlush = true };
            FilePath = path;
            FolderPath = folder;
            return true;
        }
        catch (Exception)
        {
            // This folder cannot hold the log: the caller tries the next one.
            Disable();
            return false;
        }
    }

    /// <summary>
    /// Keeps the newest <see cref="KeepFiles"/> - 1 files of this run's role, so the new one makes
    /// <see cref="KeepFiles"/>; the newest kept one becomes <see cref="PreviousFilePath"/>.
    /// </summary>
    private static void Rotate(string folder)
    {
        try
        {
            string prefix = FileName(Role) + "-";
            var files = new DirectoryInfo(folder).GetFiles(prefix + "*.log")
                .Where(file => file.Name.Length > prefix.Length && char.IsAsciiDigit(file.Name[prefix.Length]))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                .ToList();
            PreviousFilePath = KeepFiles > 1 ? files.FirstOrDefault()?.FullName : null;
            foreach (FileInfo file in files.Skip(KeepFiles - 1))
            {
                try
                {
                    file.Delete();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Another run has the file open (or a scanner does). KeepFiles is best effort.
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static IEnumerable<string> CandidateFolders()
    {
        string? chosen = Environment.GetEnvironmentVariable(FolderVariable);
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            yield return chosen;
        }

        string? local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
        {
            yield return Path.Combine(local, "SloppyAnimatsu", "logs");
        }

        // A run whose program folder is on a drive the profile folder cannot be written from (a
        // network drive, a sandbox) falls back to a folder beside the program.
        yield return Path.Combine(AppFolder.Install, "logs");

        string? temp = null;
        try
        {
            temp = Path.GetTempPath();
        }
        catch (Exception)
        {
        }

        if (!string.IsNullOrEmpty(temp))
        {
            yield return Path.Combine(temp, "SloppyAnimatsu", "logs");
        }
    }

    private static bool CaptureConsole()
        => !string.Equals(Environment.GetEnvironmentVariable(CaptureVariable), "0", StringComparison.Ordinal);

    private static void TeeConsole()
    {
        try
        {
            if (Console.Out is not TeeWriter) Console.SetOut(new TeeWriter(Console.Out));
            if (Console.Error is not TeeWriter) Console.SetError(new TeeWriter(Console.Error));
        }
        catch (Exception)
        {
        }
    }

    private static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(
            e.ExceptionObject as Exception,
            e.IsTerminating
                ? "unhandled exception; the process is terminating"
                : "unhandled exception");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Crash(e.Exception, "unobserved task exception");
            e.SetObserved();
        };
    }

    private static string FileName(string role)
    {
        var builder = new StringBuilder(role.Length);
        foreach (char c in role)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        }

        return builder.Length == 0 ? "app" : builder.ToString();
    }

    /// <summary>
    /// Copies console text into the log a line at a time, so a stack trace written to standard
    /// error is recorded in full.
    /// </summary>
    private sealed class TeeWriter(TextWriter inner) : TextWriter
    {
        private readonly object _sync = new();
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            inner.Write(value);
            if (value == '\n')
            {
                FlushLine();
            }
            else if (value != '\r')
            {
                AppendLineText(value.ToString().AsSpan());
            }
        }

        public override void Write(string? value)
        {
            inner.Write(value);
            if (value is { Length: > 0 }) Capture(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            inner.Write(buffer, index, count);
            if (count > 0) Capture(new string(buffer, index, count));
        }

        public override void WriteLine()
        {
            inner.WriteLine();
            FlushLine();
        }

        public override void Flush() => inner.Flush();

        private void Capture(string text)
        {
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] is '\r')
                {
                    AppendLineText(text.AsSpan(start, i - start));
                    start = i + 1;
                }
                else if (text[i] is '\n')
                {
                    AppendLineText(text.AsSpan(start, i - start));
                    FlushLine();
                    start = i + 1;
                }
            }

            if (start < text.Length)
            {
                AppendLineText(text.AsSpan(start));
            }
        }

        private void AppendLineText(ReadOnlySpan<char> text)
        {
            if (text.IsEmpty) return;
            lock (_sync)
            {
                _line.Append(text);
                if (_line.Length >= 8192)
                {
                    FlushLineLocked();
                }
            }
        }

        private void FlushLine()
        {
            lock (_sync)
            {
                FlushLineLocked();
            }
        }

        private void FlushLineLocked()
        {
            if (_line.Length == 0) return;
            string line = _line.ToString();
            _line.Clear();
            ConsoleLine(line);
        }
    }
}

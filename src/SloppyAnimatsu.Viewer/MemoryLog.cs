using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// An opt-in memory trace, on when <c>SLOPPYANIMATSU_MEMORY_LOG</c> names a file: every two seconds one
/// line with private bytes, the GC heap, allocation rate, collections and per-frame cost, every ten
/// seconds the sampled allocations by type (<see cref="AllocationSampler"/>), and one
/// <see cref="Mark"/> line per history event (delete, undo, redo, document rebuild).
/// </summary>
internal static class MemoryLog
{
    private static readonly string? FilePath = Environment.GetEnvironmentVariable("SLOPPYANIMATSU_MEMORY_LOG");
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static Timer? _timer;
    private static long _frames;
    private static long _frameBytes;
    private static long _frameTicks;
    private static long _lastAllocated;
    private static int _gen0;
    private static int _gen1;
    private static int _gen2;
    private static int _writes;
    private static AllocationSampler? _sampler;

    public static bool Enabled => FilePath != null;

    public static void Start()
    {
        if (FilePath == null) return;
        File.WriteAllText(FilePath, "t_s private_MB gc_heap_MB gc_free_MB committed_MB alloc_MB_s gen0 gen1 gen2 frames frame_alloc_KB frame_ms\n");
        _lastAllocated = GC.GetTotalAllocatedBytes();
        _sampler = new AllocationSampler();
        _timer = new Timer(_ => Write(), null, 2000, 2000);
    }

    /// <summary>Counts one rendered frame: what it allocated and how long it took.</summary>
    public static void Frame(long allocatedBytes, long ticks)
    {
        Interlocked.Increment(ref _frames);
        Interlocked.Add(ref _frameBytes, allocatedBytes);
        Interlocked.Add(ref _frameTicks, ticks);
    }

    /// <summary>
    /// Writes one event line: the managed heap after a full collection (so what is still reachable),
    /// private bytes, and the undo history's step count.
    /// </summary>
    public static void Mark(string what, int historySteps = -1)
    {
        if (FilePath == null) return;
        try
        {
            long managed = GC.GetTotalMemory(forceFullCollection: true);
            using Process process = Process.GetCurrentProcess();
            File.AppendAllText(FilePath, FormattableString.Invariant(
                $"# {Clock.Elapsed.TotalSeconds:F1}s {what}: live_heap_MB {managed / 1048576.0:F1} private_MB {process.PrivateMemorySize64 / 1048576.0:F0} history {historySteps}\n"));
        }
        catch (IOException)
        {
        }
    }

    private static void Write()
    {
        try
        {
            long frames = Interlocked.Exchange(ref _frames, 0L);
            long frameBytes = Interlocked.Exchange(ref _frameBytes, 0L);
            long frameTicks = Interlocked.Exchange(ref _frameTicks, 0L);
            long allocated = GC.GetTotalAllocatedBytes();
            long perSecond = (allocated - _lastAllocated) / 2;
            _lastAllocated = allocated;
            GCMemoryInfo info = GC.GetGCMemoryInfo();
            int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
            using Process process = Process.GetCurrentProcess();
            string text = FormattableString.Invariant(
                $"{Clock.Elapsed.TotalSeconds:F0} {process.PrivateMemorySize64 / 1048576.0:F0} {info.HeapSizeBytes / 1048576.0:F0} {info.FragmentedBytes / 1048576.0:F0} {info.TotalCommittedBytes / 1048576.0:F0} {perSecond / 1048576.0:F1} {gen0 - _gen0} {gen1 - _gen1} {gen2 - _gen2} {frames} {(frames == 0 ? 0.0 : frameBytes / frames / 1024.0):F1} {(frames == 0 ? 0.0 : frameTicks * 1000.0 / Stopwatch.Frequency / frames):F2}\n");
            _gen0 = gen0;
            _gen1 = gen1;
            _gen2 = gen2;
            if (++_writes % 5 == 0 && _sampler != null)
                text += "allocations, last 10 s:\n" + _sampler.TakeTable();
            File.AppendAllText(FilePath!, text);
        }
        catch (IOException)
        {
        }
    }
}

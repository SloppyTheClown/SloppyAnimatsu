using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The allocation half of <see cref="MemoryLog"/>: the runtime's sampled allocation events
/// (one per ~100 KB allocated), summed by type and heap, written as a top-15 table every ten
/// seconds when <c>SLOPPYANIMATSU_MEMORY_LOG</c> is set.
/// </summary>
internal sealed class AllocationSampler : EventListener
{
    private readonly ConcurrentDictionary<string, long> _bytes = new();

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1); // GC keyword
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName == null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.Payload == null) return;
        int kindIndex = e.PayloadNames!.IndexOf("AllocationKind");
        int typeIndex = e.PayloadNames.IndexOf("TypeName");
        int amountIndex = e.PayloadNames.IndexOf("AllocationAmount64");
        if (amountIndex < 0) amountIndex = e.PayloadNames.IndexOf("AllocationAmount");
        string kind = kindIndex >= 0 && Convert.ToInt32(e.Payload[kindIndex]) == 1 ? "LOH" : "SOH";
        string type = typeIndex >= 0 ? e.Payload[typeIndex] as string ?? "?" : "?";
        long amount = amountIndex >= 0 ? Convert.ToInt64(e.Payload[amountIndex]) : 100_000;
        _bytes.AddOrUpdate($"{kind} {type}", amount, (_, v) => v + amount);
    }

    public string TakeTable()
    {
        var rows = _bytes.ToArray().OrderByDescending(kv => kv.Value).Take(15).ToArray();
        _bytes.Clear();
        return string.Concat(rows.Select(kv => FormattableString.Invariant($"  {kv.Value / 1048576.0,8:F1} MB  {kv.Key}\n")));
    }
}

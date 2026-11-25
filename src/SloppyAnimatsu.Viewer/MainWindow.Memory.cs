// Hands memory back to the system while the window is minimized.
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.
using System.Runtime;
using Avalonia;
using Avalonia.Controls;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    /// <summary>
    /// When the window is minimized, runs one blocking, compacting full collection (large-object
    /// heap included) so the pages the garbage collector kept committed after a big edit go back
    /// to the system. Without it an idle, minimized viewer that had once edited a large scene kept
    /// several gigabytes committed: the collector keeps freed segments for reuse and never
    /// compacts the large-object heap on its own.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && change.GetNewValue<WindowState>() == WindowState.Minimized)
            ReleaseMemoryWhileMinimized();
    }

    private static void ReleaseMemoryWhileMinimized()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
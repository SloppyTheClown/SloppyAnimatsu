using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// Runs a long write (save, FBX or AKNM export) off the UI thread with a progress bar. The owner
/// window ignores keys, clicks and closing while the work runs, so nothing edits the scene being
/// written; the bar's window appears only when the work outlasts <see cref="ShowAfter"/>, so a
/// small scene saves without a flash.
/// </summary>
public sealed class ProgressDialog : Window
{
    /// <summary>How long a write runs before its progress window opens.</summary>
    public static readonly TimeSpan ShowAfter = TimeSpan.FromMilliseconds(250);

    /// <summary>Whether a write is running (autosave waits for it).</summary>
    public static bool Busy { get; private set; }

    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Width = 360, Height = 8 };
    private readonly TextBlock _percent = new() { Text = "0%" };
    private bool _done;

    private ProgressDialog(string title, string label)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
            Children = { new TextBlock { Text = label }, _bar, _percent },
        };
        Closing += (_, e) => e.Cancel = !_done;
    }

    private void Show(double value)
    {
        _bar.Value = value;
        _percent.Text = $"{Math.Round(value * 100)}%";
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a worker thread and returns when it ends; an exception it
    /// throws is rethrown here. <paramref name="work"/> reports 0 to 1.
    /// </summary>
    public static async Task Run(Window owner, string title, string label, Action<IProgress<double>> work)
    {
        var reporter = new Latest();
        var dialog = new ProgressDialog(title, label);
        // Swallow input to the owner while the scene is being read on another thread.
        EventHandler<RoutedEventArgs> block = (_, e) => e.Handled = true;
        EventHandler<WindowClosingEventArgs> keepOpen = (_, e) => e.Cancel = true;
        owner.AddHandler(InputElement.KeyDownEvent, block, RoutingStrategies.Tunnel, handledEventsToo: true);
        owner.AddHandler(InputElement.KeyUpEvent, block, RoutingStrategies.Tunnel, handledEventsToo: true);
        owner.AddHandler(InputElement.TextInputEvent, block, RoutingStrategies.Tunnel, handledEventsToo: true);
        owner.AddHandler(InputElement.PointerPressedEvent, block, RoutingStrategies.Tunnel, handledEventsToo: true);
        owner.AddHandler(InputElement.PointerWheelChangedEvent, block, RoutingStrategies.Tunnel, handledEventsToo: true);
        owner.Closing += keepOpen;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => dialog.Show(reporter.Value));
        Busy = true;
        try
        {
            var task = Task.Run(() => work(reporter));
            if (await Task.WhenAny(task, Task.Delay(ShowAfter)) != task)
            {
                dialog.Show(reporter.Value);
                timer.Start();
                _ = dialog.ShowDialog(owner);
                try { await task; } catch { /* rethrown below */ }
            }
            await task;
        }
        finally
        {
            Busy = false;
            timer.Stop();
            dialog._done = true;
            dialog.Close();
            owner.Closing -= keepOpen;
            owner.RemoveHandler(InputElement.KeyDownEvent, block);
            owner.RemoveHandler(InputElement.KeyUpEvent, block);
            owner.RemoveHandler(InputElement.TextInputEvent, block);
            owner.RemoveHandler(InputElement.PointerPressedEvent, block);
            owner.RemoveHandler(InputElement.PointerWheelChangedEvent, block);
        }
    }

    /// <summary>Keeps only the latest value; the UI polls it, so a writer can report as often as it likes.</summary>
    private sealed class Latest : IProgress<double>
    {
        private double _value;
        public double Value => Volatile.Read(ref _value);
        public void Report(double value) => Volatile.Write(ref _value, value);
    }
}

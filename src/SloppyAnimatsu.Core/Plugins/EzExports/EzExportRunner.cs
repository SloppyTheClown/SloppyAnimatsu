using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Core.Plugins.EzExports;

/// <summary>What one preset's run did.</summary>
public sealed record EzRunResult(EzPreset Preset, string? Folder, IReadOnlyList<string> Written,
    IReadOnlyList<string> Warnings, string? Error)
{
    /// <summary>The status line for this preset, e.g. <c>EZ Exports "Hero": 3 files written to C:\out</c>.</summary>
    public string StatusLine()
    {
        string head = $"EZ Exports \"{Preset.Name}\": ";
        string tail = Warnings.Count > 0 ? " (" + string.Join("; ", Warnings) + ")" : "";
        if (Error != null)
            return head + (Written.Count > 0 ? $"{Written.Count} file(s) written, then failed: " : "failed: ") + Error + tail;
        return head + (Written.Count == 1 ? "wrote " + Written[0] : $"{Written.Count} files written to {Folder}") + tail;
    }
}

/// <summary>
/// Runs presets: plans each, creates its folder, and writes its files (overwriting files already
/// there). A preset that fails is reported and the next one still runs.
/// </summary>
public static class EzExportRunner
{
    /// <param name="progress">0 to 1 over every file of every preset.</param>
    public static IReadOnlyList<EzRunResult> Run(SceneModel model, string? scenePath, IReadOnlyList<EzPreset> presets,
        IProgress<double>? progress = null)
    {
        var plans = presets.Select(p => EzExportPlanner.Plan(model, scenePath, p)).ToList();
        int total = Math.Max(1, plans.Sum(p => p.Error == null ? p.Files.Count : 0));
        int done = 0;
        var results = new List<EzRunResult>();
        foreach (var plan in plans)
        {
            var written = new List<string>();
            if (plan.Error != null)
            {
                results.Add(new EzRunResult(plan.Preset, plan.Folder, written, plan.Warnings, plan.Error));
                continue;
            }
            try
            {
                Directory.CreateDirectory(plan.Folder!);
                foreach (var file in plan.Files)
                {
                    int index = done;
                    var part = progress == null ? null
                        : new Progress(f => progress.Report((index + Math.Clamp(f, 0, 1)) / total));
                    FbxExporter.Export(model, file.Path, file.Options, part);
                    written.Add(file.Path);
                    done++;
                    progress?.Report((double)done / total);
                }
                results.Add(new EzRunResult(plan.Preset, plan.Folder, written, plan.Warnings, null));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                done += plan.Files.Count - written.Count;
                results.Add(new EzRunResult(plan.Preset, plan.Folder, written, plan.Warnings, e.Message));
            }
        }
        progress?.Report(1);
        return results;
    }

    // Reports synchronously (System.Progress<T> posts to the UI thread, out of order).
    private sealed class Progress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

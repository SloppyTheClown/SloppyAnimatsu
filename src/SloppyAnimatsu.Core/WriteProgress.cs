namespace SloppyAnimatsu.Core;

/// <summary>
/// Progress for the long writers (scene save, FBX and AKNM export): a fraction from 0 to 1. A
/// writer made of steps hands each step a <see cref="Slice"/> of its own range, and the last step
/// is always <see cref="WriteFile"/>, so the bar reaches 1 when the file is on disk.
/// </summary>
public static class WriteProgress
{
    /// <summary>The part of <paramref name="progress"/> from <paramref name="from"/> to <paramref name="to"/>; null stays null.</summary>
    public static IProgress<double>? Slice(this IProgress<double>? progress, double from, double to) =>
        progress == null ? null : new Sliced(progress, from, to);

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="path"/> in 1 MB chunks, reporting after each.</summary>
    public static void WriteFile(string path, byte[] bytes, IProgress<double>? progress)
    {
        if (progress == null)
        {
            File.WriteAllBytes(path, bytes);
            return;
        }
        const int chunk = 1 << 20;
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            for (int at = 0; at < bytes.Length; at += chunk)
            {
                stream.Write(bytes, at, Math.Min(chunk, bytes.Length - at));
                progress.Report((double)Math.Min(at + chunk, bytes.Length) / Math.Max(1, bytes.Length));
            }
        }
        progress.Report(1);
    }

    private sealed class Sliced(IProgress<double> inner, double from, double to) : IProgress<double>
    {
        public void Report(double value) => inner.Report(from + (to - from) * Math.Clamp(value, 0, 1));
    }
}

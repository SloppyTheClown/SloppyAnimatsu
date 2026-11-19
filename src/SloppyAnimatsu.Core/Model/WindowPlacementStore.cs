using System.Text.Json;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// Where SloppyAnimatsu's own floating windows were last left (the camera view window so far): one entry
/// per window key with its position, size and fold state, in <c>%APPDATA%\SloppyAnimatsu\window-placements.json</c>.
/// SloppyAnimatsu's own file; not part of any .akt file.
/// </summary>
public sealed class WindowPlacementStore
{
    public const string FileName = "window-placements.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly Dictionary<string, WindowPlacement> _data;

    public WindowPlacementStore(string path)
    {
        Path = path;
        _data = Read(path);
    }

    public string Path { get; }

    /// <summary><c>%APPDATA%\SloppyAnimatsu\window-placements.json</c>.</summary>
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SloppyAnimatsu", FileName);

    /// <summary>The last save error, or null after a successful save.</summary>
    public string? LastError { get; private set; }

    public WindowPlacement? Get(string key) => _data.TryGetValue(key, out var p) ? p : null;

    /// <summary>Stores a window's placement and writes the file at once.</summary>
    public void Set(string key, WindowPlacement placement)
    {
        _data[key] = placement;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(_data, Json));
            LastError = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LastError = e.Message;
        }
    }

    /// <summary>
    /// The position closest to an area's top left corner where a <paramref name="width"/> x
    /// <paramref name="height"/> window overlaps none of <paramref name="obstacles"/> and stays
    /// <paramref name="margin"/> inside the area. The candidates are the corner itself and the spots
    /// just right of or below each obstacle; the nearest by x + y wins (the higher one on a tie).
    /// When none fits, the corner.
    /// </summary>
    public static (double X, double Y) FreeSpotNearTopLeft(double areaWidth, double areaHeight,
        IReadOnlyList<(double X, double Y, double Width, double Height)> obstacles,
        double width, double height, double margin)
    {
        var xs = new List<double> { margin };
        var ys = new List<double> { margin };
        foreach (var r in obstacles)
        {
            xs.Add(r.X + r.Width + margin);
            ys.Add(r.Y + r.Height + margin);
        }
        (double X, double Y)? best = null;
        foreach (double x in xs)
        foreach (double y in ys)
        {
            if (x + width > areaWidth - margin + 0.5 || y + height > areaHeight - margin + 0.5) continue;
            if (obstacles.Any(o => x < o.X + o.Width && o.X < x + width && y < o.Y + o.Height && o.Y < y + height))
                continue;
            if (best is not { } b || x + y < b.X + b.Y || (x + y == b.X + b.Y && y < b.Y)) best = (x, y);
        }
        return best ?? (margin, margin);
    }

    private static Dictionary<string, WindowPlacement> Read(string path)
    {
        try
        {
            if (File.Exists(path) &&
                JsonSerializer.Deserialize<Dictionary<string, WindowPlacement>>(File.ReadAllText(path)) is { } d)
                return d;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new Dictionary<string, WindowPlacement>(StringComparer.Ordinal);
    }
}

/// <summary>A floating window's top-left corner and content width in the view area, and whether it was folded.</summary>
public sealed record WindowPlacement(double X, double Y, double Width, bool Collapsed);

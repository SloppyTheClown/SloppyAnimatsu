using System.Numerics;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The timeline's geometry and hit testing without a window
/// a frame ruler, one row per stack of
/// the animation, a key per entry, a clickable clip per audio event and a fade handle at each ramp's
/// far end. The viewer draws from these numbers and asks this class what a press or a release would
/// touch, so the pointer rules can be tested and printed from the command line; the layout itself is
/// a viewer design, not the.
/// </summary>
public sealed class TimelineLayout
{
    /// <summary>The width (DIPs) left of the keys for the stack names.</summary>
    public const double Gutter = 130;

    /// <summary>The padding after the last frame.</summary>
    public const double RightPad = 14;

    /// <summary>The ruler's height above the first row.</summary>
    public const double RulerHeight = 20;

    /// <summary>One stack row's height.</summary>
    public const double RowHeight = 18;

    /// <summary>How far (DIPs) a press may be from a key and still hit it.</summary>
    public const double HitRadius = 7;

    /// <summary>A key box's widest width: room for a two- or three-digit number.</summary>
    public const double KeyMaxWidth = 22;

    /// <summary>A key box's narrowest width: still easy to press when keys crowd together.</summary>
    public const double KeyMinWidth = 6;

    /// <summary>A key box's height, most of a row.</summary>
    public const double KeyHeight = 14;

    /// <summary>
    /// The width of key <paramref name="k"/>'s box among the first <paramref name="count"/> keys of
    /// <paramref name="stack"/>: as wide as <see cref="KeyMaxWidth"/> while its neighbours are far,
    /// shrinking to leave a 2-DIP gap to the nearer one, never below <see cref="KeyMinWidth"/>.
    /// A viewer design: the timeline shows the key numbers only while the box is wide enough.
    /// </summary>
    public double KeyWidth(AnimStack stack, int count, int k)
    {
        var keys = stack.Keys;
        double x = X(keys[k].Time), gap = double.MaxValue;
        if (k > 0) gap = Math.Min(gap, x - X(keys[k - 1].Time));
        if (k + 1 < count) gap = Math.Min(gap, X(keys[k + 1].Time) - x);
        return Math.Clamp(gap - 2, KeyMinWidth, KeyMaxWidth);
    }

    /// <summary>A fade handle's side, half a row.</summary>
    public const double FadeHandle = RowHeight / 2;

    /// <summary>The control's width in DIPs.</summary>
    public double Width { get; set; } = 800;

    /// <summary>The control's height in DIPs (the rows are drawn from the top; the height only bounds them).</summary>
    public double Height { get; set; } = 60;

    /// <summary>The animation whose stacks and keys are laid out.</summary>
    public Animation? Animation { get; set; }

    /// <summary>The animation's stacks, top to bottom.</summary>
    public IReadOnlyList<AnimStack> Stacks => Animation?.Stacks ?? [];

    /// <summary>
    /// The stack whose blendshape tracks get their own rows below the stack rows: the timeline's
    /// active stack. Its rows show the two values of each key, which the stack rows cannot.
    /// </summary>
    public AnimStack? ActiveStack { get; set; }

    /// <summary>The active stack's blendshape tracks, in track order; one row each, after the stacks.</summary>
    public IReadOnlyList<BlendshapeTrack> BlendshapeTracks =>
        ActiveStack?.Tracks.OfType<BlendshapeTrack>().ToList() ?? [];

    /// <summary>
    /// The texture layer: one row per material with
    /// texture keys in the animation, after the blendshape rows. These keys belong to the animation,
    /// not to a stack, so the rows show in every stack.
    /// </summary>
    public IReadOnlyList<Material> TextureMaterials => Animation == null ? [] : Material.Keyed(Animation);

    /// <summary>The first texture row.</summary>
    public int TextureRowStart => Stacks.Count + BlendshapeTracks.Count;

    /// <summary>How many rows the layout has: one per stack, one per blendshape track of the active stack, one per textured material.</summary>
    public int RowCount => TextureRowStart + TextureMaterials.Count;

    /// <summary>
    /// The stack drawn on <paramref name="row"/>: a blendshape row belongs to the active stack, and a
    /// texture row leaves the active stack as it is.
    /// </summary>
    public AnimStack? StackAt(int row) =>
        row < 0 ? null : row < Stacks.Count ? Stacks[row] : row < RowCount ? ActiveStack : null;

    /// <summary>The material whose texture keys are drawn on <paramref name="row"/>, or null.</summary>
    public Material? TextureAt(int row)
    {
        int index = row - TextureRowStart;
        if (index < 0) return null;
        var materials = TextureMaterials;
        return index < materials.Count ? materials[index] : null;
    }

    /// <summary>The texture key under <paramref name="p"/>: its row, material and key, or row -1.</summary>
    public (int Row, Material? Material, TextureKey Key) TextureKeyAt(Vector2 p)
    {
        int row = RowAt(p.Y);
        if (p.X < Gutter || Animation == null || TextureAt(row) is not { } material) return (-1, null, default);
        var keys = material.TextureKeys(Animation);
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int k = 0; k < keys.Count; k++)
        {
            double d = Math.Abs(X(keys[k].Time) - p.X);
            if (d <= HitRadius && d <= bestDistance) { bestDistance = d; best = k; }
        }
        return best < 0 ? (-1, null, default) : (row, material, keys[best]);
    }

    /// <summary>The blendshape track drawn on <paramref name="row"/>, or null for a stack row.</summary>
    public BlendshapeTrack? BlendshapeAt(int row)
    {
        var tracks = BlendshapeTracks;
        int index = row - Stacks.Count;
        return index >= 0 && index < tracks.Count ? tracks[index] : null;
    }

    /// <summary>The keys drawn on a row: a blendshape row shows the keys its track and the stack share.</summary>
    public int KeyCountAt(int row) =>
        row < Stacks.Count ? Stacks[row].ConsideredKeyCount
        : BlendshapeAt(row) is { } track ? Math.Min(track.Keys.Count, ActiveStack?.ConsideredKeyCount ?? 0)
        : TextureAt(row) is { } material && Animation != null ? material.TextureKeys(Animation).Count
        : 0;

    /// <summary>The two values a blendshape row's key shows, or null for a stack row or a null key.</summary>
    public float[]? BlendshapeValuesAt(int row, int key)
    {
        var track = BlendshapeAt(row);
        return track != null && key >= 0 && key < track.Keys.Count ? track.Keys[key]?.Values : null;
    }

    /// <summary>
    /// The frames the range bar shows (<see cref="TimeBar"/>), or null to span the animation's
    /// <see cref="Animation.KeyRange"/>.
    /// </summary>
    public (int Start, int End)? ViewRange { get; set; }

    /// <summary>The frames the ruler spans, at least two so that a frame has a width.</summary>
    public (int Start, int End) Range
    {
        get
        {
            var (start, end) = ViewRange ?? Animation?.KeyRange ?? (0, 0);
            return (start, Math.Max(end, start + 1));
        }
    }

    /// <summary>The width of one frame in DIPs.</summary>
    public double FrameWidth
    {
        get
        {
            double width = Width - Gutter - RightPad;
            int frames = Range.End - Range.Start + 1;
            return width > 0 && frames > 0 ? width / frames : 1;
        }
    }

    /// <summary>The centre of a frame's cell, where its keys are drawn.</summary>
    public double X(float frame) => Gutter + (frame - Range.Start + 0.5) * FrameWidth;

    /// <summary>The (fractional) frame under an x, in cells (a press in a cell's middle is its frame).</summary>
    public float FrameAt(double x) => (float)(Range.Start - 0.5 + (x - Gutter) / FrameWidth);

    /// <summary>The whole frame under <paramref name="x"/>, clamped to the time range.</summary>
    public int FrameUnder(double x) => (int)MathF.Round(Math.Clamp(FrameAt(x), Range.Start, Range.End));

    /// <summary>The top of a row's rectangle.</summary>
    public static double RowTop(int row) => RulerHeight + row * RowHeight;

    /// <summary>
    /// The stack row under <paramref name="y"/>, or -1: the ruler is above the rows and the space
    /// below the last row is not a row.
    /// </summary>
    public int RowAt(double y) => y < RulerHeight ? -1 : (int)((y - RulerHeight) / RowHeight);

    /// <summary>
    /// The gap between rows nearest to <paramref name="y"/> for a stack drag: 0 above the first row,
    /// <see cref="Stacks"/> below the last (the drop, before the row that left the gap is taken out).
    /// </summary>
    public int SlotAt(double y) => Math.Clamp((int)Math.Round((y - RulerHeight) / RowHeight), 0, Stacks.Count);

    /// <summary>The height the rows ask for (the ruler, the rows, and a row when there is none).</summary>
    public double TotalHeight => RulerHeight + Math.Max(RowCount, 1) * RowHeight + 2;

    /// <summary>The stack row under <paramref name="p"/>, and the key of that stack nearest to it.</summary>
    public (int Row, int Key) KeyAt(Vector2 p)
    {
        int row = RowAt(p.Y);
        if (row < 0 || row >= RowCount) return (-1, -1);
        if (p.X < Gutter) return (row, -1);
        // A texture row's keys are not stack keys (TextureKeyAt picks them); with no active stack it
        // has no stack at all.
        if (row >= TextureRowStart || StackAt(row) is not { } stack) return (row, -1);
        var blendshape = BlendshapeAt(row);
        // An audio clip is one clickable block, not a point at its start; its block is drawn with
        // three DIPs of padding inside the row, and a press on that padding is not the clip.
        if (row < Stacks.Count && stack is AudioStack audioStack)
        {
            double top = RowTop(row) + 3;
            for (int k = audioStack.Events.Count - 1; k >= 0; k--)
            {
                var (x0, x1) = AudioClip(audioStack.Events[k]);
                if (p.X >= x0 && p.X <= x1 && p.Y >= top && p.Y <= top + RowHeight - 6) return (row, k);
            }
        }
        var stackKeys = stack.Keys;
        int count = KeyCountAt(row);
        int best = -1;
        double bestDistance = double.MaxValue;
        // Later keys win ties, so that a key drawn over another is the one picked.
        for (int k = 0; k < count; k++)
        {
            // A blendshape track may store a null key where the stack has a time; nothing is drawn
            // there, so nothing is picked.
            if (blendshape != null && blendshape.Keys[k] == null) continue;
            double d = Math.Abs(X(stackKeys[k].Time) - p.X);
            if (d > Math.Max(HitRadius, KeyWidth(stack, count, k) / 2)) continue;
            if (d <= bestDistance) { bestDistance = d; best = k; }
        }
        return (row, best);
    }

    /// <summary>The fade handle under <paramref name="p"/>: (row, key, whether it is the fade-in), or row -1.</summary>
    public (int Row, int Key, bool FadeIn) FadeHandleAt(Vector2 p)
    {
        int row = RowAt(p.Y);
        if (row < 0 || row >= Stacks.Count || p.X < Gutter) return (-1, -1, false);
        var keys = Stacks[row].Keys;
        // Later keys win ties, as a key drawn over another is picked.
        for (int k = keys.Count - 1; k >= 0; k--)
        {
            if (keys[k].FadeOut != 0 && HandleRect(row, keys[k], false) is var (x, y, w, h) &&
                Contains(x - 1, y - 1, w + 2, h + 2, p)) return (row, k, false);
            if (keys[k].FadeIn != 0 && HandleRect(row, keys[k], true) is var (x2, y2, w2, h2) &&
                Contains(x2 - 1, y2 - 1, w2 + 2, h2 + 2, p)) return (row, k, true);
        }
        return (-1, -1, false);
    }

    /// <summary>
    /// The rectangle of a key's fade handle, half a row wide, at the far end of the ramp: ending one
    /// pixel before the frame <c>Time - FadeIn</c>, or starting at <c>Time + FadeOut</c>.
    /// </summary>
    public (double X, double Y, double Width, double Height) HandleRect(int row, StackKey key, bool fadeIn)
    {
        double y = RowTop(row) + (RowHeight - FadeHandle) / 2;
        return fadeIn
            ? (X(key.Time - key.FadeIn) - 1 - FadeHandle, y, FadeHandle, FadeHandle)
            : (X(key.Time + key.FadeOut), y, FadeHandle, FadeHandle);
    }

    /// <summary>An audio event's clip: from its start frame to start + duration, at least 3 DIPs wide.</summary>
    public (double X0, double X1) AudioClip(AudioEvent e)
    {
        double frames = e.DurationSeconds * FrameRate;
        if (frames <= 0) frames = 1;
        double x0 = X(e.StartFrame);
        double x1 = Math.Max(X((float)(e.StartFrame + frames)), x0 + 3);
        return (x0, Math.Max(x1, x0 + 3));
    }

    /// <summary>The scene's frame rate; audio clip widths are in frames.</summary>
    public double FrameRate
    {
        get
        {
            uint fps = Animation?.Stacks.FirstOrDefault()?.Model.Scene.FrameRate ?? 0;
            return fps > 0 ? fps : 30;
        }
    }

    private static bool Contains(double x, double y, double width, double height, Vector2 p) =>
        p.X >= x && p.X <= x + width && p.Y >= y && p.Y <= y + height;
}

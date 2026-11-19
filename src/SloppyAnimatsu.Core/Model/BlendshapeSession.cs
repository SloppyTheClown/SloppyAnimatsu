using System.Numerics;

namespace SloppyAnimatsu.Core.Model;

/// <summary>One entry of a channel's target list: a target shape at a position on the slider.</summary>
public sealed record BlendshapeInfoRow(string TargetName, float Position)
{
    public override string ToString() => $"{TargetName}@{Position:0.###}";
}

/// <summary>
/// One of a controller's channels, as the panel shows it: the value it follows and where its
/// targets sit.
/// </summary>
public sealed record BlendshapeChannelRow(string Value, uint Direction, float Attenuation, IReadOnlyList<BlendshapeInfoRow> Infos)
{
    /// <summary>e.g. <c>X: Jaw@1, Smile@0.5</c>; a channel without a target says so.</summary>
    public string Label =>
        $"{Value}: " + (Infos.Count == 0 ? "(no target)" : string.Join(", ", Infos));
}

/// <summary>One controller row of the Blendshapes panel.</summary>
public sealed record BlendshapeControllerRow(
    BlendshapeController Controller, string MeshName, IReadOnlyList<BlendshapeChannelRow> Channels, int KeyIndex);

/// <summary>
/// One face cell of the Blendshapes panel: the stored two-value keys and the controllers they are
/// copied into. <see cref="Ignored"/> names references that belong to another character, which
/// <see cref="BlendshapeSession.ApplyFace"/> leaves alone.
/// </summary>
public sealed record BlendshapeFaceRow(
    BlendshapeFace Face, Mesh Mesh, int Index, IReadOnlyList<string> Controllers,
    IReadOnlyList<string> Ignored, IReadOnlyList<float[]> Values)
{
    /// <summary>e.g. <c>#0 Smile</c>; the thumbnail picture is missing in every scene (a placeholder).</summary>
    public string Label => $"#{Index} {Face.Name}";

    /// <summary>Whether a click would write anything into the current stack.</summary>
    public bool Applies => Controllers.Count > 0;
}

/// <summary>A <c>CBlendShapesSight</c> marker and where it stands in the scene.</summary>
public sealed record BlendshapeSightRow(BlendshapeSight Sight, Vector3 Position);

/// <summary>What a value edit wrote: the key it landed on and whether that key was created.</summary>
public readonly record struct BlendshapeValueWrite(int KeyIndex, bool Created, float X, float Y);

/// <summary>
/// A blendshape pick zone under a point of a mesh: the face controller whose <c>Click</c> and
/// <c>InteractiveFaces</c> claim <paramref name="Triangle"/> (an index in the mesh's
/// <see cref="Mesh.Triangles"/> order) and the face that stores the values a click applies
/// </summary>
public sealed record BlendshapeZoneHit(Mesh Mesh, BlendshapeFaceController Controller, BlendshapeFace? Face, int Triangle);

/// <summary>
/// The Blendshapes panel's rules without a window
/// which controllers and faces the shown character has, what a face applies
/// to, what a value edit writes into the current stack, and which face zone a picked mesh triangle
/// belongs to (with the quadrant rule its 2x2 pad uses). The viewer only draws these rows and turns
/// its presses into <see cref="SetValues"/>, <see cref="SetQuadrant"/>, <see cref="ApplyFace"/> and
/// <see cref="ApplyFaceAt"/>; every edit changes the document in place and is wrapped in one undo
/// step by the caller.
/// </summary>
public sealed class BlendshapeSession
{
    private readonly KeyEditor _editor;

    public BlendshapeSession(Character character)
    {
        Character = character;
        Poses = new PoseEvaluator(character);
        Evaluator = new BlendshapeEvaluator(Poses);
        _editor = new KeyEditor(character, Poses);
    }

    public Character Character { get; }

    /// <summary>The evaluator the values at the playhead come from.</summary>
    public PoseEvaluator Poses { get; }

    public BlendshapeEvaluator Evaluator { get; }

    /// <summary>The animation the playhead belongs to, or null in the rest pose.</summary>
    public Animation? Animation { get; set; }

    /// <summary>The stack whose keys a value edit or a face writes (the timeline's active stack).</summary>
    public AnimStack? Stack { get; set; }

    /// <summary>The playhead, in animation frames.</summary>
    public float Time { get; set; }

    /// <summary>The whole frame under the playhead.</summary>
    public int Frame => (int)MathF.Round(Time);

    /// <summary>The character's blendshape controllers, in the order <c>Controllers</c> stores them.</summary>
    public IReadOnlyList<BlendshapeController> Controllers =>
        Character.Controllers.OfType<BlendshapeController>().ToList();

    /// <summary>The meshes carrying this character's targets and faces: its skinned meshes and its controllers' meshes.</summary>
    public IReadOnlyList<Mesh> Meshes => Character.Meshes
        .Concat(Controllers.Select(c => c.Mesh).OfType<Mesh>())
        .Distinct()
        .ToList();

    /// <summary>The stack edits go to: the one set, else the animation's current/first stack.</summary>
    public AnimStack? CurrentStack =>
        Stack ?? Animation?.CurrentStack ?? Animation?.Stacks.FirstOrDefault();

    /// <summary>
    /// The controller rows: each controller's mesh, channels (value, direction, attenuation and
    /// targets) and the stack key under the playhead (-1 when the playhead is between keys).
    /// </summary>
    public IReadOnlyList<BlendshapeControllerRow> ControllerRows
    {
        get
        {
            var stack = CurrentStack;
            return Controllers.Select(controller => new BlendshapeControllerRow(
                controller,
                controller.Mesh?.Name ?? "(no mesh)",
                controller.Channels.Select(ChannelRow).ToList(),
                stack == null ? -1 : KeyIndexAt(stack, Playhead(stack)))).ToList();
        }
    }

    /// <summary>The face cells of every mesh of the character, in mesh and array order.</summary>
    public IReadOnlyList<BlendshapeFaceRow> Faces
    {
        get
        {
            var rows = new List<BlendshapeFaceRow>();
            foreach (var mesh in Meshes)
                foreach (var face in mesh.BlendshapeFaces)
                {
                    var mine = new List<string>();
                    var ignored = new List<string>();
                    foreach (var controller in face.Controllers)
                        (IsOurs(controller) ? mine : ignored).Add(controller.Name ?? "(unnamed)");
                    rows.Add(new BlendshapeFaceRow(face, mesh, face.Index, mine, ignored, face.Values));
                }
            return rows;
        }
    }

    /// <summary>
    /// The character's <c>CBlendShapesSight</c> markers. A sight under another character is that
    /// character's; a sight at the scene root belongs to every character, so it is listed too. A
    /// sight stands at the eye of a stored blendshape point of view - its own mesh's when it is under
    /// one, else the character's first mesh that has one - and at its own matrix when no mesh it
    /// belongs to has one.
    /// </summary>
    public IReadOnlyList<BlendshapeSightRow> Sights => SightsOf(Character);

    /// <summary>The sights that belong to <paramref name="character"/> (or stand at the scene root), with where they stand.</summary>
    public static IReadOnlyList<BlendshapeSightRow> SightsOf(Character character) =>
        character.Model.Scene.Descendants<BlendshapeSight>()
            .Where(sight => sight.Ancestor<Character>() is not { } owner || ReferenceEquals(owner, character))
            .Select(sight => new BlendshapeSightRow(sight, SightPositionOf(sight, character)))
            .ToList();

    /// <summary>
    /// Where a sight stands: the eye of the point of view of the mesh it is under, else of the first
    /// mesh of <paramref name="character"/> that has one, else its own world matrix (the
    /// <c>CBlendShapesSight</c> marks the blendshape camera, so it follows that view).
    /// </summary>
    public static Vector3 SightPositionOf(BlendshapeSight sight, Character character)
    {
        var mesh = sight.Ancestor<Mesh>() ?? character.Meshes.FirstOrDefault(m => m.HasBlendshapePointOfView);
        return mesh != null && BlendshapePointOfView.EyeOf(mesh) is { } eye ? eye : sight.WorldMatrix.Translation;
    }

    // ---- the point of view ----------------

    /// <summary>
    /// The mesh's stored blendshape point of view - its world camera - or null when it has none
    /// (<see cref="BlendshapePointOfView.Of"/>: the transform relative to the anchor or the mesh,
    /// times that base).
    /// </summary>
    public Matrix4x4? PointOfView(Mesh mesh) => BlendshapePointOfView.Of(mesh);

    /// <summary>
    /// Stores <paramref name="cameraWorld"/> and its target distance on the mesh (the
    /// "set blendshapes point of view" action and its
    /// distance). The caller wraps it in one undo step; a scene whose <c>CMesh</c> class lacks the
    /// members gets them first.
    /// </summary>
    public void SetPointOfView(Mesh mesh, Matrix4x4 cameraWorld, float distance) =>
        BlendshapePointOfView.Set(mesh, cameraWorld, distance);

    /// <summary>The eye of the mesh's stored point of view, or null when it has none.</summary>
    public Vector3? SightPosition(Mesh mesh) => BlendshapePointOfView.EyeOf(mesh);

    /// <summary>The controller's two values at the playhead, mixed from the animation's stacks.</summary>
    public float[] Values(BlendshapeController controller) =>
        Evaluator.Values(controller, Animation ?? CurrentStack?.Animation, Time);

    /// <summary>The variables the two channels of <paramref name="controller"/> can take, for the steppers.</summary>
    public (float MinX, float MaxX, float MinY, float MaxY) Range(BlendshapeController controller)
    {
        float minX = 0, maxX = 0, minY = 0, maxY = 0;
        bool anyX = false, anyY = false;
        foreach (var channel in controller.Channels)
        {
            bool positive = channel.Direction is 0 or 3;
            if (channel.Group == 0) { anyX = true; if (positive) maxX = 1; else minX = -1; }
            else { anyY = true; if (positive) maxY = 1; else minY = -1; }
        }
        return (anyX ? minX : 0, anyX ? maxX : 0, anyY ? minY : 0, anyY ? maxY : 0);
    }

    /// <summary>The trim the app applies to the two values of a blendshape key.</summary>
    public float[] Clamp(BlendshapeController controller, float x, float y)
    {
        var (minX, maxX, minY, maxY) = Range(controller);
        return [Math.Clamp(x, minX, maxX), Math.Clamp(y, minY, maxY)];
    }

    /// <summary>
    /// Writes <paramref name="x"/> and <paramref name="y"/> at the playhead in <see cref="CurrentStack"/>,
    /// creating the controller's blendshape track if the stack has none and, when the playhead is
    /// between keys, a key at it (which keys every track of the stack, as K does). Key 0's value is
    /// mirrored into the last (loop) key like <see cref="BlendshapeEditor.SetKeyValues"/>.
    /// </summary>
    public BlendshapeValueWrite SetValues(BlendshapeController controller, float x, float y)
    {
        var stack = CurrentStack ?? throw new InvalidOperationException("Pick an animation and a stack first.");
        if (!IsOurs(controller)) throw new ArgumentException($"{controller} does not belong to {Character.Name}.", nameof(controller));
        var (cx, cy) = (x, y);
        var track = TrackIn(stack, controller) ?? CreateTrack(stack, controller);
        int frame = Playhead(stack);
        int key = KeyIndexAt(stack, frame);
        bool created = false;
        if (key < 0)
        {
            key = _editor.CreateKey(stack, frame);
            created = true;
            track = TrackIn(stack, controller) ?? throw new InvalidOperationException($"{stack} has no blendshape track for {controller}.");
        }
        BlendshapeEditor.SetKeyValues(track, key, cx, cy);
        Poses.Invalidate();
        return new BlendshapeValueWrite(key, created, cx, cy);
    }

    /// <summary>
    /// The <c>ApplyData</c>: copies the face's stored two-value pairs into the tracks of the
    /// controllers it references, at the times of the current stack. Pair <c>i</c> goes to stack key
    /// <c>i</c>; the loop (last) key replays key 0, and a face with fewer pairs repeats its last one.
    /// A referenced controller of another character is ignored. Returns the number of controllers
    /// written.
    /// </summary>
    public int ApplyFace(BlendshapeFace face)
    {
        var stack = CurrentStack ?? throw new InvalidOperationException("Pick an animation and a stack first.");
        var pairs = face.Values;
        if (pairs.Count == 0) return 0;
        var stackKeys = stack.Keys;
        int written = 0;
        foreach (var controller in face.Controllers)
        {
            if (!IsOurs(controller)) continue;
            if (TrackIn(stack, controller) is not { } track) continue;
            for (int i = 0; i < stackKeys.Count && i < track.Keys.Count; i++)
            {
                int pair = stackKeys.Count > 1 && i == stackKeys.Count - 1 ? 0 : Math.Min(i, pairs.Count - 1);
                track.SetValues(i, pairs[pair][0], pairs[pair][1]);
            }
            written++;
        }
        Poses.Invalidate();
        return written;
    }

    // ---- the face zones and the quadrant rule ----------------------------------------------------

    /// <summary>
    /// The mesh's face controllers as the app keeps them in a <c>CMesh</c> array: this character's
    /// <see cref="BlendshapeFaceController"/>s whose <c>Mesh</c> is <paramref name="mesh"/>, in
    /// <c>Controllers</c> order (the app builds its per-button arrays in that order too).
    /// </summary>
    public IReadOnlyList<BlendshapeFaceController> FaceControllersOf(Mesh mesh) =>
        Controllers.OfType<BlendshapeFaceController>()
            .Where(controller => ReferenceEquals(controller.Mesh, mesh))
            .ToList();

    /// <summary>
    /// The face controller that influences a click
    /// the mesh's face controller whose
    /// <see cref="BlendshapeFaceController.Click"/> equals <paramref name="click"/> and whose
    /// <see cref="BlendshapeFaceController.InteractiveFaces"/> contains
    /// <paramref name="triangle"/>, else null. The first controller claiming a triangle wins, as
    /// The per-button array stores one controller per face; a controller with an empty zone
    /// is never picked (<c>0xffffffff</c> in the original's array). The keyed-mesh remap
    /// (<c>CMesh+0x650</c>) has no SloppyAnimatsu analogue, so the triangle is an index in
    /// <see cref="Mesh.Triangles"/>'s order - a polygon's fan triangles, in polygon order.
    /// </summary>
    public BlendshapeFaceController? FaceControllerAt(Mesh mesh, int triangle, uint click = 0)
    {
        if (triangle < 0) return null;
        uint index = (uint)triangle;
        return FaceControllersOf(mesh)
            .FirstOrDefault(controller => controller.Click == click && controller.InteractiveFaces.Contains(index));
    }

    /// <summary>
    /// The zone a picked triangle of <paramref name="mesh"/> belongs to (with the mouse button the
    /// click was made with): its controller, the first face of the mesh that references that
    /// controller (if any) and the triangle, or null when no controller claims it.
    /// </summary>
    public BlendshapeZoneHit? PickZone(Mesh mesh, int triangle, uint click = 0)
    {
        if (FaceControllerAt(mesh, triangle, click) is not { } controller) return null;
        var face = mesh.BlendshapeFaces.FirstOrDefault(f => f.Controllers.Contains(controller));
        return new BlendshapeZoneHit(mesh, controller, face, triangle);
    }

    /// <summary>
    /// The click on a zone: picks the controller under <paramref name="triangle"/> and applies
    /// the face that stores its values (<see cref="ApplyFace"/>). Returns the number of controllers
    /// written; 0 when the triangle is in no zone or no face references the controller.
    /// </summary>
    public int ApplyFaceAt(Mesh mesh, int triangle, uint click = 0) =>
        PickZone(mesh, triangle, click)?.Face is { } face ? ApplyFace(face) : 0;

    /// <summary>
    /// Which of four quadrants a point falls in, inside a rectangle of <paramref name="size"/> whose
    /// origin is its top-left (x right, y down): <c>0</c> top-left, <c>1</c> top-right, <c>2</c>
    /// bottom-left, <c>3</c> bottom-right - the cell's reading order. A point on a boundary belongs
    /// to the right/bottom cell; a rectangle without a size is quadrant 0.
    /// </summary>
    public static int Quadrant(Vector2 local, Vector2 size)
    {
        if (size.X <= 0f || size.Y <= 0f) return 0;
        return (local.Y >= size.Y / 2f ? 2 : 0) + (local.X >= size.X / 2f ? 1 : 0);
    }

    /// <summary>
    /// The (group, sign) a quadrant selects: quadrant <c>i</c> takes direction <c>i</c> of the
    /// channel order (<c>0</c> +Y "up", <c>1</c> -Y "down", <c>2</c> -X "left", <c>3</c> +X
    /// "right"), whose group rule is direction 2/3 → X and 0/1 → Y. Group 0 is X, 1 is Y; the sign
    /// picks the bound of <see cref="Range"/> the edit writes (+1 = the positive one). Negative
    /// quadrants wrap, so -1 is quadrant 3.
    /// </summary>
    /// <remarks>
    /// <paramref name="selection"/> is <c>CSpinnerPreferences.BlendShapesQuadrantSelection</c>, kept
    /// for the measurement: the app treats it as a bit mask (the quadrant's own bit
    /// AND-ed with the preference, so bit <c>i</c> is quadrant <c>i</c>'s check state), which cannot
    /// reorder quadrants - hence the identity mapping above. See
    /// <see cref="QuadrantEnabled"/>.
    /// </remarks>
    public static (int Group, float Sign) QuadrantDirection(int quadrant, uint selection = 0)
    {
        _ = selection;
        return (((quadrant % 4) + 4) % 4) switch
        {
            0 => (1, 1f),   // direction 0: +Y
            1 => (1, -1f),  // direction 1: -Y
            2 => (0, -1f),  // direction 2: -X
            _ => (0, 1f),   // direction 3: +X
        };
    }

    /// <summary>Whether <paramref name="selection"/> marks <paramref name="quadrant"/> active (bit <c>i</c> = quadrant <c>i</c>).</summary>
    public static bool QuadrantEnabled(int quadrant, uint selection) =>
        (selection & (1u << (((quadrant % 4) + 4) % 4))) != 0;

    /// <summary>
    /// Writes one group's extreme at the playhead: the quadrant picks the group and the sign (see
    /// <see cref="QuadrantDirection"/>), the other group keeps the value it has now, and the extreme
    /// is that group's bound from <see cref="Range"/> (±1), or 0 for a group the controller has no
    /// channel for. The write itself is <see cref="SetValues"/>'s: it creates the controller's track
    /// or the key when the stack lacks them and mirrors key 0 into the loop key.
    /// </summary>
    public BlendshapeValueWrite SetQuadrant(BlendshapeController controller, int quadrant, uint selection = 0)
    {
        var (group, sign) = QuadrantDirection(quadrant, selection);
        var values = Values(controller);
        float x = values.Length > 0 ? values[0] : 0f;
        float y = values.Length > 1 ? values[1] : 0f;
        var (minX, maxX, minY, maxY) = Range(controller);
        if (group == 0) x = sign > 0 ? maxX : minX;
        else y = sign > 0 ? maxY : minY;
        return SetValues(controller, x, y);
    }

    private bool IsOurs(BlendshapeController controller) =>
        ReferenceEquals(controller.Ancestor<Character>(), Character) || Character.Controllers.Contains(controller);

    private BlendshapeChannelRow ChannelRow(BlendshapeChannel channel) => new(
        channel.Group == 0 ? "X" : "Y",
        channel.Direction,
        channel.Attenuation,
        channel.Infos.Select(info => new BlendshapeInfoRow(info.Blendshape?.Name ?? "(no target)", info.Position)).ToList());

    private int Playhead(AnimStack stack) =>
        (int)MathF.Round(PoseEvaluator.LocalTime(stack.Keys, stack.Flags, Time));

    private static int KeyIndexAt(AnimStack stack, int frame)
    {
        var keys = stack.Keys;
        for (int i = 0; i < keys.Count; i++)
            if (keys[i].Time == frame) return keys.Count > 1 && i == keys.Count - 1 ? 0 : i;
        return -1;
    }

    private static BlendshapeTrack? TrackIn(AnimStack stack, BlendshapeController controller) =>
        stack.TrackFor(controller) as BlendshapeTrack;

    private BlendshapeTrack CreateTrack(AnimStack stack, BlendshapeController controller)
    {
        var track = (BlendshapeTrack)TrackFactory.Create(stack, controller);
        Poses.Invalidate();
        return track;
    }
}
using System.Numerics;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// One row of the AutoRigger dialog's hot-point table:
/// The fifteen drag labels. Pelvis and Neck are central; every other row has a left and a
/// right point, because the AutoRigger builds both sides from an explicit point per side.
/// </summary>
public sealed record AutoRigPartRow(string Label, HotPoint? Centre, HotPoint? Left, HotPoint? Right)
{
    /// <summary>The point(s) this row stands for, left to right.</summary>
    public IEnumerable<HotPoint> Points =>
        new[] { Centre, Left, Right }.Where(point => point != null).Select(point => point!.Value);

    /// <summary>Whether the row has a point per side rather than one central point.</summary>
    public bool HasSides => Left != null && Right != null;
}

/// <summary>
/// The AutoRigger dialog's rows and the names its status lines use. The labels are the drag
/// button texts (the same list for both sides), and the last five are the extra fingers 6 to 10.
/// </summary>
public static class AutoRiggerParts
{
    /// <summary>The fifteen rows, in button order.</summary>
    public static IReadOnlyList<AutoRigPartRow> Rows { get; } =
    [
        new("Pelvis", HotPoint.Pelvis, null, null),
        new("Neck", HotPoint.Neck, null, null),
        new("Ankle", null, HotPoint.AnkleLeft, HotPoint.AnkleRight),
        new("Shoulder", null, HotPoint.ShoulderLeft, HotPoint.ShoulderRight),
        new("Wrist", null, HotPoint.WristLeft, HotPoint.WristRight),
        new("Thumb Tip", null, HotPoint.ThumbLeft, HotPoint.ThumbRight),
        new("Index Tip", null, HotPoint.IndexLeft, HotPoint.IndexRight),
        new("Middle Tip", null, HotPoint.MiddleLeft, HotPoint.MiddleRight),
        new("Ring Tip", null, HotPoint.RingLeft, HotPoint.RingRight),
        new("Little Tip", null, HotPoint.LittleLeft, HotPoint.LittleRight),
        new("Finger_6 Tip", null, HotPoint.Finger6Left, HotPoint.Finger6Right),
        new("Finger_7 Tip", null, HotPoint.Finger7Left, HotPoint.Finger7Right),
        new("Finger_8 Tip", null, HotPoint.Finger8Left, HotPoint.Finger8Right),
        new("Finger_9 Tip", null, HotPoint.Finger9Left, HotPoint.Finger9Right),
        new("Finger_10 Tip", null, HotPoint.Finger10Left, HotPoint.Finger10Right),
    ];

    /// <summary>The row a hot point belongs to.</summary>
    public static AutoRigPartRow RowOf(HotPoint point) =>
        Rows.First(row => row.Points.Contains(point));

    /// <summary>A hot point as a status line names it: the row's label plus its side.</summary>
    public static string Describe(HotPoint point)
    {
        AutoRigPartRow row = RowOf(point);
        string side = row.Centre == point ? "" : point == row.Left ? " L" : " R";
        return row.Label + side;
    }
}

/// <summary>One side of the AutoRigger's hot-point table; central points (Pelvis, Neck) are neither.</summary>
public enum AutoRigSide
{
    Left,
    Right,
}

/// <summary>
/// What <see cref="AutoRiggerSession.Mirror"/> wrote: how many placed points were copied onto the
/// other side, and how many of those counterparts already had a placed point and were overwritten.
/// </summary>
public readonly record struct AutoRigMirrorResult(AutoRigSide From, int Mirrored, int Replaced)
{
    /// <summary>The side the points were copied onto.</summary>
    public AutoRigSide To => From == AutoRigSide.Left ? AutoRigSide.Right : AutoRigSide.Left;
}

/// <summary>
/// The AutoRigger dialog's state: the
/// character receiving the rig, the meshes it skins, the naming set and counts, the hot points the
/// user placed and which of the required ones are still missing. It is headless and tested, so the
/// window only draws it and turns a viewport click into <see cref="Place"/>.
/// </summary>
public sealed class AutoRiggerSession
{
    /// <summary>The central hot points: the dialog always needs these.</summary>
    private static readonly HotPoint[] CentralPoints = [HotPoint.Pelvis, HotPoint.Neck];

    /// <summary>The side hot points outside the fingers: one pair, always required.</summary>
    private static readonly HotPoint[] SidePoints =
        [HotPoint.AnkleLeft, HotPoint.AnkleRight, HotPoint.ShoulderLeft, HotPoint.ShoulderRight,
         HotPoint.WristLeft, HotPoint.WristRight];

    private readonly List<Mesh> _meshes = [];
    private readonly Dictionary<HotPoint, Vector3> _points = [];
    private AutoRigNaming _naming = AutoRigNaming.Preferences;
    private int _spineJoints = 3;
    private int _fingers = 5;
    private int _phalanxJoints = 3;

    /// <param name="character">The character the rig is built on (the app needs one with no joints).</param>
    /// <param name="meshes">The meshes to skin; by default the character's own.</param>
    public AutoRiggerSession(Character character, IEnumerable<Mesh>? meshes = null)
    {
        Character = character;
        SetMeshes(meshes ?? character.Meshes);
    }

    /// <summary>The character receiving the rig.</summary>
    public Character Character { get; }

    /// <summary>The meshes the dialog will skin, in the order it lists them checked.</summary>
    public IReadOnlyList<Mesh> Meshes => _meshes;

    /// <summary>The hot points placed so far, in the scene's world space.</summary>
    public IReadOnlyDictionary<HotPoint, Vector3> Points => _points;

    /// <summary>The naming set of the new bones (type 0, 1 or 2).</summary>
    public AutoRigNaming Naming
    {
        get => _naming;
        set => _naming = Enum.IsDefined(value) ? value : AutoRigNaming.Preferences;
    }

    /// <summary>Spine joints between the pelvis and the neck; the AutoRigger supports 1 to 6.</summary>
    public int SpineJoints
    {
        get => _spineJoints;
        set => _spineJoints = Math.Clamp(value, 1, 6);
    }

    /// <summary>Fingers per hand; the AutoRigger supports 1 to 10.</summary>
    /// <remarks>
    /// Shrinking the count drops the hot points of the fingers that went away, both sides of every
    /// one: the hot-point update keeps the first
    /// <c>FingerNumber + 4</c> hot points - its drag button has one point per label, the five fixed
    /// ones (Pelvis, Neck, Ankle, Shoulder, Wrist) and one tip per finger - so
    /// <c>5 + F</c> labels remain. SloppyAnimatsu places both sides explicitly, so the same rows are the
    /// <c>8 + 2F</c> points of its <see cref="Required"/> set
    /// </remarks>
    public int Fingers
    {
        get => _fingers;
        set
        {
            int fingers = Math.Clamp(value, 1, 10);
            if (fingers < _fingers) TrimFingerPoints(fingers);
            _fingers = fingers;
        }
    }

    /// <summary>Phalanx joints per finger; the AutoRigger supports 1 to 4.</summary>
    public int PhalanxJoints
    {
        get => _phalanxJoints;
        set => _phalanxJoints = Math.Clamp(value, 1, 4);
    }

    /// <summary>Add an IK controller for each arm.</summary>
    public bool AddIK { get; set; } = true;

    /// <summary>Add a reverse-foot controller for each leg.</summary>
    public bool AddReverseFoot { get; set; } = true;

    /// <summary>Capture the reference pose as the bind pose and attach the meshes.</summary>
    public bool BindSkin { get; set; } = true;

    /// <summary>Write the nearest-bone weights (SloppyAnimatsu's own rule).</summary>
    public bool GenerateWeights { get; set; } = true;

    /// <summary>
    /// Whether the app asks which weight version to keep: the "RIGID AND SMOOTH MODIFIED" prompt appears
    /// only when the character's <c>RigidModified</c> <em>and</em> <c>SmoothModified</c> are both set
    /// </summary>
    public bool MustChooseWeightVersion => Character.RigidModified && Character.SmoothModified;

    /// <summary>
    /// Which version the build works from. The dialog asks when <see cref="MustChooseWeightVersion"/>
    /// holds; outside it SloppyAnimatsu regenerates (the character's
    /// <c>RigidModified</c> flag is not enough, because the bind is not the only weight writer).
    /// </summary>
    public AutoRigWeightVersion WeightVersion { get; set; } = AutoRigWeightVersion.Rigid;

    /// <summary>The AutoRigger's immutable settings for the current values.</summary>
    public AutoRigSettings Settings => new()
    {
        Naming = Naming,
        SpineJoints = SpineJoints,
        Fingers = Fingers,
        PhalanxJoints = PhalanxJoints,
        AddIK = AddIK,
        AddReverseFoot = AddReverseFoot,
        BindSkin = BindSkin,
        GenerateWeights = GenerateWeights,
        KeepWeights = WeightVersion == AutoRigWeightVersion.Smooth,
    };

    /// <summary>
    /// The points this build needs: the two central points, the six fixed side points and both tips
    /// of every configured finger.
    /// </summary>
    public IReadOnlyList<HotPoint> Required
    {
        get
        {
            var required = new List<HotPoint>(CentralPoints.Length + SidePoints.Length + 2 * Fingers);
            required.AddRange(CentralPoints);
            required.AddRange(SidePoints);
            for (int finger = 0; finger < Fingers; finger++)
            {
                required.Add(AutoRigger.FingerPoint(finger, left: true));
                required.Add(AutoRigger.FingerPoint(finger, left: false));
            }
            return required;
        }
    }

    /// <summary>The required points that are not placed yet.</summary>
    public IReadOnlyList<HotPoint> Missing => Required.Where(point => !_points.ContainsKey(point)).ToList();

    /// <summary>Why the build cannot run now, or null when it can.</summary>
    public string? Refusal
    {
        get
        {
            if (Character.Joints.Count != 0)
                return $"{Character.Name} already has {Character.Joints.Count} joints: the " +
                       "AutoRigger only builds a rig on an unrigged character.";
            if (_meshes.Count == 0) return "Check at least one mesh to skin.";
            if (Character.RefPose == null) return $"{Character.Name} has no reference pose to build the rig in.";
            if (Character.RigAnimation?.Stacks.FirstOrDefault() == null)
                return $"{Character.Name} has no __RigAnim stack to write the rig tracks into.";
            if (Missing.Count > 0)
            {
                var names = Missing.Select(AutoRiggerParts.Describe).ToList();
                return "Place the missing hot points: " + string.Join(", ", names) + ".";
            }
            if (_points[HotPoint.Pelvis] == _points[HotPoint.Neck])
                return "The pelvis and neck hot points are the same point.";
            return null;
        }
    }

    /// <summary>Whether <see cref="Build"/> will run.</summary>
    public bool CanBuild => Refusal == null;

    /// <summary>Checks the meshes the dialog will skin; meshes of another scene are refused by the build.</summary>
    public void SetMeshes(IEnumerable<Mesh> meshes)
    {
        _meshes.Clear();
        _meshes.AddRange(meshes.Distinct());
    }

    /// <summary>Places (or replaces) one hot point at a world position.</summary>
    public void Place(HotPoint point, Vector3 position)
    {
        if (!Enum.IsDefined(point)) throw new ArgumentOutOfRangeException(nameof(point));
        _points[point] = position;
    }

    /// <summary>Removes a placed hot point; false when it was not placed.</summary>
    public bool Remove(HotPoint point) => _points.Remove(point);

    /// <summary>Forgets every hot point (a new character or a fresh dialog).</summary>
    public void Clear() => _points.Clear();

    /// <summary>Mirrors every placed left point onto its right counterpart.</summary>
    public AutoRigMirrorResult MirrorLeftToRight() => Mirror(AutoRigSide.Left);

    /// <summary>Mirrors every placed right point onto its left counterpart.</summary>
    public AutoRigMirrorResult MirrorRightToLeft() => Mirror(AutoRigSide.Right);

    /// <summary>
    /// The dialog's hot-point mirroring gesture: every
    /// <em>placed</em> point of one side is copied onto its counterpart label with its position
    /// reflected about the character's local YZ plane - <c>x → −x</c> in the character's own space,
    /// The plane the key/pose mirroring uses
    /// Only the side rows of <see cref="AutoRiggerParts"/> mirror, so the two
    /// central points (Pelvis, Neck) are never touched, and a row whose source point is unplaced is
    /// skipped entirely: an unplaced counterpart stays unplaced.
    /// </summary>
    /// <remarks>
    /// A counterpart that already has a point is <em>overwritten</em> (last write wins), the way
    /// The mirror action writes the source key's value into the symmetric object's key
    /// <see cref="AutoRigMirrorResult.Replaced"/> counts those points. Mirroring twice
    /// from the same side therefore writes the same positions again and leaves the set unchanged.
    /// </remarks>
    /// <param name="from">The side whose placed points are the source.</param>
    public AutoRigMirrorResult Mirror(AutoRigSide from)
    {
        if (!Enum.IsDefined(from)) throw new ArgumentOutOfRangeException(nameof(from));
        Matrix4x4 world = Character.WorldMatrix;
        Matrix4x4 inverse = Matrix4x4.Invert(world, out var inverted) ? inverted : Matrix4x4.Identity;
        int mirrored = 0, replaced = 0;
        foreach (AutoRigPartRow row in AutoRiggerParts.Rows)
        {
            if (!row.HasSides) continue;
            HotPoint source = from == AutoRigSide.Left ? row.Left!.Value : row.Right!.Value;
            HotPoint target = from == AutoRigSide.Left ? row.Right!.Value : row.Left!.Value;
            if (!_points.TryGetValue(source, out Vector3 position)) continue;
            if (_points.ContainsKey(target)) replaced++;
            Vector3 local = Vector3.Transform(position, inverse);
            local.X = -local.X;
            _points[target] = Vector3.Transform(local, world);
            mirrored++;
        }
        return new AutoRigMirrorResult(from, mirrored, replaced);
    }

    /// <summary>
    /// Drops the placed tips of every finger past <paramref name="keep"/>, both sides. The central
    /// points and the fixed side pairs stay: the trim keeps the first <c>5 + F</c> labels and
    /// only the finger rows are trailing.
    /// </summary>
    private void TrimFingerPoints(int keep)
    {
        foreach (var point in _points.Keys.ToList())
            if (AutoRigger.FingerIndexOf(point) is { } finger && finger >= keep)
                _points.Remove(point);
    }

    /// <summary>A placed point's position, or null.</summary>
    public Vector3? PositionOf(HotPoint point) => _points.TryGetValue(point, out var position) ? position : null;

    /// <summary>Runs the AutoRigger; the caller wraps the change in one undo step.</summary>
    public AutoRigResult Build()
    {
        if (Refusal is { } refusal) throw new InvalidOperationException(refusal);
        return new AutoRigger(Character).Build(_meshes, _points, Settings);
    }
}

/// <summary>One answer of the smooth-again prompt: its label and the weight version it picks.</summary>
public readonly record struct AutoRigWeightChoice(string Label, AutoRigWeightVersion Version);

/// <summary>
/// The "RIGID AND SMOOTH MODIFIED" prompt: its title, what it says and the two
/// answers, which are the weight versions. The app asks it so the mirroring step can pick one
/// version; SloppyAnimatsu builds both sides itself, so its answers say what the build does instead.
/// </summary>
public static class AutoRigSmoothAgainPrompt
{
    /// <summary>The dialog title.</summary>
    public const string Title = "RIGID AND SMOOTH MODIFIED";

    /// <summary>The question, and what each answer means in SloppyAnimatsu.</summary>
    public const string Message =
        "You modified both rigid and smooth versions of your skinning. Rigid solves both weight " +
        "arrays again from the new skeleton and the hot points; Smooth keeps the weights the meshes " +
        "already have and only binds them to the new rig.";

    /// <summary>The two answers, in its button order (Rigid is its first button).</summary>
    public static IReadOnlyList<AutoRigWeightChoice> Choices { get; } = new AutoRigWeightChoice[]
    {
        new("Rigid", AutoRigWeightVersion.Rigid),
        new("Smooth", AutoRigWeightVersion.Smooth),
    };
}


using System.Numerics;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The naming set the AutoRigger gives the bones it creates
/// </summary>
public enum AutoRigNaming
{
    /// <summary>Type 0: the original lower-case names (<c>upperArm_L</c>, <c>ball_L</c>,...).</summary>
    Default = 0,

    /// <summary>Type 1: the Mixamo-style names the shipped <c>7_AddingIK</c> rig uses.</summary>
    Mixamo = 1,

    /// <summary>Type 2: the names in <c>CAutoRiggerPreferences</c> (Stomach, Arm, Forearm, ...).</summary>
    Preferences = 2,
}

/// <summary>
/// Which weight version an AutoRigger build works from
/// The two answers of the "RIGID AND SMOOTH MODIFIED" dialog.
/// </summary>
public enum AutoRigWeightVersion
{
    /// <summary>The "Rigid" answer: solve both weight arrays again from the new skeleton.</summary>
    Rigid,

    /// <summary>
    /// The "Smooth" answer: keep the weight arrays the meshes already carry and only rebind them to
    /// the new rig.
    /// </summary>
    Smooth,
}

/// <summary>
/// A hot point the AutoRigger places a joint from.
/// Finger entries are the <em>tips</em>; the other joints of a finger are derived.
/// </summary>
public enum HotPoint
{
    Pelvis, Neck,
    AnkleLeft, AnkleRight,
    ShoulderLeft, ShoulderRight,
    WristLeft, WristRight,
    ThumbLeft, ThumbRight,
    IndexLeft, IndexRight,
    MiddleLeft, MiddleRight,
    RingLeft, RingRight,
    LittleLeft, LittleRight,
    Finger6Left, Finger6Right,
    Finger7Left, Finger7Right,
    Finger8Left, Finger8Right,
    Finger9Left, Finger9Right,
    Finger10Left, Finger10Right,
}

/// <summary>The AutoRigger's options; the defaults are the shipped preferences' shape.</summary>
public sealed class AutoRigSettings
{
    /// <summary>Naming set of the new bones.</summary>
    public AutoRigNaming Naming { get; init; } = AutoRigNaming.Preferences;

    /// <summary>Number of spine joints between the pelvis and the neck (1..6).</summary>
    public int SpineJoints { get; init; } = 3;

    /// <summary>Number of fingers per hand (1..10).</summary>
    public int Fingers { get; init; } = 5;

    /// <summary>Number of phalanx joints per finger (1..4); the thumb gets one fewer.</summary>
    public int PhalanxJoints { get; init; } = 3;

    /// <summary>Influences kept per control point (the <c>NbMaxJoints</c>, default 2).</summary>
    public int MaxInfluences { get; init; } = 2;

    /// <summary>A weight at or below this is dropped before renormalising (<c>AutoSweepValue</c>).</summary>
    public float AutoSweepValue { get; init; } = 0.01f;

    /// <summary>Smoothing strength of a joint whose <c>SmoothMethod</c> is 1 (<c>FaceIteration</c>).</summary>
    public float FaceIteration { get; init; } = 0.5f;

    /// <summary>Smoothing strength of a joint whose <c>SmoothMethod</c> is 0 (<c>BodyIteration</c>).</summary>
    public float BodyIteration { get; init; } = 5f;

    /// <summary>Add an IK controller for each arm (upper arm to hand).</summary>
    public bool AddIK { get; init; } = true;

    /// <summary>Add a reverse-foot controller for each leg (thigh to ball/toes).</summary>
    public bool AddReverseFoot { get; init; } = true;

    /// <summary>Capture the reference pose as the bind pose and attach the meshes.</summary>
    public bool BindSkin { get; init; } = true;

    /// <summary>Write the skin weights: the rigid nearest-bone array and its smoothed copy.</summary>
    public bool GenerateWeights { get; init; } = true;

    /// <summary>
    /// Keep the weight arrays the meshes already carry: nothing is solved and Bind Skin does not
    /// rewrite the rigid array (the "Smooth" answer to the "RIGID AND SMOOTH MODIFIED" prompt;
    ///).
    /// </summary>
    public bool KeepWeights { get; init; }
}

/// <summary>What <see cref="AutoRigger.Build"/> created.</summary>
public sealed record AutoRigResult(
    int Joints, int Controllers, int Meshes, int WeightedPoints,
    int IkControllers, int ReverseFootControllers);

/// <summary>
/// The AutoRigger: builds a biped skeleton, its IK and reverse-foot
/// controllers and its skin weights from hot points on a character's mesh.
/// </summary>
/// <remarks>
/// The geometry rules: the
/// joints named by the hot points are placed at those points; the clavicle sits a quarter of the
/// way from the chest to the shoulder; the elbow and the knee are the exact midpoint of their two
/// ends; the head is half way between the neck and the highest mesh point and the ball 15% of the
/// way from the ankle towards the foot's far end; the fingers run from the hand to the tip hot
/// points. The skin weights use the default preferences: the nearest bone's
/// start joint as the seed, Laplacian smoothing over the mesh's vertex adjacency with the per-joint
/// <c>SmoothMethod</c> strength, then the influence cap and the negligible-influence sweep.
/// The mesh must be unrigged: the AutoRigger is enabled only on a character with no joints.
/// </remarks>
public sealed class AutoRigger
{
    private static readonly HotPoint[] RequiredPoints =
    [
        HotPoint.Pelvis, HotPoint.Neck,
        HotPoint.AnkleLeft, HotPoint.AnkleRight,
        HotPoint.ShoulderLeft, HotPoint.ShoulderRight,
        HotPoint.WristLeft, HotPoint.WristRight,
    ];

    public AutoRigger(Character character) => Character = character;

    public Character Character { get; }

    /// <summary>
    /// Builds the rig from the hot points (in the scene's world space) on <paramref name="meshes"/>.
    /// The meshes are reparented to the character keeping their world; with
    /// <see cref="AutoRigSettings.BindSkin"/> the reference pose is captured as the bind pose.
    /// </summary>
    public AutoRigResult Build(IEnumerable<Mesh> meshes, IReadOnlyDictionary<HotPoint, Vector3> hotPoints,
        AutoRigSettings? settings = null)
    {
        settings ??= new AutoRigSettings();
        Validate(settings, hotPoints);
        var targets = meshes.Distinct().ToList();
        if (targets.Count == 0)
            throw new InvalidOperationException("The AutoRigger needs at least one mesh.");
        foreach (var mesh in targets)
            if (!ReferenceEquals(mesh.Model, Character.Model))
                throw new InvalidOperationException($"'{mesh.Name}' belongs to another scene.");
        if (settings.GenerateWeights && !settings.KeepWeights)
        {
            var schema = Character.Model.Document.Schema;
            if (schema.FindStruct("CVertexWeight") == null || schema.FindStruct("CVertexWeightArray") == null)
                throw new InvalidOperationException("The scene schema cannot hold CVertexWeight arrays.");
        }
        foreach (var mesh in targets) ReparentToCharacter(mesh);

        var editor = new RigEditor(Character);
        var pose = editor.RigPose ?? throw new InvalidOperationException(
            $"{Character.Name} has no reference pose to build the rig in.");
        var names = BoneNames.For(settings.Naming);
        var probe = MeshProbe.Create(editor, pose, targets);

        Vector3 Point(HotPoint kind) => hotPoints[kind];

        // ---- torso -----------------------------------------------------------------------
        var pelvis = Point(HotPoint.Pelvis);
        var neck = Point(HotPoint.Neck);
        var spineDirection = neck - pelvis;
        float bodyLength = MathF.Max(spineDirection.Length(), 1e-4f);
        var up = Vector3.Normalize(spineDirection);
        var root = editor.CreateJointAt(Character, new Vector3(pelvis.X, 0f, pelvis.Z), names.Root);
        var hips = editor.CreateJointAt(root, pelvis, names.Pelvis);
        var chest = hips;
        for (int i = 0; i < settings.SpineJoints; i++)
        {
            var position = Vector3.Lerp(pelvis, neck, (i + 1f) / (settings.SpineJoints + 1f));
            chest = editor.CreateJointAt(chest, position, names.Spine[i]);
        }
        var neckJoint = editor.CreateJointAt(chest, neck, names.Neck);
        // The head has no hot point: the app takes the highest point of the mesh (the topmost
        // occupied column of its voxel grid) and puts the head joint half way between it and the
        // neck.
        var headTop = probe?.Highest();
        editor.CreateJointAt(neckJoint,
            headTop is { } top ? Vector3.Lerp(neck, top, 0.5f) : neck + up * (bodyLength * 0.35f),
            names.Head);

        // ---- arms ------------------------------------------------------------------------
        var rightAxis = Vector3.Normalize(SafeNormal(Point(HotPoint.ShoulderRight) - Point(HotPoint.ShoulderLeft),
            Vector3.UnitX));
        var forward = Vector3.Normalize(SafeNormal(Vector3.Cross(rightAxis, Vector3.UnitY), Vector3.UnitZ));

        void BuildArm(bool left, out Joint upperArm, out Joint hand)
        {
            var shoulderPoint = Point(left ? HotPoint.ShoulderLeft : HotPoint.ShoulderRight);
            var wristPoint = Point(left ? HotPoint.WristLeft : HotPoint.WristRight);
            var chestWorld = editor.WorldInPose(pose, chest);
            // A quarter of the way from the chest to the shoulder hot point (0.25).
            var clavicle = editor.CreateJointAt(chest, Vector3.Lerp(chestWorld.Translation, shoulderPoint, 0.25f),
                names.Side(names.Clavicle, left));
            upperArm = editor.CreateJointAt(clavicle, shoulderPoint, names.Side(names.UpperArm, left));
            var elbowPoint = Vector3.Lerp(shoulderPoint, wristPoint, 0.5f);
            var forearm = editor.CreateJointAt(upperArm, elbowPoint, names.Side(names.LowerArm, left));
            hand = editor.CreateJointAt(forearm, wristPoint, names.Side(names.Hand, left));
        }

        BuildArm(true, out var leftUpperArm, out var leftHand);
        BuildArm(false, out var rightUpperArm, out var rightHand);

        // ---- legs ------------------------------------------------------------------------
        void BuildLeg(bool left, out Joint thigh, out Joint ball)
        {
            var ankle = Point(left ? HotPoint.AnkleLeft : HotPoint.AnkleRight);
            float legHeight = MathF.Max(MathF.Abs(pelvis.Y - ankle.Y), 1e-4f);
            var hip = new Vector3(ankle.X, pelvis.Y - legHeight * 0.1f, pelvis.Z);
            thigh = editor.CreateJointAt(hips, hip, names.Side(names.Thigh, left));
            var legLength = MathF.Max(Vector3.Distance(hip, ankle), 1e-4f);
            var knee = Vector3.Lerp(hip, ankle, 0.5f);
            var calf = editor.CreateJointAt(thigh, knee, names.Side(names.Calf, left));
            var foot = editor.CreateJointAt(calf, ankle, names.Side(names.Foot, left));
            ball = editor.CreateJointAt(foot, BallPoint(probe, ankle, knee, forward, legLength),
                names.Side(names.Ball, left));
        }

        BuildLeg(true, out var leftThigh, out var leftBall);
        BuildLeg(false, out var rightThigh, out var rightBall);

        // ---- fingers ---------------------------------------------------------------------
        for (int side = 0; side < 2; side++)
        {
            bool isLeft = side == 0;
            var hand = isLeft ? leftHand : rightHand;
            var handPoint = editor.WorldInPose(pose, hand).Translation;
            for (int finger = 0; finger < settings.Fingers; finger++)
            {
                var tip = Point(FingerPoint(finger, isLeft));
                int phalanges = finger == 0 ? Math.Max(1, settings.PhalanxJoints - 1) : settings.PhalanxJoints;
                Joint parent = hand;
                var start = handPoint;
                for (int joint = 1; joint <= phalanges; joint++)
                {
                    var position = Vector3.Lerp(start, tip, joint / (float)phalanges);
                    parent = editor.CreateJointAt(parent, position, names.PhalanxName(finger, joint, isLeft));
                }
            }
        }

        // The shipped rigs have the bone axis (local Y) along the bone; orient the new joints
        // the same way, moving their children's local transforms so every world stays put.
        editor.OrientJoints(Character.Controllers.OfType<Joint>().ToList());
        // The reference pose is what Bind Skin captures. If the rigging context used a separate
        // CurrentPose, write the built (and oriented) matrices into RefPose as well.
        if (Character.RefPose is { } referencePose && editor.RigPose is { } rigPose &&
            !ReferenceEquals(referencePose, rigPose))
            referencePose.CopyMatricesFrom(rigPose);

        // ---- controllers -----------------------------------------------------------------
        int ik = 0, reverseFeet = 0;
        if (settings.AddIK)
        {
            editor.AddIK(leftUpperArm, leftHand);
            editor.AddIK(rightUpperArm, rightHand);
            ik = 2;
        }
        if (settings.AddReverseFoot)
        {
            editor.AddReverseFoot(leftThigh, leftBall);
            editor.AddReverseFoot(rightThigh, rightBall);
            reverseFeet = 2;
        }

        // ---- weights and binding ---------------------------------------------------------
        // KeepWeights is the "Smooth" answer: the meshes keep the weights the user edited, so
        // neither the generator nor Bind Skin may rewrite them
        // The arrays' BoneIndex values still describe the joints of the rig
        // they were authored on, so remember that rig's joint names before the bind pose is pointed
        // at the new one: Bind Skin maps the kept influences onto the new joints by name.
        int weighted = 0;
        if (settings.GenerateWeights && !settings.KeepWeights)
        {
            var world = editor.RigPose ?? pose;
            foreach (var mesh in targets) weighted += WriteWeights(editor, world, mesh, settings);
        }
        if (settings.BindSkin)
        {
            // A mesh taken from another character can still be marked Skinned with that character's
            // bind pose; point it at this character's bind pose before Bind Skin decides to keep it.
            var skin = new SkinEditor(Character);
            var bind = Character.BindPoses.FirstOrDefault(p => p.Count == Character.Controllers.Count);
            if (bind == null)
            {
                // A rig-less character can carry an empty bind pose from before; it has no slots now.
                foreach (var stale in Character.BindPoses.ToList()) stale.Source.Parent?.RemoveChild(stale.Source);
                bind = skin.CreateBindPose();
            }
            var previousJoints = new Dictionary<Mesh, IReadOnlyList<string>>();
            if (settings.KeepWeights)
                foreach (var mesh in targets)
                    if (mesh.BindPose?.Character is { } old && !ReferenceEquals(old, Character))
                        previousJoints[mesh] = old.Joints.Select(joint => joint.Name ?? "").ToList();
            foreach (var mesh in targets)
            {
                mesh.Source.Set("BindPose", new AktReference(bind.Source));
                mesh.Skinned = false;
            }
            skin.Bind(targets, rewriteWeights: !settings.KeepWeights, previousJoints);
        }
        else
        {
            foreach (var mesh in targets)
            {
                mesh.Skinned = false;
                if (mesh.Source.Has("BindPose")) mesh.Source.Set("BindPose", new AktReference(null));
            }
        }
        if (Character.Source.Has("RigidModified")) Character.Source.Set("RigidModified", false);
        if (Character.Source.Has("SmoothModified")) Character.Source.Set("SmoothModified", false);

        return new AutoRigResult(Character.Joints.Count, Character.Controllers.Count, targets.Count, weighted, ik, reverseFeet);
    }

    /// <summary>The hot point of finger <paramref name="index"/> (0 = thumb) on one side.</summary>
    public static HotPoint FingerPoint(int index, bool left) => index switch
    {
        0 => left ? HotPoint.ThumbLeft : HotPoint.ThumbRight,
        1 => left ? HotPoint.IndexLeft : HotPoint.IndexRight,
        2 => left ? HotPoint.MiddleLeft : HotPoint.MiddleRight,
        3 => left ? HotPoint.RingLeft : HotPoint.RingRight,
        4 => left ? HotPoint.LittleLeft : HotPoint.LittleRight,
        5 => left ? HotPoint.Finger6Left : HotPoint.Finger6Right,
        6 => left ? HotPoint.Finger7Left : HotPoint.Finger7Right,
        7 => left ? HotPoint.Finger8Left : HotPoint.Finger8Right,
        8 => left ? HotPoint.Finger9Left : HotPoint.Finger9Right,
        9 => left ? HotPoint.Finger10Left : HotPoint.Finger10Right,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, "The AutoRigger has ten finger hot points."),
    };

    /// <summary>
    /// The finger a tip hot point belongs to (0 = thumb, 9 = <c>Finger_10</c>), or null for the
    /// central and arm/leg points. The AutoRigger dialog trims the tips past the finger count with
    /// it.
    /// </summary>
    public static int? FingerIndexOf(HotPoint point)
    {
        for (int finger = 0; finger < 10; finger++)
            if (FingerPoint(finger, left: true) == point || FingerPoint(finger, left: false) == point)
                return finger;
        return null;
    }

    // ---- validation ----------------------------------------------------------------------

    private void Validate(AutoRigSettings settings, IReadOnlyDictionary<HotPoint, Vector3> points)
    {
        if (Character.Joints.Count != 0)
            throw new InvalidOperationException(
                $"'{Character.Name}' already has {Character.Joints.Count} joints: the AutoRigger only builds a rig on an unrigged character.");
        if (Character.RefPose == null)
            throw new InvalidOperationException($"'{Character.Name}' has no reference pose to build the rig in.");
        if (Character.RigAnimation?.Stacks.FirstOrDefault() == null)
            throw new InvalidOperationException($"'{Character.Name}' has no __RigAnim stack to write the rig tracks into.");
        if (settings.SpineJoints is < 1 or > 6)
            throw new InvalidOperationException("The AutoRigger supports 1 to 6 spine joints.");
        if (settings.Fingers is < 1 or > 10)
            throw new InvalidOperationException("The AutoRigger supports 1 to 10 fingers per hand.");
        if (settings.PhalanxJoints is < 1 or > 4)
            throw new InvalidOperationException("The AutoRigger supports 1 to 4 phalanx joints per finger.");
        foreach (var kind in RequiredPoints)
            if (!points.ContainsKey(kind))
                throw new InvalidOperationException($"The AutoRigger needs the '{kind}' hot point.");
        for (int finger = 0; finger < settings.Fingers; finger++)
        {
            if (!points.ContainsKey(FingerPoint(finger, true)) || !points.ContainsKey(FingerPoint(finger, false)))
                throw new InvalidOperationException(
                    $"The AutoRigger needs the '{FingerPoint(finger, true)}' and '{FingerPoint(finger, false)}' hot points.");
        }
        if (Vector3.Distance(points[HotPoint.Pelvis], points[HotPoint.Neck]) < 1e-4f)
            throw new InvalidOperationException("The pelvis and neck hot points are the same point.");
    }

    // ---- geometry ------------------------------------------------------------------------

    /// <summary>
    /// The ball/toe joint. The app snaps the leg's direction onto the 3×3×3 direction lattice
    /// , walks the mesh to its far end along it and takes 0.85 of the way back
    /// from that end to the ankle, i.e. 15% of the way
    /// from the ankle. The walk is a **ray** down the anchor's column, so the joint stays on that
    /// line (its lateral position is the ankle's). Without a mesh the joint keeps the older
    /// forward-and-below offset.
    /// </summary>
    private static Vector3 BallPoint(MeshProbe? probe, Vector3 ankle, Vector3 knee, Vector3 forward,
        float legLength)
    {
        var snapped = SnapDirection(ankle - knee);
        if (snapped != Vector3.Zero &&
            probe?.FarthestAlong(ankle, snapped, legLength * 0.35f) is { } far)
            return ankle + snapped * (Vector3.Dot(far - ankle, snapped) * 0.15f);
        float footLength = legLength * 0.16f;
        return ankle + forward * footLength - Vector3.UnitY * (footLength * 0.45f);
    }

    /// <summary>The octant direction of <paramref name="direction"/>: the direction snap.</summary>
    private static Vector3 SnapDirection(Vector3 direction)
    {
        static float Axis(float value) => MathF.Abs(value) > 1e-6f ? MathF.Sign(value) : 0f;
        var snapped = new Vector3(Axis(direction.X), Axis(direction.Y), Axis(direction.Z));
        return snapped.LengthSquared() > 0f ? Vector3.Normalize(snapped) : Vector3.Zero;
    }

    private static Vector3 SafeNormal(Vector3 v, Vector3 fallback) =>
        v.LengthSquared() > 1e-10f ? Vector3.Normalize(v) : fallback;

    private void ReparentToCharacter(Mesh mesh)
    {
        var owner = mesh.Ancestor<Character>();
        if (ReferenceEquals(owner, Character)) return;
        var world = mesh.WorldMatrix;
        mesh.Source.Parent?.RemoveChild(mesh.Source);
        Character.Source.AddChild(mesh.Source);
        if (!WorldComposition.TryDecompose(world, Character.WorldMatrix, Object3D.LocalScale(Character.LocalMatrix),
                mesh.CompensatesParentScale, out var local))
            local = world;
        mesh.LocalMatrix = local;
    }

    // ---- skin weights --------------------------------------------------------------------
    /// <summary>
    /// Writes the two weight arrays for one mesh and returns the number of control points
    /// written: <see cref="SkinWeightGenerator.NearestBones"/> for the rigid array (the bind's own
    /// solution) and <see cref="SkinWeightGenerator.Solve"/> for the smooth one.
    /// </summary>
    private int WriteWeights(RigEditor editor, CharacterPose pose, Mesh mesh, AutoRigSettings settings)
    {
        var points = mesh.ControlPoints;
        if (points.Length == 0) return 0;
        var joints = Character.Joints.ToList();
        var index = new Dictionary<Joint, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < joints.Count; i++) index[joints[i]] = i;
        var bones = new List<SkinWeightGenerator.Bone>();
        var methods = new List<int>(joints.Count);
        foreach (var joint in joints) methods.Add(joint.SmoothMethod);
        foreach (var joint in joints)
        {
            // The span between a joint and its parent is carried by the joint at its start: the
            // shoulder-to-elbow region belongs to UpperArm, not to LowerArm.
            if (joint.Parent3D is not Joint parent) continue;
            bones.Add(new SkinWeightGenerator.Bone(index[parent],
                editor.WorldInPose(pose, parent).Translation, editor.WorldInPose(pose, joint).Translation));
        }
        if (bones.Count == 0)
            throw new InvalidOperationException("The AutoRigger has no bone segments to weight the mesh to.");

        var meshWorld = editor.WorldInPose(pose, mesh);
        var neighbours = SkinWeightGenerator.PolygonNeighbours(mesh.Polygons, points.Length);
        var smooth = SkinWeightGenerator.Solve(points, meshWorld, bones, methods, neighbours,
            settings.MaxInfluences, settings.AutoSweepValue, settings.FaceIteration, settings.BodyIteration);
        var rigid = SkinWeightGenerator.NearestBones(points, meshWorld, bones);

        var schema = Character.Model.Document.Schema;
        var skin = new List<object?>(points.Length);
        var rigidArrays = new List<object?>(points.Length);
        for (int p = 0; p < points.Length; p++)
        {
            skin.Add(NewWeightArray(schema, smooth[p].Select(w => (w.Bone, w.Weight)).ToList()));
            rigidArrays.Add(NewWeightArray(schema, rigid[p].Select(w => (w.Bone, w.Weight)).ToList()));
        }
        mesh.Source.Set("SkinWeightArray", skin);
        mesh.Source.Set("RigidSkinWeightArray", rigidArrays);
        return points.Length;
    }
    private static AktStruct NewWeightArray(AktSchema schema, IReadOnlyList<(int Bone, float Weight)> weights)
    {
        var array = schema.NewStruct("CVertexWeightArray");
        var list = (List<object?>)array["WeightArray"]!;
        foreach (var (bone, weight) in weights)
        {
            var entry = schema.NewStruct("CVertexWeight");
            entry.Set("BoneIndex", (uint)bone);
            entry.Set("Weight", weight);
            list.Add(entry);
        }
        return array;
    }

    private static float DistanceToSegment(Vector3 point, Vector3 start, Vector3 end)
    {
        var delta = end - start;
        float lengthSquared = delta.LengthSquared();
        if (lengthSquared < 1e-12f) return Vector3.Distance(point, start);
        float t = Math.Clamp(Vector3.Dot(point - start, delta) / lengthSquared, 0f, 1f);
        return Vector3.Distance(point, start + delta * t);
    }

    // ---- naming --------------------------------------------------------------------------

    /// <summary>
    /// The meshes' world points at the reference pose. The app builds a voxel grid of the meshes and
    /// answers the two rules that need geometry from it - the highest point (the head) and the far
    /// end of the foot (the ball) - so SloppyAnimatsu reads the same points directly
    /// </summary>
    private sealed class MeshProbe
    {
        private readonly Vector3[] _points;

        private MeshProbe(Vector3[] points) => _points = points;

        public static MeshProbe? Create(RigEditor editor, CharacterPose pose, IEnumerable<Mesh> meshes)
        {
            var points = new List<Vector3>();
            foreach (var mesh in meshes)
            {
                var world = editor.WorldInPose(pose, mesh);
                foreach (var control in mesh.ControlPoints)
                    points.Add(Vector3.Transform(control, world));
            }
            return points.Count == 0 ? null : new MeshProbe(points.ToArray());
        }

        /// <summary>The highest world point: the topmost occupied grid column.</summary>
        public Vector3? Highest()
        {
            if (_points.Length == 0) return null;
            var best = _points[0];
            foreach (var point in _points)
                if (point.Y > best.Y) best = point;
            return best;
        }

        /// <summary>
        /// The furthest world point along <paramref name="direction"/> from the origin, within
        /// <paramref name="radius"/> (the app walks the grid out from the origin's cell, so the far
        /// end belongs to the same limb rather than to the whole character).
        /// </summary>
        public Vector3? FarthestAlong(Vector3 origin, Vector3 direction, float radius)
        {
            if (_points.Length == 0) return null;
            float best = float.NegativeInfinity;
            var result = (Vector3?)null;
            foreach (var point in _points)
            {
                if (Vector3.DistanceSquared(point, origin) > radius * radius) continue;
                float along = Vector3.Dot(point - origin, direction);
                if (along > best)
                {
                    best = along;
                    result = point;
                }
            }
            return best > 0f ? result : null;
        }
    }

    /// <summary>
    /// The three AutoRigger naming sets. The side suffix goes
    /// last, as in the shipped rig (<c>Index_Proximal_L</c>).
    /// </summary>
    private sealed class BoneNames
    {
        public required string Root { get; init; }
        public required string Pelvis { get; init; }
        public required string Neck { get; init; }
        public required string Head { get; init; }
        public required string Clavicle { get; init; }
        public required string UpperArm { get; init; }
        public required string LowerArm { get; init; }
        public required string Hand { get; init; }
        public required string Thigh { get; init; }
        public required string Calf { get; init; }
        public required string Foot { get; init; }
        public required string Ball { get; init; }
        public required string[] Spine { get; init; }
        public required string[] Fingers { get; init; }
        public required string[] Phalanx { get; init; }

        public static BoneNames For(AutoRigNaming naming)
        {
            string[] spine = naming switch
            {
                AutoRigNaming.Default => ["spine_01", "spine_02", "spine_03", "spine_04", "spine_05", "spine_06"],
                AutoRigNaming.Mixamo => ["Spine", "Chest", "UpperChest", "Spine_04", "Spine_05", "Spine_06"],
                _ => ["Stomach", "Chest", "UpperChest", "Spine_04", "Spine_05", "Spine_06"],
            };
            string[] fingers = naming switch
            {
                AutoRigNaming.Default =>
                    ["thumb", "index", "middle", "ring", "pinky", "finger_06", "finger_07", "finger_08", "finger_09", "finger_10"],
                AutoRigNaming.Mixamo =>
                    ["Thumb", "Index", "Middle", "Ring", "Little", "Finger_06", "Finger_07", "Finger_08", "Finger_09", "Finger_10"],
                _ =>
                    ["Thumb", "Index", "Middle", "Ring", "Little", "Extra_06", "Extra_07", "Extra_08", "Extra_09", "Extra_10"],
            };
            string[] phalanx = naming switch
            {
                AutoRigNaming.Mixamo => ["Proximal", "Intermediate", "Distal", "Tip"],
                _ => ["01", "02", "03", "04"],
            };
            return naming switch
            {
                AutoRigNaming.Default => new BoneNames
                {
                    Root = "root", Pelvis = "pelvis", Neck = "neck_01", Head = "head",
                    Clavicle = "clavicle", UpperArm = "upperArm", LowerArm = "lowerArm", Hand = "hand",
                    Thigh = "thigh", Calf = "calf", Foot = "foot", Ball = "ball",
                    Spine = spine, Fingers = fingers, Phalanx = phalanx,
                },
                AutoRigNaming.Mixamo => new BoneNames
                {
                    Root = "Root", Pelvis = "Hips", Neck = "Neck", Head = "Head",
                    Clavicle = "Shoulder", UpperArm = "UpperArm", LowerArm = "LowerArm", Hand = "Hand",
                    Thigh = "UpperLeg", Calf = "LowerLeg", Foot = "Foot", Ball = "Toes",
                    Spine = spine, Fingers = fingers, Phalanx = phalanx,
                },
                _ => new BoneNames
                {
                    Root = "Root", Pelvis = "Pelvis", Neck = "Neck", Head = "Head",
                    Clavicle = "Shoulder", UpperArm = "Arm", LowerArm = "Forearm", Hand = "Hand",
                    Thigh = "Thigh", Calf = "Calf", Foot = "Foot", Ball = "Toe",
                    Spine = spine, Fingers = fingers, Phalanx = phalanx,
                },
            };
        }

        public string Side(string name, bool left) => name + (left ? "_L" : "_R");

        public string PhalanxName(int finger, int joint, bool left) =>
            $"{Fingers[finger]}_{Phalanx[joint - 1]}_{(left ? "L" : "R")}";
    }
}

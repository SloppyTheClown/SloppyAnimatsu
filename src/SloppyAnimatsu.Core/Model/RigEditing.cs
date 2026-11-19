using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The axis choices of <c>CSkeletonPreferences.JointAim</c>/<c>JointUp</c>
/// The app stores 0/1/2 and its spinner's
/// order is X, Z, Y - the shipped <c>JointAim = 2</c> is the local Y every shipped bone points
/// along (probed on 7_AddingIK), which fixes the mapping.
/// </summary>
public enum JointAxis : byte
{
    X = 0,
    Z = 1,
    Y = 2,
}

/// <summary>
/// The skeleton preferences that drive orient and Create Joint: the aim and up axes, their sign
/// bits, and the "align" bit (<c>CSkeletonPreferences.Flags</c> bit 2, value 4, on in the shipped
/// file). <c>JointAimInt</c>/<c>JointUpInt</c> add 3 for the negative axis.
/// </summary>
public sealed record JointAlignment(JointAxis Aim = JointAxis.Y, JointAxis Up = JointAxis.Z,
    bool AimNegative = false, bool UpNegative = false, bool AlignOnCreate = true)
{
    /// <summary>The shipped preferences: aim Y, up Z, both positive, Create Joint aligns.</summary>
    public static JointAlignment Default { get; } = new();

    /// <summary>Create Joint's click placement without the align branch.</summary>
    public static JointAlignment NoAlign { get; } = new(AlignOnCreate: false);

    /// <summary>The 0..5 spinner value of the aim axis: 0/1/2 = +X/+Z/+Y, 3/4/5 = the negative ones.</summary>
    public int AimIndex => (int)Aim + (AimNegative ? 3 : 0);

    /// <summary>The 0..5 spinner value of the up axis.</summary>
    public int UpIndex => (int)Up + (UpNegative ? 3 : 0);

    /// <summary>The index order of the axes: 0 = X, 1 = Z, 2 = Y.</summary>
    public static JointAlignment FromIndices(int aim, int up, bool alignOnCreate = true) =>
        new((JointAxis)(aim % 3), (JointAxis)(up % 3), aim >= 3, up >= 3, alignOnCreate);

    /// <summary>The flags byte: bit 0 flips the aim, bit 1 the up, bit 2 is "align on create".</summary>
    public byte Flags => (byte)((AimNegative ? 1 : 0) | (UpNegative ? 2 : 0) | (AlignOnCreate ? 4 : 0));

    /// <summary>Reads the three members of a <c>CSkeletonPreferences</c> page.</summary>
    public static JointAlignment FromPreferences(int jointAim, int jointUp, byte flags) =>
        FromIndices(jointAim, jointUp, (flags & 4) != 0) with
        {
            AimNegative = (flags & 1) != 0,
            UpNegative = (flags & 2) != 0,
        };

    /// <summary>Reads <c>JointAimInt</c>/<c>JointUpInt</c> (the 0..5 spinner values) and the flags.</summary>
    public static JointAlignment FromSpinnerIndices(int jointAimInt, int jointUpInt, byte flags) =>
        new((JointAxis)(jointAimInt % 3), (JointAxis)(jointUpInt % 3),
            jointAimInt >= 3, jointUpInt >= 3, (flags & 4) != 0);

    /// <summary>The displayed name of one axis.</summary>
    public static string NameOf(JointAxis axis) => axis switch
    {
        JointAxis.X => "X",
        JointAxis.Z => "Z",
        _ => "Y",
    };
}

/// <summary>
/// Structural editing of a character's skeleton: create, insert, delete, parent/unparent and orient
/// joints, keeping the poses and the animations consistent.
/// </summary>
/// <remarks>
/// The pose matrices are the authoritative transforms; <c>LTM</c> is the stale "no-pose" snapshot
/// that the app rewrites on structural edits only.
/// The editor works on every pose parallel to <see cref="Character.Controllers"/> and on the rigging
/// context's pose, <see cref="RigPose"/>.
/// </remarks>
public sealed class RigEditor
{
    /// <summary>The name a new joint gets when the caller does not choose one.</summary>
    public const string DefaultJointName = "Joint_01";

    /// <summary>Shown, compose mode 1 and "new controller, not yet in the animations" (data-model flags).</summary>
    public const uint NewControllerFlags = 0x403;

    /// <summary>Shown and "new object": the flags the Add IK writes to the controller (7_AddingIK).</summary>
    public const uint NewIkControllerFlags = 0x401;

    /// <summary>The display size ratio the Add IK gives its controller (7_AddingIK).</summary>
    public const float IkTargetRenderSizeRatio = 4f;

    /// <summary>The display size ratio the Add reverse foot gives its controller (7_AddingIK).</summary>
    public const float ReverseFootTargetRenderSizeRatio = 1f;

    /// <summary><c>DecoratorAxis</c> of a reverse foot: the sole outline's plane (both samples).</summary>
    public const byte ShoeDecoratorAxis = 2;

    /// <summary><c>CObject3D.Flags</c> bit 5: cannot be reparented.</summary>
    public const uint NoReparentFlag = 0x20;

    private static readonly Regex TrailingIndex = new(@"_[0-9]+$", RegexOptions.CultureInvariant);

    public RigEditor(Character character) => Character = character;

    public Character Character { get; }

    /// <summary>
    /// The pose the rigging context shows and edits: <c>CurrentPose</c>, else the first bind pose,
    /// else <c>RefPose</c>.
    /// </summary>
    public CharacterPose? RigPose => PoseEvaluator.RestPose(Character, riggingContext: true);

    /// <summary>
    /// Every pose whose matrix list is parallel to <see cref="Character.Controllers"/>: the current,
    /// reference and previous-reference poses plus the bind poses.
    /// </summary>
    public IReadOnlyList<CharacterPose> Poses()
    {
        var poses = new List<CharacterPose>();
        void Add(CharacterPose? pose)
        {
            // The wrappers are recreated on every access, so compare the document objects.
            if (pose != null && !poses.Any(p => ReferenceEquals(p.Source, pose.Source))) poses.Add(pose);
        }
        Add(Character.CurrentPose);
        Add(Character.RefPose);
        Add(Character.RefPoseOld);
        foreach (var pose in Character.Descendants<CharacterPose>()) Add(pose);
        return poses;
    }

    /// <summary>Slot of <paramref name="controller"/> in <see cref="Character.Controllers"/>, or -1.</summary>
    public int SlotOf(Object3D controller)
    {
        for (int i = 0; i < Character.Controllers.Count; i++)
            if (ReferenceEquals(Character.Controllers[i], controller)) return i;
        return -1;
    }

    // ---- naming ------------------------------------------------------------------------------

    /// <summary>
    /// The controller-naming rule: keep
    /// the requested name when the character has no object with it, else <c>base_NN</c> one past the
    /// highest <c>base_&lt;digits&gt;</c> (at least 2, two digits).
    /// </summary>
    public static string UniqueControllerName(Character character, string requested)
    {
        var names = character.Descendants<Object3D>()
            .Select(o => o.Name)
            .Where(n => n != null)
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);
        if (!names.Contains(requested)) return requested;
        string prefix = TrailingIndex.Replace(requested, "");
        var numbered = new Regex("^" + Regex.Escape(prefix) + "_([0-9]+)$", RegexOptions.CultureInvariant);
        int highest = 1;
        foreach (string name in names)
            if (numbered.Match(name) is { Success: true } match && int.TryParse(match.Groups[1].Value, out int n))
                highest = Math.Max(highest, n);
        return $"{prefix}_{Math.Max(2, highest + 1).ToString("00", CultureInfo.InvariantCulture)}";
    }

    // ---- creation ----------------------------------------------------------------------------

    /// <summary>
    /// Adds a joint under <paramref name="parent"/> (the character when null), with the given local
    /// matrix (identity when null). Appends it to <c>Controllers</c>, <c>JointArray</c>, every pose
    /// and <c>__RigAnim</c>.
    /// </summary>
    public Joint CreateJoint(Object3D? parent, string? name = null, Matrix4x4? local = null)
    {
        var host = parent ?? Character;
        EnsureInCharacter(host);
        var source = NewJointSource(name, local ?? Matrix4x4.Identity);
        host.Source.AddChild(source);
        AddController(source, isJoint: true);
        return (Joint)Character.Model.Wrap(source);
    }

    /// <summary>
    /// Creates a joint at a world point (rigging pose), with the parent's orientation and scale (an
    /// identity local basis).
    /// </summary>
    public Joint CreateJointAt(Object3D? parent, Vector3 worldPoint, string? name = null)
    {
        var host = parent ?? Character;
        return CreateJoint(host, name, LocalAt(host, worldPoint));
    }

    /// <summary>
    /// Inserts a joint above <paramref name="joint"/> (between it and its parent), at
    /// <paramref name="worldPoint"/> or halfway along the bone, and keeps the joint's world pose
    /// The new joint is inserted before the old one
    /// in the parent's child list.
    /// </summary>
    public Joint InsertJoint(Joint joint, string? name = null, Vector3? worldPoint = null)
    {
        if (!Character.Controllers.Contains(joint))
            throw new InvalidOperationException($"{joint.Name} is not a controller of {Character.Name}.");
        var host = joint.Parent as Object3D ?? joint.Parent3D ?? (Object3D)Character;
        var pose = RigPose ?? throw new InvalidOperationException($"{Character.Name} has no pose to edit.");
        Vector3 child = WorldInPose(pose, joint).Translation;
        Vector3 parent = WorldInPose(pose, host).Translation;
        Vector3 point = worldPoint ?? Vector3.Lerp(parent, child, 0.5f);
        return InsertJointWithLocal(joint, name, LocalAt(host, point));
    }

    // ---- interactive placement ------------------------------------------------------------------
    // The working matrix and the parent rule of the Create Joint and Insert Joint

    /// <summary>
    /// The parent an interactive Create Joint picks for a selection: the selected object when it is
    /// a joint with no joint child (a chain tip), otherwise the character (nothing selected gives
    /// the character too).
    /// </summary>
    public Object3D PlacementParent(Object3D? selected) =>
        selected is Joint tip && !tip.Children.OfType<Joint>().Any() ? tip : Character;

    /// <summary>
    /// The working matrix of an interactive placement: the selected object's world matrix with its
    /// translation replaced by the picked world point (a null selection starts from the identity).
    /// </summary>
    public Matrix4x4 PlacementMatrix(Object3D? selected, Vector3 worldPoint)
    {
        var working = WorldInPose(RigPose, selected);
        working.Translation = worldPoint;
        return working;
    }

    /// <summary>
    /// Create Joint's interactive placement: the joint is created under
    /// <see cref="PlacementParent"/> at the picked world point, with the selected object's world
    /// orientation and scale. The working matrix is converted into the parent's frame, so the joint
    /// keeps that orientation where the parent's frame differs from it. When
    /// <see cref="JointAlignment.AlignOnCreate"/> is on and the parent is a chain tip, the parent's
    /// axes are re-aligned in every pose and the new joint's slot is set to the identity matrix, so
    /// the visible placement is the parent's
    /// </summary>
    public Joint PlaceJoint(Object3D? selected, Vector3 worldPoint, string? name = null,
        JointAlignment? alignment = null)
    {
        var settings = alignment ?? JointAlignment.Default;
        var parent = PlacementParent(selected);
        var joint = CreateJoint(parent, name, LocalOfWorld(parent, PlacementMatrix(selected, worldPoint)));
        // The parent rule was decided before the creation: the new joint is now a joint child of
        // the tip, so asking again would answer the character.
        if (settings.AlignOnCreate && parent is Joint tip) AlignOnCreate(tip, joint, settings);
        return joint;
    }

    /// <summary>
    /// The "align" branch of Create Joint: re-align <paramref name="parent"/>'s axes in every pose
    /// and give the new joint the identity slot there (the ref pose's slot also fills
    /// <c>RefPoseOld</c>'s, as the app does). The parent's <c>LTM</c> keeps the working matrix.
    /// </summary>
    private void AlignOnCreate(Joint parent, Joint created, JointAlignment settings)
    {
        var refPose = Character.RefPose;
        var refPoseOld = Character.RefPoseOld;
        foreach (var pose in Poses())
        {
            AlignJointInPose(pose, parent, settings);
            int slot = SlotOf(created);
            if (slot < 0 || slot >= pose.Count) continue;
            if (refPoseOld != null && ReferenceEquals(pose.Source, refPoseOld.Source) &&
                refPose != null && slot < refPose.Count)
                pose.SetMatrix(slot, refPose.Matrix(slot));
            else
                pose.SetMatrix(slot, Matrix4x4.Identity);
        }
    }

    /// <summary>
    /// Insert Joint's interactive placement: the new joint goes between <paramref name="joint"/>
    /// and its parent, at the picked world point, and the selected joint keeps its world. The
    /// working matrix comes from <paramref name="joint"/> itself, the object the action was started
    /// on.
    /// </summary>
    public Joint PlaceInsertedJoint(Joint joint, Vector3 worldPoint, string? name = null)
    {
        if (!Character.Controllers.Contains(joint))
            throw new InvalidOperationException($"{joint.Name} is not a controller of {Character.Name}.");
        var host = joint.Parent as Object3D ?? joint.Parent3D ?? (Object3D)Character;
        var local = LocalOfWorld(host, PlacementMatrix(joint, worldPoint));
        var inserted = InsertJointWithLocal(joint, name, local);
        return inserted;
    }

    /// <summary>The local matrix that composes to <paramref name="world"/> under <paramref name="parent"/>.</summary>
    private Matrix4x4 LocalOfWorld(Object3D parent, Matrix4x4 world)
    {
        var parentWorld = WorldInPose(RigPose, parent);
        if (!Matrix4x4.Invert(parentWorld, out var inverse))
            throw new InvalidOperationException($"{parent.Name} has a singular world matrix.");
        return world * inverse;
    }

    /// <summary>
    /// <see cref="InsertJoint"/> with the new joint's local matrix given: it is inserted before
    /// <paramref name="joint"/> in its parent's child list and the joint keeps its world in every
    /// pose.
    /// </summary>
    private Joint InsertJointWithLocal(Joint joint, string? name, Matrix4x4 local)
    {
        var host = joint.Parent as Object3D ?? joint.Parent3D ?? (Object3D)Character;
        var source = NewJointSource(name, local);
        int index = host.Source.Children.IndexOf(joint.Source);
        host.Source.AddChild(source);
        if (index >= 0)
        {
            host.Source.Children.Remove(source);
            host.Source.Children.Insert(Math.Min(index, host.Source.Children.Count), source);
        }
        AddController(source, isJoint: true);
        var inserted = (Joint)Character.Model.Wrap(source);
        // Insert Joint does not check Constraints: it is a structural edit above the selected joint.
        MoveKeepWorld(joint, inserted, syncLtm: false, allowConstrained: true);
        return inserted;
    }

    // ---- IK controllers ----------------------------------------------------------------------

    /// <summary>
    /// Add IK: creates a <c>CControllerIK</c> for the
    /// joint chain from <paramref name="upper"/> down to <paramref name="lower"/>, named
    /// <c>IK_&lt;lower&gt;</c>, parented to the character. The chain joints get the controller as their
    /// <c>Constraint</c>, the controller gets the lower joint's transform in the character's frame
    /// (<c>LTM</c> and every pose slot), the <c>__RigAnim</c> track of its class, and no user
    /// animation is touched (the new object carries flag bit 10).
    /// </summary>
    public ControllerIK AddIK(Joint upper, Joint lower, string? name = null)
    {
        if (!IsDescendant(upper, lower))
            throw new InvalidOperationException(
                $"'{lower.Name}' is not below '{upper.Name}': an IK chain runs from the upper joint down to the lower one.");
        var chain = JointChain(upper, lower);
        var pose = RigPose ?? throw new InvalidOperationException($"{Character.Name} has no pose to read the chain from.");
        var schema = Character.Model.Document.Schema;
        ControllerSchema.Ensure(schema);

        // The controller sits in the character's frame, on the chain's lower joint: the app reads the
        // lower joint's current global transform and stores it relative to the controller's parent.
        var characterWorld = WorldInPose(pose, Character);
        if (!WorldComposition.TryDecompose(WorldInPose(pose, lower), characterWorld,
                Object3D.LocalScale(LocalInPose(pose, Character)), compensate: false, out var local))
            throw new InvalidOperationException($"{Character.Name} has a singular world matrix.");

        var source = schema.NewObject(ControllerSchema.IkClass);
        source.Set("Name", UniqueControllerName(Character, name ?? "IK_" + lower.Name));
        source.Set("Flags", NewIkControllerFlags);
        source.Set("LTM", AktMath.ToArray(local));
        source.Set("Constraint", new AktReference(null));
        source.Set("TargetRenderSizeRatio", IkTargetRenderSizeRatio);
        var joints = (List<AktReference>)source["JointsString"]!;
        foreach (var joint in chain) joints.Add(new AktReference(joint.Source));
        source.Set("DecoratorAxis", DecoratorAxis(pose, chain));

        Character.Source.AddChild(source);
        var controller = (ControllerIK)Character.Model.Wrap(source);
        // The rig key's RefArray is the chain's FK locals, as in the 7_AddingIK sample.
        AddController(source, isJoint: false,
            chain.Select(j => Transform.FromMatrixLoose(LocalInPose(pose, j))).ToList());
        foreach (var joint in chain)
            joint.Source.Set("Constraint", new AktReference(source));
        return controller;
    }

    // ---- reverse foot ------------------------------------------------------------------------

    /// <summary>Index of each shoe locator in the shoe array.</summary>
    public static readonly IReadOnlyList<string> ShoeTypes =
    [
        "ShoeSoleHeel", "ShoeSoleLeft", "ShoeSoleRight", "ShoeSoleToes",
        "ShoeSkeletonToes", "ShoeSkeletonPlant", "ShoeSkeletonAnkle",
    ];

    /// <summary>
    /// The <c>Type</c> of each shoe locator by index: 2..5 for the four sole pivots, 1 for the three
    /// skeleton pivots.
    /// </summary>
    public static uint ShoeType(int index) => index > 3 ? 1u : (uint)(index + 2);

    /// <summary>The sole outline written when no mesh vertex is weighted to the foot, in foot lengths,
    /// in the shoe frame (<c>+x</c> right, <c>+z</c> forward): each pivot's offset from the previous
    /// one, the shape <c>19_run</c>'s (stale) shoe happens to have. A rig with foot geometry gets
    /// <see cref="SolePivots"/> instead; the shape for a rig without it is unmeasured.</summary>
    private static readonly Vector3[] SoleOffsets =
    [
        new(0f, 0f, -0.52f),    // heel: behind the ankle
        new(0.45f, 0f, 1.12f),  // left
        new(-0.95f, 0f, -0.035f), // right
        new(0.515f, 0f, 1.09f), // toes
    ];

    /// <summary>
    /// The four sole pivots in the shoe frame, fitted to the character's foot mesh as the app fits them
    /// at creation: the vertices the mesh weights to the last chain joint or to
    /// a joint below it, projected into the shoe frame, give the backward, forward and lateral extremes;
    /// the heel and the toes sit on the centre line at the two forward extremes and the two sides
    /// halfway between them at the two lateral extremes, all four on the ground plane (world
    /// <c>y</c> = 0). <c>7_AddingIK</c>'s stored shoe matches this to the file's last digit;
    /// <c>19_run</c>'s does not, because the shoe is computed once and never follows a later leg edit.
    /// The vertices are the pose's mesh in its bind shape - the app fits the skinned current pose,
    /// which differs only when the current pose is not the bind pose.
    /// <c>null</c> when no mesh vertex is weighted to the foot.
    /// </summary>
    private Vector3[]? SolePivots(CharacterPose? pose, Joint ball, Vector3 ground, Vector3 forward,
        Vector3 right)
    {
        var joints = Character.Joints;
        var foot = new HashSet<int>();
        for (int i = 0; i < joints.Count; i++)
        {
            for (Object3D? node = joints[i]; node != null; node = node.Parent3D)
            {
                if (!ReferenceEquals(node, ball)) continue;
                foot.Add(i);
                break;
            }
        }
        if (foot.Count == 0) return null;

        float lateralMin = float.MaxValue, lateralMax = float.MinValue;
        float backward = float.MaxValue, ahead = float.MinValue;
        bool any = false;
        foreach (var mesh in Character.Descendants<Mesh>())
        {
            var meshWorld = WorldInPose(pose, mesh);
            var points = mesh.ControlPoints;
            var rigid = mesh.RigidSkinWeights;
            var smooth = mesh.SkinWeights;
            for (int p = 0; p < points.Length; p++)
            {
                if (!WeightsAny(rigid, smooth, p, foot)) continue;
                var offset = Vector3.Transform(points[p], meshWorld) - ground;
                float lateral = Vector3.Dot(offset, right);
                float forwardOffset = Vector3.Dot(offset, forward);
                lateralMin = MathF.Min(lateralMin, lateral);
                lateralMax = MathF.Max(lateralMax, lateral);
                backward = MathF.Min(backward, forwardOffset);
                ahead = MathF.Max(ahead, forwardOffset);
                any = true;
            }
        }
        if (!any) return null;

        float middle = (backward + ahead) * 0.5f;
        return
        [
            new(0f, 0f, backward),        // heel: the backward extreme, on the centre line
            new(lateralMax, 0f, middle),  // left: the +x extreme, halfway along
            new(lateralMin, 0f, middle),  // right: the -x extreme, halfway along
            new(0f, 0f, ahead),           // toes: the forward extreme, on the centre line
        ];
    }

    /// <summary>Whether the mesh vertex <paramref name="p"/> carries any weight on one of
    /// <paramref name="joints"/>.</summary>
    private static bool WeightsAny(VertexWeight[][] rigid, VertexWeight[][] smooth, int p,
        HashSet<int> joints)
    {
        if (p < rigid.Length)
            foreach (var weight in rigid[p])
                if (weight.Weight > 0f && joints.Contains(weight.Bone)) return true;
        if (p < smooth.Length)
            foreach (var weight in smooth[p])
                if (weight.Weight > 0f && joints.Contains(weight.Bone)) return true;
        return false;
    }

    /// <summary>
    /// Add reverse foot: creates a
    /// <c>CControllerReverseFoot</c> for a leg chain and its seven shoe locators, named
    /// <c>RF_&lt;lower&gt;</c>. The controller keeps an identity <c>LTM</c>; its pose slot puts it on the
    /// ground under the ankle, and the shoe's seven matrices are written into every pose: the four
    /// sole pivots on the ground plane, fitted to the foot mesh's outline (see
    /// <see cref="SolePivots"/>), then the toe tip (half a foot
    /// bone past the ball), the ball joint and the ankle joint, each the previous one's inverse, so
    /// that the shoe's skeleton lands on the leg. <c>RollAxisPlant</c> is the shoe frame's lateral
    /// axis. The chain joints and the locators get the controller as their <c>Constraint</c>, the
    /// chain root's joint ancestors become <c>Parents</c>, and every object gets its pose slot and
    /// its <c>__RigAnim</c> track.
    /// </summary>
    public ControllerReverseFoot AddReverseFoot(Joint upper, Joint lower, string? name = null)
    {
        if (!IsDescendant(upper, lower))
            throw new InvalidOperationException(
                $"'{lower.Name}' is not below '{upper.Name}': a reverse foot runs from the upper joint down to the foot bone.");
        var chain = JointChain(upper, lower);
        var pose = RigPose ?? throw new InvalidOperationException($"{Character.Name} has no pose to read the chain from.");
        var schema = Character.Model.Document.Schema;
        ControllerSchema.Ensure(schema, reverseFoot: true);

        // The shoe is authored in world space: the controller cancels its parent's transform,
        // so a shoe locator's pose slot is its world matrix (7_AddingIK writes the shoe in the
        // scene's units although the character is 267-scaled, 19_run's character is untransformed).
        var characterWorld = WorldInPose(pose, Character);
        if (!Matrix4x4.Invert(characterWorld, out var inverseCharacter))
            throw new InvalidOperationException($"{Character.Name} has a singular world matrix.");
        var ankleJoint = chain[^2];
        var ballJoint = chain[^1];
        var ankle = WorldInPose(pose, ankleJoint);
        var ball = WorldInPose(pose, ballJoint);
        float length = Vector3.Distance(ball.Translation, ankle.Translation);
        if (length < 1e-6f)
            throw new InvalidOperationException($"{ballJoint.Name} has no length: a reverse foot needs a foot bone.");

        // The shoe frame: +z along the foot's horizontal direction, +y the world's up, origin on the
        // ground plane (world y = 0) under the ankle, as the (7_AddingIK's controller has the
        // identity world transform, 19_run's sits at y = 0 above its sole).
        var forward = new Vector3(ball.M41 - ankle.M41, 0f, ball.M43 - ankle.M43);
        forward = Normalized(forward, Vector3.UnitZ);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        var shoeFrame = new Matrix4x4(right.X, right.Y, right.Z, 0f, 0f, 1f, 0f, 0f,
            forward.X, forward.Y, forward.Z, 0f, 0f, 0f, 0f, 1f);
        var ground = new Vector3(ankle.M41, 0f, ankle.M43);
        Matrix4x4 At(Vector3 position) => shoeFrame with { M41 = position.X, M42 = position.Y, M43 = position.Z };

        var controllerWorld = At(ground);
        var shoeWorlds = new List<Matrix4x4>(7);
        if (SolePivots(pose, ballJoint, ground, forward, right) is { } sole)
        {
            foreach (var pivot in sole) shoeWorlds.Add(At(ground + Vector3.Transform(pivot, shoeFrame)));
        }
        else
        {
            var offset = Vector3.Zero;
            for (int i = 0; i < SoleOffsets.Length; i++)
            {
                offset = i == 0 ? SoleOffsets[0] * length : offset + SoleOffsets[i] * length;
                shoeWorlds.Add(At(ground + Vector3.Transform(offset, shoeFrame)));
            }
        }
        // The skeleton: the toe tip half a foot bone past the ball, the ball, the ankle.
        var tip = ball.Translation + (ball.Translation - ankle.Translation) * 0.5f;
        shoeWorlds.Add(At(tip));
        shoeWorlds.Add(At(ball.Translation));
        shoeWorlds.Add(At(ankle.Translation));

        var source = schema.NewObject(ControllerSchema.ReverseFootClass);
        string controllerName = UniqueControllerName(Character, name ?? "RF_" + lower.Name);
        source.Set("Name", controllerName);
        source.Set("Flags", NewIkControllerFlags);
        source.Set("LTM", AktMath.ToArray(Matrix4x4.Identity));
        source.Set("Constraint", new AktReference(null));
        source.Set("TargetRenderSizeRatio", ReverseFootTargetRenderSizeRatio);
        var joints = (List<AktReference>)source["JointsString"]!;
        foreach (var joint in chain) joints.Add(new AktReference(joint.Source));
        var parents = (List<AktReference>)source["Parents"]!;
        for (var ancestor = chain[0].Parent3D; ancestor != null; ancestor = ancestor.Parent3D)
            if (ancestor is Joint parentJoint)
                parents.Add(new AktReference(parentJoint.Source));
        source.Set("DecoratorAxis", ShoeDecoratorAxis);
        source.Set("RollAxisToes", new float[4]);
        source.Set("RollAxisPlant", new[] { 1f, 0f, 0f, 0f });

        Character.Source.AddChild(source);
        var controller = (ControllerReverseFoot)Character.Model.Wrap(source);

        // The rig key carries the chain's FK locals plus the controller relative to the ball
        var keyChain = chain.Select(j => Transform.FromMatrixLoose(LocalInPose(pose, j))).ToList();
        if (Matrix4x4.Invert(ball, out var inverseBall))
            keyChain.Add(Transform.FromMatrixLoose(controllerWorld * inverseBall));
        AddController(source, isJoint: false, keyChain, controllerWorld * inverseCharacter);
        foreach (var joint in chain)
            joint.Source.Set("Constraint", new AktReference(source));

        var shoeRefs = (List<AktReference>)source["Shoe"]!;
        Object3D? host = controller;
        for (int i = 0; i < ShoeTypes.Count; i++)
        {
            var locator = schema.NewObject(ControllerSchema.LocatorClass);
            locator.Set("Name", UniqueControllerName(Character, $"{controllerName}_{ShoeTypes[i]}"));
            locator.Set("Flags", NewIkControllerFlags);
            locator.Set("LTM", AktMath.ToArray(Matrix4x4.Identity));
            locator.Set("Constraint", new AktReference(source));
            locator.Set("Type", ShoeType(i));
            locator.Set("Category", 1u);
            locator.Set("Index", (uint)i);
            locator.Set("Controller", new AktReference(source));
            host!.Source.AddChild(locator);
            var wrapped = (ReverseFootLocator)Character.Model.Wrap(locator);
            AddController(locator, isJoint: false, slotMatrix: shoeWorlds[i] * Inverse(shoeWorlds, i, controllerWorld));
            shoeRefs.Add(new AktReference(locator));
            host = wrapped;
        }
        return controller;
    }

    /// <summary><paramref name="v"/> normalised, or <paramref name="fallback"/> when it is degenerate.</summary>
    private static Vector3 Normalized(Vector3 v, Vector3 fallback) =>
        v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : fallback;

    /// <summary>The inverse of the previous shoe matrix (the controller for the first locator).</summary>
    private static Matrix4x4 Inverse(List<Matrix4x4> worlds, int index, Matrix4x4 first)
    {
        var parent = index == 0 ? first : worlds[index - 1];
        return Matrix4x4.Invert(parent, out var inverse) ? inverse : Matrix4x4.Identity;
    }

    /// <summary>The joints from <paramref name="upper"/> down to <paramref name="lower"/>, root first.</summary>
    private static IReadOnlyList<Joint> JointChain(Joint upper, Joint lower)
    {        var chain = new List<Joint>();
        for (Object3D? node = lower; node != null; node = node.Parent3D)
        {
            if (node is Joint joint) chain.Add(joint);
            if (ReferenceEquals(node, upper)) break;
        }
        chain.Reverse();
        if (chain.Count < 2 || !ReferenceEquals(chain[0], upper))
            throw new InvalidOperationException($"'{lower.Name}' is not below '{upper.Name}'.");
        return chain;
    }

    /// <summary>
    /// <c>DecoratorAxis</c>: of the lower joint's three basis rows, the one most parallel (largest
    /// |dot|, ties to the lower index) to the direction from the joint before it, in the pose's world
    /// space.
    /// </summary>
    private byte DecoratorAxis(CharacterPose pose, IReadOnlyList<Joint> chain)
    {
        var lower = WorldInPose(pose, chain[^1]);
        var previous = WorldInPose(pose, chain[^2]);
        var direction = lower.Translation - previous.Translation;
        if (direction.LengthSquared() < 1e-12f) return 0;
        direction = Vector3.Normalize(direction);
        Span<Vector3> rows =
        [
            new Vector3(lower.M11, lower.M12, lower.M13),
            new Vector3(lower.M21, lower.M22, lower.M23),
            new Vector3(lower.M31, lower.M32, lower.M33),
        ];
        int best = 0;
        float bestValue = -1f;
        for (int i = 0; i < 3; i++)
        {
            float value = MathF.Abs(Vector3.Dot(rows[i], direction));
            if (value > bestValue)
            {
                bestValue = value;
                best = i;
            }
        }
        return (byte)best;
    }

    // ---- reparenting -------------------------------------------------------------------------

    /// <summary>
    /// Parent Joints: makes <paramref name="newParent"/> the parent of every object, keeping the
    /// world. Constrained objects, objects carrying <see cref="NoReparentFlag"/> and cycles are
    /// refused.
    /// </summary>
    public int Parent(IEnumerable<Object3D> objects, Object3D newParent)
    {
        int count = 0;
        foreach (var o in objects.Distinct().ToList())
        {
            MoveKeepWorld(o, newParent, syncLtm: false, allowConstrained: false);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Hierarchy reparent: re-syncs <c>LTM</c> from the bind pose first (object, descendants and
    /// ancestors), then keeps the world.
    /// </summary>
    public int Reparent(IEnumerable<Object3D> objects, Object3D? newParent)
    {
        int count = 0;
        foreach (var o in objects.Distinct().ToList())
        {
            MoveKeepWorld(o, newParent, syncLtm: true, allowConstrained: false);
            count++;
        }
        return count;
    }

    /// <summary>
    /// The hierarchy panel's drop beside a row: moves <paramref name="o"/> under
    /// <paramref name="sibling"/>'s parent (a hierarchy reparent, keeping the world, when that is
    /// not already its parent) and puts it just before or after <paramref name="sibling"/> in the
    /// child list. A move inside the same parent only reorders the children.
    /// </summary>
    public void PlaceBeside(Object3D o, Object3D sibling, bool after)
    {
        if (ReferenceEquals(o, sibling)) return;
        if (sibling.Parent is not Object3D parent || sibling is Character)
            throw new InvalidOperationException("Only an object below a character can have a row placed beside it.");
        if (!ReferenceEquals(o.Parent, parent)) Reparent([o], parent);
        var children = parent.Source.Children.Where(c => !ReferenceEquals(c, o.Source)).ToList();
        int at = children.IndexOf(sibling.Source);
        if (at < 0) throw new InvalidOperationException(sibling.Name + " is not a child of " + parent.Name + ".");
        children.Insert(after ? at + 1 : at, o.Source);
        parent.Source.SetChildren(children);
    }

    /// <summary>Unparent Joints: moves the objects to the character, keeping the world.</summary>
    public int Unparent(IEnumerable<Object3D> objects)
    {
        int count = 0;
        foreach (var o in objects.Distinct().ToList())
        {
            MoveKeepWorld(o, Character, syncLtm: false, allowConstrained: false);
            count++;
        }
        return count;
    }

    // ---- deletion ----------------------------------------------------------------------------

    /// <summary>
    /// Removes the joints: their tracks from every animation, their slots from
    /// <c>Controllers</c>/<c>JointArray</c> and the poses, and their subtrees. Unselected children
    /// are moved to the character keeping their world, and the meshes' skin weights are remapped
    /// A joint that a controller drives takes that
    /// controller, its shoe locators and their tracks with it, and the surviving joints it
    /// constrained lose the reference (the selection gathers the
    /// <c>__Constraint</c> of every joint it deletes - open-questions 24).
    /// </summary>
    public int DeleteJoints(IEnumerable<Joint> joints)
    {
        var list = joints.Distinct().Where(j => Character.Joints.Contains(j)).ToList();
        if (list.Count == 0) return 0;
        var controllers = list.Select(j => j.Constraint).OfType<Controller3D>().Distinct().ToList();
        var deleted = new HashSet<AktObject>(ReferenceEqualityComparer.Instance);
        // Only the selected joints: their other children are kept (step 1), not deleted with them.
        foreach (var joint in list) deleted.Add(joint.Source);
        foreach (var controller in controllers) AddSubtree(deleted, controller);
        GatherConstraints(deleted);
        var oldJoints = Character.Joints.ToList();

        // 1. Keep the direct children: move the ones that are not deleted out to the character.
        foreach (var joint in list)
            foreach (var child in joint.Source.Children.ToList())
                if (!deleted.Contains(child) && Character.Model.Wrap(child) is Object3D o)
                    MoveKeepWorld(o, Character, syncLtm: false, allowConstrained: true);

        // 2. Every joint that pointed at a deleted controller loses the reference.
        ClearConstraints(deleted);

        // 3. Drop the tracks that target the deleted objects.
        RemoveTracks(deleted);

        // 4. Remove the controller slots (pose matrices first, then the reference arrays).
        RemoveSlots(deleted);

        // 5. A deleted camera's lost view slots and CameraList entry (the hidden-joint path only).
        RemoveCameras(deleted);

        // 6. Detach the subtrees.
        Detach(deleted);

        // 6. Remap the skin weights (a SloppyAnimatsu choice).
        RemapWeights(new HashSet<AktObject>(list.Select(j => j.Source), ReferenceEqualityComparer.Instance), oldJoints);
        return list.Count;
    }

    /// <summary>
    /// Removes controllers with their whole subtree (the seven shoe locators for a reverse foot),
    /// which is what the Delete does to a selected controller row. For example,
    /// deleting
    /// <c>RF_Toes_L</c> removed it and its seven locators, cleared the four chain joints'
    /// <c>Constraint</c>, took their eight tracks (<c>__RigAnim</c> 82 -&gt; 74) and the same slots
    /// from <c>Controllers</c> and every pose's matrices (82 -&gt; 74); the joints and their tracks
    /// stayed, and the file round-tripped byte-exact. An animated camera is such a row too: its
    /// subtree holds the hidden joint, its <c>__Constraint</c> names the target
    /// (<see cref="GatherConstraints"/>), and the whole rig's tracks, slots and camera-list entry go
    /// with it.
    /// </summary>
    public int DeleteControllers(IEnumerable<Object3D> controllers)
    {
        var list = controllers.Distinct().Where(c => Character.Controllers.Contains(c)).ToList();
        if (list.Count == 0) return 0;
        // The rule: a target is deleted with its camera (its chain root),
        // never on its own; the camera brings the target through its __Constraint (gathered below).
        var sources = list.Select(c => c.Source).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var target in list.OfType<CameraTarget>())
            if (target.Camera is { } camera && !sources.Contains(camera.Source))
                throw new InvalidOperationException(
                    $"'{target.Name}' is the target of camera '{camera.Name}'; delete the camera to remove its rig.");
        var deleted = new HashSet<AktObject>(ReferenceEqualityComparer.Instance);
        foreach (var controller in list) AddSubtree(deleted, controller);
        GatherConstraints(deleted);
        ClearConstraints(deleted);
        RemoveTracks(deleted);
        RemoveSlots(deleted);
        RemoveCameras(deleted);
        Detach(deleted);
        return list.Count;
    }

    private static void AddSubtree(HashSet<AktObject> deleted, SceneObject root)
    {
        foreach (var node in root.Source.PreOrder()) deleted.Add(node);
    }

    /// <summary>
    /// The delete gathers the selected object's
    /// whole hierarchy and, <b>for every gathered object, the object its <c>__Constraint</c>
    /// names</b>. With <see cref="AddSubtree"/> above that is how a deleted camera takes the rig it
    /// constrains: the camera's <c>__Constraint</c> is its target, and the hidden joint (a child of the
    /// camera) names the same target, so the target's tracks and slot go with the camera
    /// A gathered camera target
    /// also brings its chain root, since the app refuses to delete a target whose camera
    /// is not deleted.
    /// </summary>
    private void GatherConstraints(HashSet<AktObject> deleted)
    {
        var queue = new Queue<AktObject>(deleted);
        void Gather(AktObject root)
        {
            foreach (var node in root.PreOrder())
                if (deleted.Add(node)) queue.Enqueue(node);
        }
        while (queue.Count > 0)
        {
            var source = queue.Dequeue();
            if (source["Constraint"] is AktReference { Target: { } constrained }) Gather(constrained);
            if (Character.Model.Wrap(source) is CameraTarget { Camera: { } camera }) Gather(camera.Source);
        }
    }

    /// <summary>
    /// A deleted camera leaves <c>CScene.CameraList</c>, and every view slot that looked through it
    /// falls back to Perspective.
    /// </summary>
    private void RemoveCameras(HashSet<AktObject> deleted)
    {
        foreach (var source in deleted)
            if (Character.Model.Wrap(source) is Camera camera)
                Character.Model.Scene.RemoveCamera(camera);
    }

    /// <summary>Every joint whose <c>Constraint</c> points into the deleted set loses it.</summary>
    private void ClearConstraints(HashSet<AktObject> deleted)
    {
        foreach (var joint in Character.Joints)
            if (joint.Source["Constraint"] is AktReference { Target: { } target } && deleted.Contains(target))
                joint.Source.Set("Constraint", new AktReference(null));
    }

    /// <summary>
    /// Takes the deleted objects' slots out of <c>Controllers</c> and out of every pose's
    /// <c>Matrices</c> (which stay parallel to it), and their entries out of <c>JointArray</c>.
    /// </summary>
    private void RemoveSlots(HashSet<AktObject> deleted)
    {
        var controllers = References(Character.Source, "Controllers");
        var indices = new List<int>();
        for (int i = 0; i < controllers.Count; i++)
            if (controllers[i].Target is { } target && deleted.Contains(target)) indices.Add(i);
        foreach (var pose in Poses())
        {
            var matrices = SceneValues.Items(pose.Source, "Matrices");
            foreach (int index in indices.OrderByDescending(i => i))
                if (index < matrices.Count) matrices.RemoveAt(index);
        }
        foreach (int index in indices.OrderByDescending(i => i)) controllers.RemoveAt(index);
        var jointRefs = References(Character.Source, "JointArray");
        jointRefs.RemoveAll(r => r.Target is { } target && deleted.Contains(target));
    }

    /// <summary>Detaches the deleted set's top objects; a child whose parent goes with it stays.</summary>
    private static void Detach(HashSet<AktObject> deleted)
    {
        foreach (var source in deleted)
            if (source.Parent is not { } parent || !deleted.Contains(parent))
                source.Parent?.RemoveChild(source);
    }

    // ---- orientations -----------------------------------------------------------------------

    /// <summary>
    /// Orient Joints: turns each joint so its aim axis (<see cref="JointAlignment.Aim"/>; local Y
    /// with the shipped preferences) points at its first child, keeping the joint's translation, its
    /// scale and every child's world; the up axis is kept orthogonal where possible
    /// Writes the rig pose and <c>LTM</c>.
    /// </summary>
    public int OrientJoints(IEnumerable<Joint> joints, JointAlignment? alignment = null)
    {
        var settings = alignment ?? JointAlignment.Default;
        var pose = RigPose ?? throw new InvalidOperationException($"{Character.Name} has no pose to edit.");
        int count = 0;
        // Parents first: a joint's own basis is then final when its children's compensations are
        // computed, and the LTM chain the save reads stays equal to the pose chain.
        foreach (var joint in joints.Distinct().OrderBy(DepthOf))
        {
            if (!AlignJointInPose(pose, joint, settings)) continue;
            count++;
        }
        return count;
    }

    /// <summary>Hierarchy depth, the order <see cref="OrientJoints"/> processes a chain in.</summary>
    private static int DepthOf(Object3D o)
    {
        int depth = 0;
        for (var p = o.Parent3D; p != null; p = p.Parent3D) depth++;
        return depth;
    }

    /// <summary>
    /// Aligns one joint's basis in one pose so its aim axis points at its first controller child,
    /// keeping the child's world (the pivot change's compensation). Returns false for a leaf.
    /// </summary>
    private bool AlignJointInPose(CharacterPose pose, Joint joint, JointAlignment settings)
    {
        // The app looks for the next controller under the joint; a mesh there is not a bone.
        var children = joint.Children.OfType<Object3D>()
            .Where(c => c.Parent3D == joint && Character.Controllers.Contains(c))
            .ToList();
        if (children.Count == 0) return false;
        var jointWorld = WorldInPose(pose, joint);
        var childWorld = WorldInPose(pose, children[0]);
        var direction = childWorld.Translation - jointWorld.Translation;
        if (direction.LengthSquared() < 1e-10f) return false;
        var parentWorld = WorldInPose(pose, joint.Parent3D);
        var parentScale = joint.Parent3D is { } parent
            ? Object3D.LocalScale(LocalInPose(pose, parent))
            : Vector3.One;
        var parentBasis = joint.CompensatesParentScale
            ? WorldComposition.Compensated(parentWorld, parentScale)
            : parentWorld;
        if (!Matrix4x4.Invert(parentBasis, out var inverseParent)) return false;
        var childWorlds = children.Select(c => WorldInPose(pose, c)).ToList();

        var basis = AlignedBasis(jointWorld, Vector3.Normalize(direction), settings) * inverseParent;
        var local = LocalInPose(pose, joint);
        local.M11 = basis.M11; local.M12 = basis.M12; local.M13 = basis.M13;
        local.M21 = basis.M21; local.M22 = basis.M22; local.M23 = basis.M23;
        local.M31 = basis.M31; local.M32 = basis.M32; local.M33 = basis.M33;
        int slot = SlotOf(joint);
        if (slot >= 0 && slot < pose.Count) pose.SetMatrix(slot, local);
        // The saved file's worlds are chained from LTM, so the joint's LTM has to take the new
        // basis in the same step the pose does - otherwise the children's compensation below is
        // computed against a stale parent world and every world past this joint moves.
        bool writingLtm = ReferenceEquals(pose, RigPose);
        if (writingLtm) joint.LocalMatrix = local;

        // A pivot change keeps its children where they are (the transform-axis action's
        // compensation), so the new aim axis really points at the unchanged child.
        var newJointWorld = WorldInPose(pose, joint);
        var jointScale = Object3D.LocalScale(local);
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            int childSlot = SlotOf(child);
            if (childSlot < 0 || childSlot >= pose.Count ||
                !WorldComposition.TryDecompose(childWorlds[i], newJointWorld, jointScale,
                    child.CompensatesParentScale, out var childLocal)) continue;
            pose.SetMatrix(childSlot, childLocal);
            // Pose and LTM get the same matrix, so the child's world survives in both chains and
            // a later joint in the same call still reads a consistent parent.
            if (writingLtm) child.LocalMatrix = childLocal;
        }
        return true;
    }

    /// <summary>
    /// The joint's rig-pose matrix turned so that its configured aim axis is
    /// <paramref name="direction"/> and its up axis is kept where possible. The basis rows are the
    /// images of the local axes in a row-vector matrix, so <see cref="JointAxis.Y"/> is row 2.
    /// </summary>
    private static Matrix4x4 AlignedBasis(Matrix4x4 world, Vector3 direction, JointAlignment settings)
    {
        var aimAxis = settings.Aim;
        var upAxis = settings.Up == aimAxis
            ? (aimAxis == JointAxis.X ? JointAxis.Z : JointAxis.X)
            : settings.Up;
        var thirdAxis = (JointAxis)(3 - (int)aimAxis - (int)upAxis);
        var aim = direction * (settings.AimNegative ? -1f : 1f);
        var upSource = Row(world, upAxis);
        if (upSource.LengthSquared() < 1e-12f) upSource = Row(world, thirdAxis);
        var up = upSource - aim * Vector3.Dot(aim, upSource);
        if (up.LengthSquared() < 1e-8f)
        {
            var third = Row(world, thirdAxis);
            up = Vector3.Cross(third, aim);
        }
        if (up.LengthSquared() < 1e-8f) return world;
        up = Vector3.Normalize(up);
        if (settings.UpNegative) up = -up;
        var thirdVector = Vector3.Normalize(Vector3.Cross(aim, up));
        up = Vector3.Cross(thirdVector, aim);
        // JointAxis is the preference enum (X = 0, Z = 1, Y = 2); a basis row is X = row 1,
        // Y = row 2, Z = row 3, so the slot is a separate mapping.
        var axes = new Vector3[3];
        axes[SlotOf(aimAxis)] = aim;
        axes[SlotOf(upAxis)] = up;
        axes[SlotOf(thirdAxis)] = thirdVector;
        float sx = Row(world, JointAxis.X).Length();
        float sy = Row(world, JointAxis.Y).Length();
        float sz = Row(world, JointAxis.Z).Length();
        var scales = new[] { sx, sy, sz };
        return new Matrix4x4(
            axes[0].X * scales[0], axes[0].Y * scales[0], axes[0].Z * scales[0], 0,
            axes[1].X * scales[1], axes[1].Y * scales[1], axes[1].Z * scales[1], 0,
            axes[2].X * scales[2], axes[2].Y * scales[2], axes[2].Z * scales[2], 0,
            0, 0, 0, 1);
    }

    /// <summary>The matrix row of an axis: X is row 1, Y row 2, Z row 3.</summary>
    private static int SlotOf(JointAxis axis) => axis switch
    {
        JointAxis.X => 0,
        JointAxis.Y => 1,
        _ => 2,
    };

    /// <summary>One basis row of a matrix, i.e. the world image of that local axis.</summary>
    private static Vector3 Row(Matrix4x4 m, JointAxis axis) => axis switch
    {
        JointAxis.X => new Vector3(m.M11, m.M12, m.M13),
        JointAxis.Z => new Vector3(m.M31, m.M32, m.M33),
        _ => new Vector3(m.M21, m.M22, m.M23),
    };

    // ---- LTM snapshot ------------------------------------------------------------------------

    /// <summary>
    /// Sets each object's <c>LTM</c> from its slot in the character's bind pose (or <c>RefPose</c>
    /// when there is no bind pose), and does the same for its descendants and its ancestors, as
    /// <c>UpdateChildAndAncestorLTMAccordingToBindOrDefaultPose</c> does.
    /// </summary>
    public void SyncLtm(IEnumerable<Object3D> objects)
    {
        var pose = Character.BindPoses.FirstOrDefault() ?? Character.RefPose;
        if (pose == null) return;
        var all = new List<Object3D>();
        foreach (var o in objects)
        {
            all.AddRange(o.Source.PreOrder().Select(Character.Model.Wrap).OfType<Object3D>());
            for (var p = o.Parent; p != null; p = p.Parent)
                if (p is Object3D ancestor) all.Add(ancestor);
        }
        foreach (var o in all.Distinct())
        {
            int slot = SlotOf(o);
            if (slot >= 0 && slot < pose.Count) o.LocalMatrix = pose.Matrix(slot);
        }
    }

    /// <summary>Syncs every controller's <c>LTM</c> from the bind pose.</summary>
    public void SyncLtm() => SyncLtm(Character.Controllers);

    // ---- world helpers -----------------------------------------------------------------------

    /// <summary>The object's local matrix in a pose (its pose slot, else its <c>LTM</c>).</summary>
    public Matrix4x4 LocalInPose(CharacterPose? pose, Object3D o)
    {
        int slot = SlotOf(o);
        return pose != null && slot >= 0 && slot < pose.Count ? pose.Matrix(slot) : o.LocalMatrix;
    }

    /// <summary>The object's world matrix in a pose, composed like <see cref="Object3D.WorldMatrix"/>.</summary>
    public Matrix4x4 WorldInPose(CharacterPose? pose, Object3D? o)
    {
        if (o == null) return Matrix4x4.Identity;
        var chain = new List<Object3D>();
        for (var c = o; c != null; c = c.Parent3D) chain.Add(c);
        chain.Reverse();
        var world = Matrix4x4.Identity;
        var parentScale = Vector3.One;
        foreach (var node in chain)
        {
            var local = LocalInPose(pose, node);
            world = WorldComposition.Compose(local, world, parentScale, node.CompensatesParentScale);
            parentScale = Object3D.LocalScale(local);
        }
        return world;
    }

    /// <summary>A local matrix that puts a joint at <paramref name="world"/> under <paramref name="parent"/>.</summary>
    private Matrix4x4 LocalAt(Object3D parent, Vector3 world)
    {
        var parentWorld = WorldInPose(RigPose, parent);
        if (!Matrix4x4.Invert(parentWorld, out var inverse))
            throw new InvalidOperationException($"{parent.Name} has a singular world matrix.");
        return Matrix4x4.CreateTranslation(Vector3.Transform(world, inverse));
    }

    // ---- internals ---------------------------------------------------------------------------

    private void EnsureInCharacter(Object3D o)
    {
        if (ReferenceEquals(o, Character)) return;
        if (o.Ancestor<Character>() != Character)
            throw new InvalidOperationException($"{o.Name} is not part of {Character.Name}.");
    }

    private AktObject NewJointSource(string? name, Matrix4x4 local)
    {
        var schema = Character.Model.Document.Schema;
        AktObject source;
        try
        {
            source = schema.NewObject("CJoint");
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("The scene schema has no CJoint class; it has no skeleton to edit.");
        }
        source.Set("Name", UniqueControllerName(Character, name ?? DefaultJointName));
        source.Set("Flags", NewControllerFlags);
        source.Set("LTM", AktMath.ToArray(local));
        source.Set("Constraint", new AktReference(null));
        source.Set("SmoothMethod", 0);
        return source;
    }

    /// <summary>
    /// The add-controller part: append to <c>Controllers</c> (and
    /// <c>JointArray</c>), add one matrix at the slot of every pose, and create the
    /// <c>__RigAnim</c> track. For an IK controller <paramref name="chain"/> is the track's
    /// <c>RefArray</c>.
    /// </summary>
    private void AddController(AktObject source, bool isJoint, IReadOnlyList<Transform>? chain = null,
        Matrix4x4? slotMatrix = null)
    {
        var controllers = References(Character.Source, "Controllers");
        int slot = controllers.Count;
        controllers.Add(new AktReference(source));
        if (isJoint) References(Character.Source, "JointArray").Add(new AktReference(source));

        var matrix = slotMatrix is { } m ? AktMath.ToArray(m) : (float[])source["LTM"]!;
        foreach (var pose in Poses())
        {
            var matrices = SceneValues.Items(pose.Source, "Matrices");
            if (slot < matrices.Count) matrices.Insert(slot, (float[])matrix.Clone());
            else matrices.Add((float[])matrix.Clone());
        }

        var wrapped = Character.Model.Wrap(source) as Object3D;
        if (wrapped != null && Character.RigAnimation?.Stacks.FirstOrDefault() is { } stack &&
            stack.TrackFor(wrapped) == null)
            TrackFactory.Create(stack, wrapped, chain);
    }

    private static List<AktReference> References(AktFieldContainer container, string field) =>
        container[field] as List<AktReference> ?? [];

    private void RemoveTracks(HashSet<AktObject> targets)
    {
        var animations = Character.Animations.ToList();
        if (Character.RigAnimation is { } rig) animations.Add(rig);
        foreach (var animation in animations.Distinct())
            foreach (var stack in animation.Stacks)
                foreach (var track in stack.Tracks.ToList())
                    if (track.Target is { } target && targets.Contains(target.Source))
                        stack.Source.RemoveChild(track.Source);
    }

    /// <summary>
    /// Moves <paramref name="o"/> under <paramref name="newParent"/> keeping its world in every pose
    /// and in <c>LTM</c> (the smart update that keeps the world).
    /// A null parent means the scene root.
    /// </summary>
    private void MoveKeepWorld(Object3D o, Object3D? newParent, bool syncLtm, bool allowConstrained)
    {
        if (ReferenceEquals(o, newParent)) throw new InvalidOperationException("An object cannot be parented to itself.");
        if (newParent != null && IsDescendant(o, newParent))
            throw new InvalidOperationException($"{newParent.Name} is a descendant of {o.Name}.");
        if (!allowConstrained && o.Constraint is { } constraint)
            throw new InvalidOperationException($"{o.Name} is constrained by {constraint.Name} and cannot be reparented.");
        if (!allowConstrained && (o.Flags & NoReparentFlag) != 0)
            throw new InvalidOperationException($"{o.Name} cannot be reparented.");
        if (syncLtm) SyncLtm([o]);

        // Capture the whole subtree's world matrices before the move. The object's local scale is
        // part of the composition of a scale-compensated child, so the subtree is re-decomposed
        // against its new parents to keep every world exactly
        var subtree = o.Source.PreOrder().Select(Character.Model.Wrap).OfType<Object3D>().ToList();
        var entries = Poses().Select(pose =>
        {
            var worlds = new Dictionary<Object3D, Matrix4x4>(ReferenceEqualityComparer.Instance);
            foreach (var node in subtree) worlds[node] = WorldInPose(pose, node);
            return (Pose: pose, Worlds: worlds);
        }).ToList();
        var ltmWorlds = new Dictionary<Object3D, Matrix4x4>(ReferenceEqualityComparer.Instance);
        foreach (var node in subtree) ltmWorlds[node] = node.WorldMatrix;

        o.Source.Parent?.RemoveChild(o.Source);
        if (newParent != null) newParent.Source.AddChild(o.Source);
        else Character.Model.Scene.Source.AddChild(o.Source);

        int slot = SlotOf(o);
        foreach (var entry in entries)
        {
            if (slot < 0 || slot >= entry.Pose.Count) continue;
            if (WorldComposition.TryDecompose(entry.Worlds[o], WorldInPose(entry.Pose, newParent),
                    newParent != null ? Object3D.LocalScale(LocalInPose(entry.Pose, newParent)) : Vector3.One,
                    o.CompensatesParentScale, out var local))
                entry.Pose.SetMatrix(slot, local);
        }
        if (WorldComposition.TryDecompose(ltmWorlds[o], newParent?.WorldMatrix ?? Matrix4x4.Identity,
                newParent != null ? Object3D.LocalScale(newParent.LocalMatrix) : Vector3.One,
                o.CompensatesParentScale, out var ltm))
            o.LocalMatrix = ltm;

        // Children keep their worlds: parents first, so each decomposition sees the final parent.
        foreach (var entry in entries) PreserveWorlds(entry.Pose, o, entry.Worlds);
        PreserveLtmWorlds(o, ltmWorlds);
    }

    /// <summary>Re-decomposes every controller below <paramref name="root"/> in one pose so its world is kept.</summary>
    private void PreserveWorlds(CharacterPose pose, Object3D root, Dictionary<Object3D, Matrix4x4> worlds)
    {
        foreach (var node in root.Source.PreOrder().Skip(1).Select(Character.Model.Wrap).OfType<Object3D>())
        {
            int slot = SlotOf(node);
            if (slot < 0 || slot >= pose.Count || node.Parent3D is not { } parent) continue;
            if (!worlds.TryGetValue(node, out var world)) continue;
            if (WorldComposition.TryDecompose(world, WorldInPose(pose, parent),
                    Object3D.LocalScale(LocalInPose(pose, parent)), node.CompensatesParentScale, out var local))
                pose.SetMatrix(slot, local);
        }
    }

    /// <summary>The <c>LTM</c> version of <see cref="PreserveWorlds"/>.</summary>
    private void PreserveLtmWorlds(Object3D root, Dictionary<Object3D, Matrix4x4> worlds)
    {
        foreach (var node in root.Source.PreOrder().Skip(1).Select(Character.Model.Wrap).OfType<Object3D>())
        {
            if (SlotOf(node) < 0 || node.Parent3D is not { } parent) continue;
            if (!worlds.TryGetValue(node, out var world)) continue;
            if (WorldComposition.TryDecompose(world, parent.WorldMatrix,
                    Object3D.LocalScale(parent.LocalMatrix), node.CompensatesParentScale, out var local))
                node.LocalMatrix = local;
        }
    }

    private static bool IsDescendant(Object3D ancestor, Object3D node)
    {
        for (var p = node.Parent; p != null; p = p.Parent)
            if (ReferenceEquals(p, ancestor)) return true;
        return false;
    }

    /// <summary>
    /// Deletes the influences of the removed joints from every skinned mesh bound to the character,
    /// shifts the remaining bone indices down and renormalises the weights (a SloppyAnimatsu choice).
    /// </summary>
    private void RemapWeights(HashSet<AktObject> deleted, IReadOnlyList<Joint> oldJoints)
    {
        var map = new Dictionary<int, int>();
        for (int i = 0, next = 0; i < oldJoints.Count; i++)
            if (!deleted.Contains(oldJoints[i].Source)) map[i] = next++;
        foreach (var mesh in Character.Meshes)
            foreach (string field in new[] { "SkinWeightArray", "RigidSkinWeightArray" })
            {
                if (mesh.Source[field] is not List<object?> arrays) continue;
                foreach (var entry in arrays)
                {
                    if (entry is not AktStruct array) continue;
                    var weights = SceneValues.Items(array, "WeightArray");
                    var kept = new List<object?>();
                    float sum = 0;
                    foreach (var item in weights)
                    {
                        if (item is not AktStruct weight) continue;
                        uint old = weight.Get<uint>("BoneIndex");
                        if (old >= oldJoints.Count || !map.TryGetValue((int)old, out int next)) continue;
                        weight.Set("BoneIndex", (uint)next);
                        kept.Add(weight);
                        sum += weight.Get<float>("Weight");
                    }
                    if (kept.Count == 0 && map.Count > 0)
                    {
                        var weight = Character.Model.Document.Schema.NewStruct("CVertexWeight");
                        weight.Set("BoneIndex", 0u);
                        weight.Set("Weight", 1f);
                        kept.Add(weight);
                        sum = 1f;
                    }
                    if (sum > 1e-6f && Math.Abs(sum - 1f) > 1e-5f)
                        foreach (var item in kept)
                        {
                            var weight = (AktStruct)item!;
                            weight.Set("Weight", weight.Get<float>("Weight") / sum);
                        }
                    weights.Clear();
                    weights.AddRange(kept);
                }
            }
    }
}




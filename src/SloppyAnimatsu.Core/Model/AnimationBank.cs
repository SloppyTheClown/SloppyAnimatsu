using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Audio;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The naming rule applied whenever an object is renamed, created or copied under a parent
/// </summary>
public static class UniqueNames
{
    private static readonly Regex Suffix = new(@"_[0-9]+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// <paramref name="requested"/> if no other child of <paramref name="parent"/> has that name,
    /// else <c>base_NN</c> with NN one past the highest <c>base_&lt;digits&gt;</c> sibling (at least 2).
    /// A base that already ends in an underscore keeps one separator, and a file-like name keeps its
    /// extension last: the.akt import renamed <c>Dup Bomb_</c> to <c>Dup Bomb_02</c> and
    /// <c>Dup_b_NoTrans.png</c> to <c>Dup_b_NoTrans_02.png</c>.
    /// </summary>
    public static string For(AktObject? parent, string requested, AktObject? self = null)
    {
        if (parent == null) return requested;
        var siblings = parent.Children.Where(c => !ReferenceEquals(c, self)).Select(c => c.Name).ToList();
        if (!siblings.Contains(requested)) return requested;
        var (requestedStem, extension) = SplitExtension(requested);
        string @base = Suffix.Replace(requestedStem, "").TrimEnd('_');
        var numbered = new Regex("^" + Regex.Escape(@base) + "_([0-9]+)" + Regex.Escape(extension) + "$",
            RegexOptions.CultureInvariant);
        long highest = 0;
        foreach (var name in siblings)
            if (name != null && numbered.Match(name) is { Success: true } m && long.TryParse(m.Groups[1].Value, out long n))
                highest = Math.Max(highest, n);
        return $"{@base}_{Math.Max(2, highest + 1).ToString("00", CultureInfo.InvariantCulture)}{extension}";
    }

    /// <summary>
    /// The rule the app applies to an imported **texture**: a free name stays, a taken one gets
    /// <c>_02</c> before its extension, and a name that already carries a number is numbered again
    /// (<c>Dup_b_NoTrans_02.png</c> -> <c>Dup_b_NoTrans_02_02.png</c>) - from
    /// its own .akt import, where the material pool numbered instead (<c>Dup_b</c> -> <c>Dup_b_02</c>
    /// -> <c>Dup_b_03</c>).
    /// </summary>
    public static string ForFile(AktObject? parent, string requested)
    {
        if (parent == null) return requested;
        var siblings = parent.Children.Select(c => c.Name).ToList();
        if (!siblings.Contains(requested)) return requested;
        var (stem, extension) = SplitExtension(requested);
        for (int n = 2; n < 100; n++)
        {
            string candidate = $"{stem}_{n.ToString("00", CultureInfo.InvariantCulture)}{extension}";
            if (!siblings.Contains(candidate)) return candidate;
        }
        return requested;
    }

    /// <summary>
    /// Splits a file-like name into stem and extension (<c>Dup_b.png</c> -> <c>Dup_b</c> +
    /// <c>.png</c>). Only a final dot followed by two to five letters counts, so an authoring name
    /// such as <c>Spine.001</c> stays whole.
    /// </summary>
    private static (string Stem, string Extension) SplitExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || name.Length - dot - 1 is < 2 or > 5) return (name, "");
        for (int i = dot + 1; i < name.Length; i++)
            if (!char.IsAsciiLetter(name[i])) return (name, "");
        return (name[..dot], name[dot..]);
    }
}

/// <summary>
/// Builds new tracks with the defaults
/// </summary>
internal static class TrackFactory
{
    private const uint MrsMask = (uint)(AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Scale);
    private const uint IKMask = (uint)(AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.PoleAngle);
    /// <summary>A camera's channels: translation, rotation and the FOV; scale is forbidden.</summary>
    private const uint CameraMask = (uint)(AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Fov);
    private const uint FootMask = 0x1FFF;

    /// <summary>Track class, key type, mask, tangent channel count, tangent mode and requested name for <paramref name="target"/>.</summary>
    public static (string Class, string Key, uint Mask, int Channels, uint Mode, string Name) Kind(Object3D target) => target switch
    {
        ControllerReverseFoot => ("CAnimTrackReverseFoot_VK", "CKeyReverseFoot", FootMask, 13, 0x144, "Reverse Foot Track"),
        ControllerIK => ("CAnimTrackIK_VK", "CKeyIK", IKMask, 10, 0x144, "Track"),
        CameraTarget => (CameraSchema.LookerTrackClass, ControllerSchema.IkKeyStruct, IKMask, 10, 0x144, "Track_02"),
        CameraAnimated => (CameraSchema.CameraTrackClass, CameraSchema.CameraKeyStruct, CameraMask, 10, 0x133, "Camera Track"),
        BlendshapeController => ("CAnimTrackBlendshape", "CKeyBlendshape",
            (uint)(AnimChannels.BlendshapeX | AnimChannels.BlendshapeY), 2, 0x133, "Blendshape Track"),
        ReverseFootLocator => ("CAnimTrackMRS", "CKeyMRS", MrsMask, 9, 0x133, "Track"),
        _ => ("CAnimTrackMRS", "CKeyMRS", MrsMask, 9, 0x100, "Track"),
    };

    /// <summary>
    /// Appends a new track for <paramref name="target"/> to <paramref name="stack"/>, one key per
    /// stack key: value keys are identity offsets, tangent keys are zero slopes. For IK and
    /// reverse-foot targets <paramref name="chain"/> is the value keys' <c>RefArray</c>
    /// (FK keys: flags 0, angles 0).
    /// </summary>
    public static AnimTrack Create(AnimStack stack, Object3D target, IReadOnlyList<Transform>? chain = null)
    {
        var model = stack.Model;
        var schema = model.Document.Schema;
        // A scene made by File > New declares no animation classes at all: the track layer is added
        // with the first track.
        AnimationSchema.Ensure(schema);
        var kind = Kind(target);
        bool blendshape = kind.Key == "CKeyBlendshape";
        if (blendshape) BlendshapeSchema.Ensure(schema);
        if (kind.Class == CameraSchema.CameraTrackClass) CameraSchema.Ensure(schema);
        if (kind.Class == CameraSchema.LookerTrackClass) CameraSchema.EnsureRig(schema);
        var source = schema.NewObject(kind.Class);
        source.Set("Name", UniqueNames.For(stack.Source, kind.Name));
        source.Set("ChannelMask", kind.Mask);
        source.Set("Target", new AktReference(target.Source));
        int n = stack.Keys.Count;
        var infos = (List<object?>)source["TangentInfoArray"]!;
        foreach (var field in new[] { "KeyList", "TInList", "TOutList" })
        {
            var list = (List<object?>)source[field]!;
            bool value = field == "KeyList";
            for (int i = 0; i < n; i++)
            {
                var key = schema.NewPolymorphic(kind.Key);
                if (blendshape)
                {
                    var values = (List<object?>)key["_values"]!;
                    values.Clear();
                    values.Add(0f);
                    values.Add(0f);
                }
                else
                {
                    var wrapped = (TransformKey)AnimKey.Wrap(model, key);
                    wrapped.Value = value ? Transform.Identity : new Transform(Vector3.Zero, Quaternion.Identity, Vector3.Zero);
                    if (wrapped is CameraKey cameraKey) cameraKey.Fov = value ? CameraKey.DefaultFov : 0f;
                    if (value) key.Set("TrackMRS", new AktReference(source));
                    if (key.Has("TargetControllerIK"))
                    {
                        key.Set("TargetControllerIK", new AktReference(target.Source));
                        if (key["RenderKey"] is AktStruct render) WriteIdentity(render);
                        if (value && chain != null) SetRefArray(schema, key, chain);
                    }
                }
                list.Add(key);
            }
        }
        for (int i = 0; i < n; i++)
        {
            var info = schema.NewPolymorphic(kind.Class == "CAnimTrackMRS" ? "CTangentInfoListMRS"
                : blendshape ? BlendshapeSchema.TangentListStruct : TangentListType(schema, kind.Class));
            var channels = (List<object?>)info["TangentInfoChannels"]!;
            for (int c = 0; c < kind.Channels; c++)
            {
                var channel = schema.NewStruct("CTangentInfo");
                // The angle channels after the nine transform channels (IK pole angle, foot rolls)
                // use the locator mode.
                channel.Set("TangentMode", c >= 9 ? 0x133u : kind.Mode);
                channels.Add(channel);
            }
            infos.Add(info);
        }
        stack.Source.AddChild(source);
        var track = (AnimTrack)model.Wrap(source);
        // A camera's editor rig keys in the same stack: its hidden joint's MRS track and its target's
        // looker track, in file order after the camera track.
        if (target is CameraAnimated animated && CameraEditor.Rig(animated) is { } rig)
        {
            if (stack.TrackFor(rig.HideJoint) == null) Create(stack, rig.HideJoint);
            if (stack.TrackFor(rig.Target) == null) Create(stack, rig.Target);
        }
        return track;
    }

    /// <summary>The tangent-info list struct used by IK and reverse-foot tracks in this file's schema.</summary>
    private static string TangentListType(AktSchema schema, string trackClass)
    {
        var names = trackClass switch
        {
            "CAnimTrackReverseFoot_VK" => new[] { "CTangentInfoListReverseFoot", "CTangentInfoListIK", "CTangentInfoListMRS" },
            CameraSchema.CameraTrackClass => new[] { CameraSchema.CameraTangentListStruct, "CTangentInfoListMRS" },
            _ => new[] { "CTangentInfoListIK", "CTangentInfoListMRS" },
        };
        foreach (var name in names)
            if (schema.FindStruct(name) != null) return name;
        return "CTangentInfoListMRS";
    }

    private static void WriteIdentity(AktStruct key)
    {
        key.Set("TranslationX", 0f);
        key.Set("TranslationY", 0f);
        key.Set("TranslationZ", 0f);
        key.Set("Rotation", new[] { 0f, 0f, 0f, 1f });
        key.Set("ScaleX", 1f);
        key.Set("ScaleY", 1f);
        key.Set("ScaleZ", 1f);
    }

    private static void SetRefArray(AktSchema schema, AktStruct key, IReadOnlyList<Transform> chain)
    {
        var items = (IList<object?>)key["RefArray"]!;
        items.Clear();
        foreach (var t in chain)
        {
            var r = schema.NewStruct("IKBaseRefInfo");
            r.Set("Translation", Fill(r.Get<float[]>("Translation"), [t.Translation.X, t.Translation.Y, t.Translation.Z], 1f));
            r.Set("Rotation", AktMath.ToArray(t.Rotation));
            r.Set("Scale", Fill(r.Get<float[]>("Scale"), [t.Scale.X, t.Scale.Y, t.Scale.Z], 1f));
            items.Add(r);
        }
    }

    /// <summary><paramref name="xyz"/> in the stored array's length; a fourth component is <paramref name="w"/>.</summary>
    private static float[] Fill(float[] stored, float[] xyz, float w)
    {
        var result = new float[Math.Max(stored.Length, 3)];
        Array.Copy(xyz, result, 3);
        for (int i = 3; i < result.Length; i++) result[i] = w;
        return result;
    }
}

/// <summary>
/// The Anim Bank and the Mixer: creating, copying, deleting and renaming animations, and the stack
/// actions. Edits change the scene document in place.
/// </summary>
public sealed class AnimationBankEditor
{
    /// <summary>Flags bit 0: the stack loops.</summary>
    public const uint CycleFlag = 0x1;
    /// <summary>Flags bit 1: the stack is mixed.</summary>
    public const uint EnabledFlag = 0x2;
    /// <summary>Flags bit 2: the hammer.</summary>
    public const uint HammerFlag = 0x4;
    /// <summary>Flags bit 3: additive layer, else override.</summary>
    public const uint AdditiveFlag = 0x8;

    public AnimationBankEditor(Character character, PoseEvaluator? evaluator = null)
    {
        Character = character;
        Evaluator = evaluator ?? new PoseEvaluator(character);
        Keys = new KeyEditor(character, Evaluator);
    }

    public Character Character { get; }
    public PoseEvaluator Evaluator { get; }
    private KeyEditor Keys { get; }

    // ---- names ---------------------------------------------------------------------------------

    /// <summary>Renames <paramref name="o"/> through the unique-name rule; returns the name it got.</summary>
    public static string Rename(SceneObject o, string name)
    {
        string unique = UniqueNames.For(o.Source.Parent, name, o.Source);
        o.Name = unique;
        return unique;
    }

    // ---- animations ----------------------------------------------------------------------------

    /// <summary>
    /// Creates an animation in <paramref name="folder"/> (default: the Anim Bank root) with one
    /// stack "All" keyed at 0 and a track for every controller, last controller first; it becomes
    /// the current animation.
    /// </summary>
    public Animation CreateAnimation(SceneObject? folder = null, string name = "New animation")
    {
        folder ??= Character.AnimBank ?? throw new InvalidOperationException($"{Character.Name} has no Anim Bank.");
        var schema = Character.Model.Document.Schema;
        var source = schema.NewObject("CAnimation");
        source.Set("Name", UniqueNames.For(folder.Source, name));
        source.Set("CurrentTime", 0);
        folder.Source.AddChild(source);
        var animation = (Animation)Character.Model.Wrap(source);

        var stack = NewStack(animation, "All", 0, 0);
        Evaluator.Invalidate();
        // The current pose (all identity offsets: the rest pose) for the IK keys' RefArray.
        var local = Evaluator.LocalTransforms(animation, 0, out var world);
        var controllers = Character.Controllers;
        // A camera's hidden joint and target key in the tracks its own camera track brings with it.
        var rigParts = new HashSet<Object3D>(ReferenceEqualityComparer.Instance);
        foreach (var controller in controllers)
            if (controller is CameraAnimated animated && CameraEditor.Rig(animated) is { } rig)
            {
                rigParts.Add(rig.HideJoint);
                rigParts.Add(rig.Target);
            }
        for (int i = controllers.Count - 1; i >= 0; i--)
        {
            if (rigParts.Contains(controllers[i])) continue;
            TrackFactory.Create(stack, controllers[i], ChainOf(controllers[i], local, world));
        }
        Evaluator.Invalidate();
        CameraRigKeys.FillLookerChains(stack, Evaluator);
        foreach (var track in stack.Tracks) TangentRecompute.Recompute(track, Evaluator);
        Character.CurrentAnimation = animation;
        Evaluator.Invalidate();
        return animation;
    }

    /// <summary>The FK <c>RefArray</c> of a new IK or reverse-foot key at the evaluated pose, else null.</summary>
    private IReadOnlyList<Transform>? ChainOf(Object3D controller, Transform[] local, Matrix4x4[] world)
    {
        if (controller is not ControllerIK ik) return null;
        var joints = ik.Chain;
        if (joints.Any(j => Evaluator.IndexOf(j) < 0)) return [];
        var chain = joints.Select(j => local[Evaluator.IndexOf(j)]).ToList();
        if (controller is ControllerReverseFoot && joints.Count == 4 && Evaluator.IndexOf(controller) is var ci and >= 0)
        {
            // Entry 4: the controller relative to the ball.
            Matrix4x4.Invert(world[Evaluator.IndexOf(joints[3])], out var inverseBall);
            chain.Add(Transform.FromMatrixLoose(world[ci] * inverseBall));
        }
        return chain;
    }

    /// <summary>Deep-copies <paramref name="animation"/> into its folder (name uniquified); the copy becomes current if the original was.</summary>
    public Animation CopyAnimation(Animation animation)
    {
        var parent = animation.Source.Parent ?? throw new InvalidOperationException($"{animation.Name} has no parent.");
        var copy = animation.Source.CloneHierarchy();
        copy.Set("Name", UniqueNames.For(parent, animation.Name ?? "New animation"));
        // A copy is a new animation for the Added sort: unnumbered, so it lists as the newest.
        if (copy.Has(MaterialSchema.AddedOrderMember)) copy.Set(MaterialSchema.AddedOrderMember, 0);
        parent.AddChild(copy);
        var result = (Animation)Character.Model.Wrap(copy);
        if (Character.CurrentAnimation == animation) Character.CurrentAnimation = result;
        Evaluator.Invalidate();
        return result;
    }

    /// <summary>Removes <paramref name="animation"/> and its sub-tree; the current animation becomes none if it was this one.</summary>
    public void DeleteAnimation(Animation animation)
    {
        animation.Source.Parent?.RemoveChild(animation.Source);
        if (Character.CurrentAnimation == animation) Character.CurrentAnimation = null;
        Evaluator.Invalidate();
    }

    // ---- folders ------------------------

    /// <summary>Creates a folder (name uniquified) as the last child of the Anim Bank root.</summary>
    public Folder CreateFolder(string name = "New folder")
    {
        var root = Character.AnimBank ?? throw new InvalidOperationException($"{Character.Name} has no Anim Bank.");
        var source = Character.Model.Document.Schema.NewObject("CDummy");
        source.Set("Name", UniqueNames.For(root.Source, name));
        root.Source.AddChild(source);
        return (Folder)Character.Model.Wrap(source);
    }

    /// <summary>
    /// Deep-copies <paramref name="folder"/> - every folder and animation inside it - beside the
    /// original (name uniquified). The copied animations count as new for the Added sort.
    /// </summary>
    public Folder CopyFolder(Folder folder)
    {
        if (!IsUserFolder(folder)) throw new InvalidOperationException($"{folder.Name} is not a folder of the Anim Bank.");
        var parent = folder.Source.Parent ?? throw new InvalidOperationException($"{folder.Name} has no parent.");
        var copy = folder.Source.CloneHierarchy();
        copy.Set("Name", UniqueNames.For(parent, folder.Name ?? "New folder"));
        var pending = new Stack<AktObject>([copy]);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node.Has(MaterialSchema.AddedOrderMember)) node.Set(MaterialSchema.AddedOrderMember, 0);
            foreach (var child in node.Children) pending.Push(child);
        }
        parent.AddChild(copy);
        Evaluator.Invalidate();
        return (Folder)Character.Model.Wrap(copy);
    }

    /// <summary>Removes <paramref name="folder"/> with every folder and animation inside it; the current animation becomes none if it was inside.</summary>
    public void DeleteFolder(Folder folder)
    {
        if (!IsUserFolder(folder)) throw new InvalidOperationException($"{folder.Name} is not a folder of the Anim Bank.");
        if (Character.CurrentAnimation is { } current && IsInside(current, folder)) Character.CurrentAnimation = null;
        folder.Source.Parent?.RemoveChild(folder.Source);
        Evaluator.Invalidate();
    }

    /// <summary>A folder of the Anim Bank that the user made (not the <c>__Anims</c> root itself).</summary>
    public bool IsUserFolder(SceneObject o) =>
        o is Folder && Character.AnimBank is { } root && o.Source != root.Source && IsInside(o, root);

    /// <summary>
    /// Whether <paramref name="item"/> (an animation or a user folder of this bank) can be dropped
    /// into <paramref name="target"/> (a user folder or the root). A folder never goes into itself
    /// or one of its sub-folders (a SloppyAnimatsu rule).
    /// </summary>
    public bool CanMove(SceneObject item, SceneObject target, out string? reason)
    {
        reason = null;
        var root = Character.AnimBank;
        if (root == null) reason = $"{Character.Name} has no Anim Bank.";
        else if (!(item is Animation && IsInside(item, root)) && !IsUserFolder(item))
            reason = $"{item.Name} is not an animation or folder of the Anim Bank.";
        else if (target.Source != root.Source && !IsUserFolder(target))
            reason = $"{target.Name} is not a folder of the Anim Bank.";
        else if (item is Folder && (target.Source == item.Source || IsInside(target, item)))
            reason = $"{item.Name} cannot go inside itself.";
        return reason == null;
    }

    /// <summary>
    /// Moves <paramref name="item"/> to the end of <paramref name="target"/>'s children, keeping its
    /// name. Returns false when it is already in <paramref name="target"/>; throws when it cannot move.
    /// </summary>
    public bool Move(SceneObject item, SceneObject target)
    {
        if (!CanMove(item, target, out string? reason)) throw new InvalidOperationException(reason);
        if (item.Source.Parent == target.Source) return false;
        if (Character.AnimBank is { } bank) AnimBankView.NumberAdded(bank);
        item.Source.Parent!.RemoveChild(item.Source);
        target.Source.AddChild(item.Source);
        return true;
    }

    /// <summary>
    /// Moves several items into <paramref name="target"/> in bank order, skipping items inside
    /// another moved folder. Checks every item before moving any. Returns the items that moved.
    /// </summary>
    public IReadOnlyList<SceneObject> Move(IEnumerable<SceneObject> items, SceneObject target)
    {
        var list = items.Distinct().ToList();
        list.RemoveAll(i => list.Any(other => other is Folder && other != i && IsInside(i, other)));
        foreach (var item in list)
            if (!CanMove(item, target, out string? reason))
                throw new InvalidOperationException(reason);
        var order = Character.AnimBank!.Source.PreOrder().Select((o, i) => (o, i)).ToDictionary(p => p.o, p => p.i);
        return list.OrderBy(i => order[i.Source]).Where(i => Move(i, target)).ToList();
    }

    /// <summary>
    /// The Anim Bank's free sort: puts <paramref name="items"/> (in bank order, skipping items inside
    /// another placed folder) into <paramref name="target"/> just before <paramref name="before"/>, a
    /// child of <paramref name="target"/> that is not itself placed, or at the end when it is null. The
    /// bank's file order is the free order, so it is saved with the scene. Checks every item first;
    /// returns whether the order changed.
    /// </summary>
    public bool Place(IEnumerable<SceneObject> items, SceneObject target, SceneObject? before)
    {
        var list = items.Distinct().ToList();
        list.RemoveAll(i => list.Any(other => other is Folder && other != i && IsInside(i, other)));
        foreach (var item in list)
            if (!CanMove(item, target, out string? reason))
                throw new InvalidOperationException(reason);
        if (before != null && (before.Source.Parent != target.Source || list.Any(i => i.Source == before.Source)))
            throw new InvalidOperationException($"{before.Name} is not a place in {target.Name}.");
        var order = Character.AnimBank!.Source.PreOrder().Select((o, i) => (o, i)).ToDictionary(p => p.o, p => p.i);
        list = list.OrderBy(i => order[i.Source]).ToList();
        // The Added sort reads the bank order of unnumbered animations: number them before it changes.
        AnimBankView.NumberAdded(Character.AnimBank!);
        var parents = list.Select(i => i.Source.Parent!).Append(target.Source).Distinct().ToList();
        var old = parents.Select(p => p.Children.ToList()).ToList();
        foreach (var item in list) item.Source.Parent!.RemoveChild(item.Source);
        int index = before == null ? target.Source.Children.Count : target.Source.Children.IndexOf(before.Source);
        foreach (var item in list) target.Source.InsertChild(index++, item.Source);
        return parents.Select((p, k) => !p.Children.SequenceEqual(old[k])).Any(changed => changed);
    }

    /// <summary>Whether <paramref name="o"/> is somewhere below <paramref name="ancestor"/>.</summary>
    private static bool IsInside(SceneObject o, SceneObject ancestor)
    {
        for (var p = o.Source.Parent; p != null; p = p.Parent)
            if (p == ancestor.Source) return true;
        return false;
    }

    // ---- stacks --------------------------------------------------------------------------------

    /// <summary>
    /// Creates "New Stack" just above the current stack (or at the bottom), keyed at 0, with a track
    /// for each of <paramref name="targets"/> (<see cref="KeyEditor.AddToStack"/>); it becomes the
    /// current stack. Throws, before changing anything, when a target cannot be added.
    /// </summary>
    public AnimStack CreateStack(Animation animation, IEnumerable<Object3D> targets)
    {
        var list = targets.Distinct().ToList();
        foreach (var t in list)
            if (!KeyEditor.CanAddToStack(t, out string? reason))
                throw new InvalidOperationException(reason);
        var refs = animation.StackRefs;
        int index = animation.CurrentStack is { } current && refs.FindIndex(r => r.Target == current.Source) is var i and >= 0
            ? i : refs.Count;
        var stack = NewStack(animation, "New Stack", index, refs.Count);
        Keys.AddToStack(stack, list);
        Evaluator.Invalidate();
        return stack;
    }

    /// <summary>A new stack with the defaults, keyed <c>[0 0]</c>, inserted at <paramref name="index"/> of the stack list and made current.</summary>
    private AnimStack NewStack(Animation animation, string name, int index, int colourIndex)
    {
        var schema = Character.Model.Document.Schema;
        var source = schema.NewObject("CAnimStack");
        source.Set("Name", UniqueNames.For(animation.Source, name));
        source.Set("Flags", EnabledFlag);
        source.Set("FlagsNoModify", 0u);
        source.Set("Color", StackColor(colourIndex));
        source.Set("Opacity", 1f);
        source.Set("Tweenage", 0.5f);
        var times = (List<object?>)source["TimeArray"]!;
        for (int k = 0; k < 2; k++) times.Add(schema.NewStruct("CAnimStackKey"));
        animation.Source.AddChild(source);
        animation.StackRefs.Insert(index, new AktReference(source));
        var stack = (AnimStack)Character.Model.Wrap(source);
        animation.CurrentStack = stack;
        return stack;
    }

    /// <summary>
    /// Deep-copies <paramref name="stack"/> just above it (name uniquified). The copy becomes current
    /// if the original was; otherwise the current stack is kept.
    /// </summary>
    public AnimStack DuplicateStack(AnimStack stack)
    {
        var animation = stack.Animation ?? throw new InvalidOperationException($"{stack.Name} is not in an animation.");
        var copy = stack.Source.CloneHierarchy();
        copy.Set("Name", UniqueNames.For(animation.Source, stack.Name ?? "New Stack"));
        animation.Source.AddChild(copy);
        var refs = animation.StackRefs;
        int index = refs.FindIndex(r => r.Target == stack.Source);
        refs.Insert(index < 0 ? refs.Count : index, new AktReference(copy));
        var result = (AnimStack)Character.Model.Wrap(copy);
        if (animation.CurrentStack == stack) animation.CurrentStack = result;
        Evaluator.Invalidate();
        return result;
    }

    /// <summary>
    /// Removes <paramref name="stack"/> and its tracks. The last stack of an animation is kept
    /// (returns false). A deleted current stack is replaced by the one below it, else above.
    /// </summary>
    public bool DeleteStack(AnimStack stack)
    {
        var animation = stack.Animation ?? throw new InvalidOperationException($"{stack.Name} is not in an animation.");
        var refs = animation.StackRefs;
        int index = refs.FindIndex(r => r.Target == stack.Source);
        if (refs.Count <= 1) return false;
        bool wasCurrent = animation.CurrentStack == stack;
        if (index >= 0) refs.RemoveAt(index);
        animation.DeselectStack(stack);
        animation.Source.RemoveChild(stack.Source);
        if (wasCurrent)
        {
            int next = Math.Clamp(index, 0, refs.Count - 1);
            animation.CurrentStack = Character.Model.Wrap(refs[next]) as AnimStack;
        }
        Evaluator.Invalidate();
        return true;
    }

    /// <summary>Moves <paramref name="stack"/> by <paramref name="delta"/> places (negative = up, towards index 0). Returns whether it moved.</summary>
    public bool MoveStack(AnimStack stack, int delta)
    {
        var animation = stack.Animation ?? throw new InvalidOperationException($"{stack.Name} is not in an animation.");
        var refs = animation.StackRefs;
        int index = refs.FindIndex(r => r.Target == stack.Source);
        if (index < 0) return false;
        int to = Math.Clamp(index + delta, 0, refs.Count - 1);
        if (to == index) return false;
        var r = refs[index];
        refs.RemoveAt(index);
        refs.Insert(to, r);
        Evaluator.Invalidate();
        return true;
    }

    /// <summary>
    /// Reorders the Mixer: stack i of the new order is the one at <paramref name="order"/>[i] before.
    /// Returns false, changing nothing, unless <paramref name="order"/> is a permutation of the stacks
    /// that changes their order.
    /// </summary>
    public bool ReorderStacks(Animation animation, IReadOnlyList<int> order)
    {
        var refs = animation.StackRefs;
        if (order.Count != refs.Count || order.Distinct().Count() != order.Count || order.Any(i => i < 0 || i >= refs.Count))
            return false;
        if (order.Select((v, i) => v == i).All(same => same)) return false;
        var old = refs.ToList();
        for (int i = 0; i < order.Count; i++) refs[i] = old[order[i]];
        Evaluator.Invalidate();
        return true;
    }

    /// <summary>
    /// The Mixer's drag and drop: moves <paramref name="stack"/> to place <paramref name="index"/>
    /// (0 = top) of the final order, the others keeping theirs. Returns whether anything moved.
    /// </summary>
    public bool MoveStackTo(AnimStack stack, int index)
    {
        var animation = stack.Animation ?? throw new InvalidOperationException($"{stack.Name} is not in an animation.");
        var refs = animation.StackRefs;
        int from = refs.FindIndex(r => r.Target == stack.Source);
        if (from < 0) return false;
        var order = Enumerable.Range(0, refs.Count).Where(i => i != from).ToList();
        order.Insert(Math.Clamp(index, 0, order.Count), from);
        return ReorderStacks(animation, order);
    }

    /// <summary>
    /// The Mixer's **Merge**: bakes
    /// <paramref name="selection"/> into one new stack and deletes the sources. One selected stack
    /// merges with the next lower one, as the app does; stacks at opacity 0 are ignored. Every key
    /// holds the mixed result at that time (the union of the sources' key times), so the new stack
    /// reproduces the sources on its own. It becomes current and selected.
    /// </summary>
    public AnimStack MergeStacks(Animation animation, IReadOnlyList<AnimStack> selection)
    {
        var refs = animation.StackRefs;
        var candidates = selection.Where(s => s.Animation == animation && s is not AudioStack).Distinct().ToList();
        if (candidates.Count == 0) throw new InvalidOperationException("Select a stack to merge.");
        int IndexOf(AnimStack s) => refs.FindIndex(r => r.Target == s.Source);
        var ordered = candidates.Where(s => IndexOf(s) >= 0).OrderBy(IndexOf).ToList();
        if (ordered.Count == 1)
        {
            for (int i = IndexOf(ordered[0]) + 1; i < refs.Count; i++)
                if (Character.Model.Wrap(refs[i]) is AnimStack below && below is not AudioStack) { ordered.Add(below); break; }
        }
        if (ordered.Count < 2)
            throw new InvalidOperationException("Merge needs two stacks: select a stack and another one below it.");
        var sources = ordered.Where(s => s.Opacity > 1e-5f).ToList();
        if (sources.Count < 2)
            throw new InvalidOperationException("A stack at opacity 0 is not merged, so fewer than two stacks are left.");

        var times = sources.SelectMany(s => s.Keys.Skip(1).Select(k => k.Time)).Append(0)
            .Distinct().OrderBy(t => t).ToList();
        var targets = sources.SelectMany(s => s.Tracks).Select(t => t.Target).OfType<Object3D>()
            .Distinct().ToList();
        foreach (var target in targets)
            if (!KeyEditor.CanAddToStack(target, out string? reason)) throw new InvalidOperationException(reason);

        // The mixed animation is sampled before the new stack exists, so it cannot influence it.
        Evaluator.Invalidate();
        var locals = new Transform[times.Count][];
        var worlds = new Matrix4x4[times.Count][];
        for (int i = 0; i < times.Count; i++)
            locals[i] = Evaluator.LocalTransforms(animation, times[i], out worlds[i]);
        var fovs = targets.OfType<CameraAnimated>()
            .ToDictionary(c => c, c => times.Select(t => Evaluator.CameraFov(animation, c, t)).ToArray());
        var blends = targets.OfType<BlendshapeController>()
            .ToDictionary(c => c, c => times.Select(t => Evaluator.BlendshapeValues(animation, c, t)).ToArray());

        var named = ordered.Contains(animation.CurrentStack!) ? animation.CurrentStack! : ordered[^1];
        int index = animation.CurrentStack is { } current && IndexOf(current) is var currentIndex and >= 0
            ? currentIndex : refs.Count;
        var stack = NewStack(animation, named.Name ?? "Merged", index, refs.Count);
        // One key at every collected time: frame 0, each source key time, and the last source time.
        stack.SetKeyTime(1, times[^1]);
        for (int i = 1; i <= times.Count - 2; i++) stack.InsertDefaultKeyTime(i, times[i]);
        foreach (var target in targets)
            if (stack.TrackFor(target) == null) TrackFactory.Create(stack, target);

        Evaluator.Invalidate();
        foreach (var track in stack.Tracks)
        {
            if (track.Target is not { } target) continue;
            int ci = Evaluator.IndexOf(target);
            if (ci < 0) throw new InvalidOperationException($"{target.Name} cannot be evaluated, so it cannot be merged.");
            for (int i = 0; i < times.Count && i < track.Keys.Count; i++)
            {
                if (track.Keys[i] is TransformKey key)
                {
                    key.Value = PoseEvaluator.Decompose(Evaluator.RestTransforms[ci], locals[i][ci]);
                    if (key.Source.Has("Space")) key.Space = null;
                    if (key is CameraKey cameraKey)
                    {
                        if (target is CameraAnimated camera && fovs.TryGetValue(camera, out var fov)) cameraKey.Fov = fov[i];
                    }
                    else if (key is IKKey ik)
                    {
                        // A baked key is an FK key: the chain comes from RefArray, not a solve.
                        ik.SetIkKey(false);
                        ik.PoleAngle = 0f;
                        if (ik is ReverseFootKey foot) { foot.RollAngle = 0f; foot.TwistAngle = 0f; foot.BankAngle = 0f; }
                        if (ChainAt(target, locals[i], worlds[i]) is { Count: > 0 } chain) ik.InitChainTransforms(chain);
                    }
                }
                else if (track is BlendshapeTrack blend && target is BlendshapeController controller
                         && blends.TryGetValue(controller, out var values))
                    BlendshapeEditor.SetKeyValues(blend, i, values[i][0], values[i][1], mirrorLoop: false);
            }
        }
        Evaluator.Invalidate();
        foreach (var source in sources) RemoveStack(animation, source);
        Evaluator.Invalidate();
        foreach (var track in stack.Tracks) TangentRecompute.Recompute(track, Evaluator);
        animation.SelectStacks([stack]);
        Evaluator.Invalidate();
        return stack;
    }

    /// <summary>The chain a baked IK or looker key stores: the joints' mixed locals, plus the reverse foot's controller-relative-to-ball entry.</summary>
    private IReadOnlyList<Transform>? ChainAt(Object3D target, Transform[] local, Matrix4x4[] world)
    {
        if (target is CameraTarget looker)
            return looker.Chain.Count == 0 || looker.Chain.Any(j => Evaluator.IndexOf(j) < 0)
                ? null : looker.Chain.Select(j => local[Evaluator.IndexOf(j)]).ToArray();
        return ChainOf(target, local, world);
    }

    /// <summary>
    /// The Mixer's **Extract**: moves the
    /// channels <paramref name="channels"/> of the selected tracks of <paramref name="source"/> into a
    /// new stack named after the first of them, and clears those channels from the source (a source
    /// track that loses every channel is removed). The keys are copied, not rebaked. The new stack
    /// becomes current and selected.
    /// </summary>
    public AnimStack ExtractChannels(AnimStack source, AnimChannels channels, IEnumerable<AnimTrack> selectedTracks)
    {
        var animation = source.Animation ?? throw new InvalidOperationException($"{source.Name} is not in an animation.");
        uint mask = (uint)channels;
        if (mask == 0) throw new InvalidOperationException("Select keys first: Extract moves their channels.");
        var tracks = selectedTracks.Distinct().Where(t => t.Stack == source && ((uint)t.Channels & mask) != 0).ToList();
        if (tracks.Count == 0) throw new InvalidOperationException("No selected track of the stack carries a selected channel.");
        var first = tracks[0].Target ?? throw new InvalidOperationException($"{tracks[0].Name} has no target.");
        var refs = animation.StackRefs;
        int at = refs.FindIndex(r => r.Target == source.Source);
        var stack = NewStack(animation, first.Name ?? "Extracted", at < 0 ? refs.Count : at, refs.Count);
        // The new stack is the source's twin: flags, colour, opacity and the same key times.
        stack.Source.Set("Flags", source.Source["Flags"] ?? EnabledFlag);
        stack.Source.Set("FlagsNoModify", source.Source["FlagsNoModify"] ?? 0u);
        stack.Source.Set("Opacity", source.Opacity);
        stack.Source.Set("Tweenage", source.Tweenage);
        if (source.Source["Color"] is byte[] color) stack.Source.Set("Color", (byte[])color.Clone());
        if (source.Source["TimeArray"] is List<object?> times)
            stack.Source.Set("TimeArray", times.Select(t => t is AktStruct s ? s.Clone() : null).Cast<object?>().ToList());
        foreach (var track in tracks)
        {
            var copy = (AnimTrack)Character.Model.Wrap(track.Source.CloneHierarchy());
            copy.Source.Set("ChannelMask", (uint)(track.Channels & channels));
            stack.Source.AddChild(copy.Source);
            uint rest = (uint)track.Channels & ~mask;
            if (rest == 0) source.Source.RemoveChild(track.Source);
            else track.Source.Set("ChannelMask", rest);
        }
        animation.SelectStacks([stack]);
        Evaluator.Invalidate();
        return stack;
    }

    /// <summary>
    /// The Mixer's **Audio** button: adds
    /// <paramref name="sound"/> as a new audio stack of <paramref name="animation"/> and makes it
    /// current. The stack is named after the file, keyed at 0 and at the sound's length in frames,
    /// and its single key holds a fresh <c>CAudioEntry</c>; the loop key has no file. The character
    /// gets the entry (and an audio object if it has none).
    /// </summary>
    public AudioStack AddAudioStack(Animation animation, string fileName, byte[] sound, double durationSeconds)
    {
        var schema = Character.Model.Document.Schema;
        // A scene made by File > New declares no animation classes until its first track.
        AnimationSchema.Ensure(schema);
        AudioSchema.Ensure(schema);
        if (schema.FindClass(AudioSchema.StackClass) == null || schema.FindStruct(AudioSchema.KeyStruct) == null)
            throw new InvalidOperationException("The scene cannot hold audio stacks (its schema has no CAnimStackAudio).");

        // The app decodes the imported file through Qt Multimedia and embeds headerless stereo PCM
        // at 44.1 kHz, so the scene keeps playing without its source file
        // A caller that passes the
        // converted form keeps it as it is.
        if (WaveFile.TryDecode(sound) is { } decoded)
        {
            durationSeconds = decoded.Duration;
            sound = decoded.ToEmbeddedPcm();
        }

        string leaf = Path.GetFileName(fileName.Replace('\\', '/'));
        var entrySource = schema.NewObject(AudioSchema.EntryClass);
        entrySource.Set("Name", UniqueNames.For(Character.Source, "Audio Entry"));
        entrySource.Set("FileName", leaf);
        entrySource.Set("EmbedBufferData", sound);
        Character.Source.AddChild(entrySource);
        var entry = (AudioEntry)Character.Model.Wrap(entrySource);

        var audioObject = Character.Children.OfType<AudioObject>().FirstOrDefault();
        if (audioObject == null)
        {
            var objectSource = schema.NewObject(AudioSchema.ObjectClass);
            objectSource.Set("Name", UniqueNames.For(Character.Source, "Audio Object"));
            objectSource.Set("Flags", 1025u);
            objectSource.Set("LTM", AktMath.ToArray(Matrix4x4.Identity));
            Character.Source.AddChild(objectSource);
            audioObject = (AudioObject)Character.Model.Wrap(objectSource);
        }
        // CCharacter.AudioObject and CCharacter.Controllers both point at the audio object, which is
        // what makes the app render it
        // a scene that leaves the reference null fails to load in plain .akt readers. A
        // controller needs its slot in every pose too: the pose matrices are indexed by Controllers
        // and the app reads the audio object's pose back.
        Character.Source.Set("AudioObject", new AktReference(audioObject.Source));
        if (Character.Source["Controllers"] is List<AktReference> controllers
            && !controllers.Any(r => r.Target == audioObject.Source))
        {
            controllers.Add(new AktReference(audioObject.Source));
            int slot = controllers.Count - 1;
            IEnumerable<CharacterPose> poses = new[] { Character.RefPose, Character.CurrentPose, Character.RefPoseOld }
                .OfType<CharacterPose>().Concat(Character.BindPoses).Distinct();
            foreach (var pose in poses)
            {
                var matrices = SceneValues.Items(pose.Source, "Matrices");
                if (slot < matrices.Count) matrices.Insert(slot, AktMath.ToArray(Matrix4x4.Identity));
                else matrices.Add(AktMath.ToArray(Matrix4x4.Identity));
            }
        }

        uint rate = Character.Model.Scene.FrameRate;
        int end = (int)Math.Ceiling(durationSeconds * (rate == 0 ? 30 : rate));
        var refs = animation.StackRefs;
        int index = animation.CurrentStack is { } current && refs.FindIndex(r => r.Target == current.Source) is var i and >= 0
            ? i : refs.Count;
        var stackSource = schema.NewObject(AudioSchema.StackClass);
        stackSource.Set("Name", UniqueNames.For(animation.Source, leaf));
        stackSource.Set("Flags", EnabledFlag);
        stackSource.Set("FlagsNoModify", 0u);
        stackSource.Set("Color", StackColor(refs.Count));
        stackSource.Set("Opacity", 1f);
        stackSource.Set("Tweenage", 0.5f);
        var stackTimes = (List<object?>)stackSource["TimeArray"]!;
        for (int k = 0; k < 2; k++)
        {
            var time = schema.NewStruct("CAnimStackKey");
            time.Set("Time", k == 0 ? 0 : end);
            stackTimes.Add(time);
        }
        animation.Source.AddChild(stackSource);
        refs.Insert(index, new AktReference(stackSource));

        var trackSource = schema.NewObject(AudioSchema.TrackClass);
        trackSource.Set("Name", UniqueNames.For(stackSource, "Track"));
        trackSource.Set("ChannelMask", 0u);
        trackSource.Set("Target", new AktReference(audioObject.Source));
        var keys = (List<object?>)trackSource["KeyList"]!;
        keys.Add(AudioKey(entry));
        keys.Add(AudioKey(null));
        AktStruct AudioKey(AudioEntry? file)
        {
            var key = schema.NewPolymorphic(AudioSchema.KeyStruct);
            key.Set("AudioFile", new AktReference(file?.Source));
            key.Set("Volume", 1f);
            key.Set("Mute", false);
            return key;
        }
        foreach (string field in new[] { "TInList", "TOutList", "TangentInfoArray" })
        {
            var list = (List<object?>)trackSource[field]!;
            list.Add(null);
            list.Add(null);
        }
        stackSource.AddChild(trackSource);
        stackSource.Set("AudioTrack", new AktReference(trackSource));
        var stack = (AudioStack)Character.Model.Wrap(stackSource);
        animation.CurrentStack = stack;
        animation.SelectStacks([stack]);
        Evaluator.Invalidate();
        return stack;
    }

    /// <summary>Removes a stack and its tracks without the "keep the last stack" rule (used by Merge).</summary>
    private void RemoveStack(Animation animation, AnimStack stack)
    {
        var refs = animation.StackRefs;
        int index = refs.FindIndex(r => r.Target == stack.Source);
        if (index >= 0) refs.RemoveAt(index);
        animation.DeselectStack(stack);
        animation.Source.RemoveChild(stack.Source);
        if (animation.CurrentStack == stack) animation.CurrentStack = null;
    }

    public void SetOpacity(AnimStack stack, float opacity)
    {
        stack.Source.Set("Opacity", Math.Clamp(opacity, 0f, 1f));
        Evaluator.Invalidate();
    }

    /// <summary>The stack type: additive sets Flags bit 3, override clears it.</summary>
    public void SetAdditive(AnimStack stack, bool additive) =>
        SetFlags(stack, additive ? stack.Flags | AdditiveFlag : stack.Flags & ~AdditiveFlag);

    /// <summary>
    /// Flips <paramref name="flag"/>. Turning cycle on moves a loop key that is not after the
    /// penultimate key: +10 frames with two keys, else the last gap repeated.
    /// </summary>
    public void ToggleFlag(AnimStack stack, uint flag)
    {
        uint flags = stack.Flags ^ flag;
        if ((flag & CycleFlag) != 0 && (flags & CycleFlag) != 0)
        {
            var keys = stack.Keys;
            int n = keys.Count;
            if (n >= 2 && keys[n - 1].Time <= keys[n - 2].Time)
                stack.SetKeyTime(n - 1, n == 2 ? keys[0].Time + 10 : 2 * keys[n - 2].Time - keys[n - 3].Time);
        }
        SetFlags(stack, flags);
    }

    private void SetFlags(AnimStack stack, uint flags)
    {
        stack.Source.Set("Flags", flags);
        Evaluator.Invalidate();
    }

    public void SetCurrentStack(AnimStack stack)
    {
        if (stack.Animation is { } animation) animation.CurrentStack = stack;
    }

    /// <summary>
    /// The colour of the stack created when <paramref name="n"/> stacks exist: grey for the first,
    /// then an HSL colour (S 0.5, L 0.55) whose hue follows sin n.
    /// </summary>
    public static byte[] StackColor(int n)
    {
        if (n == 0) return [0x99, 0x99, 0x99, 0xFF];
        double hue = (Math.Sin(n) % 1.0 + 0.6180340051651001) % 1.0;
        if (hue < 0) hue += 1; // a negative hue is wrapped (hypothesis, not checked against a sample)
        const double s = 0.5, l = 0.55;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double h = hue * 6;
        double x = c * (1 - Math.Abs(h % 2 - 1));
        double m = l - c / 2;
        var (r, g, b) = (int)h switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        static byte B(double v) => (byte)Math.Clamp((int)(v * 255), 0, 255);
        return [B(r + m), B(g + m), B(b + m), 0xFF];
    }
}

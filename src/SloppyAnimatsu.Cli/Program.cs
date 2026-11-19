using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Numerics;
using SloppyAnimatsu.Core;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Aknm;
using SloppyAnimatsu.Core.Diagnostics;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Cli;

internal static partial class Program
{
    private const string Usage = """
        SloppyAnimatsu command-line tool

          --version                            print the build's version
          --help                               this list

          info      <file.slopa>...              version, sizes, object counts per class
          tree      <file.slopa> [maxDepth]       object hierarchy with indices
          dump      <file.slopa> <index|name>...  all member values of selected objects
          schema    <file.slopa>...               merged class/struct schema as Markdown
          roundtrip <file.slopa|folder>...        read, re-write and compare bytes
          convert   <in.slopa|in.akt> <out.slopa|out.akt> [--akeytsu|--brotli|--zlib|--stored]
                                                re-store a scene: the plain .akt layout or the
                                                SloppyAnimatsu container (default brotli)
          --akeytsu-format                      (any command) save scenes in the plain .akt layout,
                                                not the SloppyAnimatsu container, for other .akt tools
          scene     <file.slopa>                  typed summary: characters, meshes, animations
          new       <out.slopa> [--frame-rate=n]  write a new, empty scene
          prefs     <preferences.slopa|.akt> [--pages] [--page=Class] [--get=Class.Member]...
                    [--set=Class.Member=Value]... [--dock] [--add=path]... [--save] [--save-as=out.slopa]
                                                the recent-scene list, default frame rate, autosave
                                                interval, every preference page and the dock layout
          shortcuts <shortcuts.slopa|shortcuts.akt> [--list] [--action=name] [--set=Action=Key]...
                    [--unbind=Action]... [--reset=Action]... [--reset-all] [--defaults]
                    [--save] [--save-as=out.slopa]
                                                the keyboard shortcut map: list, assign, unbind and
                                                reset bindings
          layout    <preferences.slopa|.akt> [--context=Rigging|Animation] [--size=WxH] [--panel=Class]
                    [--set=Class:height]... [--save] [--save-as=out.slopa]
                                                the saved dock layout: the columns and DockDown
                                                chains of a context and their rectangles at a window
                                                size
          views     <file.slopa> [--layout=single|2views|curveboard|4views] [--toggle] [--focused=n]
                    [--view=n --camera=Name]... [--save] [--save-as=out.slopa]
                                                the viewport layouts and their per-view cameras
          autosave  <scene.slopa> [--dir=folder] [--base=name]
                                                write the next autosave slot of a scene
          outliner  <file.slopa> [--context=rigging|animation] [--show=name...] [--hide=name...]
                    [--toggle=name...] [--show-all] [--hide-all] [--lock=mesh...] [--unlock=mesh...]
                    [--lock-character] [--unlock-character] [--delete-tree=name...]
                    [--character=name] [--save-as=out.slopa]
                                                the hierarchy panel: rows and their kind, interface
                                                context, Show/Hide, Lock and Delete tree object
          timeline  <file.slopa> [--animation=name] [--character=name] [--width=w]
                    [--press=x,y] [--to=x,y] [--apply] [--select=i,j] [--ripple]
                    [--row=n]... [--keys] [--save-as=out.slopa]
                                                the timeline's rows, key positions and what a press
                                                at a point would hit; --apply replays the drag from
                                                --press to --to and saves
          curveboard <file.slopa> [--animation=name] [--character=name] [--stack=name] [--track=name]...
                    [--width=w] [--height=h] [--press=x,y] [--select=x,y]... [--to=x,y] [--handles]
                    [--apply] [--save-as=out.slopa]
                                                the Curveboard's curves, key and tangent-handle
                                                positions and what a press would hit; --apply replays
                                                the drag from --press to --to and saves
          poses     <file.slopa>                  compare stored and evaluated poses with LTMs
          fbx       <file.fbx> [maxDepth] [filter]  FBX node tree (filter: substring of a node's first property)
          animinfo  <file.slopa> <anim>...        stacks of an animation: flags, key times, tracks,
                                                tangent modes, space-switched keys
          fbxcompare <file.slopa> <baked.fbx> [anim...] [--joint=name] [--between] [--frames] [--jitter] [--linear] [--plain-chain] [--no-ik]
                                                evaluated poses vs an FBX export, per frame
                                                (--jitter: allow the bake's one-frame sampling slips)
          skincheck <file.slopa> <file.fbx>       bind-time joint and mesh world matrices vs the FBX skin clusters
          tangentcheck <file.slopa>... [--show=N] [--plain]  recomputed vs stored tangents, per channel and tangent mode
                                                (--show: print the first N mismatches)
          ikkeycheck <file.slopa>... [--show=N]  at every IK key: chain end vs controller, RefArray vs joint keys,
                                                split by the key's IK flag
          audio     <file.slopa>                  audio objects, entries, events and what plays at the scene time
          camera    <file.slopa> [frame]          scene cameras and their views, and every animated camera's
                                                FOV track and values at the frame
          blendshape <file.slopa> [frame] [--animation=name]
                                                blendshape targets, controllers, channels and the mixed
                                                values and influences at the frame
          fbxexport <file.slopa> <out.fbx> [--preset=plain|unity|godot|blender] [--baked|--unbaked]
                    [--plain|--no-plain] [--linear|--step] [--min-scale=x]
                    [--animation=name]... [--character=name]... [--textures=dir]
                                                write the scene as FBX
          fbximport <file.fbx> <out.slopa> [--no-animations] [--no-skins] [--no-materials] [--textures=dir]
                                                read an FBX scene into a new scene
          fbxcheck  <ours.fbx> <reference.fbx>  models, geometry, skins, bind poses and curves of two FBX files
          fbxrewrite <in.fbx> <out.fbx>         read a binary FBX and write it back through FbxWriter
          posedump <file.slopa> <out.json> [--animation=name]... [--character=name]... [--step=1]
                                                every joint's evaluated world position per sample time, the
                                                reference engine imports are compared against
          aknmexport <file.slopa> <out.aknm> [--character=name] [--animation=name]... [--all-animations]
                                                the character's rig and selected animations as .aknm
          aknmimport <scene.slopa> <file.aknm> <out.slopa> [--character=name] [--root] [--force]
                                                copy an .aknm's animations onto a character of the scene
          aktimport <scene.slopa> <file.slopa> <out.slopa> [--force]
                                                merge another .akt scene into the scene (characters with
                                                their animations, materials and textures)
          retarget  <scene.slopa> <source char> <target char> <out.slopa> [--animation=name|dir/name]...
                    [--root] [--match=source=target]... [--method=auto|reference|local|world]
                    [--cache=dir] [--no-cache]
                                                copy the source character's animations onto the target,
                                                converting the keys;
                                                --method picks the reading of a key's delta: local
                                                (default), world, reference (the decoded different-
                                                reference-pose reading) or auto (reference when the two
                                                reference poses differ, local when they coincide),
                                                --cache writes the name-pair cache somewhere other than
                                                Documents/SloppyAnimatsu/Retargeting, --no-cache skips it
          restpose  <scene.slopa> <out.slopa> <joint> [--character=name] [--translate=x,y,z]
                    [--rotate=x,y,z] [--scale=x,y,z] [--convert|--keep|--return]
                                                edit a joint's reference pose (composed with the
                                                current local; rotate is X,Y,Z degrees in the
                                                joint's frame) and optionally resolve the
                                                animations against it; with no answer the scene
                                                keeps the pending state
          joint     <scene.slopa> <out.slopa> <action> ...
                                                skeleton edits:
                                                create --parent=name --name=name [--at=x,y,z]
                                                insert --joint=name --name=name [--at=x,y,z]
                                                delete|unparent|orient --joint=name...
                                                parent --parent=name --joint=name...
                                                ltm [--joint=name...]
                                                ([--character=name] selects the character)
          skin      <scene.slopa> <out.slopa> <bind|unbind> [--character=name] [--mesh=name...]
                                                bind the character's and the scene's free meshes
                                                (bind pose, rigid weights, the .akt skin flags),
                                                or unbind every skinned mesh of the scene keeping
                                                its world
          skinpaint <scene.slopa> <out.slopa> <paint|set|add|prune|fix|flood|mirror|copy-paste> [options]
                                                weight painting:
                                                paint --mesh=name --joint=name --at=x,y,z|--vertex=i
                                                      [--radius=r] [--mode=add|aim|smooth] [--value=v]
                                                      [--strength=s] [--soft] [--rigid] [--animation=name] [--frame=n]
                                                set|add --mesh=name --joint=name --vertex=i (--value=v|--delta=d)
                                                prune --mesh=name [--limit=0.001]
                                                fix --mesh=name --vertex=i,i,... [--passes=2]
                                                flood --mesh=name --vertex=i,i,...
                                                mirror [--character=name] [--joint=name...] [--mesh=name...]
                                                copy-paste --from=mesh --from-vertex=i,i,...
                                                           --to=mesh --vertex=i,i,... [--rigid]
                                                (all actions take [--character=name] and [--rigid])
          autorig   <scene.slopa> <out.slopa> [--character=name] [--hotpoint=Name:x,y,z]...
                    [--hotpoints=file] [--mesh=name...] [--naming=default|mixamo|preferences]
                    [--spine=1..6] [--fingers=1..10] [--phalanges=1..4]
                    [--no-ik] [--no-reverse-foot] [--no-weights] [--no-bind]
                                                build a biped rig from hot points on an unrigged
                                                character
          material  <scene.slopa> [<out.slopa> --material=Name [--replace] [--add=file[|name]]...
                    [--active=name|index] [--transparent=on|off] [--nearest=on|off]]...
                                                list materials and their texture swap lists, or
                                                edit them
          blendshape-add <scene.slopa> <character> <mesh> --target=Name --controller=Name
                    [--direction=0..3] [--attenuation=f] [--position=f] [--face=Name]
                    [--face-values=x,y] [--zone=first-last] [--zone-faces=i,j,k]
                    [--sight=Name] [--sight-at=x,y,z] [--context=rigging|animation]
                    [--out=out.slopa]
                                                add a blendshape target, controller, channel and
                                                info entry (and a face/sight) to a scene
          blendshape-pov <scene.slopa> <mesh> --eye=x,y,z --target=x,y,z [--out=out.slopa]
                                                store a blendshape point of view (the viewport
                                                camera and its target distance) on one mesh
          blendshape-setup <export|import> <scene.slopa> <mesh> <file.bsu> [--out=out.slopa]
                                                write the mesh's controllers as a .akt-style
                                                blendshape setup file, or apply a setup onto the
                                                mesh and save the scene
        """;

    public static int Main(string[] args)
    {
        // The log records the command, everything printed and a full stack trace of a failure
        // (SloppyAnimatsu.Core.Diagnostics.AppLog: a new file per run, three kept, 4 MiB each). The
        // output encoding is set before the log takes a copy of standard output.
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
        }

        AppLog.Start("cli", args);
        try
        {
            int code = Run(args);
            AppLog.Info($"exit code {code}");
            return code;
        }
        catch (Exception e)
        {
            // A command failure must not end in an unhandled exception: on Windows that raises the
            // "Application Error" dialog (0xe0434352) and leaves the process waiting for a click,
            // which stalls scripts, the test loop and anything else that runs the CLI unattended.
            // Say what failed and exit 1 like every other refusal. A closed standard error must not
            // turn this handler into the unhandled exception it exists to prevent.
            AppLog.Crash(e, "the command failed");
            try
            {
                Console.Error.WriteLine($"error: {e.GetType().Name}: {e.Message}");
                if (e.StackTrace is { Length: > 0 } stack) Console.Error.WriteLine(stack);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            return 1;
        }
        finally
        {
            AppLog.Shutdown();
        }
    }

    private static int Run(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args is ["--version"] or ["-v"] or ["version"])
        {
            Console.WriteLine($"{BuildInfo.Name} ({RuntimeInformation.FrameworkDescription}, " +
                              $"{RuntimeInformation.ProcessArchitecture})");
            return 0;
        }
        if (args is ["--help"] or ["-h"] or ["help"])
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (Array.IndexOf(args, "--akeytsu-format") is var fi and >= 0)
        {
            SceneModel.SaveFormat = AktFileFormat.Akeytsu;
            args = [.. args[..fi], .. args[(fi + 1)..]];
        }
        if (args.Length < 2)
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }
        var rest = args[1..];
        try
        {
            return args[0] switch
            {
                "info" => Info(rest),
                "tree" => Tree(rest),
                "dump" => Dump(rest),
                "schema" => Schema(rest),
                "roundtrip" => RoundTrip(rest),
                "convert" => ConvertCommand(rest),
                "scene" => SceneSummary(rest),
                "new" => NewSceneCommand(rest),
                "prefs" => PreferencesCommand(rest),
                "layout" => LayoutCommand(rest),
                "views" => ViewsCommand(rest),
                "shortcuts" => ShortcutsCommand(rest),
                "autosave" => AutosaveCommand(rest),
                "outliner" => OutlinerCommand(rest),
                "timeline" => TimelineCommand(rest),
                "curveboard" => CurveboardCommand(rest),
                "poses" => Poses(rest),
                "fbx" => FbxTree(rest),
                "animinfo" => AnimInfo(rest),
                "fbxcompare" => FbxCompare(rest),
                "skincheck" => SkinCheck(rest),
                "tangentcheck" => TangentCheck(rest),
                "ikkeycheck" => IKKeyCheck(rest),
                "keyjump" => KeyJump(rest),
                "rfkeycheck" => RFKeyCheck(rest),
                "audio" => AudioSummary(rest),
                "camera" => CameraSummary(rest),
                "blendshape" => BlendshapeSummary(rest),
                "blendshape-add" => BlendshapeAddCommand(rest),
                "blendshape-pov" => BlendshapePovCommand(rest),
                "blendshape-setup" => BlendshapeSetUpCommand(rest),
                "fbxexport" => FbxExport(rest),
                "posedump" => PoseDump(rest),
                "fbxrewrite" => FbxRewrite(rest),
                "fbximport" => FbxImport(rest),
                "fbxcheck" => FbxCheck(rest),
                "aknmexport" => AknmExportCommand(rest),
                "aknmimport" => AknmImportCommand(rest),
                "aktimport" => AktImportCommand(rest),
                "retarget" => RetargetCommand(rest),
                "joint" => JointCommand(rest),
                "restpose" => RestPoseCommand(rest),
                "skin" => SkinCommand(rest),
                "skinpaint" => SkinPaintCommand(rest),
                "autorig" => AutoRigCommand(rest),
                "material" => MaterialCommand(rest),
                _ => Fail($"Unknown command '{args[0]}'.\n{Usage}"),
            };
        }
        catch (Exception e) when (e is AktFormatException or IOException or InvalidDataException)
        {
            return Fail(e.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    /// <summary>The value of <c>--name=value</c>, or null when the option is absent.</summary>
    private static string? Option(IEnumerable<string> args, string name)
    {
        string prefix = "--" + name + "=";
        return args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }

    /// <summary>Every value of a repeatable <c>--name=value</c> option, in order.</summary>
    private static List<string> Repeated(IEnumerable<string> args, string name)
    {
        string prefix = "--" + name + "=";
        return args.Where(a => a.StartsWith(prefix, StringComparison.Ordinal))
            .Select(a => a[prefix.Length..]).ToList();
    }

    private static IEnumerable<string> ExpandFiles(IEnumerable<string> paths) =>
        paths.SelectMany<string, string>(p => Directory.Exists(p)
            ? SceneFiles.Enumerate(p)
            : new[] { p });

    // ---- info -----------------------------------------------------------------------------

    private static int Info(string[] files)
    {
        foreach (var path in ExpandFiles(files))
        {
            var sw = Stopwatch.StartNew();
            var doc = AktDocument.Load(path);
            sw.Stop();
            var objects = doc.Objects();
            Console.WriteLine($"{path}");
            Console.WriteLine($"  format {doc.SourceFormat}");
            Console.WriteLine($"  version {doc.Version}{(doc.HasHeader ? "" : " (legacy, no header)")}, " +
                              $"{new FileInfo(path).Length:N0} bytes, {doc.SourceAtoms.Count} atoms, " +
                              $"{doc.Schema.AllClasses.Count()} classes, {doc.Schema.Structs.Count} structs, " +
                              $"{objects.Count} objects, read in {sw.ElapsedMilliseconds} ms");
            foreach (var g in objects.GroupBy(o => o.ClassKey).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
                Console.WriteLine($"  {g.Count(),7}  {g.Key}");
        }
        return 0;
    }

    // ---- tree -----------------------------------------------------------------------------

    private static int Tree(string[] args)
    {
        var doc = AktDocument.Load(args[0]);
        int maxDepth = args.Length > 1 ? int.Parse(args[1]) : int.MaxValue;
        int index = 0;
        void Walk(AktObject o, int depth)
        {
            int myIndex = index++;
            if (depth <= maxDepth)
            {
                string more = depth == maxDepth && o.Children.Count > 0 ? $"  (+{o.Children.Count} children)" : "";
                Console.WriteLine($"{new string(' ', depth * 2)}#{myIndex} {o.ClassKey} '{o.Name}'{more}");
            }
            foreach (var c in o.Children) Walk(c, depth + 1);
        }
        foreach (var root in doc.Roots) Walk(root, 0);
        return 0;
    }

    // ---- dump -----------------------------------------------------------------------------

    private static int Dump(string[] args)
    {
        var doc = AktDocument.Load(args[0]);
        var objects = doc.Objects();
        var index = new Dictionary<AktObject, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < objects.Count; i++) index[objects[i]] = i;
        int maxItems = int.TryParse(Environment.GetEnvironmentVariable("SLOPPYANIMATSU_DUMP_ITEMS"), out int m) ? m : 8;
        var fmt = new ValueFormatter(o => index.TryGetValue(o, out int i) ? i : -1, maxItems);

        foreach (var sel in args[1..])
        {
            var matches = int.TryParse(sel, out int idx)
                ? objects.Skip(idx).Take(1)
                : objects.Where(o => o.Name == sel);
            foreach (var o in matches)
            {
                Console.WriteLine($"#{index[o]} {o.TypeName}");
                foreach (var f in o.Fields)
                    Console.WriteLine($"  {AktNames.Key(f.Owner)}.{f.Name} : {f.TypeName} = {fmt.Format(f.Value, 2)}");
                Console.WriteLine($"  children: {string.Join(", ", o.Children.Select(c => $"#{index[c]} '{c.Name}'"))}");
            }
        }
        return 0;
    }

    // ---- schema ---------------------------------------------------------------------------

    private static int Schema(string[] files)
    {
        // Merge: class key -> (parent key, members in newest-seen order); struct key -> members.
        var classParent = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        var classMembers = new Dictionary<string, List<AktMember>>();
        var structMembers = new SortedDictionary<string, List<AktMember>>(StringComparer.Ordinal);

        static void Merge(Dictionary<string, List<AktMember>> into, string key, List<AktMember> members)
        {
            if (!into.TryGetValue(key, out var existing) || members.Count > existing.Count)
                into[key] = members;
        }

        int count = 0;
        foreach (var path in ExpandFiles(files))
        {
            AktDocument doc;
            try { doc = AktDocument.Load(path); }
            catch (AktFormatException e) { Console.Error.WriteLine($"skipped {path}: {e.Message}"); continue; }
            count++;
            foreach (var c in doc.Schema.AllClasses)
            {
                classParent[c.Key] = c.Parent?.Key;
                Merge(classMembers, c.Key, c.Members);
            }
            var tmp = new Dictionary<string, List<AktMember>>(structMembers);
            foreach (var s in doc.Schema.Structs)
                Merge(tmp, s.Key, s.Members);
            foreach (var kv in tmp) structMembers[kv.Key] = kv.Value;
        }

        Console.WriteLine($"# Schema catalog\n\nMerged from {count} file(s).\n\n## Classes");
        void WriteClass(string key, int depth)
        {
            string pad = new(' ', depth * 2);
            Console.WriteLine($"{pad}- `{key}`");
            foreach (var mbr in classMembers[key])
                Console.WriteLine($"{pad}    - {mbr.Name} : `{AktNames.Key(mbr.TypeName)}`");
            foreach (var child in classParent.Where(kv => kv.Value == key).Select(kv => kv.Key))
                WriteClass(child, depth + 1);
        }
        foreach (var root in classParent.Where(kv => kv.Value == null).Select(kv => kv.Key))
            WriteClass(root, 0);

        Console.WriteLine("\n## Structs");
        foreach (var (key, members) in structMembers)
        {
            Console.WriteLine($"- `{key}`");
            foreach (var mbr in members)
                Console.WriteLine($"    - {mbr.Name} : `{AktNames.Key(mbr.TypeName)}`");
        }
        return 0;
    }

    // ---- convert --------------------------------------------------------------------------

    private static int ConvertCommand(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("convert <in.slopa|in.akt> <out.slopa|out.akt> [--akeytsu|--brotli|--zlib|--stored]");
            return 1;
        }
        // The output's extension picks the format unless an option says otherwise.
        var format = SceneFiles.FormatFor(args[1], AktFileFormat.SloppyAnimatsu);
        foreach (var a in args[2..])
            format = a switch
            {
                "--akeytsu" => AktFileFormat.Akeytsu,
                "--brotli" => AktFileFormat.SloppyAnimatsu,
                "--zlib" => AktFileFormat.SloppyAnimatsu with { Codec = AktCodec.Zlib },
                "--stored" => AktFileFormat.SloppyAnimatsu with { Codec = AktCodec.Stored },
                _ => throw new ArgumentException($"Unknown convert option '{a}'."),
            };
        var inputBytes = File.ReadAllBytes(args[0]);
        var payload = AktContainer.Unwrap(inputBytes);
        // An .akt layout is parsed and rewritten so SloppyAnimatsu's own members are dropped
        // (AkeytsuCompatibility); a container is rewrapped byte for byte.
        var output = format.Kind == AktFileKind.Akeytsu
            ? AktReader.Read(payload.Payload).Write(format)
            : AktContainer.Wrap(payload.Payload, format);
        File.WriteAllBytes(args[1], output);
        Console.WriteLine($"{args[0]} ({payload.Format}, {inputBytes.Length:N0} bytes) -> {args[1]} ({format}, {output.Length:N0} bytes)");
        return 0;
    }

    // ---- roundtrip ------------------------------------------------------------------------

    private static int RoundTrip(string[] paths)
    {
        int ok = 0, bad = 0, failed = 0;
        foreach (var path in ExpandFiles(paths))
        {
            byte[] original;
            try { original = File.ReadAllBytes(path); }
            catch (IOException e) { Console.WriteLine($"ERROR {path}: {e.Message}"); failed++; continue; }
            try
            {
                var doc = AktReader.Read(original);
                original = AktContainer.Unwrap(original).Payload;
                byte[] written = AktWriter.Write(doc);
                int diff = FirstDifference(original, written);
                if (diff < 0)
                {
                    ok++;
                    Console.WriteLine($"OK    {path}");
                }
                else
                {
                    bad++;
                    Console.WriteLine($"DIFF  {path}: first difference at 0x{diff:X} (sizes {original.Length} vs {written.Length})");
                }
            }
            catch (Exception e) when (e is AktFormatException or InvalidOperationException)
            {
                failed++;
                Console.WriteLine($"ERROR {path}: {e.Message}");
            }
        }
        Console.WriteLine($"{ok} identical, {bad} different, {failed} errors");
        return bad + failed == 0 ? 0 : 2;
    }

    // ---- scene ----------------------------------------------------------------------------

    private static int SceneSummary(string[] args)
    {
        var model = SceneModel.Load(args[0]);
        var scene = model.Scene;
        Console.WriteLine($"Scene '{scene.Name}': {scene.FrameRate} fps, time {scene.Time?.ToString() ?? "-"}, " +
                          $"{scene.Cameras.Count} cameras, {scene.Materials.Count()} materials, {scene.Textures.Count()} textures");
        foreach (var mesh in scene.Meshes)
            Console.WriteLine($"  mesh '{mesh.Name}': {mesh.ControlPoints.Length} points, {mesh.Polygons.Count} polygons, " +
                              $"{mesh.Triangles().Count()} triangles, {mesh.Materials.Count} materials, " +
                              $"skinned={mesh.Skinned}, bind pose '{mesh.BindPose?.Name}' of '{mesh.BindPose?.Character?.Name}'");
        foreach (var ch in scene.Characters)
        {
            var anims = ch.Animations.ToList();
            Console.WriteLine($"  character '{ch.Name}': {ch.Joints.Count} joints, {ch.Controllers.Count} controllers " +
                              $"({ch.IKControllers.Count()} IK), {ch.Meshes.Count()} meshes, {anims.Count} animations, " +
                              $"current '{ch.CurrentAnimation?.Name}'");
            if (ch.Controllers.Count > 0)
            {
                var world = new PoseEvaluator(ch).WorldMatrices(null, 0);
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                foreach (var m in world) { min = Vector3.Min(min, m.Translation); max = Vector3.Max(max, m.Translation); }
                Console.WriteLine($"    controller bounds at rest: {min.X:0.##} {min.Y:0.##} {min.Z:0.##} .. {max.X:0.##} {max.Y:0.##} {max.Z:0.##}");
            }
            foreach (var ik in ch.IKControllers)
                Console.WriteLine($"    {ik.ClassName} '{ik.Name}': chain {string.Join(" > ", ik.Chain.Select(j => j.Name))}" +
                                  (ik is ControllerReverseFoot rf ? $", shoe of {rf.Shoe.Count}" : ""));
            foreach (var anim in ch.RigAnimation is { } rig ? anims.Prepend(rig) : anims)
            {
                var (start, end) = anim.TimeRange;
                string folder = anim.FolderPath.Count > 0 ? string.Join("/", anim.FolderPath) + "/" : "";
                Console.WriteLine($"    anim {folder}{anim.Name}: frames {start}..{end}, " +
                                  string.Join(", ", anim.Stacks.Select(s => $"stack '{s.Name}' {s.Keys.Count} keys x {s.Tracks.Count()} tracks (flags {s.Flags})")));
            }
        }
        return 0;
    }

    // ---- scene lifecycle -------------------------------

    /// <summary>File > New Scene: writes the empty scene the app builds.</summary>
    private static int NewSceneCommand(string[] args)
    {
        if (args.Length < 1) return Fail("new <out.slopa> [--frame-rate=n]");
        uint frameRate = SceneFactory.DefaultFrameRate;
        if (Option(args[1..], "frame-rate") is { } fpsText)
        {
            if (!uint.TryParse(fpsText, out frameRate) || frameRate == 0)
                return Fail($"--frame-rate: expected a positive whole number, got '{fpsText}'.");
        }

        var model = new SceneModel(SceneFactory.NewScene(frameRate));
        model.Save(args[0]);
        Console.WriteLine($"{args[0]}: {new FileInfo(args[0]).Length:N0} bytes, " +
                          $"{model.Scene.Cameras.Count} cameras, character '{model.Scene.CurrentCharacter?.Name}', " +
                          $"{model.Scene.FrameRate} fps");
        return 0;
    }

    /// <summary>
    /// The recent-scene list, default frame rate, autosave interval, pages and dock layout of
    /// preferences.slopa.
    /// </summary>
    private static int PreferencesCommand(string[] args)
    {
        if (args.Length < 1)
            return Fail("prefs <preferences.slopa|.akt> [--pages] [--page=Class] [--get=Class.Member]... " +
                        "[--set=Class.Member=Value]... [--dock] [--add=path]... [--clear-personal] [--save] [--save-as=out.slopa]");
        var options = args[1..].ToList();
        Preferences preferences;
        try { preferences = Preferences.Load(args[0]); }
        catch (InvalidDataException e) { return Fail(e.Message); }

        foreach (string spec in Repeated(options, "set"))
        {
            int eq = spec.IndexOf('=');
            int dot = spec.IndexOf('.');
            if (eq < 0 || dot < 0 || dot > eq)
                return Fail($"--set: expected Class.Member=Value, got '{spec}'.");
            string className = spec[..dot];
            string member = spec[(dot + 1)..eq];
            string value = spec[(eq + 1)..];
            if (!preferences.TrySet(className, member, value, out string? error))
                return Fail($"--set={spec}: {error}");
        }

        // Empties the recent scenes and remembered folders (how the embedded default template is made).
        if (options.Contains("--clear-personal")) preferences.ClearPersonal();

        var added = new List<string>();
        foreach (string path in Repeated(options, "add"))
        {
            if (!preferences.AddRecentFile(path))
                return Fail($"'{args[0]}' has no CMainPreferences.RecentFiles member.");
            added.Add(path);
        }

        bool listed = options.Contains("--pages") || options.Contains("--dock") ||
                      options.Any(a => a.StartsWith("--page=", StringComparison.Ordinal)) ||
                      options.Any(a => a.StartsWith("--get=", StringComparison.Ordinal));

        Console.WriteLine($"{args[0]}: default frame rate {preferences.DefaultFrameRate} fps, " +
                          $"autosave every {preferences.AutoSaveMinutes} min, " +
                          $"{preferences.Pages.Count} preference page(s), " +
                          $"{preferences.RecentFiles.Count} recent scene(s)");

        if (options.Contains("--pages"))
        {
            foreach (var page in preferences.Pages)
                Console.WriteLine($"  {page.ClassName,-32} '{page.Name}' {page.Members.Count - 1} member(s)");
        }

        foreach (string className in Repeated(options, "page"))
        {
            var page = preferences.Page(className);
            if (page == null)
                return Fail($"No {className} page in '{args[0]}'.");
            Console.WriteLine($"# {page.ClassName} '{page.Name}'");
            var fmt = new ValueFormatter(_ => -1, 8);
            foreach (var field in page.Members)
                Console.WriteLine($"  {field.Name} : {field.TypeName} = {fmt.Format(field.Value, 2)}");
        }

        foreach (string path in Repeated(options, "get"))
        {
            int dot = path.IndexOf('.');
            if (dot < 0)
                return Fail($"--get: expected Class.Member, got '{path}'.");
            string className = path[..dot];
            string member = path[(dot + 1)..];
            object? value = preferences.Get(className, member);
            if (value == null && preferences.Page(className)?.Has(member) != true)
                return Fail($"No {className}.{member} in '{args[0]}'.");
            var fmt = new ValueFormatter(_ => -1, 8);
            Console.WriteLine($"{className}.{member} = {fmt.Format(value, 0)}");
        }

        if (options.Contains("--dock"))
        {
            foreach (var placement in preferences.DialogPlacements)
                Console.WriteLine($"  {placement}");
        }

        if (!listed)
        {
            for (int i = 0; i < preferences.RecentFiles.Count; i++)
                Console.WriteLine($"  {i + 1,2}. {preferences.RecentFiles[i]}");
        }
        foreach (string path in added) Console.WriteLine($"added '{path}'");

        string? saveAs = Option(options, "save-as");
        if (saveAs != null) preferences.SaveAs(saveAs);
        else if (options.Contains("--save")) preferences.Save();
        if (saveAs != null || options.Contains("--save"))
            Console.WriteLine($"saved {(saveAs ?? preferences.Path)} ({new FileInfo(saveAs ?? preferences.Path).Length:N0} bytes)");
        return 0;
    }

    /// <summary>
    /// The dock layout preferences.slopa saves per interface context
    /// the
    /// columns and their <c>DockDown</c> chains, the rectangles they resolve to at a window size,
    /// and a panel's height.
    /// </summary>
    private static int LayoutCommand(string[] args)
    {
        if (args.Length < 1)
            return Fail("layout <preferences.slopa|.akt> [--context=Rigging|Animation] [--size=WxH] " +
                        "[--panel=Class] [--set=Class:height]... [--save] [--save-as=out.slopa]");
        var options = args[1..].ToList();
        Preferences preferences;
        try
        {
            preferences = Preferences.Load(args[0]);
        }
        catch (Exception e) when (e is InvalidDataException or AktFormatException or IOException)
        {
            return Fail(e.Message);
        }

        string context = Option(options, "context") ?? "Animation";
        int width = 1600, height = 900;
        if (Option(options, "size") is { } size)
        {
            string[] parts = size.Split('x', 'X');
            if (parts.Length != 2 || !int.TryParse(parts[0], out width) || !int.TryParse(parts[1], out height) ||
                width <= 0 || height <= 0)
                return Fail($"--size: expected WxH, got '{size}'.");
        }

        var layout = DockLayout.For(preferences, context);
        Console.WriteLine($"{args[0]}: {context} dock layout, {layout.Columns.Count} column(s)" +
                          (layout.Bottom != null ? $", bottom {layout.Bottom.DialogClass}" : ""));
        foreach (var column in layout.Columns)
            Console.WriteLine($"  {column}");

        if (Option(options, "panel") is { } panelName)
        {
            if (layout.Panel(panelName) is not { } slot)
                return Fail($"No {panelName} panel in the {context} context of '{args[0]}'.");
            Console.WriteLine($"  {slot.DialogClass}: {slot.Anchor.ToString().ToLowerInvariant()} column " +
                              $"{slot.Distance}, top {slot.Top:0.####}, " +
                              $"height {(slot.HasStoredHeight ? slot.Height.ToString(CultureInfo.InvariantCulture) : "not stored")}" +
                              (slot.Down != null ? $", down {slot.Down}" : ""));
        }

        foreach (var rect in layout.Resolve(width, height))
            Console.WriteLine($"  {rect.DialogClass,-26} x {rect.X,6} y {rect.Y,5} " +
                              $"{rect.Width,5} x {rect.Height,4} (content {rect.ContentHeight})");

        int changed = 0;
        foreach (string spec in Repeated(options, "set"))
        {
            int colon = spec.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(spec[(colon + 1)..], out int panelHeight) || panelHeight <= 0)
                return Fail($"--set: expected Class:height, got '{spec}'.");
            string dialogClass = spec[..colon];
            if (!layout.SetHeight(dialogClass, panelHeight))
                return Fail($"--set={spec}: no {dialogClass} panel in the {context} context of '{args[0]}'.");
            Console.WriteLine($"  set {dialogClass} height {panelHeight}");
            changed++;
        }

        string? saveAs = Option(options, "save-as");
        if (saveAs != null) preferences.SaveAs(saveAs);
        else if (options.Contains("--save")) preferences.Save();
        if (saveAs != null || options.Contains("--save"))
            Console.WriteLine($"saved {(saveAs ?? preferences.Path)} " +
                              $"({new FileInfo(saveAs ?? preferences.Path).Length:N0} bytes, {changed} change(s))");
        return 0;
    }

    /// <summary>
    /// The viewport layouts of a scene: the active layout, the
    /// per-layout camera slots and the Layout/Camera commands. Edits need --save or --save-as.
    /// </summary>
    private static int ViewsCommand(string[] args)
    {
        if (args.Length == 0)
            return Fail("views <file.slopa> [--layout=single|2views|curveboard|4views] [--toggle] [--focused=n] " +
                        "[--view=n --camera=Name]... [--save] [--save-as=out.slopa]");
        var model = SceneModel.Load(args[0]);
        var scene = model.Scene;
        var options = args[1..].ToList();
        string? Option(string name)
        {
            string prefix = "--" + name + "=";
            return options.FirstOrDefault(o => o.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        }

        var views = new SceneViews(scene);
        var changes = new List<string>();
        int focused = 0;
        if (Option("focused") is { } focusedText)
        {
            if (!int.TryParse(focusedText, out focused) || focused < 0)
                return Fail($"--focused: expected a view number, got '{focusedText}'.");
        }

        if (Option("layout") is { } layoutText)
        {
            if (!ViewLayouts.TryParse(layoutText, out var layout))
                return Fail($"--layout: expected single|2views|curveboard|4views, got '{layoutText}'.");
            views.Change(layout, focused);
            focused = Math.Clamp(focused, 0, ViewLayouts.ViewCount(layout) - 1);
            changes.Add($"layout {ViewLayouts.Name(layout)}");
        }
        if (options.Contains("--toggle"))
        {
            focused = views.Toggle(focused);
            changes.Add($"navigated to {ViewLayouts.Name(views.Layout)}, view {focused + 1}");
        }

        int CameraIndex(string name)
        {
            if (name is "orbit" or "perspective" || name.Equals("Perspective (Orbit)", StringComparison.OrdinalIgnoreCase)) return 0;
            if (int.TryParse(name, out int number))
                return number >= 0 && number < scene.Cameras.Count ? number
                    : throw new InvalidOperationException($"--camera: no camera {number}; the scene has {scene.Cameras.Count}.");
            for (int i = 0; i < scene.Cameras.Count; i++)
                if (string.Equals(scene.Cameras[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            throw new InvalidOperationException($"--camera: no camera '{name}' in the scene.");
        }

        try
        {
            int? view = null;
            foreach (string option in options)
            {
                if (option.StartsWith("--view=", StringComparison.Ordinal))
                {
                    if (!int.TryParse(option["--view=".Length..], out int number) || number < 0)
                        return Fail($"--view: expected a view number, got '{option["--view=".Length..]}'.");
                    view = number;
                }
                else if (option.StartsWith("--camera=", StringComparison.Ordinal))
                {
                    int target = view ?? focused;
                    if (target >= ViewLayouts.ViewCount(views.Layout))
                        return Fail($"--view={target}: the {ViewLayouts.Name(views.Layout)} layout has " +
                                    $"{ViewLayouts.ViewCount(views.Layout)} view(s).");
                    int camera = CameraIndex(option["--camera=".Length..]);
                    views.SetCamera(views.Layout, target, camera);
                    changes.Add($"view {target + 1} camera {camera} '{(views.CameraName(camera) ?? "?")}'");
                    view = null;
                }
            }
        }
        catch (InvalidOperationException e)
        {
            return Fail(e.Message);
        }

        Console.WriteLine($"{args[0]}: layout {views.RawLayoutIndex} ({ViewLayouts.Name(views.Layout)}), " +
                          $"toggled layout {views.ToggledLayoutIndex}, toggled view {views.ToggledLocalViewIndex}, " +
                          $"{views.CameraIndices.Count} camera slot(s)");
        foreach (var layout in Enum.GetValues<SceneLayout>())
        {
            var cameras = views.Cameras(layout);
            string line = string.Join(", ", cameras.Select((c, i) =>
                $"view {i + 1} = {c} '{(views.CameraName(c) ?? "?")}'"));
            Console.WriteLine($"  {(layout == views.Layout ? "*" : " ")} {ViewLayouts.Name(layout),-26} {line}");
        }
        Console.WriteLine($"  focused view {focused + 1}");

        if (changes.Count == 0) return 0;
        string? saveAs = Option("save-as");
        if (saveAs == null && !options.Contains("--save"))
            return Fail("The view changes need --save or --save-as=out.slopa.");
        model.Save(saveAs ?? args[0]);
        Console.WriteLine($"{(saveAs ?? args[0])}: {string.Join("; ", changes)}");
        return 0;
    }

    /// <summary>
    /// The keyboard shortcut map (shortcuts.slopa):
    /// list, assign, unbind and reset bindings.
    /// </summary>
    private static int ShortcutsCommand(string[] args)
    {
        if (args.Length < 1)
            return Fail("shortcuts <shortcuts.slopa|shortcuts.akt> [--list] [--action=name] [--set=Action=Key]... " +
                        "[--unbind=Action]... [--reset=Action]... [--reset-all] [--defaults] [--save] [--save-as=out.slopa]");
        var options = args[1..].ToList();
        Shortcuts shortcuts;
        try
        {
            shortcuts = options.Contains("--defaults")
                ? Shortcuts.CreateDefault()
                : Shortcuts.Load(args[0]);
        }
        catch (Exception e) when (e is InvalidDataException or AktFormatException or IOException)
        {
            return Fail(e.Message);
        }

        foreach (string spec in Repeated(options, "set"))
        {
            int eq = spec.IndexOf('=');
            if (eq <= 0)
                return Fail($"--set: expected Action=Key, got '{spec}'.");
            string action = spec[..eq];
            ShortcutKey key;
            try { key = ShortcutKey.Parse(spec[(eq + 1)..]); }
            catch (FormatException e) { return Fail($"--set={action}: {e.Message}"); }
            if (!shortcuts.TrySet(action, key, out string? reason))
                return Fail($"--set={action}: {reason}");
        }

        foreach (string action in Repeated(options, "unbind"))
            shortcuts.Unbind(action);

        foreach (string action in Repeated(options, "reset"))
            if (!shortcuts.Reset(action))
                return Fail($"--reset: '{action}' has no shipped default.");

        if (options.Contains("--reset-all")) shortcuts.ResetAll();

        bool show = options.Contains("--list") || options.Count == 0 ||
                    !options.Any(a => a.StartsWith("--action=", StringComparison.Ordinal));
        foreach (string action in Repeated(options, "action"))
        {
            var key = shortcuts.KeyFor(action);
            Console.WriteLine($"{action} = {(key is { IsEmpty: false } k ? k.ToString() : "(none)")}");
        }
        if (show)
        {
            Console.WriteLine($"{args[0]}: {shortcuts.Bindings.Count} binding(s)");
            foreach (var binding in shortcuts.Bindings)
                Console.WriteLine($"  {binding.Action,-48} {binding.Code:X4}  " +
                                  (binding.Key.IsEmpty ? "(none)" : binding.Key.ToString()));
        }

        string? saveAs = Option(options, "save-as");
        if (saveAs != null) shortcuts.SaveAs(saveAs);
        else if (options.Contains("--save"))
        {
            if (shortcuts.Path == null) shortcuts.SaveAs(args[0]);
            else shortcuts.Save();
        }
        if (saveAs != null || options.Contains("--save"))
            Console.WriteLine($"saved {(saveAs ?? shortcuts.Path)} ({new FileInfo(saveAs ?? shortcuts.Path!).Length:N0} bytes)");
        return 0;
    }

    /// <summary>Writes the next autosave slot of a scene.</summary>
    private static int AutosaveCommand(string[] args)
    {
        if (args.Length < 1) return Fail("autosave <scene.slopa> [--dir=folder] [--base=name]");
        var model = SceneModel.Load(args[0]);
        string folder = Option(args[1..], "dir") ?? Autosave.DefaultFolder;
        string baseName = Option(args[1..], "base") ?? Autosave.BaseName(args[0]);
        string path = Autosave.Save(model, folder, baseName);
        Console.WriteLine($"{path}: {new FileInfo(path).Length:N0} bytes");
        return 0;
    }

    // ---- cameras -------------------------------------------------------------------------

    /// <summary>
    /// The scene's cameras and their views, and every animated camera's FOV track and value at the
    /// frame.
    /// </summary>
    private static int CameraSummary(string[] args)
    {
        var model = SceneModel.Load(args[0]);
        var scene = model.Scene;
        int frame = scene.Time ?? 0;
        if (args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int f)) frame = f;
        Console.WriteLine($"scene '{scene.Name}': {scene.FrameRate} fps, {scene.Cameras.Count} scene camera(s)");

        foreach (var camera in scene.Cameras)
        {
            string what = camera switch
            {
                OrthoCamera ortho => $"ortho, height {ortho.CentimetersInHeight:0.###}, " +
                                     $"centre {ortho.WorldMatrix.M41:0.###} {ortho.WorldMatrix.M42:0.###} {ortho.WorldMatrix.M43:0.###}, " +
                                     $"pan {ortho.WorldOffset.X:0.###} {ortho.WorldOffset.Y:0.###}",
                CameraAnimated animated => $"animated, mode {animated.Mode}, ratio {animated.Ratio} (aspect {animated.AspectRatio:0.####}), " +
                                           $"near {animated.NearClipPlane:0.###}, far {animated.FarClipPlane:0.###}",
                PerspectiveCamera perspective => $"perspective, ratio {perspective.AspectRatio:0.####}",
                _ => "camera",
            };
            Console.WriteLine($"  '{camera.Name}': {camera.ClassName}, {what}");
        }

        foreach (var character in scene.Characters)
        {
            foreach (var camera in character.AnimatedCameras)
            {
                Console.WriteLine($"  animated camera '{camera.Name}' of '{character.Name}': " +
                                  $"mode {camera.Mode}, ratio {camera.Ratio} (aspect {camera.AspectRatio:0.####}), " +
                                  $"near {camera.NearClipPlane:0.###}, far {camera.FarClipPlane:0.###}");
                var evaluator = new PoseEvaluator(character);
                int index = evaluator.IndexOf(camera);
                Console.WriteLine($"    controller slot {index} of {character.Controllers.Count}");
                foreach (var animation in character.Animations)
                {
                    var tracks = animation.Stacks.Select(s => (Stack: s, Track: s.TrackFor(camera)))
                        .Where(x => x.Track is { HasFov: true }).ToList();
                    if (tracks.Count == 0) continue;
                    Console.WriteLine($"    anim '{animation.Name}': FOV {evaluator.CameraFov(animation, camera, frame):0.###}° at frame {frame}");
                    foreach (var (stack, track) in tracks)
                    {
                        var keys = stack.Keys;
                        var values = track!.Keys
                            .Select((k, i) => k is CameraKey ck && i < keys.Count ? $"{keys[i].Time}={ck.Fov:0.###}" : null)
                            .Where(v => v != null).Take(8).ToList();
                        Console.WriteLine($"      stack '{stack.Name}' ({stack.Flags}, opacity {stack.Opacity:0.###}): " +
                                          $"channels {track.Channels}, {track.Keys.Count} keys, FOV {string.Join(" ", values)}" +
                                          (track.Keys.Count > 8 ? " …" : ""));
                    }
                }
            }
        }
        return 0;
    }


    // ---- blendshapes -----------------------------------------------------------------------

    /// <summary>
    /// Blendshape targets, controllers and channels of a scene, with the values mixed at the frame
    /// and the influence array they produce.
    /// </summary>
    private static int BlendshapeSummary(string[] args)
    {
        var model = SceneModel.Load(args[0]);
        var scene = model.Scene;
        int frame = scene.Time ?? 0;
        string? animationName = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--animation=", StringComparison.Ordinal))
                animationName = args[i]["--animation=".Length..];
            else if (int.TryParse(args[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int f))
                frame = f;
        }

        var meshes = scene.Meshes.Where(m => m.BlendshapeTargets.Count > 0).ToList();
        Console.WriteLine($"scene '{scene.Name}': {scene.FrameRate} fps, frame {frame}, " +
                          $"{meshes.Count} mesh(es) with blendshapes");
        foreach (var mesh in meshes)
        {
            Console.WriteLine($"  mesh '{mesh.Name}': {mesh.BlendshapeTargets.Count} target(s), " +
                              $"{mesh.BlendshapeFaces.Count} face(s)");
            foreach (var target in mesh.BlendshapeTargets)
            {
                var array = target.VertexArray;
                Console.WriteLine($"    target '{target.Name}': {array.Length} vertices, " +
                                  $"max length {target.MaxLength:0.####}");
            }
        }

        foreach (var character in scene.Characters)
        {
            var controllers = character.Controllers.OfType<BlendshapeController>().ToList();
            if (controllers.Count == 0) continue;
            var animation = animationName == null
                ? character.CurrentAnimation ?? character.RigAnimation
                : character.Animations.FirstOrDefault(a => a.Name == animationName);
            var poses = new PoseEvaluator(character);
            var evaluator = new BlendshapeEvaluator(poses);
            Console.WriteLine($"  character '{character.Name}': {controllers.Count} blendshape controller(s), " +
                              $"animation '{(animation?.Name ?? "(rest pose)")}'");
            foreach (var controller in controllers)
            {
                var values = evaluator.Values(controller, animation, frame);
                Console.WriteLine($"    controller '{controller.Name}': mesh '{controller.Mesh?.Name}', " +
                                  $"values X {values[0]:0.####} Y {values[1]:0.####}, {controller.Channels.Count} channel(s)");
                foreach (var channel in controller.Channels)
                {
                    Console.WriteLine($"      channel direction {channel.Direction} (value {channel.Group}), " +
                                      $"attenuation {channel.Attenuation:0.####}: " +
                                      string.Join(", ", channel.Infos.Select(i =>
                                          $"{i.Blendshape?.Name}@{i.Position:0.####}")));
                }
            }
            foreach (var mesh in character.Meshes.Where(m => m.BlendshapeTargets.Count > 0))
            {
                var influences = evaluator.Influences(mesh, animation, frame);
                var names = mesh.BlendshapeTargets.Select(t => t.Name);
                Console.WriteLine($"    influences of '{mesh.Name}': " +
                                  string.Join(", ", names.Zip(influences, (n, v) => $"{n} {v:0.####}")));
            }
        }
        return 0;
    }


    /// <summary>
    /// Adds a blendshape target, a controller on a mesh, a channel and an info entry (and,
    /// optionally, a face with stored two-value pairs and a <c>CBlendShapesSight</c>) to a scene and
    /// saves it. No sample scene carries a blendshape, so this verb
    /// is how the viewer's Blendshapes panel and the tests get one.
    /// </summary>
    private static int BlendshapeAddCommand(string[] args)
    {
        var options = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (positional.Count < 3)
            return Fail("Usage: blendshape-add <scene.slopa> <character> <mesh> --target=Name --controller=Name " +
                        "[--direction=0..3] [--attenuation=f] [--position=f] [--face=Name] [--face-values=x,y] " +
                        "[--zone=first-last] [--zone-faces=i,j,k] [--sight=Name] [--sight-at=x,y,z] [--out=out.slopa]");
        string? targetName = Option(options, "target"), controllerName = Option(options, "controller");
        if (string.IsNullOrWhiteSpace(targetName) || string.IsNullOrWhiteSpace(controllerName))
            return Fail("blendshape-add needs --target=Name and --controller=Name.");

        var model = SceneModel.Load(positional[0]);
        var scene = model.Scene;
        var character = scene.Characters.FirstOrDefault(c => c.Name == positional[1])
                        ?? scene.Characters.FirstOrDefault(c => string.Equals(c.Name, positional[1], StringComparison.OrdinalIgnoreCase));
        if (character == null) return Fail($"The scene has no character '{positional[1]}'.");
        var mesh = character.Meshes.FirstOrDefault(m => m.Name == positional[2])
                   ?? scene.Meshes.FirstOrDefault(m => m.Name == positional[2]);
        if (mesh == null) return Fail($"The scene has no mesh '{positional[2]}'.");

        var editor = new BlendshapeEditor(character);
        var target = editor.AddTarget(mesh, targetName);
        string? faceName = Option(options, "face");
        BlendshapeFaceController? faceController = null;
        BlendshapeController controller;
        if (faceName == null)
        {
            controller = editor.AddController(mesh, controllerName);
        }
        else
        {
            // A face references a face controller; it drives the mesh like any other controller.
            faceController = editor.AddFaceController(mesh, controllerName);
            controller = faceController;
        }
        uint direction = uint.TryParse(Option(options, "direction"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint d) ? d : 0;
        float attenuation = float.TryParse(Option(options, "attenuation"), NumberStyles.Float, CultureInfo.InvariantCulture, out float a) ? a : 1f;
        float position = float.TryParse(Option(options, "position"), NumberStyles.Float, CultureInfo.InvariantCulture, out float p) ? p : 1f;
        var channel = editor.AddChannel(controller, direction, attenuation);
        editor.AddInfo(channel, target, position);

        int faces = 0, sights = 0, zoneFaces = 0;
        if (faceName != null && faceController != null)
        {
            var pairs = (Option(options, "face-values") ?? "0,1").Split(';')
                .Select(pair => pair.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray())
                .ToList();
            editor.AddFace(mesh, faceName, [faceController], pairs);
            faces = 1;
            // The pick zone: the mesh triangles this controller answers to, so a viewport click on
            // one of them applies the face.
            if (ZoneFaces(options, mesh) is { } zone && zone.Length > 0)
            {
                faceController.InteractiveFaces = zone;
                zoneFaces = zone.Length;
            }
        }
        if (Option(options, "sight") is { } sightName)
        {
            Vector3? at = Option(options, "sight-at") is { } text ? ParsePoint(text) : null;
            editor.AddSight(sightName, at ?? MeshCentre(mesh) + new Vector3(0f, 0f, 40f));
            sights = 1;
        }

        string outPath = Option(options, "out") ?? positional[0];
        // The saved interface context decides which panels the viewer opens on; a blendshape setup
        // is edited in Animation when the scene has an animation to key.
        if (Option(options, "context") is { } context)
            scene.Context = string.Equals(context, "animation", StringComparison.OrdinalIgnoreCase)
                ? InterfaceContext.Animation
                : InterfaceContext.Rigging;
        model.Save(outPath);
        Console.WriteLine($"added target '{target.Name}' and controller '{controller.Name}' " +
                          $"(channel direction {channel.Direction}, {channel.Infos.Count} info entr(ies), " +
                          $"{controller.Channels.Count} channel(s)), {faces} face(s), {sights} sight(s) to '{mesh.Name}'" +
                          (zoneFaces > 0 ? $", pick zone of {zoneFaces} triangle(s)" : ""));
        Console.WriteLine($"saved {outPath}");
        return 0;
    }

    /// <summary>
    /// Writes or applies the blendshape setup file (<c>.bsu</c>, the container
    /// the blendshape set-up export writes).
    /// <c>export</c> writes the mesh's controller array in order; <c>import</c> applies a setup onto
    /// the mesh - matching controllers by name, channels by direction and infos by target, and
    /// creating what the file names - then saves the scene to <c>--out</c> (else in place).
    /// </summary>
    private static int BlendshapeSetUpCommand(string[] args)
    {
        const string usage = "blendshape-setup <export|import> <scene.slopa> <mesh> <file.bsu> [--out=out.slopa]";
        if (args.Length < 1) return Fail(usage);
        string verb = args[0];
        var rest = args[1..];
        var options = rest.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var positional = rest.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (verb is not ("export" or "import") || positional.Count < 3) return Fail(usage);

        var model = SceneModel.Load(positional[0]);
        var mesh = model.Scene.Meshes.FirstOrDefault(m => m.Name == positional[1])
                   ?? model.Scene.Meshes.FirstOrDefault(m =>
                       string.Equals(m.Name, positional[1], StringComparison.OrdinalIgnoreCase));
        if (mesh == null) return Fail($"The scene has no mesh '{positional[1]}'.");

        string file = positional[2];
        try
        {
            if (verb == "export")
            {
                var setup = BlendshapeSetUp.Capture(mesh);
                byte[] bytes = BlendshapeSetUp.Write(setup);
                File.WriteAllBytes(file, bytes);
                Console.WriteLine($"exported {setup.Count} controller(s), {setup.Sum(c => c.Channels.Count)} channel(s), " +
                                  $"{setup.Sum(c => c.Channels.Sum(ch => ch.Infos.Count))} info entr(ies) from '{mesh.Name}' " +
                                  $"to {file} ({bytes.Length} bytes)");
                return 0;
            }

            var imported = BlendshapeSetUp.Read(File.ReadAllBytes(file));
            BlendshapeSetUpApplyResult result = BlendshapeSetUp.Apply(mesh, imported);
            string outPath = Option(options, "out") ?? positional[0];
            model.Save(outPath);
            Console.WriteLine($"imported {imported.Count} controller(s) onto '{mesh.Name}': {result}");
            Console.WriteLine($"saved {outPath}");
            return 0;
        }
        catch (FormatException e)
        {
            // A bad setup file is the reader's FormatException, which Main's catch filter does not
            // cover; the verb reports it like every other refusal.
            return Fail(e.Message);
        }
    }

    /// <summary>
    /// Stores a blendshape point of view on one mesh without the viewer
    /// the camera that looks from
    /// <c>--eye=x,y,z</c> at <c>--target=x,y,z</c>, with the distance between them as its target
    /// distance, which is what the blendshape panel's
    /// "Set point of view" button stores. The scene is saved so the result can be inspected.
    /// </summary>
    private static int BlendshapePovCommand(string[] args)
    {
        var options = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (positional.Count < 2)
            return Fail("Usage: blendshape-pov <scene.slopa> <mesh> --eye=x,y,z --target=x,y,z [--out=out.slopa]");
        if (Option(options, "eye") is not { } eyeText || Option(options, "target") is not { } targetText)
            return Fail("blendshape-pov needs --eye=x,y,z and --target=x,y,z.");
        if (ParsePoint(eyeText) is not { } eye || ParsePoint(targetText) is not { } target)
            return Fail("blendshape-pov needs --eye=x,y,z and --target=x,y,z, each three numbers.");
        float distance = Vector3.Distance(eye, target);
        // The stored distance is the camera's target distance, and "set" means distance > 0.
        if (!BlendshapePointOfView.IsSet(distance))
            return Fail("blendshape-pov needs an eye and a target that differ: the point of view's distance is the camera's target distance and must be positive.");

        var model = SceneModel.Load(positional[0]);
        var mesh = model.Scene.Meshes.FirstOrDefault(m => m.Name == positional[1])
                   ?? model.Scene.Meshes.FirstOrDefault(m => string.Equals(m.Name, positional[1], StringComparison.OrdinalIgnoreCase));
        if (mesh == null) return Fail($"The scene has no mesh '{positional[1]}'.");

        var world = BlendshapePointOfView.FromLookAt(eye, target, Vector3.UnitY);
        var session = mesh.Ancestor<Character>() is { } character ? new BlendshapeSession(character) : null;
        if (session != null) session.SetPointOfView(mesh, world, distance);
        else BlendshapePointOfView.Set(mesh, world, distance);

        string? anchor = mesh.BlendshapeCameraAnchor?.Name;
        Console.WriteLine($"point of view on '{mesh.Name}': eye ({Format(eye)}), target ({Format(target)}), distance {distance:0.####}");
        Console.WriteLine($"  relative to {(anchor == null ? "the mesh's world matrix" : $"'{anchor}'")}; " +
                          $"stored {BlendshapeSchema.CameraPositionMember}:");
        float[] stored = AktMath.ToArray(mesh.BlendshapeCameraPositionFromAnchor);
        for (int row = 0; row < 4; row++)
            Console.WriteLine("    [ " + string.Join(" ", stored[(row * 4)..(row * 4 + 4)].Select(v => v.ToString("0.######", CultureInfo.InvariantCulture))) + " ]");
        Console.WriteLine($"  stored {BlendshapeSchema.CameraDistanceMember} = {mesh.BlendshapeCameraDistanceFromAnchor:0.####}");
        if (session?.PointOfView(mesh) is { } read)
            Console.WriteLine($"  reads back as eye ({Format(read.Translation)}), target ({Format(BlendshapePointOfView.TargetOf(read, distance))})");

        string outPath = Option(options, "out") ?? positional[0];
        model.Save(outPath);
        Console.WriteLine($"saved {outPath}");
        return 0;

        static string Format(Vector3 v) =>
            string.Create(CultureInfo.InvariantCulture, $"{v.X:0.####}, {v.Y:0.####}, {v.Z:0.####}");
    }

    /// <summary>
    /// The triangles of a <c>--zone=first-last</c> (a range of polygon indices, either order) or
    /// <c>--zone-faces=i,j,k</c> option, clamped to the mesh's <see cref="Mesh.Triangles"/> count
    /// (that order is the <c>InteractiveFaces</c> index space). Null when neither option is given;
    /// an empty array when the range names no triangle.
    /// </summary>
    private static uint[]? ZoneFaces(IReadOnlyList<string> options, Mesh mesh)
    {
        int count = mesh.Triangles().Count();
        if (Option(options, "zone-faces") is { } list)
            return list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(text => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ? index : -1)
                .Where(index => index >= 0 && index < count)
                .Distinct()
                .Select(index => (uint)index)
                .ToArray();
        if (Option(options, "zone") is not { } range) return null;
        var parts = range.Split('-');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int first)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int last))
            return [];
        if (first > last) (first, last) = (last, first);
        first = Math.Max(first, 0);
        last = Math.Min(last, count - 1);
        return first > last ? [] : Enumerable.Range(first, last - first + 1).Select(index => (uint)index).ToArray();
    }

    /// <summary>The centre of a mesh's control points in the scene, used to place a default sight.</summary>
    private static Vector3 MeshCentre(Mesh mesh)
    {
        var points = mesh.ControlPoints;
        if (points.Length == 0) return mesh.WorldMatrix.Translation;
        var sum = Vector3.Zero;
        foreach (var point in points) sum += point;
        return Vector3.Transform(sum / points.Length, mesh.WorldMatrix);
    }

    /// <summary>
    /// Audio objects, entries and events, and what the schedule says plays at the scene time
    /// Audio clips are shown when their WAV bytes decode.
    /// </summary>
    private static int AudioSummary(string[] args)
    {
        var model = SceneModel.Load(args[0]);
        var scene = model.Scene;
        var objects = scene.AudioObjects.ToList();
        var entries = scene.AudioEntries.ToList();
        Console.WriteLine($"scene '{scene.Name}': {scene.FrameRate} fps, {objects.Count} audio object(s), {entries.Count} audio entr(ies)");

        foreach (var o in objects)
            Console.WriteLine($"  audio object '{o.Name}' of character '{o.Character?.Name}'");
        foreach (var e in entries)
        {
            var wave = e.Audio;
            string format = wave == null
                ? "not decodable (RIFF/WAVE or the .akt raw PCM only)"
                : $"{wave.SampleRate} Hz, {wave.Channels} ch, {wave.Duration:0.###} s, {wave.Format}";
            Console.WriteLine($"  entry '{e.DisplayName}': file '{e.FileName}', embedded {e.Data.Length} bytes, {format}");
        }

        int frame = scene.Time ?? 0;
        int stacks = 0;
        foreach (var anim in scene.Descendants<Animation>())
        {
            var audioStacks = anim.Stacks.OfType<AudioStack>().ToList();
            if (audioStacks.Count == 0) continue;
            stacks += audioStacks.Count;
            var (start, end) = anim.TimeRange;
            Console.WriteLine($"  anim '{anim.Name}' (frames {start}..{end}): {audioStacks.Count} audio stack(s)");
            foreach (var stack in audioStacks)
            {
                Console.WriteLine($"    stack '{stack.Name}': flags {stack.Flags} (enabled {stack.IsEnabled}, cycle {stack.IsCycle}), " +
                                  $"opacity {stack.Opacity}, {stack.Events.Count} event(s)");
                foreach (var e in stack.Events)
                {
                    string delay = e.DelaySinceStartKey != 0 ? $" (+{e.DelaySinceStartKey} delay)" : "";
                    string length = e.Audio == null ? "no decodable sound" : $"{e.DurationSeconds:0.###} s";
                    Console.WriteLine($"      event {e.Index} at frame {e.StartFrame}{delay}: '{e.Entry?.DisplayName ?? "(none)"}', " +
                                      $"{length}, volume {e.Volume}, mute {e.Mute}");
                }
            }
            var clips = AudioSchedule.At(anim, frame / (double)Math.Max(scene.FrameRate, 1u), frame, scene.FrameRate);
            Console.WriteLine($"    at frame {frame}: " + (clips.Count == 0
                ? "silence"
                : string.Join(", ", clips.Select(c => c.Key is AudioEvent e
                    ? $"'{e.Entry?.DisplayName}' from {c.StartSeconds:0.###} s at volume {c.Volume}"
                    : c.Key.ToString()))));
        }
        if (stacks == 0) Console.WriteLine("  no audio stacks in any animation");
        return 0;
    }

    // ---- outliner -------------------------------------------------------------------------

    /// <summary>
    /// The hierarchy panel: the rows, the interface
    /// context and the Show/Hide and Lock commands. Edits need <c>--save-as</c>.
    /// </summary>
    private static int OutlinerCommand(string[] args)
    {
        if (args.Length == 0)
            return Fail("outliner <file.slopa> [--context=rigging|animation] [--show=name...] [--hide=name...] " +
                        "[--toggle=name...] [--show-all] [--hide-all] [--lock=mesh...] [--unlock=mesh...] " +
                        "[--lock-character] [--unlock-character] [--delete-tree=name...] [--character=name] [--save-as=out.slopa]");
        var model = SceneModel.Load(args[0]);
        var scene = model.Scene;
        var options = args[1..].ToList();
        string? Option(string name)
        {
            string prefix = "--" + name + "=";
            return options.FirstOrDefault(o => o.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        }
        List<string> Repeated(string name)
        {
            string prefix = "--" + name + "=";
            return options.Where(o => o.StartsWith(prefix, StringComparison.Ordinal))
                .Select(o => o[prefix.Length..]).ToList();
        }

        var changes = new List<string>();
        try
        {
            if (Option("context") is { } context)
            {
                var value = context.Trim().ToLowerInvariant() switch
                {
                    "rigging" => InterfaceContext.Rigging,
                    "animation" => InterfaceContext.Animation,
                    _ => throw new InvalidOperationException($"--context: expected rigging|animation, got '{context}'."),
                };
                scene.Context = value;
                changes.Add($"context {value.ToString().ToLowerInvariant()}");
            }
            foreach (string name in Repeated("hide"))
            {
                int count = Outliner.Hide([FindOutlinerObject(model, name)]);
                changes.Add($"hid '{name}' ({count} object(s))");
            }
            foreach (string name in Repeated("show"))
            {
                int count = Outliner.Show([FindOutlinerObject(model, name)]);
                changes.Add($"showed '{name}' ({count} object(s))");
            }
            foreach (string name in Repeated("toggle"))
            {
                int count = Outliner.ToggleShown([FindOutlinerObject(model, name)]);
                changes.Add($"toggled '{name}' ({count} object(s))");
            }
            if (options.Contains("--show-all")) changes.Add($"showed all ({Outliner.ShowAll(scene)} object(s))");
            if (options.Contains("--hide-all")) changes.Add($"hid all ({Outliner.HideAll(scene)} object(s))");
            foreach (string name in Repeated("lock"))
            {
                int count = Outliner.SetMeshesLocked([FindOutlinerMesh(model, name)], true);
                changes.Add($"locked '{name}' ({count} mesh(es))");
            }
            foreach (string name in Repeated("unlock"))
            {
                int count = Outliner.SetMeshesLocked([FindOutlinerMesh(model, name)], false);
                changes.Add($"unlocked '{name}' ({count} mesh(es))");
            }
            if (options.Contains("--lock-character") || options.Contains("--unlock-character"))
            {
                var character = FindCharacter(model, Option("character"))
                                ?? throw new InvalidOperationException("The scene has no character to lock.");
                bool locked = options.Contains("--lock-character");
                int count = Outliner.SetCharacterMeshesLocked(character, locked);
                changes.Add($"{(locked ? "locked" : "unlocked")} {count} skinned mesh(es) of '{character.Name}'");
            }
            foreach (string name in Repeated("delete-tree"))
            {
                var row = FindOutlinerObject(model, name);
                if (TreeCharacter(model, row) is not { } owner)
                    throw new InvalidOperationException($"--delete-tree='{name}': no character owns that row.");
                var editor = new RigEditor(owner);
                var plan = Outliner.PlanTreeDelete(editor, [row]);
                if (!plan.Ok)
                    throw new InvalidOperationException($"--delete-tree='{name}': {plan.Refusal}");
                int deleted = editor.DeleteControllers(plan.Controllers) + editor.DeleteJoints(plan.Joints);
                changes.Add($"deleted '{name}' ({deleted} object(s))");
            }
        }
        catch (InvalidOperationException e)
        {
            return Fail(e.Message);
        }

        var rows = Outliner.Rows(scene);
        Console.WriteLine($"{args[0]}: context {(scene.Context == InterfaceContext.Rigging ? "rigging" : "animation")} " +
                          $"(InterfaceContextIndex {scene.InterfaceContextIndex}), {rows.Count} row(s)");
        foreach (var (o, depth) in rows)
        {
            var marks = new List<string>();
            if (!o.IsShown) marks.Add("hidden");
            if (o.IsUnpickable) marks.Add("unpickable");
            if (o is Mesh mesh && mesh.SkinnedMeshIsLocked) marks.Add("locked");
            // The row's picture.
            Console.WriteLine($"{new string(' ', 2 * depth)}[{OutlinerStyle.KindOf(o)}] {o.ClassName} '{o.Name}'" +
                              (marks.Count > 0 ? $"  [{string.Join(", ", marks)}]" : ""));
        }
        int hidden = rows.Count(r => !r.Object.IsShown);
        int lockedMeshes = rows.Count(r => r.Object is Mesh m && m.SkinnedMeshIsLocked);
        Console.WriteLine($"{rows.Count} row(s), {rows.Count - hidden} shown, {hidden} hidden, {lockedMeshes} locked mesh(es)");
        if (changes.Count == 0) return 0;
        if (Option("save-as") is not { } output)
            return Fail("The outliner edits need --save-as=out.slopa.");
        model.Save(output);
        Console.WriteLine($"{output}: {string.Join("; ", changes)}");
        return 0;
    }

    /// <summary>A 3D object by unique name, or <c>#documentIndex</c> (the index <c>tree</c> prints).</summary>
    private static Object3D FindOutlinerObject(SceneModel model, string selector)
    {
        if (selector.StartsWith('#') &&
            int.TryParse(selector[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            var all = model.Document.Objects();
            if (index >= 0 && index < all.Count && model.Wrap(all[index]) is Object3D byIndex) return byIndex;
            throw new InvalidOperationException($"#{index} is not a 3D object of the scene.");
        }
        var matches = model.Scene.Descendants<Object3D>().Where(o => o.Name == selector).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"The scene has no 3D object named '{selector}'."),
            _ => throw new InvalidOperationException(
                $"'{selector}' names {matches.Count} 3D objects; use #index instead (the tree command prints them)."),
        };
    }

    private static Mesh FindOutlinerMesh(SceneModel model, string selector) =>
        FindOutlinerObject(model, selector) as Mesh
        ?? throw new InvalidOperationException($"'{selector}' is not a mesh.");

    /// <summary>The character that owns a hierarchy row: itself, its closest character ancestor, else the scene's current one.</summary>
    private static Character? TreeCharacter(SceneModel model, Object3D row) =>
        row as Character ?? row.Ancestor<Character>() ?? FindCharacter(model, null);

    // ---- timeline --------------------------------------------------------------------------

    /// <summary>
    /// The timeline's geometry and hit testing for an animation
    /// one row per stack with its key span, and what a press at a point would hit - a key,
    /// an audio clip, a fade handle, a stack name or a scrub to a frame. The viewer draws and
    /// presses through the same <see cref="TimelineLayout"/>, so this answers "what is under that
    /// pixel" without opening a window.
    /// </summary>
    private static int TimelineCommand(string[] args)
    {
        if (args.Length == 0)
            return Fail("timeline <file.slopa> [--animation=name] [--character=name] [--width=w] " +
                        "[--press=x,y] [--to=x,y] [--apply] [--select=i,j] [--ripple] " +
                        "[--row=n]... [--keys] [--save-as=out.slopa]");
        var model = SceneModel.Load(args[0]);
        var options = args[1..].ToList();
        string? Option(string name)
        {
            string prefix = "--" + name + "=";
            return options.FirstOrDefault(o => o.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        }
        var rowsToListed = options.Where(o => o.StartsWith("--row=", StringComparison.Ordinal))
            .Select(o => int.TryParse(o["--row=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : -1)
            .ToList();

        var character = FindCharacter(model, Option("character"));
        if (character == null) return Fail("The scene has no character.");
        Animation? animation;
        if (Option("animation") is { } wanted)
        {
            animation = character.Animations.FirstOrDefault(a => a.Name == wanted);
            if (animation == null) return Fail($"'{character.Name}' has no animation named '{wanted}'.");
        }
        else
        {
            animation = character.Animations.FirstOrDefault();
            if (animation == null) return Fail($"'{character.Name}' has no animations.");
        }

        double width = Option("width") is { } text &&
                       double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) && w > TimelineLayout.Gutter
            ? w
            : 1200;
        var layout = new TimelineLayout { Width = width, Animation = animation };
        var (start, end) = layout.Range;
        Console.WriteLine($"{args[0]}: character '{character.Name}', animation '{animation.Name}', " +
                          $"frames {start}..{end}, {layout.FrameWidth:0.###} DIP per frame at width {width:0.##}");

        var stacks = animation.Stacks;
        for (int row = 0; row < stacks.Count; row++)
        {
            var stack = stacks[row];
            var keys = stack.Keys;
            string span = keys.Count == 0 ? "-" : $"{keys[0].Time}..{keys[^1].Time}";
            string kind = stack is AudioStack ? " (audio)" : stack.IsCycle ? " (cycle)" : stack.IsAdditive ? " (additive)" : "";
            int fades = keys.Count(k => k.FadeIn != 0 || k.FadeOut != 0);
            Console.WriteLine($"  row {row,2}  '{stack.Name}'{kind}  {keys.Count} key(s), frames {span}, " +
                              $"{fades} fade(s), y {TimelineLayout.RowTop(row):0.#}..{TimelineLayout.RowTop(row) + TimelineLayout.RowHeight:0.#}");
            if (rowsToListed.Contains(row) || options.Contains("--keys"))
                for (int k = 0; k < keys.Count; k++)
                {
                    string fade = keys[k].FadeIn != 0 || keys[k].FadeOut != 0 ? $"  fade {keys[k].FadeIn}/{keys[k].FadeOut}" : "";
                    Console.WriteLine($"      key {k,3} @ frame {keys[k].Time,6}  x {layout.X(keys[k].Time),9:0.#}{fade}" +
                                      (k == keys.Count - 1 ? "  (loop key)" : ""));
                }
        }

        if (Option("press") is not { } press)
            return 0;
        if (!TryPoint(press, out var at))
            return Fail("--press expects x,y in DIPs.");
        float px = at.X, py = at.Y;
        var (hitRow, hitKey, fadeIn) = layout.FadeHandleAt(at);
        var (rowAt, keyAt) = hitRow >= 0 ? (hitRow, hitKey) : layout.KeyAt(at);
        if (rowAt >= 0 && keyAt >= 0)
        {
            var key = stacks[rowAt].Keys[keyAt];
            Console.WriteLine(hitRow >= 0
                ? $"press ({px:0.#}, {py:0.#}): fade-{(fadeIn ? "in" : "out")} handle of key {keyAt} of row {rowAt} '{stacks[rowAt].Name}'"
                : $"press ({px:0.#}, {py:0.#}): {(stacks[rowAt] is AudioStack ? "audio clip" : "key")} {keyAt} " +
                  $"of row {rowAt} '{stacks[rowAt].Name}', frame {key.Time}");
        }
        else if (rowAt >= 0)
            Console.WriteLine($"press ({px:0.#}, {py:0.#}): {(px < TimelineLayout.Gutter ? "stack name" : "empty row")} " +
                              $"of row {rowAt} '{stacks[rowAt].Name}'" +
                              (px < TimelineLayout.Gutter ? "" : $", scrubbing to frame {layout.FrameUnder(px)}"));
        else
            Console.WriteLine($"press ({px:0.#}, {py:0.#}): nothing (ruler or below the rows)");

        if (!options.Contains("--apply")) return 0;

        // The release point, then the same edit the widget's release makes (KeyTimeline.OnPointerReleased).
        if (Option("to") is not { } toText || !TryPoint(toText, out var to))
            return Fail("--apply needs --to=x,y, the pointer's release point.");
        if (Option("save-as") is not { } output)
            return Fail("--apply needs --save-as=out.slopa.");
        if (rowAt < 0 || keyAt < 0)
            return Fail("A drag has to start on a key or a fade handle.");
        if (rowAt < 0 || keyAt < 0)
            return Fail("A drag has to start on a key or a fade handle.");

        var dragged = stacks[rowAt];
        if (dragged is AudioStack)
            return Fail("An audio clip is selected as one block, but a drag does not retime it.");
        var editor = new KeyEditor(character);
        if (hitRow >= 0)
        {
            var key = dragged.Keys[keyAt];
            int frames = KeyEditor.DraggedFade(fadeIn ? key.FadeIn : key.FadeOut, fadeIn,
                layout.FrameUnder(to.X) - layout.FrameUnder(px));
            ushort set = editor.SetKeyFade(dragged, keyAt, fadeIn, frames);
            Console.WriteLine($"  fade-{(fadeIn ? "in" : "out")} of key {keyAt} set to {set} frame(s)");
        }
        else
        {
            int delta = (int)Math.Round((to.X - px) / layout.FrameWidth);
            var selected = Option("select") is { } list
                ? list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(i => int.TryParse(i, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : -1)
                    .Where(i => i >= 0 && i < dragged.Keys.Count).ToList()
                : [keyAt];
            if (selected.Count == 0) selected = [keyAt];
            if (options.Contains("--ripple"))
            {
                int frame = dragged.Keys[keyAt].Time + delta;
                if (keyAt > 0) frame = Math.Max(frame, dragged.Keys[keyAt - 1].Time + 1);
                int set = editor.SetKeyTime(dragged, keyAt, frame);
                Console.WriteLine($"  key {keyAt} rippled to frame {set}, moving the later keys");
            }
            else
            {
                var moved = editor.MoveKeys(dragged, selected, delta);
                // MoveKeys returns every key's new index; the dragged ones are the selected.
                var now = selected.Where(k => k < moved.Length).Select(k => dragged.Keys[moved[k]].Time);
                Console.WriteLine($"  key(s) {string.Join(",", selected)} moved by {delta} frame(s), " +
                                  $"now at frame(s) {string.Join(",", now)}");
            }
        }
        model.Save(output);
        Console.WriteLine($"{output}: written");
        return 0;
    }

    // ---- curveboard ------------------------------------------------------------------------

    /// <summary>
    /// Prints the Curveboard's curves, its key and tangent-handle positions and what a press at a
    /// point would hit, then replays the press's drag with <c>--apply</c>.
    /// </summary>
    private static int CurveboardCommand(string[] args)
    {
        if (args.Length == 0)
            return Fail("curveboard <file.slopa> [--animation=name] [--character=name] [--stack=name] " +
                        "[--track=name]... [--width=w] [--height=h] [--press=x,y] [--select=x,y]... " +
                        "[--to=x,y] [--handles] [--apply] [--save-as=out.slopa]");
        var model = SceneModel.Load(args[0]);
        var options = args[1..].ToList();
        string? Option(string name)
        {
            string prefix = "--" + name + "=";
            return options.FirstOrDefault(o => o.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        }

        var character = FindCharacter(model, Option("character"));
        if (character == null) return Fail("The scene has no character.");
        Animation? animation;
        if (Option("animation") is { } wanted)
        {
            animation = character.Animations.FirstOrDefault(a => a.Name == wanted);
            if (animation == null) return Fail($"'{character.Name}' has no animation named '{wanted}'.");
        }
        else
        {
            animation = character.Animations.FirstOrDefault();
            if (animation == null) return Fail($"'{character.Name}' has no animations.");
        }

        AnimStack? stack;
        if (Option("stack") is { } wantedStack)
        {
            stack = animation.Stacks.FirstOrDefault(s => s.Name == wantedStack);
            if (stack == null) return Fail($"'{animation.Name}' has no stack named '{wantedStack}'.");
        }
        else
        {
            stack = animation.Stacks.FirstOrDefault(s => s.Keys.Count > 1 && s.Tracks.Any(t => t.Keys.Count > 1));
            if (stack == null) return Fail($"'{animation.Name}' has no stack with keys.");
        }

        var names = options.Where(o => o.StartsWith("--track=", StringComparison.Ordinal))
            .Select(o => o["--track=".Length..]).ToHashSet(StringComparer.Ordinal);
        var trackFilter = names.Count == 0 ? null : stack.Tracks.Where(t => names.Contains(t.Target?.Name ?? ""));
        var tracks = CurveboardLayout.TracksOf(stack, trackFilter).Take(12).ToArray();
        if (tracks.Length == 0) return Fail("No track with keys to show.");

        double width = Option("width") is { } widthText &&
                       double.TryParse(widthText, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) &&
                       w > CurveboardLayout.Gutter
            ? w
            : 1200;
        double height = Option("height") is { } heightText &&
                        double.TryParse(heightText, NumberStyles.Float, CultureInfo.InvariantCulture, out double h) && h > 10
            ? h
            : 400;
        var layout = new CurveboardLayout
        {
            Width = width, Height = height, Animation = animation, Evaluator = new PoseEvaluator(character),
        };
        layout.Show(stack, tracks);
        var (start, end) = layout.Range;
        Console.WriteLine($"{args[0]}: character '{character.Name}', animation '{animation.Name}', " +
                          $"stack '{stack.Name}', frames {start}..{end}, " +
                          $"{layout.FrameWidth:0.###} DIP per frame at {width:0.##}x{height:0.##}, " +
                          $"values {layout.YMin:0.###}..{layout.YMax:0.###}");
        foreach (var group in layout.KeyPoints().GroupBy(p => (p.Track, p.Channel)))
        {
            string values = string.Join(" ", group.Select(p =>
                $"{stack.Keys[p.Key].Time}:{layout.ValueAt(p.Point.Y).ToString("0.###", CultureInfo.InvariantCulture)}"));
            Console.WriteLine($"  '{group.Key.Track.Target?.Name}' {Curves.ChannelNames[group.Key.Channel]}: {values}");
        }

        // The extra clicks are Shift presses, so the selection they build is the drag's selection.
        foreach (var text in options.Where(o => o.StartsWith("--select=", StringComparison.Ordinal)))
        {
            if (!TryPoint(text["--select=".Length..], out var at))
                return Fail("--select expects x,y in DIPs.");
            layout.Press(at, true, false);
        }

        if (Option("press") is not { } pressText) return 0;
        if (!TryPoint(pressText, out var pressAt))
            return Fail("--press expects x,y in DIPs.");
        var press = layout.Press(pressAt, false, false);
        var target = layout.PressedPoint is { } p ? $"'{p.Track.Target?.Name}' {Curves.ChannelNames[p.Channel]}" : "";
        if (layout.PressedHandle is { } handle)
            Console.WriteLine($"press ({pressAt.X:0.#}, {pressAt.Y:0.#}): {target} tangent handle " +
                              $"({handle.Side}), end at ({handle.End.X:0.#}, {handle.End.Y:0.#})");
        else if (layout.PressedPoint is { } point)
            Console.WriteLine($"press ({pressAt.X:0.#}, {pressAt.Y:0.#}): key {point.Key} {target}, " +
                              $"frame {stack.Keys[point.Key].Time} = {layout.ValueAt(point.Point.Y):0.###}, " +
                              $"tangent in {Curves.TangentSlope(stack, point.Track, point.Key, point.Channel, TangentSide.In):0.###} " +
                              $"out {Curves.TangentSlope(stack, point.Track, point.Key, point.Channel, TangentSide.Out):0.###}" +
                              (press.Gesture == CurveboardGesture.Press ? "" : " (already selected: Ctrl removed it)"));
        else
            Console.WriteLine($"press ({pressAt.X:0.#}, {pressAt.Y:0.#}): nothing, a click sets frame " +
                              $"{layout.FrameUnder(pressAt.X)}; a drag selects in a box");

        if (options.Contains("--handles"))
            foreach (var shown in layout.Handles())
                Console.WriteLine($"  handle {Curves.ChannelNames[shown.Channel]} {shown.Side} of key {shown.Key} " +
                                  $"'{shown.Track.Target?.Name}' end ({shown.End.X:0.#}, {shown.End.Y:0.#}), " +
                                  $"slope {Curves.TangentSlope(stack, shown.Track, shown.Key, shown.Channel, shown.Side):0.###}");

        if (!options.Contains("--apply")) return 0;

        if (Option("to") is not { } toText || !TryPoint(toText, out var to))
            return Fail("--apply needs --to=x,y, the pointer's release point.");
        if (Option("save-as") is not { } output)
            return Fail("--apply needs --save-as=out.slopa.");
        var editor = new KeyEditor(character);
        if (layout.PressedHandle is { } draggedHandle)
        {
            float slope = layout.Slope(draggedHandle, to);
            editor.SetTangentSlope(draggedHandle.Track, draggedHandle.Key, draggedHandle.Channel,
                draggedHandle.Side, slope);
            Console.WriteLine($"  {target} {draggedHandle.Side} tangent slope set to {slope:0.####}");
        }
        else if (layout.PressedPoint is { } draggedPoint)
        {
            float delta = layout.MovedValue(pressAt.Y, to.Y);
            int moved = editor.MoveKeyValues(layout.Selection, delta);
            Console.WriteLine($"  {moved} key value(s) moved by {delta:0.####} " +
                              $"(key {draggedPoint.Key} now {layout.ValueAt(draggedPoint.Point.Y) + delta:0.###})");
        }
        else
            return Fail("A drag has to start on a key or a tangent handle.");
        model.Save(output);
        Console.WriteLine($"{output}: written");
        return 0;
    }

    private static bool TryPoint(string text, out Vector2 point)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        point = default;
        if (parts.Length != 2 ||
            !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
            !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return false;
        point = new Vector2(x, y);
        return true;
    }

    // ---- poses ----------------------------------------------------------------------------

    /// <summary>
    /// For each character controller, compares every stored pose matrix (and key 0 of the rig
    /// animation) with the controller's local and world matrices.
    /// </summary>
    private static int Poses(string[] args)
    {
        const float tolerance = 1e-3f;
        var model = SceneModel.Load(args[0]);
        foreach (var ch in model.Scene.Characters)
        {
            var controllers = ch.Controllers;
            var sources = new List<(string Name, Matrix4x4[] Matrices)>();
            foreach (var (label, pose) in new[] { ("RefPose", ch.RefPose), ("CurrentPose", ch.CurrentPose), ("RefPoseOld", ch.RefPoseOld) }
                         .Concat(ch.BindPoses.Select(p => ($"bind '{p.Name}'", (CharacterPose?)p))))
                if (pose != null) sources.Add(($"{label} '{pose.Name}'", pose.Matrices));
            if (ch.RigAnimation?.Stacks.FirstOrDefault() is { } rigStack)
            {
                for (int k = 0; k < rigStack.Keys.Count; k++)
                {
                    var m = new Matrix4x4[controllers.Count];
                    for (int i = 0; i < controllers.Count; i++)
                        m[i] = rigStack.TrackFor(controllers[i])?.Keys[k] is TransformKey key ? key.Value.ToMatrix() : new Matrix4x4();
                    sources.Add(($"__RigAnim key {k} (TRS)", m));
                }
            }
            // The evaluated pose at the scene time.
            var evaluator = new PoseEvaluator(ch);
            var anim = ch.CurrentAnimation;
            int time = model.Scene.Time ?? anim?.CurrentTime ?? 0;
            sources.Add(("evaluated rest pose", evaluator.LocalTransforms(null, 0).Select(t => t.ToMatrix()).ToArray()));
            if (anim != null)
                sources.Add(($"evaluated '{anim.Name}' @{time}", evaluator.LocalTransforms(anim, time).Select(t => t.ToMatrix()).ToArray()));

            Console.WriteLine($"character '{ch.Name}': {controllers.Count} controllers");
            // Plausibility check: joints of the evaluated pose should mostly lie within the bound meshes.
            var restWorld = evaluator.WorldMatrices(null, 0);
            var animWorld = anim != null ? evaluator.WorldMatrices(anim, time) : null;
            foreach (var mesh in ch.Meshes)
            {
                var pts = mesh.ControlPoints.Select(p => Vector3.Transform(p, mesh.WorldMatrix)).ToArray();
                if (pts.Length == 0) continue;
                var lo = pts.Aggregate(Vector3.Min); var hi = pts.Aggregate(Vector3.Max);
                bool Inside(Vector3 v) => Vector3.Min(Vector3.Max(v, lo - new Vector3(5)), hi + new Vector3(5)) == v;
                int CountInside(Matrix4x4[] world) => ch.Joints.Count(j => evaluator.IndexOf(j) is >= 0 and var i && Inside(world[i].Translation));
                Console.WriteLine($"  mesh '{mesh.Name}': joints inside its bounds: rest pose {CountInside(restWorld)}/{ch.Joints.Count}" +
                                  (animWorld != null ? $", animated {CountInside(animWorld)}/{ch.Joints.Count}" : "") +
                                  $", LTM chain {ch.Joints.Count(j => Inside(j.WorldMatrix.Translation))}/{ch.Joints.Count}");
            }
            // How much scale-compensated composition moves
            // the evaluated pose compared with the plain matrix chain.
            if (controllers.Count == 0)
            {
                Console.WriteLine("  no controllers to pose.");
                continue;
            }
            var plain = new PoseEvaluator(ch) { ScaleCompensation = false };
            foreach (var (label, a) in new[] { ("rest pose", (Animation?)null), ($"'{anim?.Name}' @{time}", anim) }.Where(x => x.Item1 == "rest pose" || x.Item2 != null))
            {
                var ours = evaluator.WorldMatrices(a, time);
                var theirs = plain.WorldMatrices(a, time);
                int worst = Enumerable.Range(0, controllers.Count).MaxBy(i => AktMath.MaxDifference(ours[i], theirs[i]));
                Console.WriteLine($"  scale compensation ({controllers.Count(c => c.CompensatesParentScale)} flagged controllers), {label}: " +
                                  $"world differs from the plain chain by up to {AktMath.MaxDifference(ours[worst], theirs[worst]):0.####} ('{controllers[worst].Name}')");
            }
            foreach (string wanted in args[1..])
            {
                int i = controllers.ToList().FindIndex(c => c.Name == wanted);
                if (i < 0) continue;
                var c = controllers[i];
                Console.WriteLine($"  #{i} {c.ClassName} '{c.Name}', parent '{c.Parent3D?.Name}'");
                Console.WriteLine($"    {"LTM",-34} {FormatMatrix(c.LocalMatrix)}");
                Console.WriteLine($"    {"world",-34} {FormatMatrix(c.WorldMatrix)}");
                foreach (var (name, matrices) in sources)
                    if (i < matrices.Length)
                        Console.WriteLine($"    {name,-34} {FormatMatrix(matrices[i])}");
            }
            foreach (var (name, matrices) in sources)
            {
                int n = Math.Min(matrices.Length, controllers.Count);
                int local = 0, world = 0;
                var firstMiss = new List<string>();
                for (int i = 0; i < n; i++)
                {
                    float dl = AktMath.MaxDifference(matrices[i], controllers[i].LocalMatrix);
                    float dw = AktMath.MaxDifference(matrices[i], controllers[i].WorldMatrix);
                    if (dl < tolerance) local++;
                    if (dw < tolerance) world++;
                    if (dl >= tolerance && dw >= tolerance && firstMiss.Count < 3)
                        firstMiss.Add($"#{i} {controllers[i].ClassName} '{controllers[i].Name}' (local diff {dl:0.###}, world diff {dw:0.###})");
                }
                Console.WriteLine($"  {name}: {matrices.Length} matrices, = local {local}/{n}, = world {world}/{n}" +
                                  (firstMiss.Count > 0 ? "\n      e.g. " + string.Join("\n           ", firstMiss) : ""));
            }
        }
        return 0;
    }

    // ---- fbx ------------------------------------------------------------------------------

    private static int FbxTree(string[] args)
    {
        var doc = SloppyAnimatsu.Core.Fbx.FbxDocument.Load(args[0]);
        int maxDepth = args.Length > 1 ? int.Parse(args[1]) : int.MaxValue;
        string? filter = args.Length > 2 ? args[2] : null;
        Console.WriteLine($"FBX {doc.Version}");

        static string Prop(object p) => p switch
        {
            string s => $"\"{s.Replace("\0\u0001", "::")}\"",
            byte[] b => $"<{b.Length} bytes>",
            Array a => $"{a.GetType().GetElementType()!.Name}[{a.Length}] {{{string.Join(", ", a.Cast<object>().Take(6).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)))}{(a.Length > 6 ? ", ..." : "")}}}",
            _ => Convert.ToString(p, CultureInfo.InvariantCulture) ?? "?",
        };

        void Walk(SloppyAnimatsu.Core.Fbx.FbxNode n, int depth)
        {
            if (depth == 1 && filter != null && !(n.PropertyCount > 1 && n[1] is string s && s.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                return;
            string more = depth == maxDepth && n.Children.Count > 0 ? $"  (+{n.Children.Count} children)" : "";
            Console.WriteLine($"{new string(' ', depth * 2)}{n.Name}: {string.Join(", ", n.Properties.Select(Prop))}{more}");
            if (depth < maxDepth)
                foreach (var c in n.Children) Walk(c, depth + 1);
        }
        foreach (var n in doc.Nodes) Walk(n, 0);
        return 0;
    }

    // ---- animinfo -------------------------------------------------------------------------

    /// <summary>Per stack of the named animations: flags, keys, tracks, tangent modes and space-switched keys.</summary>
    private static int AnimInfo(string[] args)
    {
        var model = SceneModel.Load(args[0]);
        var wanted = args[1..].ToHashSet();
        foreach (var ch in model.Scene.Characters)
            foreach (var anim in ch.Animations.Where(a => wanted.Contains(a.Name ?? "")))
            {
                Console.WriteLine($"{ch.Name}: anim '{string.Join("/", anim.FolderPath.Append(anim.Name ?? ""))}', {anim.Stacks.Count} stack(s)");
                foreach (var s in anim.Stacks)
                {
                    var keys = s.Keys;
                    var tracks = s.Tracks.ToList();
                    Console.WriteLine($"  stack '{s.Name}' flags {s.Flags} opacity {s.Opacity} keys [{string.Join(" ", keys.Select(k =>
                        $"{k.Time}{(k.Flags != 0 ? $"f{k.Flags}" : "")}{(k.FadeIn != 0 ? $"i{k.FadeIn}" : "")}{(k.FadeOut != 0 ? $"o{k.FadeOut}" : "")}"))}], " +
                                      $"{tracks.Count} tracks" + (tracks.Count <= 6 ? $" ({string.Join(", ", tracks.Select(t => $"{t.Target?.Name} {t.Channels}"))})" : ""));
                    var modes = tracks.SelectMany(t => t.TangentModes.SelectMany(m => m)).GroupBy(m => m.Raw).OrderByDescending(g => g.Count());
                    Console.WriteLine($"    tangent modes: {string.Join(", ", modes.Select(g => $"0x{g.Key:X3}×{g.Count()}"))}");
                    // Tracks whose modes are not all 0x100, with the key indices and modes.
                    foreach (var t in tracks)
                    {
                        var odd = t.TangentModes.Select((m, i) => (m, i)).Where(x => x.m.Any(c => c.Raw != 0x100)).ToList();
                        if (odd.Count > 0)
                            Console.WriteLine($"    {t.Target?.Name}: " + string.Join(" ", odd.Take(12).Select(x =>
                                $"@{keys.ElementAtOrDefault(x.i).Time}={string.Join(",", x.m.Select(c => c.Raw.ToString("X")).Distinct())}")) +
                                (odd.Count > 12 ? " ..." : ""));
                    }
                    foreach (var t in tracks)
                    {
                        var spaces = t.Keys.Select((k, i) => (k, i)).Where(x => x.k is TransformKey tk && tk.Space != null).ToList();
                        if (spaces.Count > 0)
                            Console.WriteLine($"    {t.Target?.Name} space: " + string.Join(" ", spaces.Select(x => $"@{keys.ElementAtOrDefault(x.i).Time}={((TransformKey)x.k!).Space!.Name}")));
                    }
                }
            }
        return 0;
    }

    // ---- fbxexport -----------------------------------------------------------------------

    /// <summary>
    /// Writes a scene as FBX: models, geometry, skins, materials and either the stacks' own keys
    /// (unbaked) or one key per frame (baked)
    /// </summary>
    private static int FbxExport(string[] args)
    {
        var model = SceneModel.Load(args[0]);
        string outPath = args[1];
        var options = args[2..].Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var animationNames = options.Where(o => o.StartsWith("--animation=", StringComparison.Ordinal))
            .Select(o => o["--animation=".Length..]).ToHashSet();
        var characterNames = options.Where(o => o.StartsWith("--character=", StringComparison.Ordinal))
            .Select(o => o["--character=".Length..]).ToHashSet();
        string? textures = options.FirstOrDefault(o => o.StartsWith("--textures=", StringComparison.Ordinal))?["--textures=".Length..];
        string? presetName = options.FirstOrDefault(o => o.StartsWith("--preset=", StringComparison.Ordinal))?["--preset=".Length..];
        var preset = SloppyAnimatsu.Core.Fbx.FbxExportPreset.Plain;
        if (presetName != null && !Enum.TryParse(presetName, true, out preset))
        {
            Console.Error.WriteLine($"unknown preset '{presetName}' (plain, unity, godot, blender)");
            return 2;
        }
        bool? Flag(string on, string off) => options.Contains(on) ? true : options.Contains(off) ? false : null;
        var defaults = new SloppyAnimatsu.Core.Fbx.FbxExportOptions { Preset = preset };
        bool baked = Flag("--baked", "--unbaked") ?? defaults.Baked;
        bool plain = Flag("--plain", "--no-plain") ?? defaults.PlainHierarchy;
        bool linear = Flag("--linear", "--step") ?? defaults.LinearBakedKeys;
        string? minScaleText = options.FirstOrDefault(o => o.StartsWith("--min-scale=", StringComparison.Ordinal))?["--min-scale=".Length..];
        float minScale = minScaleText != null
            ? float.Parse(minScaleText, System.Globalization.CultureInfo.InvariantCulture)
            : defaults.MinimumScale;

        var characters = characterNames.Count > 0
            ? model.Scene.Characters.Where(c => characterNames.Contains(c.Name ?? "")).ToList()
            : model.Scene.Characters.ToList();
        var animations = characters.SelectMany(c => c.Animations)
            .Where(a => animationNames.Count == 0 || animationNames.Contains(a.Name ?? ""))
            .ToList();

        var watch = Stopwatch.StartNew();
        SloppyAnimatsu.Core.Fbx.FbxExporter.Export(model, outPath, new SloppyAnimatsu.Core.Fbx.FbxExportOptions
        {
            Preset = preset,
            Baked = baked,
            PlainHierarchy = plain,
            LinearBakedKeys = linear,
            MinimumScale = minScale,
            Characters = characters,
            Animations = animations,
            TextureDirectory = textures,
            DocumentPath = outPath,
            Timestamp = DateTime.Now,
        });
        watch.Stop();
        Console.WriteLine($"{outPath}: {new FileInfo(outPath).Length:N0} bytes, {characters.Count} characters, " +
                          $"{animations.Count} animations ({(baked ? "baked" : "unbaked")}{(baked && plain ? ", plain hierarchy" : "")}{(baked && linear ? ", linear keys" : "")}, preset {preset}), {watch.ElapsedMilliseconds} ms");
        return 0;
    }

    // ---- posedump ------------------------------------------------------------------------

    /// <summary>
    /// Writes the evaluated world position of every joint, per animation and sample time, as JSON:
    /// the reference that an engine's import of an FBX export is measured against
    /// </summary>
    private static int PoseDump(string[] args)
    {
        if (args.Length < 2) return Fail("posedump <file.slopa> <out.json> [--animation=name]... [--character=name]... [--step=1]");
        var model = SceneModel.Load(args[0]);
        var options = args[2..];
        var names = options.Where(o => o.StartsWith("--animation=", StringComparison.Ordinal))
            .Select(o => o["--animation=".Length..]).ToHashSet();
        var characterNames = options.Where(o => o.StartsWith("--character=", StringComparison.Ordinal))
            .Select(o => o["--character=".Length..]).ToHashSet();
        float step = float.Parse(options.FirstOrDefault(o => o.StartsWith("--step=", StringComparison.Ordinal))?["--step=".Length..] ?? "1",
            System.Globalization.CultureInfo.InvariantCulture);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new StringBuilder("{");
        bool firstAnim = true;
        // The names an engine-preset export of the same characters gives the stacks.
        var unique = SloppyAnimatsu.Core.Fbx.FbxExporter.UniqueNames(model.Scene.Characters
            .Where(c => characterNames.Count == 0 || characterNames.Contains(c.Name ?? ""))
            .SelectMany(c => c.Animations).Where(a => a.Stacks.Count > 0));
        foreach (var character in model.Scene.Characters)
        {
            if (characterNames.Count > 0 && !characterNames.Contains(character.Name ?? "")) continue;
            var evaluator = new PoseEvaluator(character);
            var joints = character.Joints.Select(j => (j, evaluator.IndexOf(j))).Where(p => p.Item2 >= 0).ToList();
            foreach (var animation in character.Animations)
            {
                if (animation.Stacks.Count == 0) continue;
                if (names.Count > 0 && !names.Contains(animation.Name ?? "") && !names.Contains(unique[animation])) continue;
                int stop = SloppyAnimatsu.Core.Fbx.FbxExporter.StopOf(animation);
                if (!firstAnim) sb.Append(',');
                firstAnim = false;
                sb.Append('"').Append(JsonEscape(unique[animation])).Append("\":{\"fps\":").Append(model.Scene.FrameRate)
                  .Append(",\"stop\":").Append(stop).Append(",\"times\":[");
                var times = new List<float>();
                for (float t = 0; t <= stop + 1e-4f; t += step) times.Add(t);
                sb.Append(string.Join(",", times.Select(t => t.ToString("R", inv)))).Append("],\"joints\":{");
                var worlds = times.Select(t => evaluator.WorldMatrices(animation, t)).ToList();
                for (int j = 0; j < joints.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append('"').Append(JsonEscape(joints[j].j.Name ?? "")).Append("\":[");
                    sb.Append(string.Join(",", worlds.Select(w =>
                    {
                        var p = w[joints[j].Item2].Translation;
                        return $"[{p.X.ToString("R", inv)},{p.Y.ToString("R", inv)},{p.Z.ToString("R", inv)}]";
                    })));
                    sb.Append(']');
                }
                sb.Append("}}");
            }
        }
        sb.Append('}');
        File.WriteAllText(args[1], sb.ToString());
        Console.WriteLine($"{args[1]}: {new FileInfo(args[1]).Length:N0} bytes");
        return 0;

        static string JsonEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>Reads a binary FBX and writes it back through FbxWriter (a writer bisect tool).</summary>
    private static int FbxRewrite(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: fbxrewrite <in.fbx> <out.fbx>"); return 2; }
        var doc = SloppyAnimatsu.Core.Fbx.FbxDocument.Load(args[0]);
        static SloppyAnimatsu.Core.Fbx.FbxOutNode Copy(SloppyAnimatsu.Core.Fbx.FbxNode n)
        {
            var o = new SloppyAnimatsu.Core.Fbx.FbxOutNode(n.Name);
            o.Properties.AddRange(n.Properties);
            foreach (var c in n.Children) o.Children.Add(Copy(c));
            return o;
        }
        SloppyAnimatsu.Core.Fbx.FbxWriter.Write(args[1], doc.Nodes.Select(Copy).ToList());
        Console.WriteLine($"wrote {args[1]}");
        return 0;
    }

    // ---- fbximport -----------------------------------------------------------------------

    /// <summary>Reads an FBX scene into a new scene and saves it.</summary>
    private static int FbxImport(string[] args)
    {
        if (args.Length < 2)
            return Fail("fbximport <file.fbx> <out.slopa> [--no-animations] [--no-skins] [--no-materials] [--textures=dir]");
        var options = args[2..].Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
        string? textures = options.FirstOrDefault(o => o.StartsWith("--textures=", StringComparison.Ordinal))?["--textures=".Length..];
        var warnings = new List<string>();
        var watch = Stopwatch.StartNew();
        var model = SloppyAnimatsu.Core.Fbx.FbxImporter.Import(args[0], new SloppyAnimatsu.Core.Fbx.FbxImportOptions
        {
            Animations = !options.Contains("--no-animations"),
            Skins = !options.Contains("--no-skins"),
            Materials = !options.Contains("--no-materials"),
            TextureDirectory = textures,
            Warning = warnings.Add,
        });
        watch.Stop();
        model.Save(args[1]);
        var scene = model.Scene;
        Console.WriteLine($"{args[1]}: {new FileInfo(args[1]).Length:N0} bytes, {scene.Characters.Count()} characters, " +
                          $"{scene.Meshes.Count()} meshes, {scene.Materials.Count()} materials, " +
                          $"{scene.Characters.Sum(c => c.Animations.Count())} animations, {watch.ElapsedMilliseconds} ms");
        foreach (string warning in warnings) Console.WriteLine($"warning: {warning}");
        return 0;
    }

    // ---- fbxcheck ------------------------------------------------------------------------

    /// <summary>Compares two FBX files structurally, ignoring ids, time stamps and file layout.</summary>
    private static int FbxCheck(string[] args)
    {
        var options = args[2..].Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
        float tolerance = 1e-3f;
        double timeTolerance = 0.01;
        int max = 40;
        foreach (var option in options)
        {
            if (option.StartsWith("--tolerance=", StringComparison.Ordinal)) tolerance = float.Parse(option["--tolerance=".Length..], CultureInfo.InvariantCulture);
            if (option.StartsWith("--time-tolerance=", StringComparison.Ordinal)) timeTolerance = double.Parse(option["--time-tolerance=".Length..], CultureInfo.InvariantCulture);
            if (option.StartsWith("--max=", StringComparison.Ordinal)) max = int.Parse(option["--max=".Length..], CultureInfo.InvariantCulture);
        }
        var comparison = SloppyAnimatsu.Core.Fbx.FbxCompare.CompareFiles(args[0], args[1],
            new SloppyAnimatsu.Core.Fbx.FbxCompareOptions { ValueTolerance = tolerance, TimeToleranceFrames = timeTolerance, MaxReported = max });
        Console.WriteLine(comparison.ToString());
        return comparison.IsMatch ? 0 : 1;
    }

    // ---- aknmexport ----------------------------------------------------------------------

    /// <summary>
    /// Writes the character's rig and its selected animations as an `.aknm`
    /// The default selection is the current animation; the rig
    /// animation `__RigAnim` is always included.
    /// </summary>
    private static int AknmExportCommand(string[] args)
    {
        if (args.Length < 2)
            return Fail("aknmexport <file.slopa> <out.aknm> [--character=name] [--animation=name]... [--all-animations]");
        var model = SceneModel.Load(args[0]);
        var options = args[2..].Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
        string? characterName = options.FirstOrDefault(o => o.StartsWith("--character=", StringComparison.Ordinal))?["--character=".Length..];
        var character = FindCharacter(model, characterName);
        if (character == null) return Fail(characterName == null ? "The scene has no character." : $"No character '{characterName}'.");

        var names = options.Where(o => o.StartsWith("--animation=", StringComparison.Ordinal))
            .Select(o => o["--animation=".Length..]).ToHashSet(StringComparer.Ordinal);
        var animations = new List<Animation>();
        if (options.Contains("--all-animations") || names.Contains("*"))
            animations = character.Animations.ToList();
        else if (names.Count > 0)
        {
            foreach (string name in names)
            {
                var animation = character.Animations.FirstOrDefault(a => a.Name == name);
                if (animation == null) return Fail($"The character has no animation '{name}'.");
                animations.Add(animation);
            }
        }
        else if (character.CurrentAnimation is { } current)
            animations.Add(current);

        var document = AknmExport.Build(model, character, animations);
        document.Save(args[1]);
        var kept = document.Root.PreOrder()
            .Where(o => AknmFormat.IsA(document, o, AknmFormat.AnimationClass))
            .Select(o => o.Name ?? "?");
        Console.WriteLine($"{args[1]}: {new FileInfo(args[1]).Length:N0} bytes, root {document.Root.TypeName}, " +
                          $"{document.AllObjects().Count()} objects, animations: {string.Join(", ", kept)}");
        return 0;
    }

    // ---- aknmimport ----------------------------------------------------------------------

    /// <summary>Reads an `.aknm` and copies its animations onto a character of the scene.</summary>
    private static int AknmImportCommand(string[] args)
    {
        if (args.Length < 3)
            return Fail("aknmimport <scene.slopa> <file.aknm> <out.slopa> [--character=name] [--root] [--force]");
        var model = SceneModel.Load(args[0]);
        var file = AknmFile.Read(args[1]);
        var options = args[3..].Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
        string? characterName = options.FirstOrDefault(o => o.StartsWith("--character=", StringComparison.Ordinal))?["--character=".Length..];
        var character = FindCharacter(model, characterName);
        if (character == null) return Fail(characterName == null ? "The scene has no character." : $"No character '{characterName}'.");

        var warnings = new List<string>();
        AknmImportResult result;
        try
        {
            result = AknmImport.Import(model, file, new AknmImportOptions
            {
                Character = character,
                RetargetFolder = !options.Contains("--root"),
                AllowDifferentPose = options.Contains("--force"),
                Warning = warnings.Add,
            });
        }
        catch (InvalidOperationException e)
        {
            return Fail(e.Message);
        }
        model.Save(args[2]);
        Console.WriteLine($"{args[2]}: {new FileInfo(args[2]).Length:N0} bytes, {result.Animations.Count} animations " +
                          $"from '{file.Name}' into '{result.Character.Name}' under '{result.Destination.Name}', " +
                          $"{result.DroppedTracks} tracks dropped, {result.UnmatchedControllers} controllers unmatched");
        foreach (string warning in warnings) Console.WriteLine($"warning: {warning}");
        return 0;
    }

    // ---- aktimport -----------------------------------------------------------------------

    /// <summary>
    /// Merges another `.akt` scene into the scene: the source's
    /// characters (with their whole Anim Bank), materials and textures are added; its cameras and
    /// blendshape sights are left out. The imported source must be the running version unless
    /// <c>--force</c>, as the app refuses files with a different version number.
    /// </summary>
    private static int AktImportCommand(string[] args)
    {
        if (args.Length < 3)
            return Fail("aktimport <scene.slopa> <file.slopa> <out.slopa> [--force]");
        var model = SceneModel.Load(args[0]);
        var options = args[3..].Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var warnings = new List<string>();
        AktImportResult result;
        try
        {
            result = AktImport.Import(model, AktDocument.Load(args[1]), new AktImportOptions
            {
                AllowDifferentVersion = options.Contains("--force"),
                Warning = warnings.Add,
            }, args[1]);
        }
        catch (InvalidOperationException e)
        {
            return Fail(e.Message);
        }
        model.Save(args[2]);
        Console.WriteLine($"{args[2]}: {new FileInfo(args[2]).Length:N0} bytes, imported {result.Objects.Count} objects: " +
                          $"{result.Characters.Count} characters " +
                          $"({string.Join(", ", result.Characters.Select(c => $"'{c.Name}'"))}), " +
                          $"{result.Animations.Count} animations, {result.Materials.Count} materials, " +
                          $"{result.Textures.Count} textures; skipped {result.SkippedCameras} cameras, " +
                          $"{result.SkippedBlendShapeSights} blendshape sights");
        foreach (string warning in warnings) Console.WriteLine($"warning: {warning}");
        return 0;
    }

    // ---- retarget ------------------------------------------------------------------------

    /// <summary>
    /// Copies one character's animations onto another character of the same scene, converting the
    /// keys: local changes from the source's reference pose applied
    /// to the target's, with translations scaled by the reference-pose ratio.
    /// </summary>
    private static int RetargetCommand(string[] args)
    {
        if (args.Length < 4)
            return Fail("retarget <scene.slopa> <source character> <target character> <out.slopa> " +
                        "[--animation=name|dir/name]... [--root] [--match=source=target]... " +
                        "[--method=auto|reference|local|world]");
        var model = SceneModel.Load(args[0]);
        var source = model.Scene.Characters.FirstOrDefault(c => c.Name == args[1]);
        if (source == null) return Fail($"No character '{args[1]}'.");
        var target = model.Scene.Characters.FirstOrDefault(c => c.Name == args[2]);
        if (target == null) return Fail($"No character '{args[2]}'.");

        var options = args[4..].Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
        IReadOnlyList<Animation>? animations = null;
        var wanted = options.Where(o => o.StartsWith("--animation=", StringComparison.Ordinal)).ToList();
        if (wanted.Count > 0)
        {
            var selected = new List<Animation>();
            foreach (string option in wanted)
            {
                string name = option["--animation=".Length..];
                // A bank folder path ("Common/Idle") selects one animation; a bare name the first
                // of that name, as in the other commands.
                var animation = source.Animations.FirstOrDefault(a => AnimationPath(a) == name)
                                ?? source.Animations.FirstOrDefault(a => a.Name == name);
                if (animation == null)
                    return Fail($"'{source.Name}' has no animation '{name}'.");
                selected.Add(animation);
            }
            animations = selected;
        }

        var pairs = new List<RetargetingPair>();
        foreach (string option in options.Where(o => o.StartsWith("--match=", StringComparison.Ordinal)))
        {
            string spec = option["--match=".Length..];
            int split = spec.IndexOf('=');
            if (split <= 0 || split == spec.Length - 1)
                return Fail($"--match needs source=target, not '{spec}'.");
            string sourceName = spec[..split], targetName = spec[(split + 1)..];
            var from = RetargetingMap.Candidates(source).FirstOrDefault(c => c.Name == sourceName);
            var to = RetargetingMap.Candidates(target).FirstOrDefault(c => c.Name == targetName);
            if (from == null) return Fail($"'{sourceName}' is not a retargetable controller of '{source.Name}'.");
            if (to == null) return Fail($"'{targetName}' is not a retargetable controller of '{target.Name}'.");
            pairs.Add(new RetargetingPair(from, to));
        }

        var warnings = new List<string>();
        var method = RetargetMethod.Auto;
        foreach (string option in options.Where(o => o.StartsWith("--method=", StringComparison.Ordinal)))
        {
            string value = option["--method=".Length..];
            if (value is not ("auto" or "reference" or "local" or "world"))
                return Fail($"--method needs auto, reference, local or world, not '{value}'.");
            method = value switch
            {
                "auto" => RetargetMethod.Auto,
                "reference" => RetargetMethod.Reference,
                "world" => RetargetMethod.World,
                _ => RetargetMethod.Local,
            };
        }
        string? cacheDirectory = options.Contains("--no-cache") ? null : RetargetingPreferences.DefaultDirectory;
        foreach (string option in options.Where(o => o.StartsWith("--cache=", StringComparison.Ordinal)))
            cacheDirectory = option["--cache=".Length..];
        RetargetingResult result;
        try
        {
            result = Retargeting.Transfer(model, source, target, new RetargetingOptions
            {
                Animations = animations,
                RetargetFolder = !options.Contains("--root"),
                Pairs = pairs,
                Method = method,
                CacheDirectory = cacheDirectory,
                Warning = warnings.Add,
            });
        }
        catch (InvalidOperationException e)
        {
            return Fail(e.Message);
        }
        model.Save(args[3]);
        Console.WriteLine($"{args[3]}: {new FileInfo(args[3]).Length:N0} bytes, retargeted " +
                          $"{result.Animations.Count} animations from '{source.Name}' onto '{target.Name}' " +
                          $"through {result.Map.Pairs.Count} controller pairs (ratio {result.Ratio:0.####}), " +
                          $"{result.CopiedStacks} stacks into '{result.Destination.Name}'");
        foreach (string warning in warnings) Console.WriteLine($"warning: {warning}");
        return 0;
    }

    /// <summary>The animation's path below the Anim Bank root, <c>folder/.../name</c>.</summary>
    private static string AnimationPath(Animation animation) =>
        string.Join("/", animation.FolderPath.Append(animation.Name ?? ""));

    /// <summary>The named character, else the scene's current one, else the first.</summary>
    private static Character? FindCharacter(SceneModel model, string? name)
    {
        if (name != null) return model.Scene.Characters.FirstOrDefault(c => c.Name == name);
        return model.Scene.CurrentCharacter ?? model.Scene.Characters.FirstOrDefault();
    }

    // ---- joint editing ---------------------------------------------------------------------

    /// <summary>
    /// Skeleton edits: create, insert, place, delete, parent,
    /// unparent, orient and the bind-pose LTM sync. Every edit is saved to the output file.
    /// </summary>
    private static int JointCommand(string[] args)
    {
        if (args.Length < 3)
            return Fail("joint <scene.slopa> <out.slopa> <create|place|insert|delete|parent|unparent|orient|add-ik|add-reverse-foot|ltm> ... " +
                        "[--aim=x|z|y] [--up=x|z|y] [--aim-negative] [--up-negative] [--align=on|off]");
        var model = SceneModel.Load(args[0]);
        string output = args[1];
        string action = args[2];
        var options = args[3..].ToList();
        string? Option(string name)
        {
            string prefix = "--" + name + "=";
            return options.FirstOrDefault(o => o.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        }
        List<string> Repeated(string name)
        {
            string prefix = "--" + name + "=";
            return options.Where(o => o.StartsWith(prefix, StringComparison.Ordinal))
                .Select(o => o[prefix.Length..]).ToList();
        }

        var character = FindCharacter(model, Option("character"));
        if (character == null) return Fail("The scene has no character to edit.");
        var editor = new RigEditor(character);
        var alignment = JointAlignmentOption(options);
        string describe;
        try
        {
            switch (action)
            {
                case "create":
                {
                    var parent = JointOrCharacter(character, Option("parent"));
                    var point = ParsePoint(Option("at"))
                                ?? editor.WorldInPose(editor.RigPose, parent ?? character).Translation;
                    var joint = editor.CreateJointAt(parent, point, Option("name"));
                    describe = $"created '{joint.Name}' under '{joint.Parent?.Name ?? character.Name}'";
                    break;
                }
                case "place":
                {
                    // Create Joint's interactive placement, selected object and click included
                    var selected = Option("selected") is { } selectedName ? FindJoint(character, selectedName) : null;
                    var point = ParsePoint(Option("at"))
                                ?? editor.WorldInPose(editor.RigPose, (Object3D?)selected ?? character).Translation;
                    var joint = editor.PlaceJoint(selected, point, Option("name"), alignment);
                    describe = $"placed '{joint.Name}' under '{joint.Parent?.Name ?? character.Name}'";
                    break;
                }
                case "insert":
                {
                    var target = FindJoint(character, Option("joint"));
                    var joint = editor.InsertJoint(target, Option("name"), ParsePoint(Option("at")));
                    describe = $"inserted '{joint.Name}' above '{target.Name}'";
                    break;
                }
                case "delete":
                {
                    int count = editor.DeleteJoints(FindJoints(character, Repeated("joint")));
                    describe = $"deleted {count} joints";
                    break;
                }
                case "parent":
                {
                    var parent = FindJoint(character, Option("parent"));
                    int count = editor.Parent(FindJoints(character, Repeated("joint")), parent);
                    describe = $"parented {count} objects to '{parent.Name}'";
                    break;
                }
                case "unparent":
                {
                    int count = editor.Unparent(FindJoints(character, Repeated("joint")));
                    describe = $"unparented {count} objects";
                    break;
                }
                case "orient":
                {
                    int count = editor.OrientJoints(FindJoints(character, Repeated("joint")), alignment);
                    describe = $"oriented {count} joints" +
                               (alignment is { } a ? $" (aim {JointAlignment.NameOf(a.Aim)}, up {JointAlignment.NameOf(a.Up)})" : "");
                    break;
                }
                case "add-ik":
                {
                    var upper = FindJoint(character, Option("joint"));
                    var lower = FindJoint(character, Option("to"));
                    var controller = editor.AddIK(upper, lower, Option("name"));
                    describe = $"added '{controller.Name}' over {string.Join(" > ", controller.Chain.Select(j => j.Name))}";
                    break;
                }
                case "add-reverse-foot":
                {
                    var upper = FindJoint(character, Option("joint"));
                    var lower = FindJoint(character, Option("to"));
                    var controller = editor.AddReverseFoot(upper, lower, Option("name"));
                    describe = $"added '{controller.Name}' over {string.Join(" > ", controller.Chain.Select(j => j.Name))} " +
                               $"with {controller.Shoe.Count} shoe locators";
                    break;
                }
                case "ltm":
                {
                    var names = Repeated("joint");
                    if (names.Count == 0)
                    {
                        editor.SyncLtm();
                        describe = $"synced {character.Controllers.Count} LTMs";
                    }
                    else
                    {
                        var joints = FindJoints(character, names);
                        editor.SyncLtm(joints);
                        describe = $"synced {joints.Count} LTMs";
                    }
                    break;
                }
                default:
                    return Fail($"Unknown joint action '{action}' (create, place, insert, delete, parent, unparent, orient, add-ik, add-reverse-foot, ltm).");
            }
        }
        catch (InvalidOperationException e)
        {
            return Fail(e.Message);
        }
        model.Save(output);
        int tracks = character.RigAnimation?.Stacks.FirstOrDefault()?.Tracks.Count() ?? 0;
        Console.WriteLine($"{output}: {describe}; {character.Joints.Count} joints, " +
                          $"{character.Controllers.Count} controllers, {editor.Poses().Count} poses, {tracks} rig tracks");
        return 0;
    }

    // ---- rest pose ------------------------------------------------------------------------

    /// <summary>
    /// Edits a character's reference pose and answers the REFERENCE POSE dialog
    /// writes a joint's local transform in <c>RefPose</c>,
    /// composed with the current one, and with <c>--convert</c>, <c>--keep</c> or <c>--return</c>
    /// resolves the animations against it. With no answer the saved scene keeps the pending state
    /// (<c>RefPose</c> differs from <c>RefPoseOld</c>), which is what the dialog reads.
    /// </summary>
    private static int RestPoseCommand(string[] args)
    {
        if (args.Length < 3)
            return Fail("restpose <scene.slopa> <out.slopa> <joint> [--character=name] [--translate=x,y,z] " +
                        "[--rotate=x,y,z] [--scale=x,y,z] [--convert|--keep|--return]");
        var model = SceneModel.Load(args[0]);
        string output = args[1];
        string jointName = args[2];
        var options = args[3..].ToList();
        string? Option(string name)
        {
            string prefix = "--" + name + "=";
            return options.FirstOrDefault(o => o.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        }
        if (options.Count(o => o is "--convert" or "--keep" or "--return") > 1)
            return Fail("Give at most one of --convert, --keep and --return.");
        if (Option("translate") is null && Option("rotate") is null && Option("scale") is null)
            return Fail("Give --translate=, --rotate= or --scale=.");

        var character = FindCharacter(model, Option("character"));
        if (character == null) return Fail("The scene has no character to edit.");
        var joint = FindJoint(character, jointName);
        var editor = new RestPoseEditor(character);
        string describe;
        try
        {
            if (!editor.CanEdit(joint, out var reason)) return Fail(reason ?? $"{joint.Name} cannot be edited in the rest pose.");
            var current = editor.GetLocal(joint);
            var delta = new Transform(
                ParsePoint(Option("translate")) ?? Vector3.Zero,
                Degrees(ParsePoint(Option("rotate"))),
                ParsePoint(Option("scale")) ?? Vector3.One);
            editor.SetLocal(joint, PoseEvaluator.Compose(current, delta));
            int pending = editor.ChangedSlots.Count;
            var resolution = options.Contains("--convert") ? RestPoseResolution.ConvertAnimations
                : options.Contains("--keep") ? RestPoseResolution.KeepAnimations
                : options.Contains("--return") ? RestPoseResolution.ReturnToPrevious
                : (RestPoseResolution?)null;
            if (resolution is { } r)
            {
                int converted = editor.Resolve(r);
                describe = r switch
                {
                    RestPoseResolution.ConvertAnimations => $"converted {converted} key(s)",
                    RestPoseResolution.KeepAnimations => $"kept the keys (changed {pending} slot(s))",
                    _ => "returned to the previous rest pose",
                };
            }
            else
            {
                describe = $"pending on {pending} slot(s) ({editor.ChangedSlots.Count} still differ)";
            }
        }
        catch (InvalidOperationException e)
        {
            return Fail(e.Message);
        }
        model.Save(output);
        Console.WriteLine($"{output}: {character.Name} '{joint.Name}' {describe}; {character.Animations.Count()} animations");
        return 0;
    }

    /// <summary>Degrees about X, Y and Z (in that order, in the joint's own frame) as a quaternion.</summary>
    private static Quaternion Degrees(Vector3? degrees)
    {
        if (degrees is not { } d) return Quaternion.Identity;
        var r = d * (MathF.PI / 180f);
        return Quaternion.CreateFromYawPitchRoll(r.Y, r.X, r.Z);
    }

    /// <summary>
    /// The joint-axis options (<c>--aim</c>, <c>--up</c>, their <c>-negative</c> flags and
    /// <c>--align</c>), or null when none were given so the editor's shipped default applies.
    /// </summary>
    private static JointAlignment? JointAlignmentOption(List<string> options)
    {
        string? Value(string name)
        {
            string prefix = "--" + name + "=";
            return options.FirstOrDefault(o => o.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        }
        bool Flag(string name) => options.Contains("--" + name);
        JointAxis? ParseAxis(string? text) => text?.ToLowerInvariant() switch
        {
            "x" => JointAxis.X,
            "z" => JointAxis.Z,
            "y" => JointAxis.Y,
            _ => null,
        };
        var aim = ParseAxis(Value("aim"));
        var up = ParseAxis(Value("up"));
        string? alignText = Value("align");
        if (aim == null && up == null && alignText == null && !Flag("aim-negative") && !Flag("up-negative") &&
            !Flag("no-align"))
            return null;
        bool align = alignText?.ToLowerInvariant() switch
        {
            "on" or "true" or "1" => true,
            "off" or "false" or "0" => false,
            null => !Flag("no-align") && JointAlignment.Default.AlignOnCreate,
            _ => JointAlignment.Default.AlignOnCreate,
        };
        return new JointAlignment(aim ?? JointAlignment.Default.Aim, up ?? JointAlignment.Default.Up,
            Flag("aim-negative"), Flag("up-negative"), align);
    }

    // ---- skin binding ---------------------------------------------------------------------

    /// <summary>
    /// Bind and Unbind Skin: Bind captures the
    /// character's reference pose as its bind pose and attaches the free meshes; Unbind detaches
    /// every skinned mesh of the scene to the scene root, keeping its world.
    /// </summary>
    private static int SkinCommand(string[] args)
    {
        if (args.Length < 3)
            return Fail("skin <scene.slopa> <out.slopa> <bind|unbind> [--character=name] [--mesh=name...]");
        var model = SceneModel.Load(args[0]);
        string output = args[1];
        string action = args[2];
        var options = args[3..].ToList();
        string? Option(string name)
        {
            string prefix = "--" + name + "=";
            return options.FirstOrDefault(o => o.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        }
        List<string> Repeated(string name)
        {
            string prefix = "--" + name + "=";
            return options.Where(o => o.StartsWith(prefix, StringComparison.Ordinal))
                .Select(o => o[prefix.Length..]).ToList();
        }

        string describe;
        try
        {
            switch (action)
            {
                case "bind":
                {
                    var character = FindCharacter(model, Option("character"))
                                    ?? throw new InvalidOperationException("The scene has no character to bind to.");
                    var editor = new SkinEditor(character);
                    var names = Repeated("mesh");
                    var meshes = names.Count == 0 ? null : SkinEditor.Named(model.Scene, names);
                    int count = editor.Bind(meshes);
                    describe = $"bound {count} meshes to '{character.Name}' " +
                               $"({character.BindPoses.Count()} bind poses)";
                    break;
                }
                case "unbind":
                {
                    int count = SkinEditor.Unbind(model.Scene);
                    describe = $"unbound {count} meshes to the scene";
                    break;
                }
                default:
                    return Fail($"Unknown skin action '{action}' (bind, unbind).");
            }
        }
        catch (InvalidOperationException e)
        {
            return Fail(e.Message);
        }
        model.Save(output);
        Console.WriteLine($"{output}: {describe}");
        return 0;
    }

    // ---- skin workshop --------------------------------------------------------------------

    /// <summary>
    /// The Skin Workshop: the Add/Aim/Smooth brush, Set and
    /// Spinner weight, Prune, Fix, the Flooder and Mirror Skin. The brush works in world space; the
    /// smooth weight array is edited unless <c>--rigid</c> asks for the rigging one.
    /// </summary>
    private static int SkinPaintCommand(string[] args)
    {
        if (args.Length < 3)
            return Fail("skinpaint <scene.slopa> <out.slopa> <paint|set|add|prune|fix|flood|mirror|copy-paste|hide|show|scan> [options]");
        var model = SceneModel.Load(args[0]);
        string output = args[1];
        string action = args[2];
        var options = args[3..].ToList();

        string? Option(string name)
        {
            string prefix = "--" + name + "=";
            return options.FirstOrDefault(o => o.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        }
        List<string> Repeated(string name)
        {
            string prefix = "--" + name + "=";
            return options.Where(o => o.StartsWith(prefix, StringComparison.Ordinal))
                .Select(o => o[prefix.Length..]).ToList();
        }
        bool Flag(string name) => options.Contains("--" + name, StringComparer.Ordinal);
        float Number(string name, float fallback) =>
            Option(name) is { } text ? float.Parse(text, CultureInfo.InvariantCulture) : fallback;
        Vector3 PointAt(string text)
        {
            var parts = text.Split(',');
            if (parts.Length != 3) throw new InvalidOperationException($"'{text}' is not x,y,z.");
            return new Vector3(float.Parse(parts[0], CultureInfo.InvariantCulture),
                float.Parse(parts[1], CultureInfo.InvariantCulture),
                float.Parse(parts[2], CultureInfo.InvariantCulture));
        }
        int[] VerticesOf()
        {
            string text = Option("vertex") ?? throw new InvalidOperationException("--vertex=i[,i,...] is required.");
            return text.Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        }

        Character CharacterOf() =>
            FindCharacter(model, Option("character"))
            ?? throw new InvalidOperationException("The scene has no character to paint.");

        Mesh MeshNamed(string name)
        {
            var matches = model.Scene.Meshes.Where(m => m.Name == name).ToList();
            if (matches.Count == 0) throw new InvalidOperationException($"The scene has no mesh '{name}'.");
            if (matches.Count > 1) throw new InvalidOperationException($"The scene has {matches.Count} meshes named '{name}'.");
            return matches[0];
        }

        Mesh MeshOf() =>
            MeshNamed(Option("mesh") ?? throw new InvalidOperationException("--mesh=name is required."));

        int JointOf(Character character)
        {
            string name = Option("joint") ?? throw new InvalidOperationException("--joint=name is required.");
            for (int i = 0; i < character.Joints.Count; i++)
                if (character.Joints[i].Name == name) return i;
            throw new InvalidOperationException($"'{name}' is not a joint of '{character.Name}'.");
        }

        Animation? AnimationOf(Character character)
        {
            string? name = Option("animation");
            if (name == null) return null;
            return character.Animations.FirstOrDefault(a => a.Name == name || AnimationPath(a) == name)
                   ?? throw new InvalidOperationException($"'{character.Name}' has no animation '{name}'.");
        }

        (Vector3[] Positions, Vector3[] Joints) Frame(Character character, Mesh mesh, bool rigid)
        {
            var animation = AnimationOf(character);
            int frame = ParseInt(Option("frame"), 0);
            var evaluator = new PoseEvaluator(character, riggingContext: rigid);
            var worlds = evaluator.WorldMatrices(animation, frame);
            var positions = new MeshSkin(mesh).Positions(worlds);
            var index = new Dictionary<Object3D, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < character.Controllers.Count; i++) index.TryAdd(character.Controllers[i], i);
            var joints = new Vector3[character.Joints.Count];
            for (int j = 0; j < joints.Length; j++)
                joints[j] = index.TryGetValue(character.Joints[j], out int controller) && controller < worlds.Length
                    ? worlds[controller].Translation
                    : character.Joints[j].WorldMatrix.Translation;
            return (positions, joints);
        }

        try
        {
            string describe;
            switch (action)
            {
                case "paint":
                {
                    var character = CharacterOf();
                    var mesh = MeshOf();
                    int joint = JointOf(character);
                    bool rigid = Flag("rigid");
                    var (positions, jointPositions) = Frame(character, mesh, rigid);
                    var centers = Repeated("at").Select(PointAt).ToList();
                    if (centers.Count == 0)
                    {
                        int vertex = ParseInt(Option("vertex"), -1);
                        if (vertex < 0 || vertex >= positions.Length)
                            throw new InvalidOperationException("Paint needs --at=x,y,z or a valid --vertex=i.");
                        centers.Add(positions[vertex]);
                    }
                    var mode = Option("mode")?.ToLowerInvariant() switch
                    {
                        null or "add" => SkinPaintMode.Add,
                        "aim" => SkinPaintMode.Aim,
                        "smooth" => SkinPaintMode.Smooth,
                        _ => throw new InvalidOperationException($"Unknown paint mode '{Option("mode")}' (add, aim, smooth)."),
                    };
                    var brush = new SkinPaintBrush
                    {
                        Mode = mode,
                        Radius = Number("radius", 1f),
                        Strength = Number("strength", 1f),
                        Value = Number("value", 1f),
                        Hard = !Flag("soft"),
                        Rigid = rigid,
                        FrontBack = !Flag("front-only"),
                    };
                    Vector3? eye = Option("eye") is { } eyeText ? PointAt(eyeText) : null;
                    Vector3[]? normals = null;
                    if (eye != null)
                    {
                        var evaluator = new PoseEvaluator(character, riggingContext: rigid);
                        var worlds = evaluator.WorldMatrices(AnimationOf(character), ParseInt(Option("frame"), 0));
                        normals = new MeshSkin(mesh).Normals(worlds);
                    }
                    var stroke = SkinWorkshop.BeginStroke(mesh, joint, rigid, character);
                    int painted = 0;
                    foreach (var center in centers)
                        painted = Math.Max(painted, stroke.Apply(brush, center, positions, jointPositions, normals, eye));
                    stroke.Commit();
                    describe = $"painted {painted} control points of '{mesh.Name}' for '{character.Joints[joint].Name}' ({mode})";
                    break;
                }
                case "set":
                case "add":
                {
                    var character = CharacterOf();
                    var mesh = MeshOf();
                    int joint = JointOf(character);
                    bool rigid = Flag("rigid");
                    var vertices = VerticesOf();
                    var (positions, jointPositions) = Frame(character, mesh, rigid);
                    float current = SkinWorkshop.Weight(mesh, vertices[0], joint, rigid);
                    float value = action == "set" ? Number("value", current) : Number("delta", 0f);
                    foreach (int vertex in vertices)
                    {
                        if (vertex < 0 || vertex >= positions.Length)
                            throw new InvalidOperationException($"There is no control point {vertex}.");
                        if (action == "set")
                            SkinWorkshop.SetWeight(mesh, vertex, joint, value, rigid, null, jointPositions,
                                positions[vertex], character);
                        else
                            SkinWorkshop.AddWeight(mesh, vertex, joint, value, rigid, null, jointPositions,
                                positions[vertex], character);
                    }
                    float now = SkinWorkshop.Weight(mesh, vertices[0], joint, rigid);
                    describe = $"{action} '{character.Joints[joint].Name}' on {vertices.Length} control point(s): {current:0.###} -> {now:0.###}";
                    break;
                }
                case "prune":
                {
                    var mesh = MeshOf();
                    bool rigid = Flag("rigid");
                    int removed = SkinWorkshop.Prune(mesh, rigid, Number("limit", 0.001f));
                    describe = $"pruned {removed} influence(s) below {Number("limit", 0.001f)} of '{mesh.Name}'";
                    break;
                }
                case "fix":
                {
                    var mesh = MeshOf();
                    bool rigid = Flag("rigid");
                    var selection = SkinSelection.From(mesh, VerticesOf());
                    int changed = SkinWorkshop.Fix(mesh, selection, rigid, ParseInt(Option("passes"), 2));
                    describe = $"fixed {changed} control point(s) of '{mesh.Name}'";
                    break;
                }
                case "flood":
                {
                    var mesh = MeshOf();
                    var selection = SkinSelection.From(mesh, VerticesOf());
                    int count = SkinWorkshop.Flood(mesh, selection);
                    describe = $"flooded the selection of '{mesh.Name}' to {count} control point(s)";
                    break;
                }
                case "mirror":
                {
                    var character = CharacterOf();
                    var meshes = Repeated("mesh").Select(name =>
                    {
                        var matches = model.Scene.Meshes.Where(m => m.Name == name).ToList();
                        if (matches.Count != 1) throw new InvalidOperationException($"Expected one mesh '{name}', found {matches.Count}.");
                        return matches[0];
                    }).ToList();
                    var joints = Repeated("joint").Select(name =>
                        character.Joints.FirstOrDefault(j => j.Name == name)
                        ?? throw new InvalidOperationException($"'{name}' is not a joint of '{character.Name}'.")).ToList();
                    int written = SkinMirror.Mirror(character, joints.Count == 0 ? null : joints,
                        meshes.Count == 0 ? null : meshes, Number("tolerance", 0f));
                    describe = $"mirrored the weights of '{character.Name}' onto {written} control point(s)";
                    break;
                }
                case "hide":
                {
                    var character = CharacterOf();
                    var mesh = MeshOf();
                    int changed = mesh.SetVerticesHidden(VerticesOf(), true);
                    if (changed > 0) character.HiddenVerticesCounter += 1;
                    describe = $"hid {changed} control point(s) of '{mesh.Name}'";
                    break;
                }
                case "show":
                {
                    var character = CharacterOf();
                    var mesh = MeshOf();
                    int changed = mesh.ShowAllVertices();
                    if (changed > 0) character.HiddenVerticesCounter += 1;
                    describe = $"showed {changed} control point(s) of '{mesh.Name}'";
                    break;
                }
                case "scan":
                {
                    var character = CharacterOf();
                    var mesh = MeshOf();
                    bool rigid = Flag("rigid");
                    var (positions, _) = Frame(character, mesh, rigid);
                    string at = Option("at") ?? throw new InvalidOperationException("--at=x,y,z is required.");
                    var session = new SkinPaintSession(character, mesh, rigid) { Radius = Number("radius", 1f) };
                    var hit = session.Scan(PointAt(at), positions, radius: session.Radius);
                    if (hit is not { } scan) throw new InvalidOperationException("No visible control point is within the radius.");
                    describe = $"control point {scan.Vertex} carries {scan.Weights.Count} influence(s): " +
                               string.Join(", ", scan.Weights.Select(w => $"{w.Name} {w.Weight:0.###}"));
                    break;
                }
                case "copy-paste":
                {
                    var character = CharacterOf();
                    bool rigid = Flag("rigid");
                    var source = MeshNamed(Option("from")
                        ?? throw new InvalidOperationException("--from=mesh is required."));
                    var target = MeshNamed(Option("to")
                        ?? throw new InvalidOperationException("--to=mesh is required."));
                    string fromVertices = Option("from-vertex")
                        ?? throw new InvalidOperationException("--from-vertex=i[,i,...] is required (the copied control points).");
                    var sourceSelection = SkinSelection.From(source,
                        fromVertices.Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)));
                    var targetSelection = SkinSelection.From(target, VerticesOf());
                    var sourceTable = new SkinWeightTable(source, rigid, character);
                    var clipboard = SkinWeightClipboard.Copy(sourceTable, sourceSelection, character, out string? refusal);
                    if (clipboard == null) throw new InvalidOperationException(refusal);
                    var targetTable = new SkinWeightTable(target, rigid, character);
                    var result = clipboard.Paste(targetTable, targetSelection, character, out refusal);
                    if (result.Points == 0) throw new InvalidOperationException(refusal ?? "Nothing was pasted.");
                    describe = $"pasted {result.Points} control point(s) from '{source.Name}' ({clipboard.VertexCount} copied) onto '{target.Name}'";
                    if (result.Warnings.Count > 0) describe += "; " + string.Join(" ", result.Warnings);
                    break;
                }
                default:
                    return Fail($"Unknown skinpaint action '{action}' (paint, set, add, prune, fix, flood, mirror, copy-paste, hide, show, scan).");
            }
            model.Save(output);
            Console.WriteLine($"{output}: {describe}");
            return 0;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return Fail(e.Message);
        }
    }

    // ---- auto rigger ----------------------------------------------------------------------

    /// <summary>
    /// The AutoRigger: builds a biped skeleton, its IK and
    /// reverse-foot controllers and skin weights from hot points on an unrigged character.
    /// </summary>
    private static int AutoRigCommand(string[] args)
    {
        if (args.Length < 2)
            return Fail("autorig <scene.slopa> <out.slopa> [--character=name] [--hotpoint=Name:x,y,z]... " +
                        "[--hotpoints=file] [--mesh=name...] [--naming=default|mixamo|preferences] " +
                        "[--spine=1..6] [--fingers=1..10] [--phalanges=1..4] " +
                        "[--no-ik] [--no-reverse-foot] [--no-weights] [--no-bind]");
        var model = SceneModel.Load(args[0]);
        string output = args[1];
        var options = args[2..].ToList();
        string? Option(string name)
        {
            string prefix = "--" + name + "=";
            return options.FirstOrDefault(o => o.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        }
        List<string> Repeated(string name)
        {
            string prefix = "--" + name + "=";
            return options.Where(o => o.StartsWith(prefix, StringComparison.Ordinal))
                .Select(o => o[prefix.Length..]).ToList();
        }

        try
        {
            var character = FindCharacter(model, Option("character"))
                            ?? throw new InvalidOperationException("The scene has no character to rig.");
            var points = new Dictionary<HotPoint, Vector3>();
            foreach (string spec in Repeated("hotpoint")) AddHotPoint(points, spec);
            if (Option("hotpoints") is { } file)
                foreach (string line in File.ReadAllLines(file))
                {
                    string text = line.Trim();
                    if (text.Length == 0 || text.StartsWith('#')) continue;
                    if (text.Contains(':'))
                    {
                        AddHotPoint(points, text);
                    }
                    else
                    {
                        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length != 4)
                            throw new InvalidOperationException($"Bad hot point line '{line}' (use Name:x,y,z or Name x y z).");
                        AddHotPoint(points, $"{parts[0]}:{parts[1]},{parts[2]},{parts[3]}");
                    }
                }

            AutoRigNaming naming = Option("naming")?.ToLowerInvariant() switch
            {
                null or "preferences" or "custom" => AutoRigNaming.Preferences,
                "default" => AutoRigNaming.Default,
                "mixamo" => AutoRigNaming.Mixamo,
                var other => throw new InvalidOperationException($"Unknown --naming '{other}' (default, mixamo, preferences)."),
            };
            var settings = new AutoRigSettings
            {
                Naming = naming,
                SpineJoints = ParseInt(Option("spine"), 3),
                Fingers = ParseInt(Option("fingers"), 5),
                PhalanxJoints = ParseInt(Option("phalanges"), 3),
                AddIK = !options.Contains("--no-ik"),
                AddReverseFoot = !options.Contains("--no-reverse-foot"),
                GenerateWeights = !options.Contains("--no-weights"),
                BindSkin = !options.Contains("--no-bind"),
            };
            var names = Repeated("mesh");
            var meshes = names.Count == 0
                ? new SkinEditor(character).Candidates()
                : SkinEditor.Named(model.Scene, names);
            if (meshes.Count == 0)
                throw new InvalidOperationException(
                    $"'{character.Name}' has no meshes and the scene has no free ones; pick the meshes with --mesh=name.");
            var result = new AutoRigger(character).Build(meshes, points, settings);
            model.Save(output);
            Console.WriteLine($"{output}: {result.Joints} joints, {result.Controllers} controllers " +
                              $"({result.IkControllers} IK, {result.ReverseFootControllers} reverse foot), " +
                              $"{result.Meshes} meshes, {result.WeightedPoints} weighted points");
            return 0;
        }
        catch (InvalidOperationException e)
        {
            return Fail(e.Message);
        }
    }

    private static int ParseInt(string? text, int fallback) =>
        text == null ? fallback : int.Parse(text, CultureInfo.InvariantCulture);

    /// <summary>Adds one <c>Name:x,y,z</c> hot point.</summary>
    private static void AddHotPoint(Dictionary<HotPoint, Vector3> points, string spec)
    {
        int colon = spec.IndexOf(':');
        if (colon <= 0 || colon == spec.Length - 1)
            throw new InvalidOperationException($"Bad hot point '{spec}' (use Name:x,y,z).");
        string name = spec[..colon].Trim();
        var point = ParsePoint(spec[(colon + 1)..].Replace(';', ',').Replace(' ', ','))
                    ?? throw new InvalidOperationException($"Bad hot point '{spec}' (use Name:x,y,z).");
        points[ParseHotPointName(name)] = point;
    }

    /// <summary>
    /// A hot point name: the enum spelling, or the dialog/mesh spellings (<c>Ankle_L</c>,
    /// <c>Thumb Tip</c>, <c>Finger6Tip_R</c>).
    /// </summary>
    private static HotPoint ParseHotPointName(string name)
    {
        string normalized = new(name.Where(char.IsLetterOrDigit).Select(c => char.ToLowerInvariant(c)).ToArray());
        string side = "";
        if (normalized.EndsWith("left", StringComparison.Ordinal))
        {
            side = "left";
            normalized = normalized[..^4];
        }
        else if (normalized.EndsWith("right", StringComparison.Ordinal))
        {
            side = "right";
            normalized = normalized[..^5];
        }
        else if (normalized.EndsWith('l'))
        {
            side = "left";
            normalized = normalized[..^1];
        }
        else if (normalized.EndsWith('r'))
        {
            side = "right";
            normalized = normalized[..^1];
        }
        if (normalized.EndsWith("tip", StringComparison.Ordinal)) normalized = normalized[..^3];
        normalized += side;
        foreach (var kind in Enum.GetValues<HotPoint>())
            if (string.Equals(kind.ToString().ToLowerInvariant(), normalized, StringComparison.Ordinal))
                return kind;
        throw new InvalidOperationException(
            $"Unknown hot point '{name}' (Pelvis, Neck, Ankle_L/R, Shoulder_L/R, Wrist_L/R, Thumb..Little Tip_L/R, Finger6..10 Tip_L/R).");
    }

    /// <summary>A joint by name, or the character itself for the parent of a root joint.</summary>
    private static Object3D? JointOrCharacter(Character character, string? name)
    {
        if (string.IsNullOrEmpty(name) || name == "character" || name == character.Name) return null;
        return FindJoint(character, name);
    }

    private static Joint FindJoint(Character character, string? name)
    {
        if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("--joint=name is required.");
        var matches = character.Controllers.OfType<Joint>().Where(j => j.Name == name).ToList();
        if (matches.Count == 0)
            throw new InvalidOperationException($"'{character.Name}' has no joint '{name}'.");
        if (matches.Count > 1)
            throw new InvalidOperationException($"'{character.Name}' has {matches.Count} joints named '{name}'.");
        return matches[0];
    }

    private static List<Joint> FindJoints(Character character, List<string> names)
    {
        if (names.Count == 0) throw new InvalidOperationException("--joint=name is required.");
        var joints = new List<Joint>();
        foreach (string name in names)
        {
            var joint = FindJoint(character, name);
            if (!joints.Contains(joint)) joints.Add(joint);
        }
        return joints;
    }

    private static Vector3? ParsePoint(string? text)
    {
        if (text == null) return null;
        var parts = text.Split(',');
        if (parts.Length != 3)
            throw new InvalidOperationException("--at needs three comma-separated numbers: x,y,z.");
        return new Vector3(
            float.Parse(parts[0], CultureInfo.InvariantCulture),
            float.Parse(parts[1], CultureInfo.InvariantCulture),
            float.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    // ---- fbxcompare -----------------------------------------------------------------------

    /// <summary>
    /// Compares <see cref="PoseEvaluator"/> with a (baked) FBX export of the same scene, frame by
    /// frame: per joint the local rotation angle difference, local translation distance and world
    /// position distance. Characters match FBX Null models by name, animations match stacks.
    /// </summary>
    private static int FbxCompare(string[] args)
    {
        var model = SceneModel.Load(args[0]);
        var fbx = SloppyAnimatsu.Core.Fbx.FbxScene.Load(args[1]);
        var options = args[2..].Where(a => a.StartsWith("--")).ToList();
        var wanted = args[2..].Where(a => !a.StartsWith("--")).ToHashSet();
        string? detailJoint = options.FirstOrDefault(o => o.StartsWith("--joint="))?[8..];
        bool interpolated = options.Contains("--between");
        bool showFrames = options.Contains("--frames");
        bool jitter = options.Contains("--jitter");
        Console.WriteLine($"FBX: {fbx.Models.Count} models, {fbx.Stacks.Count} stacks, {fbx.FrameRate} fps; scene {model.Scene.FrameRate} fps");
        foreach (var bad in fbx.Models.Where(m => m.HasUnsupportedTransform))
            Console.WriteLine($"  warning: {bad} uses pivots/pre-rotation/rotation order, which are ignored");

        static float Angle(Matrix4x4 a, Matrix4x4 b)
        {
            if (!Transform.TryFromMatrix(a, out var ta) || !Transform.TryFromMatrix(b, out var tb)) return float.NaN;
            float dot = MathF.Min(1f, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(ta.Rotation), Quaternion.Normalize(tb.Rotation))));
            return 2f * MathF.Acos(dot) * 180f / MathF.PI;
        }

        foreach (var ch in model.Scene.Characters)
        {
            var root = fbx.Models.FirstOrDefault(m => m.Name == ch.Name && m.Kind == "Null");
            if (root == null)
            {
                Console.WriteLine($"character '{ch.Name}': no FBX Null of that name");
                continue;
            }
            var fbxByName = root.Descendants().GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.First());
            var evaluator = new PoseEvaluator(ch, options.Contains("--linear") ? LinearKeyInterpolator.Instance : null)
            {
                ScaleCompensation = !options.Contains("--plain-chain"),
                SolveIK = !options.Contains("--no-ik"),
            };
            var pairs = ch.Controllers.Select((c, i) => (i, c, f: fbxByName.GetValueOrDefault(c.Name ?? ""))).Where(p => p.f != null).ToList();
            var fbxModels = pairs.Select(p => p.f!).ToHashSet();
            var ikJoints = ch.IKControllers.SelectMany(ik => ik.Chain).Select(j => j.Name ?? "").ToHashSet();
            // Joints in or below an IK chain: their world position depends on IK solving.
            bool UnderIk(Object3D o) => ikJoints.Contains(o.Name ?? "") || o.Parent3D is { } p && UnderIk(p);
            Console.WriteLine($"character '{ch.Name}': {ch.Controllers.Count} controllers, {pairs.Count} matched to FBX models " +
                              $"(unmatched: {string.Join(", ", ch.Controllers.Where(c => !fbxByName.ContainsKey(c.Name ?? "")).Select(c => c.Name).Take(12))})");

            var inherit = pairs.GroupBy(p => p.f!.InheritType).Select(g => $"{g.Key}: {g.Count()}");
            Console.WriteLine($"  FBX InheritType of matched models: {string.Join(", ", inherit)}; scene Flags&6 set on " +
                              $"{pairs.Count(p => (p.c.Flags & 6) != 0)} ({string.Join(", ", pairs.Where(p => (p.c.Flags & 6) != 0 && p.f!.InheritType != 2).Select(p => p.c.Name).Take(8))} not Rrs)");

            Matrix4x4 FbxWorld(SloppyAnimatsu.Core.Fbx.FbxAnimStack? stack, SloppyAnimatsu.Core.Fbx.FbxModel m, long ticks)
            {
                // InheritType 2 (Rrs) is the scale-compensated composition; the others are
                // treated as the plain chain.
                Matrix4x4 Local(SloppyAnimatsu.Core.Fbx.FbxModel x) => stack != null ? stack.LocalMatrix(x, ticks) : x.LocalMatrix;
                return m.Parent is { } p
                    ? WorldComposition.Compose(Local(m), FbxWorld(stack, p, ticks), Object3D.LocalScale(Local(p)), m.InheritType == 2)
                    : Local(m);
            }

            void Report(string label, Animation? anim, SloppyAnimatsu.Core.Fbx.FbxAnimStack? stack, IEnumerable<float> frames)
            {
                float worstR = 0, worstT = 0, worstW = 0, worstFreeW = 0;
                string worstRAt = "", worstTAt = "", worstWAt = "", worstFreeWAt = "";
                int samples = 0, good = 0, twisted = 0;
                var perJoint = new Dictionary<string, float>();
                var keyFlags = anim?.Stacks.Take(1).SelectMany(s => s.Keys).GroupBy(k => k.Time).ToDictionary(g => g.Key, g => g.First().Flags) ?? [];
                var keyTimes = anim?.Stacks.SelectMany(s => s.Keys.Select(k => k.Time)).ToHashSet() ?? [];
                var perFrame = new List<string>();
                foreach (float f in frames)
                {
                    float frameWorst = 0;
                    long ticks = SloppyAnimatsu.Core.Fbx.FbxScene.ToTicks(f / fbx.FrameRate);
                    // The baked export sometimes samples a frame at the previous or next
                    // frame's time (repeated values). With --jitter, compare against the best of
                    // f-1, f, f+1, chosen by the summed local error of the non-IK joints.
                    float ourTime = f;
                    if (jitter && anim != null && stack != null)
                    {
                        float Cost(float t)
                        {
                            var l = evaluator.LocalTransforms(anim, t);
                            // Joints that cannot be decomposed (NaN, e.g. a zero-scale prop) are
                            // skipped; one NaN would otherwise disable the choice for the frame.
                            // Translation counts too (1 unit ~ 1°): a root sliding while every
                            // other joint holds still has no rotation error to go by.
                            // IK chain joints count too: in animations where only they move
                            // (e.g. 19_run DamageHi1 f7-10) the other joints cannot tell the
                            // frames apart, and a near-tie would pick the wrong one.
                            return pairs
                                .Select(p =>
                                {
                                    var theirs = stack.LocalMatrix(p.f!, ticks);
                                    return Angle(l[p.i].ToMatrix(), theirs) + Vector3.Distance(l[p.i].Translation, theirs.Translation);
                                })
                                .Where(a => !float.IsNaN(a)).Sum();
                        }
                        float best = Cost(f);
                        foreach (float t in new[] { f - 1, f + 1 })
                            if (t >= anim.TimeRange.Start && Cost(t) is var c && c < best - 1e-3f) (best, ourTime) = (c, t);
                    }
                    var local = evaluator.LocalTransforms(anim, ourTime);
                    var world = evaluator.WorldMatrices(anim, ourTime);
                    foreach (var (i, c, fm) in pairs)
                    {
                        var ours = local[i].ToMatrix();
                        var theirs = stack != null ? stack.LocalMatrix(fm!, ticks) : fm!.LocalMatrix;
                        float dr = Angle(ours, theirs);
                        // The bake can twist an IK joint's frame (e.g. 180° about its own axis)
                        // while compensating in its children's locals, so their world rotation is
                        // unchanged. For a child of an IK joint, also compare parent·child.
                        if (dr >= 0.1f && !ikJoints.Contains(c.Name ?? "") && c.Parent3D is { } cp && ikJoints.Contains(cp.Name ?? "")
                            && evaluator.IndexOf(cp) is var pi and >= 0 && fm!.Parent is { } fp && stack != null)
                        {
                            var oursUp = ours * local[pi].ToMatrix();
                            var theirsUp = theirs * stack.LocalMatrix(fp, ticks);
                            if (Angle(oursUp, theirsUp) is var composed && composed < dr)
                            {
                                (dr, ours, theirs) = (composed, oursUp, theirsUp);
                                twisted++;
                            }
                        }
                        float dt = Vector3.Distance(ours.Translation, theirs.Translation);
                        float dw = Vector3.Distance(world[i].Translation, FbxWorld(stack, fm!, ticks).Translation);
                        samples++;
                        if (dr < 0.1f && dt < 0.01f) good++;
                        perJoint[c.Name ?? ""] = MathF.Max(perJoint.GetValueOrDefault(c.Name ?? ""), dr);
                        if (!ikJoints.Contains(c.Name ?? "")) frameWorst = MathF.Max(frameWorst, dr);
                        if (dr > worstR) (worstR, worstRAt) = (dr, $"{c.Name}@{f}");
                        if (dt > worstT) (worstT, worstTAt) = (dt, $"{c.Name}@{f}");
                        if (dw > worstW) (worstW, worstWAt) = (dw, $"{c.Name}@{f}");
                        if (!UnderIk(c) && dw > worstFreeW) (worstFreeW, worstFreeWAt) = (dw, $"{c.Name}@{f}");
                        if (c.Name == detailJoint)
                        {
                            Transform.TryFromMatrix(theirs, out var tt);
                            var fbxDelta = Quaternion.Normalize(Quaternion.Inverse(evaluator.RestTransforms[i].Rotation) * tt.Rotation);
                            if (fbxDelta.W < 0) fbxDelta = -fbxDelta;
                            Console.WriteLine($"      {c.Name}@{f}: rot {dr:0.###}°, ours R {local[i].Rotation} fbx R {tt.Rotation} (fbx delta {fbxDelta}), " +
                                              $"T ours {ours.Translation} fbx {theirs.Translation}, S ours {local[i].Scale} fbx {tt.Scale} (det {theirs.GetDeterminant():0.###e0}), " +
                                              $"world pos ours {world[i].Translation} fbx {FbxWorld(stack, fm!, ticks).Translation}");
                        }
                    }
                    string mark = keyTimes.Contains((int)f) && f == (int)f ? "*" + (keyFlags.TryGetValue((int)f, out uint kf) ? $"{kf:X}" : "") : "";
                    perFrame.Add($"{f}{mark}{(ourTime < f ? "<" : ourTime > f ? ">" : "")}:{frameWorst:0.#}");
                }
                if (showFrames)
                    Console.WriteLine($"      per frame (non-IK max rot °, * = key): {string.Join(" ", perFrame)}");
                Console.WriteLine($"  {label}: {good}/{samples} joint-frames match; max rot {worstR:0.###}° ({worstRAt}), " +
                                  $"local T {worstT:0.###} ({worstTAt}), world pos {worstW:0.###} ({worstWAt}), outside IK {worstFreeW:0.###} ({worstFreeWAt})");
                if (worstR >= 0.1f)
                    Console.WriteLine($"      worst joints: {string.Join(", ", perJoint.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key}{(ikJoints.Contains(kv.Key) ? "(IK)" : "")} {kv.Value:0.#}°"))}");
                var nonIk = perJoint.Where(kv => !ikJoints.Contains(kv.Key)).ToList();
                if (ikJoints.Count > 0 && nonIk.Count > 0)
                    Console.WriteLine($"      outside IK chains: max rot {nonIk.Max(kv => kv.Value):0.###}°, {nonIk.Count(kv => kv.Value < 0.1f)}/{nonIk.Count} joints within 0.1°; " +
                                      string.Join(", ", nonIk.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key} {kv.Value:0.#}°")));
                if (twisted > 0)
                    Console.WriteLine($"      {twisted} joint-frames below an IK joint compared as parent·child (twisted IK frame in the bake)");
            }

            Report("rest pose vs FBX static", null, null, [0]);

            // Stacks of this character, by name in file order; the nth animation of a name takes the nth stack.
            var stacksByName = fbx.Stacks
                .Where(s => s.Layers.Any(l => l.CurveNodes.Any(cn => cn.Model != null && fbxModels.Contains(cn.Model))))
                .GroupBy(s => s.Name).ToDictionary(g => g.Key, g => new Queue<SloppyAnimatsu.Core.Fbx.FbxAnimStack>(g));
            foreach (var anim in ch.Animations)
            {
                var stack = stacksByName.TryGetValue(anim.Name ?? "", out var q) && q.Count > 0 ? q.Dequeue() : null;
                if (wanted.Count > 0 && !wanted.Contains(anim.Name ?? "")) continue;
                string folder = anim.FolderPath.Count > 0 ? string.Join("/", anim.FolderPath) + "/" : "";
                if (stack == null)
                {
                    Console.WriteLine($"  anim '{folder}{anim.Name}': no FBX stack");
                    continue;
                }
                var (start, end) = anim.TimeRange;
                if (detailJoint != null && ch.Controllers.FirstOrDefault(c => c.Name == detailJoint) is { } dj)
                {
                    foreach (var s in anim.Stacks)
                        if (s.TrackFor(dj) is { } tr)
                        {
                            Console.WriteLine($"    stack '{s.Name}' flags {s.Flags} track '{dj.Name}' channels {tr.Channels}:");
                            var keys = s.Keys;
                            var modes = tr.TangentModes;
                            for (int k = 0; k < keys.Count; k++)
                            {
                                var v = tr.Keys[k] is TransformKey tk ? tk.Value : (Transform?)null;
                                var tin = tr.TangentsIn.ElementAtOrDefault(k) is TransformKey a ? a.Value : (Transform?)null;
                                var tout = tr.TangentsOut.ElementAtOrDefault(k) is TransformKey b ? b.Value : (Transform?)null;
                                Console.WriteLine($"      key {keys[k].Time}: {(v is { } vv ? $"T {vv.Translation} R {vv.Rotation}" : "null")}" +
                                                  $"  modes {string.Join(",", modes.ElementAtOrDefault(k) ?? [])}" +
                                                  $"\n        TIn R {tin?.Rotation} T {tin?.Translation}\n        TOut R {tout?.Rotation} T {tout?.Translation}");
                            }
                        }
                    int di = evaluator.IndexOf(dj);
                    Console.WriteLine($"    rest R {evaluator.RestTransforms[di].Rotation}");
                    if (fbxByName.GetValueOrDefault(dj.Name ?? "") is { } fm)
                        foreach (var cn in stack.Layers.SelectMany(l => l.CurveNodes).Where(c => c.Model == fm && c.Property is "Lcl Rotation" or "Lcl Translation"))
                            for (int axis = 0; axis < 3; axis++)
                                if (cn.Curves[axis] is { } cx)
                                    Console.WriteLine($"    FBX {cn.Property} {"XYZ"[axis]} key frames: {string.Join(" ", cx.Times.Select(t => (SloppyAnimatsu.Core.Fbx.FbxScene.ToSeconds(t) * fbx.FrameRate).ToString("0.###")))}");
                }
                double stop = SloppyAnimatsu.Core.Fbx.FbxScene.ToSeconds(stack.Stop) * fbx.FrameRate;
                // The baked curves end at the stack's LocalStop.
                int last = Math.Min(end, (int)Math.Round(stop));
                var frames = Enumerable.Range(start, Math.Max(1, last - start + 1)).Select(f => (float)f);
                if (interpolated) frames = frames.Select(f => f + 0.5f).Where(f => f < last);
                Report($"anim '{folder}{anim.Name}' frames {start}..{end} (FBX stop {stop:0.##}, {anim.Stacks.Count} scene stack(s))", anim, stack, frames);
            }
        }
        return 0;
    }

    private static string FormatMatrix(Matrix4x4 m) =>
        string.Join(" | ", Enumerable.Range(0, 4).Select(r =>
            string.Join(" ", AktMath.ToArray(m).Skip(r * 4).Take(4).Select(f => f.ToString("0.###").PadLeft(8)))));

    /// <summary>
    /// Compares <see cref="MeshSkin"/> with the skin clusters of an FBX export: each joint's bind world against <c>TransformLink</c>, and the mesh world against
    /// <c>Transform</c> × <c>TransformLink</c> (the app writes <c>Transform</c> relative to the link).
    /// </summary>
    private static int SkinCheck(string[] args)
    {
        var model = SceneModel.Load(args[0]);
        var fbx = SloppyAnimatsu.Core.Fbx.FbxScene.Load(args[1]);
        bool verbose = args.Contains("--verbose");
        var models = fbx.Models.ToDictionary(m => m.Id);
        var nodes = (fbx.Document.Node("Objects")?.Children ?? [])
            .Where(n => n.PropertyCount >= 3 && n[0] is long).ToDictionary(n => (long)n[0]);
        var children = new Dictionary<long, List<long>>();
        foreach (var c in fbx.Document.Node("Connections")?.ChildrenNamed("C") ?? [])
            if (c.PropertyCount >= 3 && c[1] is long child && c[2] is long parent)
                (children.TryGetValue(parent, out var l) ? l : children[parent] = []).Add(child);
        IEnumerable<SloppyAnimatsu.Core.Fbx.FbxNode> ChildNodes(long id, string name) =>
            (children.GetValueOrDefault(id) ?? []).Select(i => nodes.GetValueOrDefault(i)).Where(n => n?.Name == name)!;

        static Matrix4x4 FbxMatrix(SloppyAnimatsu.Core.Fbx.FbxNode node, string name) =>
            node.Child(name)?[0] is double[] d && d.Length == 16 ? AktMath.ToMatrix(d.Select(x => (float)x).ToArray()) : Matrix4x4.Identity;

        var characterNames = model.Scene.Characters.Select(c => c.Name).ToHashSet();
        foreach (var mesh in model.Scene.Meshes)
        {
            var skin = new MeshSkin(mesh);
            var ch = skin.Character;
            if (ch == null || !skin.IsSkinned) { Console.WriteLine($"mesh '{mesh.Name}': not skinned"); continue; }
            var jointIndex = ch.Joints.Select((j, i) => (j.Name, i)).GroupBy(x => x.Name).ToDictionary(g => g.Key ?? "", g => g.First().i);
            int clusters = 0, matched = 0;
            float worstPos = 0, worstBasis = 0, worstMesh = 0;
            string worstJoint = "";
            // FBX meshes of that name; the clusters hang under Geometry > Skin deformer.
            foreach (var fbxMesh in nodes.Values.Where(n => n.Name == "Model" && SloppyAnimatsu.Core.Fbx.FbxScene.ShortName(n) == mesh.Name))
            foreach (var geometry in ChildNodes((long)fbxMesh[0], "Geometry"))
            foreach (var skinDeformer in ChildNodes((long)geometry[0], "Deformer"))
            foreach (var cluster in ChildNodes((long)skinDeformer[0], "Deformer"))
            {
                var link = (children.GetValueOrDefault((long)cluster[0]) ?? []).Select(models.GetValueOrDefault).FirstOrDefault(m => m != null);
                // Joint (and mesh) names repeat across characters: the link must belong to this character.
                var owner = link?.Parent;
                while (owner != null && !characterNames.Contains(owner.Name)) owner = owner.Parent;
                if (link == null || owner?.Name != ch.Name || !jointIndex.TryGetValue(link.Name, out int j)) continue;

                clusters++;
                var linkWorld = FbxMatrix(cluster, "TransformLink");
                var ours = skin.BindWorlds[j];
                float dPos = Vector3.Distance(ours.Translation, linkWorld.Translation);
                float dBasis = AktMath.MaxDifference(Normalized(ours), Normalized(linkWorld));
                float dMesh = AktMath.MaxDifference(FbxMatrix(cluster, "Transform") * linkWorld, skin.MeshWorld) / Math.Max(1f, skin.MeshWorld.Translation.Length());
                if (dPos < 0.01f && dBasis < 0.001f) matched++;
                else if (verbose) Console.WriteLine($"    {link.Name}: ours {ours.Translation}, FBX {linkWorld.Translation}");
                if (dPos > worstPos) { worstPos = dPos; worstJoint = link.Name; }
                worstBasis = Math.Max(worstBasis, dBasis);
                worstMesh = Math.Max(worstMesh, dMesh);
            }
            Console.WriteLine($"mesh '{mesh.Name}' (bind pose of '{ch.Name}'): {matched}/{clusters} joint bind worlds match; " +
                              $"worst position {worstPos:G3} ({worstJoint}), worst basis {worstBasis:G3}, worst relative mesh world {worstMesh:G3}");
        }
        return 0;

        // Basis rows normalized, so scale differences don't hide rotation differences.
        static Matrix4x4 Normalized(Matrix4x4 m)
        {
            var x = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
            var y = Vector3.Normalize(new Vector3(m.M21, m.M22, m.M23));
            var z = Vector3.Normalize(new Vector3(m.M31, m.M32, m.M33));
            return new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        }
    }

    // ---- tangentcheck ---------------------------------------------------------------------

    /// <summary>
    /// Recomputes every track's tangents (<see cref="TangentRecompute"/>) and compares them with
    /// the stored ones. Keys 0 … n−2 are counted per channel group (T, R, S), tangent mode, stack
    /// kind and position; a group matches when all its components agree within 1e-3 (relative above 1).
    /// Totals are also given per file and for space-switched tracks (any key with a Space). The
    /// space-aware neighbour lookup is used unless <c>--plain</c>. Each track's stored tangents are
    /// restored after the comparison, so every track is converted against the unedited scene.
    /// </summary>
    /// <summary>
    /// At every key of every IK track: the distance between the chain end and the controller
    /// (relative to the chain length) in the evaluated pose, and whether <c>RefArray</c> equals the
    /// chain joints' evaluated locals. Counted separately for keys with and without IK flag bit 0.
    /// </summary>
    /// <summary>
    /// <c>keyjump &lt;scene&gt; [--anim=&lt;text&gt;] [--fix] [--save-as=&lt;out&gt;]</c>: for every IK-locked
    /// key of every IK/reverse-foot track, how far the chain's end joint (a reverse foot's ball)
    /// is at the key frame from where it is just after it, inside the override segment. A large
    /// value is a one-frame jump at the key. <c>--fix</c> runs
    /// <see cref="KeyEditor.SolveLockedKeys"/> on the matching animations and reports again;
    /// <c>--save-as</c> then writes the scene.
    /// </summary>
    private static int KeyJump(string[] args)
    {
        string? filter = args.Where(a => a.StartsWith("--anim=")).Select(a => a[7..]).FirstOrDefault();
        string? saveAs = args.Where(a => a.StartsWith("--save-as=")).Select(a => a[10..]).FirstOrDefault();
        bool fix = args.Contains("--fix");
        bool list = args.Contains("--list");
        bool legacyIK = args.Contains("--legacy-ik");
        bool mixedMiss = args.Contains("--mixed-miss");
        var file = args.First(a => !a.StartsWith("--"));
        var model = SceneModel.Load(file);
        static string PathOf(Animation anim) => string.Join("/", anim.FolderPath.Append(anim.Name ?? ""));
        bool Matches(Animation anim) =>
            filter == null || PathOf(anim).Contains(filter.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        void Report(string label)
        {
            foreach (var ch in model.Scene.Characters)
            {
                var evaluator = new PoseEvaluator(ch) { ExactScaleIK = !legacyIK };
                foreach (var anim in ch.Animations.Where(Matches))
                {
                    double worst = 0, worstMiss = 0;
                    string where = "", missWhere = "";
                    int keys = 0;
                    foreach (var stack in anim.Stacks)
                    {
                        if (!stack.IsEnabled) continue;
                        int n = stack.Keys.Count;
                        foreach (var track in stack.Tracks)
                        {
                            if (track.Target is not ControllerIK ik) continue;
                            var end = ik is ControllerReverseFoot && ik.Chain.Count >= 4 ? ik.Chain[3] : ik.Chain[^1];
                            int ei = evaluator.IndexOf(end);
                            if (ei < 0) continue;
                            // In-segment miss: the solved joint (IK end, reverse foot's ankle) against its target.
                            var (solvedJoint, goal) = ik is ControllerReverseFoot rfm && rfm.Chain.Count >= 4 && rfm.Shoe.Count >= 7
                                ? (rfm.Chain[2], (Object3D)rfm.Shoe[ReverseFootSolver.SkeletonAnkle]) : (ik.Chain[^1], (Object3D)ik);
                            int si = evaluator.IndexOf(solvedJoint), gi = evaluator.IndexOf(goal);
                            if (n > 1 && si >= 0 && gi >= 0)
                                for (float ft = MathF.Ceiling(stack.Keys[0].Time); ft <= stack.Keys[n - 1].Time; ft += 1)
                                {
                                    if (evaluator.OverrideString(anim, ik, ft) == null) continue;
                                    // Measured in the IK track's own stack (the world the solve sees): motion
                                    // that other stacks add, such as a root-motion layer, is not a miss.
                                    // --mixed-miss measures in the full mix instead.
                                    int last = ik.Chain.ToList().IndexOf(solvedJoint);
                                    var objs = ik.Chain.Take(last + 1).Append(goal).ToArray();
                                    Vector3[] p;
                                    if (mixedMiss)
                                    {
                                        var w = evaluator.WorldMatrices(anim, ft);
                                        p = objs.Select(o => w[evaluator.IndexOf(o)].Translation).ToArray();
                                    }
                                    else if (evaluator.OwnStackPositions(anim, ik, ft, objs) is { } own) p = own;
                                    else continue;
                                    // Only reachable targets: closer to the chain's first joint than the chain is long.
                                    double reach = 0;
                                    for (int j = 1; j <= last; j++) reach += Vector3.Distance(p[j], p[j - 1]);
                                    if (Vector3.Distance(p[0], p[^1]) > 0.98 * reach) continue;
                                    double m = Vector3.Distance(p[last], p[^1]);
                                    if (m > worstMiss) { worstMiss = m; missWhere = $"{ik.Name} frame {ft}"; }
                                }
                            for (int k = 0; k < Math.Max(1, n - 1) && k < track.Keys.Count; k++)
                            {
                                if (track.Keys[k] is not IKKey key || (key.Flags & 1) == 0) continue;
                                float t = stack.Keys[k].Time;
                                var at = evaluator.WorldMatrices(anim, t)[ei].Translation;
                                var after = evaluator.WorldMatrices(anim, t + 0.01f)[ei].Translation;
                                // The step at the key without the motion across the probe: with
                                // p(t+e) = p(t) + S + e*v for e > 0, two probes give S exactly, so
                                // fast FK motion past a key no longer counts as a jump.
                                var near = evaluator.WorldMatrices(anim, t + 0.001f)[ei].Translation;
                                double d = ((10 * (near - at) - (after - at)) / 9).Length();
                                keys++;
                                if (list && d > 1)
                                    Console.WriteLine($"  {label} {ik.Name} key {k} frame {t}: {d:0.###} (at {at.X:0.#},{at.Y:0.#},{at.Z:0.#} after {after.X:0.#},{after.Y:0.#},{after.Z:0.#}) roll {(key as ReverseFootKey)?.RollAngle:0.##}"
                                        + Diagnose(evaluator, anim, stack, ik, solvedJoint, goal, t, at, ei));
                                if (d > worst) { worst = d; where = $"{ik.Name} frame {t}"; }
                            }
                        }
                    }
                    Console.WriteLine($"{label} {ch.Name} | {PathOf(anim)} | IK-locked keys {keys} | worst jump {worst:0.###} at {where} | worst miss {worstMiss:0.###} at {missWhere}");
                }
            }
        }
        // Why a locked key still jumps: the step at +0.001 against +0.01 (a real step, or fast
        // motion), whether a segment follows, the own-stack reach (root->goal / chain length) and
        // miss just inside it, chain joints without a track in the IK's stack, and the joint whose
        // local rotation changes most across the step.
        static string Diagnose(PoseEvaluator ev, Animation anim, AnimStack stack, ControllerIK ik, Object3D solvedJoint,
            Object3D goal, float t, Vector3 at, int ei)
        {
            float side = t + 0.01f;
            double d1 = Vector3.Distance(at, ev.WorldMatrices(anim, t + 0.001f)[ei].Translation);
            bool seg = ev.OverrideString(anim, ik, side) != null;
            int last = ik.Chain.ToList().IndexOf(solvedJoint);
            string reach = "";
            if (ev.OwnStackPositions(anim, ik, side, ik.Chain.Take(last + 1).Append(goal).ToArray()) is { } p)
            {
                double len = 0;
                for (int j = 1; j <= last; j++) len += Vector3.Distance(p[j], p[j - 1]);
                reach = $" reach {Vector3.Distance(p[0], p[^1]) / Math.Max(len, 1e-6):0.###} ownMiss {Vector3.Distance(p[last], p[^1]):0.###}";
            }
            var untracked = ik.Chain.Where(j => stack.TrackFor(j) == null).Select(j => j.Name).ToList();
            var la = ev.LocalTransforms(anim, t);
            var lb = ev.LocalTransforms(anim, side);
            var objs = ik.Chain.Cast<Object3D>().Concat(ik is ControllerReverseFoot rf ? rf.Shoe.Cast<Object3D>() : Enumerable.Empty<Object3D>()).ToList();
            string worstJoint = ""; double worstAngle = 0;
            foreach (var o in objs)
            {
                int i = ev.IndexOf(o);
                if (i < 0) continue;
                double dot = Math.Min(1, Math.Abs(Quaternion.Dot(Quaternion.Normalize(la[i].Rotation), Quaternion.Normalize(lb[i].Rotation))));
                double angle = 2 * Math.Acos(dot) * 180 / Math.PI;
                if (angle > worstAngle) { worstAngle = angle; worstJoint = o.Name ?? ""; }
            }
            return $" | d0.001 {d1:0.###} seg {(seg ? "yes" : "no")}{reach} untracked [{string.Join(",", untracked)}] most {worstJoint} {worstAngle:0.#}deg";
        }

        int probe = args.Where(a => a.StartsWith("--probe=")).Select(a => int.Parse(a[8..])).DefaultIfEmpty(-1).First();
        void Probe(string label)
        {
            if (probe < 0) return;
            foreach (var ch in model.Scene.Characters)
            {
                var evaluator = new PoseEvaluator(ch);
                foreach (var anim in ch.Animations.Where(Matches))
                    foreach (var stack in anim.Stacks)
                        foreach (var track in stack.Tracks)
                        {
                            if (track.Target is not ControllerReverseFoot rf || probe >= stack.Keys.Count) continue;
                            float t = stack.Keys[probe].Time;
                            var parent = rf.Chain[0].Parent3D!;
                            foreach (float tt in new[] { t, t + 0.01f })
                            {
                                var w = evaluator.WorldMatrices(anim, tt);
                                string P(Object3D o) { var v = w[evaluator.IndexOf(o)].Translation; return $"{v.X:0.#},{v.Y:0.#},{v.Z:0.#}"; }
                                Console.WriteLine($"  {label} {anim.Name} [{stack.Name}] {rf.Name} t={tt}: hipParent {P(parent)} thigh {P(rf.Chain[0])} ankle {P(rf.Chain[2])} ball {P(rf.Chain[3])} ctl {P(rf)}");
                            }
                        }
            }
        }
        Probe("before");
        Report("before");
        if (!fix) return 0;
        foreach (var ch in model.Scene.Characters)
        {
            var editor = new KeyEditor(ch);
            foreach (var anim in ch.Animations.Where(Matches))
                foreach (var stack in anim.Stacks)
                    editor.SolveLockedKeys(stack);
        }
        Probe("after ");
        Report("after ");
        if (saveAs != null)
        {
            model.Save(saveAs);
            Console.WriteLine($"saved {saveAs}");
        }
        return 0;
    }

    private static int IKKeyCheck(string[] args)
    {
        int show = args.Where(a => a.StartsWith("--show=")).Select(a => int.Parse(a[7..])).DefaultIfEmpty(0).Max();
        int shown = 0;
        var stats = new SortedDictionary<string, (int Total, int EndMatch, int RefMatch, int RefLocal, double MaxErr, int RotMatch)>();
        void Count(string key, bool endOk, bool refOk, bool refLocal, double err, bool rotOk)
        {
            stats.TryGetValue(key, out var c);
            stats[key] = (c.Total + 1, c.EndMatch + (endOk ? 1 : 0), c.RefMatch + (refOk ? 1 : 0),
                c.RefLocal + (refLocal ? 1 : 0), Math.Max(c.MaxErr, err), c.RotMatch + (rotOk ? 1 : 0));
        }
        static bool NearT(Transform a, Transform b) =>
            Vector3.Distance(a.Translation, b.Translation) <= 1e-2f * MathF.Max(1, a.Translation.Length())
            && MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a.Rotation), Quaternion.Normalize(b.Rotation))) >= 0.99999f;

        foreach (var file in ExpandFiles(args.Where(a => !a.StartsWith("--"))))
        {
            var model = SceneModel.Load(file);
            string name = Path.GetFileNameWithoutExtension(file);
            foreach (var ch in model.Scene.Characters)
            {
                var evaluator = new PoseEvaluator(ch);
                var plain = new PoseEvaluator(ch) { SolveIK = false };
                foreach (var anim in ch.Animations)
                    foreach (var stack in anim.Stacks)
                    {
                        if (!stack.IsEnabled) continue;
                        var times = stack.Keys;
                        foreach (var track in stack.Tracks)
                        {
                            if (track.Target is not ControllerIK ik || ik is ControllerReverseFoot) continue;
                            var chain = ik.Chain;
                            int ci = evaluator.IndexOf(ik);
                            var ji = chain.Select(evaluator.IndexOf).ToArray();
                            if (ci < 0 || ji.Any(i => i < 0) || chain.Count < 2) continue;
                            int n = Math.Min(times.Count, track.Keys.Count);
                            for (int k = 0; k < n - 1; k++)
                            {
                                if (track.Keys[k] is not IKKey key) continue;
                                float t = times[k].Time;
                                var world = plain.WorldMatrices(anim, t);
                                var local = plain.LocalTransforms(anim, t);
                                double length = 0;
                                for (int j = 1; j < chain.Count; j++)
                                    length += Vector3.Distance(world[ji[j]].Translation, world[ji[j - 1]].Translation);
                                double err = Vector3.Distance(world[ji[^1]].Translation, world[ci].Translation) / Math.Max(length, 1e-6);
                                var refs = key.ChainTransforms;
                                bool refOk = refs.Count == chain.Count
                                    && Enumerable.Range(0, chain.Count).All(j => NearT(refs[j], local[ji[j]]));
                                // RefArray as the joint tracks' own key values in this stack.
                                // FABRIK from RefArray towards the controller reproduces the joint locals.
                                bool refLocal = false;
                                if (refs.Count == chain.Count)
                                {
                                    var @base = chain[0].Parent3D is { } bp && plain.IndexOf(bp) is var bi and >= 0
                                        ? Transform.FromMatrixLoose(world[bi]) : Transform.Identity;
                                    var accumulates = chain.Take(chain.Count - 1).Select(j => !j.CompensatesParentScale).ToArray();
                                    var solved = IKSolver.Solve(@base, refs, Transform.FromMatrixLoose(world[ci]), accumulates);
                                    refLocal = solved != null && Enumerable.Range(0, chain.Count).All(j => NearT(solved[j], local[ji[j]]));
                                }
                                // Out of reach: the controller is farther from the chain's first joint than the chain is long.
                                bool far = Vector3.Distance(world[ji[0]].Translation, world[ci].Translation) > length * 0.999;
                                int enabled = anim.Stacks.Count(s => s.IsEnabled);
                                string kind = ((key.Flags & 1) != 0 ? "IK-key" : "FK-key") + (stack.IsAdditive ? " add" : "")
                                    + (enabled > 1 ? " multi" : "") + (far ? " far" : "");
                                bool rotOk = MathF.Abs(Quaternion.Dot(Transform.FromMatrixLoose(world[ji[^1]]).Rotation,
                                    Transform.FromMatrixLoose(world[ci]).Rotation)) > 0.9999f;
                                Count($"{name} {kind}", err < 1e-3, refOk, refLocal, err, rotOk);
                                if (shown < show && (err >= 1e-3 || !refOk))
                                {
                                    shown++;
                                    Console.WriteLine($"  {name} {anim.Name} [{stack.Name}] {ik.Name} key {k} t={t} flags=0x{key.Flags:x} err={err:G3} ref={refOk} stacks={anim.Stacks.Count(s => s.IsEnabled)} far={far}");
                                }
                            }
                        }
                    }
            }
        }
        Console.WriteLine($"{"file / kind",-40} {"keys",6} {"end=ctl",8} {"ref=mix",8} {"fabrik",8} {"max err",9} {"rot=end",8}");
        foreach (var (k, c) in stats)
            Console.WriteLine($"{k,-40} {c.Total,6} {c.EndMatch,8} {c.RefMatch,8} {c.RefLocal,8} {c.MaxErr,9:G3} {c.RotMatch,8}");
        return 0;
    }

    /// <summary>
    /// At every key of every reverse-foot track (single enabled stack, plain pose): the
    /// <c>RefArray</c> layout and how the controller, the leg and the shoe relate at FK and IK keys.
    /// </summary>
    private static int RFKeyCheck(string[] args)
    {
        int show = args.Where(a => a.StartsWith("--show=")).Select(a => int.Parse(a[7..])).DefaultIfEmpty(0).Max();
        var stats = new SortedDictionary<string, int[]>();
        string[] columns = ["keys", "ref5", "ref=leg", "r4=b2c", "c=ank/P", "c=b*r4", "solve", "roll=leg"];
        static bool NearT(Transform a, Transform b) =>
            Vector3.Distance(a.Translation, b.Translation) <= 1e-2f * MathF.Max(1, a.Translation.Length())
            && MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a.Rotation), Quaternion.Normalize(b.Rotation))) >= 0.9999f;
        static bool NearM(Matrix4x4 a, Matrix4x4 b) => NearT(Transform.FromMatrixLoose(a), Transform.FromMatrixLoose(b));
        static Matrix4x4 Inv(Matrix4x4 m) => Matrix4x4.Invert(m, out var r) ? r : Matrix4x4.Identity;

        foreach (var file in ExpandFiles(args.Where(a => !a.StartsWith("--"))))
        {
            var model = SceneModel.Load(file);
            string name = Path.GetFileNameWithoutExtension(file);
            var limits = ReverseFootLimits.FromScene(model.Scene);
            foreach (var ch in model.Scene.Characters)
            {
                var plain = new PoseEvaluator(ch) { SolveIK = false };
                foreach (var anim in ch.Animations)
                {
                    if (anim.Stacks.Count(s => s.IsEnabled) != 1) continue;
                    foreach (var stack in anim.Stacks)
                    {
                        if (!stack.IsEnabled) continue;
                        var times = stack.Keys;
                        foreach (var track in stack.Tracks)
                        {
                            if (track.Target is not ControllerReverseFoot rf || rf.Shoe.Count < 7 || rf.Chain.Count != 4) continue;
                            var chain = rf.Chain;
                            int ci = plain.IndexOf(rf);
                            var ji = chain.Select(plain.IndexOf).ToArray();
                            var si = rf.Shoe.Take(7).Select(plain.IndexOf).ToArray();
                            if (ci < 0 || ji.Any(i => i < 0) || si.Any(i => i < 0)) continue;
                            var restShoe = si.Select(i => plain.RestTransforms[i]).ToArray();
                            var axis = new Vector3(rf.RollAxisPlant.X, rf.RollAxisPlant.Y, rf.RollAxisPlant.Z);
                            int n = Math.Min(times.Count, track.Keys.Count);
                            for (int k = 0; k < n - 1; k++)
                            {
                                if (track.Keys[k] is not ReverseFootKey key) continue;
                                float t = times[k].Time;
                                var world = plain.WorldMatrices(anim, t);
                                var local = plain.LocalTransforms(anim, t);
                                var refs = key.ChainTransforms;
                                var shoe = ReverseFootSolver.ShoeLocals(restShoe, key.RollAngle, key.TwistAngle, key.BankAngle, limits, axis);
                                // Shoe chain products in the controller's frame (row vectors: child * parent).
                                var p = Matrix4x4.Identity;
                                var shoeW = new Matrix4x4[7];
                                for (int j = 0; j < 7; j++)
                                {
                                    p = shoe[j].ToMatrix() * p;
                                    shoeW[j] = p * world[ci];
                                }
                                var ctl = world[ci];
                                var ball = world[ji[3]];
                                var ankle = world[ji[2]];
                                bool ref5 = refs.Count == 5;
                                bool refLeg = refs.Count >= 4 && Enumerable.Range(0, 4).All(j => NearT(refs[j], local[ji[j]]));
                                bool r4b2c = ref5 && NearM(refs[4].ToMatrix(), ctl * Inv(ball));
                                bool cAnk = NearM(ctl, Inv(p) * ankle);
                                bool cBall = ref5 && NearM(ctl, refs[4].ToMatrix() * ball);
                                bool solve = false;
                                if (refs.Count >= 4)
                                {
                                    int bi = chain[0].Parent3D is { } bp ? plain.IndexOf(bp) : -1;
                                    var bw = bi >= 0 ? world[bi] : Matrix4x4.Identity;
                                    var bs = bi >= 0 ? local[bi].Scale : Vector3.One;
                                    var solved = ReverseFootSolver.Solve(bw, bs, refs.Take(4).ToList(), chain.Select(j => j.CompensatesParentScale).ToArray(),
                                        plain.RestTransforms[ji[3]], restShoe[5], shoeW[6], shoeW[5], shoeW[4]);
                                    solve = solved != null && Enumerable.Range(0, 4).All(j => NearT(solved[j], local[ji[j]]));
                                }
                                // Roll from the ball joint's rotation away from rest (angle only).
                                var dq = Quaternion.Inverse(plain.RestTransforms[ji[3]].Rotation) * local[ji[3]].Rotation;
                                float a = 2 * MathF.Acos(Math.Clamp(MathF.Abs(dq.W), 0, 1));
                                bool rollLeg = MathF.Abs(a - MathF.Max(0, Math.Min(key.RollAngle, limits.RollPre))) < 1e-2f;
                                string kind = $"{name} {((key.Flags & 1) != 0 ? "IK" : "FK")}";
                                if (!stats.TryGetValue(kind, out var c)) stats[kind] = c = new int[columns.Length];
                                bool[] v = [true, ref5, refLeg, r4b2c, cAnk, cBall, solve, rollLeg];
                                for (int j = 0; j < v.Length; j++) if (v[j]) c[j]++;
                                if (v.Any(b => !b) && show-- > 0)
                                    Console.WriteLine($"  {name} {anim.Name} {rf.Name} k{k} t={t} flags=0x{key.Flags:x} refs={refs.Count} roll={key.RollAngle:F3} tw={key.TwistAngle:F3} bank={key.BankAngle:F3} ballAng={a:F3} pole={key.PoleAngle:F3}/{key.RefPoleAngle:F3} [{string.Join(",", v.Select(b => b ? 1 : 0))}]");
                            }
                        }
                    }
                }
            }
        }
        Console.WriteLine($"{"file / kind",-28} " + string.Join(" ", columns.Select(c => $"{c,8}")));
        foreach (var (k, c) in stats)
            Console.WriteLine($"{k,-28} " + string.Join(" ", c.Select(x => $"{x,8}")));
        return 0;
    }

    private static int TangentCheck(string[] args)
    {
        int show = args.Where(a => a.StartsWith("--show=")).Select(a => int.Parse(a[7..])).DefaultIfEmpty(0).Max();
        bool plain = args.Contains("--plain");
        var counts = new SortedDictionary<string, (int Match, int Total)>();
        var totals = new List<(string Name, int Match, int Total, int SpaceMatch, int SpaceTotal)>();
        string currentFile = "";
        bool spaceTrack = false;
        void Count(string key, bool ok)
        {
            counts.TryGetValue(key, out var c);
            counts[key] = (c.Match + (ok ? 1 : 0), c.Total + 1);
            int last = totals.Count - 1;
            if (last < 0 || totals[last].Name != currentFile) { totals.Add((currentFile, 0, 0, 0, 0)); last++; }
            var t = totals[last];
            int m = ok ? 1 : 0;
            totals[last] = spaceTrack ? (t.Name, t.Match + m, t.Total + 1, t.SpaceMatch + m, t.SpaceTotal + 1) : (t.Name, t.Match + m, t.Total + 1, t.SpaceMatch, t.SpaceTotal);
        }
        static bool Near(float a, float b) => MathF.Abs(a - b) <= 1e-3f * MathF.Max(1, MathF.Max(MathF.Abs(a), MathF.Abs(b)));
        static bool NearV(Vector3 a, Vector3 b) => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);
        // Rotation tangents: the quaternion's x, y, z (w is 0).
        static Vector3 Vec(Quaternion q) => new(q.X, q.Y, q.Z);

        foreach (var file in ExpandFiles(args.Where(a => !a.StartsWith("--"))))
        {
            var model = SceneModel.Load(file);
            currentFile = Path.GetFileName(file);
            foreach (var ch in model.Scene.Characters)
            {
                var evaluator = plain ? null : new PoseEvaluator(ch);
                foreach (var anim in ch.Animations)
                {
                    var converter = evaluator == null ? null : new KeySpaceConverter(evaluator, anim);
                    foreach (var stack in anim.Stacks)
                        foreach (var track in stack.Tracks)
                        {
                            var tIn = track.TangentsIn;
                            var tOut = track.TangentsOut;
                            int n = Math.Min(stack.Keys.Count, track.Keys.Count);
                            var before = Enumerable.Range(0, n).Select(i => (
                                In: (tIn.ElementAtOrDefault(i) as TransformKey)?.Value,
                                Out: (tOut.ElementAtOrDefault(i) as TransformKey)?.Value)).ToArray();
                            spaceTrack = track.Keys.Any(k => k is TransformKey { Space: not null });
                            var restore = SnapshotTangents(track);
                            TangentRecompute.Recompute(track, converter);
                            var modes = track.TangentModes;
                            string cyc = stack.IsCycle ? "cycle" : "plain";
                            for (int i = 0; i < n - 1; i++)
                            {
                                if (before[i].In is not { } bi || before[i].Out is not { } bo) continue;
                                var ai = ((TransformKey)tIn[i]!).Value;
                                var ao = ((TransformKey)tOut[i]!).Value;
                                var m = modes.ElementAtOrDefault(i);
                                string Mode(int c) => m != null && c < m.Length ? m[c].Raw.ToString("X3") : "---";
                                string pos = i == 0 ? "first" : i == n - 2 ? "last" : "mid";
                                void Check(string group, int channel, bool okIn, bool okOut, string detail)
                                {
                                    bool ok = okIn && okOut;
                                    Count($"{group} {Mode(channel)} {cyc} {pos}", ok);
                                    if (!ok && show-- > 0)
                                        Console.WriteLine($"  {Path.GetFileName(file)} {anim.Name}/{stack.Name} {track.Target?.Name} key {i}@{stack.Keys[i].Time} {group} mode {Mode(channel)} {cyc}: {detail}");
                                }
                                if ((track.Channels & AnimChannels.Translate) != 0)
                                    Check("T", 0, NearV(ai.Translation, bi.Translation), NearV(ao.Translation, bo.Translation),
                                        $"in {bi.Translation} -> {ai.Translation}, out {bo.Translation} -> {ao.Translation}");
                                if ((track.Channels & AnimChannels.Rotate) != 0)
                                    Check("R", 3, NearV(Vec(ai.Rotation), Vec(bi.Rotation)), NearV(Vec(ao.Rotation), Vec(bo.Rotation)),
                                        $"in {Vec(bi.Rotation)} -> {Vec(ai.Rotation)}, out {Vec(bo.Rotation)} -> {Vec(ao.Rotation)}");
                                if ((track.Channels & AnimChannels.Scale) != 0)
                                    Check("S", 6, NearV(ai.Scale, bi.Scale), NearV(ao.Scale, bo.Scale),
                                        $"in {bi.Scale} -> {ai.Scale}, out {bo.Scale} -> {ao.Scale}");
                            }
                            restore();
                        }
                }
            }
        }

        static string Pct(int m, int t) => $"{m} / {t} ({100.0 * m / Math.Max(t, 1):F2}%)";
        Console.WriteLine($"lookup: {(plain ? "plain" : "space-aware")}");
        Console.WriteLine("group mode stack position: matching / total");
        foreach (var (key, c) in counts)
            Console.WriteLine($"  {key,-24} {c.Match,6} / {c.Total,-6} {100.0 * c.Match / c.Total,6:F1}%");
        foreach (var t in totals)
            Console.WriteLine($"file {t.Name}: {Pct(t.Match, t.Total)}; space-switched tracks {Pct(t.SpaceMatch, t.SpaceTotal)}");
        Console.WriteLine($"space-switched tracks {Pct(totals.Sum(t => t.SpaceMatch), totals.Sum(t => t.SpaceTotal))}");
        int all = counts.Values.Sum(c => c.Total), match = counts.Values.Sum(c => c.Match);
        Console.WriteLine($"total {Pct(match, all)}");
        return 0;
    }

    /// <summary>Saves a track's tangent values and IK scalars; the returned action puts them back.</summary>
    private static Action SnapshotTangents(AnimTrack track)
    {
        var saved = track.TangentsIn.Concat(track.TangentsOut).OfType<TransformKey>()
            .Select(k => (Key: k, k.Value, Pole: (k as IKKey)?.PoleAngle ?? 0,
                Roll: (k as ReverseFootKey)?.RollAngle ?? 0, Twist: (k as ReverseFootKey)?.TwistAngle ?? 0, Bank: (k as ReverseFootKey)?.BankAngle ?? 0))
            .ToArray();
        return () =>
        {
            foreach (var s in saved)
            {
                s.Key.Value = s.Value;
                if (s.Key is IKKey ik) ik.PoleAngle = s.Pole;
                if (s.Key is ReverseFootKey r) (r.RollAngle, r.TwistAngle, r.BankAngle) = (s.Roll, s.Twist, s.Bank);
            }
        };
    }

    private static int FirstDifference(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        int i = a.AsSpan(0, n).CommonPrefixLength(b.AsSpan(0, n));
        return i < n || a.Length != b.Length ? i : -1;
    }
}

/// <summary>Compact text rendering of member values for the dump command.</summary>
internal sealed class ValueFormatter(Func<AktObject, int> indexOf, int maxItems)
{
    public string Format(object? v, int indent)
    {
        switch (v)
        {
            case null: return "null";
            case string s: return $"\"{s}\"";
            case float f: return F(f);
            case float[] fa: return "(" + string.Join(", ", fa.Select(F)) + ")";
            case byte[] ba:
                return ba.Length <= 16 ? $"[{string.Join(" ", ba.Select(x => x.ToString("X2")))}]"
                                       : $"<{ba.Length} bytes: {Convert.ToHexString(ba, 0, 16)}...>";
            case AktReference r:
                return r.Target != null ? $"#{indexOf(r.Target)} '{r.Target.Name}'" : r.IsNull ? "null" : $"#{r.RawIndex}?";
            case List<AktReference> refs:
                return $"[{refs.Count}] " + string.Join(", ", refs.Take(maxItems).Select(x => Format(x, indent))) + More(refs.Count);
            case AktStruct s:
            {
                var sb = new StringBuilder(AktNames.Key(s.TypeName)).Append(" {");
                foreach (var f in s.Fields)
                    sb.Append('\n').Append(' ', indent + 2).Append(f.Name).Append(" = ").Append(Format(f.Value, indent + 2));
                return sb.Append('\n').Append(' ', indent).Append('}').ToString();
            }
            case List<object?> list:
            {
                var sb = new StringBuilder($"[{list.Count}]");
                for (int i = 0; i < Math.Min(list.Count, maxItems); i++)
                    sb.Append('\n').Append(' ', indent + 2).Append($"[{i}] ").Append(Format(list[i], indent + 2));
                if (list.Count > maxItems) sb.Append('\n').Append(' ', indent + 2).Append(More(list.Count).Trim());
                return sb.ToString();
            }
            case object?[] tuple:
                return "(" + string.Join(", ", tuple.Select(x => Format(x, indent))) + ")";
            case AktMap map:
            {
                var sb = new StringBuilder($"map[{map.Count}]");
                for (int i = 0; i < Math.Min(map.Count, maxItems); i++)
                    sb.Append('\n').Append(' ', indent + 2)
                      .Append(Format(map.Entries[i].Key, indent + 2))
                      .Append(" = ")
                      .Append(Format(map.Entries[i].Value, indent + 2));
                if (map.Count > maxItems) sb.Append('\n').Append(' ', indent + 2).Append(More(map.Count).Trim());
                return sb.ToString();
            }
            default:
                return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "?";
        }
    }

    private string More(int count) => count > maxItems ? $" ... ({count - maxItems} more)" : "";

    private static string F(float f) => f.ToString("0.######", CultureInfo.InvariantCulture);
}



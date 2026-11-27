using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Core.Plugins.EzExports;

namespace SloppyAnimatsu.Cli;

// The `ezexport` verb: lists a scene's EZ Exports presets with what each writes, adds or removes
// presets, and runs them. Options apply in order; --add starts a new preset that the following
// --item/--character/--folder/--key/--target/--scale options fill in. --character adds a
// character's model (written without animations).
internal static partial class Program
{
    private const string EzExportUsage =
        "ezexport <scene.slopa> [--add=Name [--item=path]... [--character=Name]... [--folder=dir] [--key=Key] [--target=name] [--scale=f]]... " +
        "[--remove=Name]... [--out=out.slopa] [--run[=Name]]";

    private static int EzExportCommand(string[] args)
    {
        if (args.Length < 1) return Fail(EzExportUsage);
        string scenePath = Path.GetFullPath(args[0]);
        var model = SceneModel.Load(scenePath);
        var presets = EzPresetStore.Load(model, out string? error);
        if (error != null) return Fail(error);

        EzPreset? current = null;
        string? output = null;
        bool run = false;
        string? runName = null;
        bool edited = false;
        foreach (string arg in args[1..])
        {
            int eq = arg.IndexOf('=');
            string key = eq > 0 ? arg[..eq] : arg;
            string value = eq > 0 ? arg[(eq + 1)..] : "";
            switch (key)
            {
                case "--add":
                    current = new EzPreset { Name = value.Length > 0 ? value : "Preset" };
                    presets.Add(current);
                    edited = true;
                    break;
                case "--remove":
                    if (presets.RemoveAll(p => p.Name == value) == 0) return Fail($"No preset named '{value}'.");
                    current = null;
                    edited = true;
                    break;
                case "--item":
                {
                    if (current == null) return Fail("--item needs an --add first.");
                    var found = FindBankItem(model, value);
                    if (found.Count == 0) return Fail($"No animation or Anim Bank folder '{value}'.");
                    if (found.Count > 1)
                        return Fail($"'{value}' names {found.Count} items; give the full path:\n  " +
                                    string.Join("\n  ", found.Select(EzExportPlanner.ItemPath)));
                    current.Items.Add(found[0]);
                    break;
                }
                case "--character":
                {
                    if (current == null) return Fail("--character needs an --add first.");
                    string name = value.EndsWith(EzExportPlanner.ModelSuffix, StringComparison.Ordinal) ? value[..^EzExportPlanner.ModelSuffix.Length] : value;
                    var characters = model.Scene.Characters.Where(c => c.Name == name).ToList();
                    if (characters.Count == 0) return Fail($"No character '{name}'.");
                    if (characters.Count > 1) return Fail($"'{name}' names {characters.Count} characters.");
                    current.Items.Add(characters[0]);
                    break;
                }
                case "--folder":
                    if (current == null) return Fail("--folder needs an --add first.");
                    current.Folder = value;
                    break;
                case "--key":
                    if (current == null) return Fail("--key needs an --add first.");
                    current.Key = value;
                    break;
                case "--target":
                {
                    if (current == null) return Fail("--target needs an --add first.");
                    var target = FbxExportSettings.Targets.FirstOrDefault(t =>
                        string.Equals(t.Target.ToString(), value, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t.Label, value, StringComparison.OrdinalIgnoreCase));
                    if (target.Label == null)
                        return Fail($"Unknown target '{value}'; one of: {string.Join(", ", FbxExportSettings.Targets.Select(t => t.Target))}.");
                    current.Settings = FbxExportSettings.For(target.Target, current.Settings);
                    break;
                }
                case "--scale":
                    if (current == null) return Fail("--scale needs an --add first.");
                    if (!float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float scale)
                        || !FbxExportSettings.IsValidScaleFactor(scale))
                        return Fail($"Bad scale factor '{value}'.");
                    current.Settings = current.Settings with { ScaleFactor = scale };
                    break;
                case "--list-items":
                    foreach (var item in FindBankItem(model, null)) Console.WriteLine(EzExportPlanner.ItemPath(item));
                    return 0;
                case "--out":
                    output = value;
                    break;
                case "--run":
                    run = true;
                    runName = value.Length > 0 ? value : null;
                    break;
                default:
                    return Fail($"Unknown option '{arg}'.\n{EzExportUsage}");
            }
        }

        if (edited && output == null && !run) return Fail("Presets were changed: give --out=<out.slopa> to save them.");
        EzPresetStore.Save(model, presets);
        if (output != null)
        {
            model.Save(output);
            scenePath = Path.GetFullPath(output);
            Console.WriteLine($"saved {output}");
        }

        if (!run)
        {
            PrintEzPresets(model, scenePath, presets);
            return 0;
        }
        var toRun = runName == null ? presets : presets.Where(p => p.Name == runName).ToList();
        if (toRun.Count == 0) return Fail(runName == null ? "The scene has no EZ Exports presets." : $"No preset named '{runName}'.");
        var results = EzExportRunner.Run(model, scenePath, toRun);
        foreach (var result in results)
        {
            Console.WriteLine(result.StatusLine());
            foreach (string file in result.Written) Console.WriteLine("  " + file);
        }
        return results.Any(r => r.Error != null) ? 1 : 0;
    }

    private static void PrintEzPresets(SceneModel model, string scenePath, IReadOnlyList<EzPreset> presets)
    {
        if (presets.Count == 0)
        {
            Console.WriteLine("No EZ Exports presets.");
            return;
        }
        foreach (var preset in presets)
        {
            Console.WriteLine($"{(preset.Checked ? "[x]" : "[ ]")} {preset.Name}  folder=\"{preset.Folder}\" key=\"{preset.Key}\" target={preset.Settings.Target} scale={preset.Settings.ScaleFactor}");
            foreach (var item in preset.Items) Console.WriteLine("    item " + EzExportPlanner.ItemPath(item));
            var plan = EzExportPlanner.Plan(model, scenePath, preset);
            foreach (var file in plan.Files) Console.WriteLine("    writes " + file.Path);
            foreach (string warning in plan.Warnings) Console.WriteLine("    warning: " + warning);
            if (plan.Error != null) Console.WriteLine("    error: " + plan.Error);
        }
    }

    /// <summary>
    /// The animations and Anim Bank folders <paramref name="path"/> names: a full path as the item
    /// list shows it (<c>Hero: Locomotion/Run</c>, a folder with a trailing slash), the same without
    /// the character, or a bare name when it is unique.
    /// </summary>
    /// <remarks>A null <paramref name="path"/> gives every animation and folder (--list-items).</remarks>
    private static List<SceneObject> FindBankItem(SceneModel model, string? path)
    {
        var candidates = new List<SceneObject>();
        foreach (var character in model.Scene.Descendants<Character>())
        {
            if (character.AnimBank is not { } bank) continue;
            candidates.Add(bank);
            candidates.AddRange(bank.Descendants<SceneObject>().Where(o => o is Animation or Folder));
        }
        if (path == null) return candidates;
        static string WithoutCharacter(string p) => p.IndexOf(": ", StringComparison.Ordinal) is int i and >= 0 ? p[(i + 2)..] : p;
        var exact = candidates.Where(o => EzExportPlanner.ItemPath(o) == path).ToList();
        if (exact.Count > 0) return exact;
        var bare = candidates.Where(o => WithoutCharacter(EzExportPlanner.ItemPath(o)) == path).ToList();
        if (bare.Count > 0) return bare;
        return candidates.Where(o => o.Name == path.TrimEnd('/')).ToList();
    }
}

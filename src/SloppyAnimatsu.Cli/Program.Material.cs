using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Cli;

// The `material` verb: lists a scene's materials with their
// texture swap lists, or edits them. Options apply in order to every material named by the last
// --material (a scene can hold several copies of one material, one per imported character); a file
// added to more than one copy is stored once and shared.
internal static partial class Program
{
    private static int MaterialCommand(string[] args)
    {
        if (args.Length < 1)
            return Fail("material <scene.slopa> [<out.slopa> --material=Name [--replace] [--add=file[|name]]... " +
                        "[--active=name|index] [--transparent=on|off] [--nearest=on|off]]...");
        var model = SceneModel.Load(args[0]);
        if (args.Length == 1)
        {
            PrintMaterials(model);
            return 0;
        }
        string output = args[1];
        List<Material> targets = [];
        var shared = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);
        // Per target: the entry count before this --material's adds, for --replace.
        var replace = new Dictionary<Material, int>(ReferenceEqualityComparer.Instance);

        void FinishReplace()
        {
            foreach (var (m, before) in replace)
            {
                for (int i = before - 1; i >= 0; i--) m.RemoveTexture(i);
            }
            replace.Clear();
        }

        foreach (string arg in args[2..])
        {
            int eq = arg.IndexOf('=');
            string key = eq > 0 ? arg[..eq] : arg;
            string value = eq > 0 ? arg[(eq + 1)..] : "";
            switch (key)
            {
                case "--material":
                    FinishReplace();
                    targets = model.Scene.Materials.Where(m => m.Name == value).ToList();
                    if (targets.Count == 0) return Fail($"No material named '{value}'.");
                    break;
                case "--replace":
                    if (targets.Count == 0) return Fail("--replace needs a --material first.");
                    foreach (var m in targets) replace[m] = m.SwapTextures.Count;
                    break;
                case "--add":
                {
                    if (targets.Count == 0) return Fail("--add needs a --material first.");
                    int bar = value.IndexOf('|');
                    string path = bar >= 0 ? value[..bar] : value;
                    string? display = bar >= 0 ? value[(bar + 1)..] : null;
                    if (!File.Exists(path)) return Fail($"No file '{path}'.");
                    string full = Path.GetFullPath(path);
                    foreach (var m in targets)
                    {
                        if (shared.TryGetValue(full, out var texture)) m.AddExistingTexture(texture, display);
                        else
                        {
                            int index = m.AddTexture(Path.GetFileName(path), File.ReadAllBytes(path), display);
                            shared[full] = m.SwapTextures[index];
                        }
                    }
                    break;
                }
                case "--active":
                    foreach (var m in targets)
                    {
                        // Removed-by---replace entries still sit in front: count and search from the new ones.
                        int before = replace.TryGetValue(m, out int b) ? b : 0;
                        int index = int.TryParse(value, out int n) ? n + before
                            : Enumerable.Range(before, m.SwapTextures.Count - before).FirstOrDefault(i => m.TextureName(i) == value, -1);
                        if (!m.SetActiveTexture(index) && m.ActiveTextureIndex != index)
                            return Fail($"'{m.Name}' has no texture '{value}'.");
                    }
                    break;
                case "--transparent":
                    foreach (var m in targets) m.SetTransparent(OnOff(value));
                    break;
                case "--nearest":
                    foreach (var m in targets) m.SetNearestFilter(OnOff(value));
                    break;
                default:
                    return Fail($"Unknown option '{arg}'.");
            }
        }
        FinishReplace();
        model.Save(output);
        Console.WriteLine($"{output}: {new FileInfo(output).Length:N0} bytes");
        PrintMaterials(SceneModel.Load(output));
        return 0;

        static bool OnOff(string v) => v is "on" or "true" or "1" or "yes";
    }

    private static void PrintMaterials(SceneModel model)
    {
        foreach (var m in model.Scene.Materials)
        {
            var textures = m.SwapTextures;
            Console.WriteLine($"{m.Name}: {textures.Count} texture(s), rest {m.ActiveTextureIndex}" +
                              $"{(m.HasSwapList ? "" : " (diffuse only)")}, transparent {(m.Transparent ? "on" : "off")}, " +
                              $"nearest {(m.NearestFilter ? "on" : "off")}");
            for (int i = 0; i < textures.Count; i++)
                Console.WriteLine($"  {i}{(i == m.ActiveTextureIndex ? "*" : " ")} {m.TextureName(i)}  [{textures[i].Name}, {textures[i].Data.Length:N0} bytes]");
        }
        foreach (var a in model.Scene.Descendants<Animation>())
            foreach (var m in model.Scene.Materials)
                if (m.TextureKeys(a) is { Count: > 0 } keys)
                    Console.WriteLine($"keys '{a.Name}' / '{m.Name}': " + string.Join(", ", keys.Select(k => $"{k.Time}={m.TextureName(k.Index)}")));
    }
}

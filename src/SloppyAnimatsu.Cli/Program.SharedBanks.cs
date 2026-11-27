using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Cli;

// The shared Anim Bank verbs: `banks` lists the groups, `link-banks` links characters to one
// bank (reporting eligibility and the duplicates it drops), `unlink-bank` takes one out.
internal static partial class Program
{
    private static int BanksCommand(string[] args)
    {
        if (args.Length < 1) return Fail("banks <scene.slopa>");
        var model = SceneModel.Load(Path.GetFullPath(args[0]));
        var groups = SharedAnimBanks.Groups(model);
        if (groups.Count == 0) Console.WriteLine("no shared Anim Banks");
        int n = 1;
        foreach (var group in groups)
        {
            Console.WriteLine($"group {n++}: {string.Join(", ", group.Select(c => c.Name))}");
            foreach (var c in group)
            {
                var animations = c.AnimBank?.Source.PreOrder().Count(o => model.Wrap(o) is Animation) ?? 0;
                Console.WriteLine($"  {c.Name}: {animations} animations");
            }
        }
        return 0;
    }

    private static int LinkBanksCommand(string[] args)
    {
        var positional = args.Where(a => !a.StartsWith("--")).ToList();
        if (positional.Count < 3)
            return Fail("link-banks <scene.slopa> <reference> <character>... [--keep-duplicates] [--dry-run] [--out=out.slopa]");
        var model = SceneModel.Load(Path.GetFullPath(positional[0]));
        var characters = new List<Character>();
        foreach (string name in positional.Skip(1))
        {
            var found = model.Scene.Characters.FirstOrDefault(c => c.Name == name);
            if (found == null) return Fail($"No character named '{name}'.");
            characters.Add(found);
        }
        var reference = characters[0];
        var others = characters.Skip(1).ToList();
        foreach (var other in others)
        {
            var e = SharedAnimBanks.Eligibility(reference, other);
            Console.WriteLine($"{other.Name}: {(e.Eligible ? "eligible" : "NOT eligible")} - {e.Summary}");
        }
        var duplicates = SharedAnimBanks.FindDuplicates(reference, others);
        Console.WriteLine($"{duplicates.Count} duplicate animations");
        foreach (var d in duplicates)
            Console.WriteLine($"  keep {d.KeptOwner.Name}:{d.KeptPath}  drop {d.DroppedOwner.Name}:{d.DroppedPath}");
        if (args.Contains("--dry-run")) return 0;

        var result = SharedAnimBanks.Link(model, reference, others, args.Contains("--keep-duplicates") ? [] : duplicates);
        Console.WriteLine($"linked {string.Join(", ", result.Members.Select(c => c.Name))}: " +
                          $"{result.MergedAnimations} animations merged, {result.Dropped.Count} dropped, " +
                          $"{result.ConformedControllers} ref-pose controllers conformed");
        foreach (var c in result.Members)
            Console.WriteLine($"  {c.Name}: {c.AnimBank?.Source.PreOrder().Count(o => model.Wrap(o) is Animation) ?? 0} animations");
        return SaveIfAsked(model, args);
    }

    private static int UnlinkBankCommand(string[] args)
    {
        var positional = args.Where(a => !a.StartsWith("--")).ToList();
        if (positional.Count < 2) return Fail("unlink-bank <scene.slopa> <character> [--out=out.slopa]");
        var model = SceneModel.Load(Path.GetFullPath(positional[0]));
        var character = model.Scene.Characters.FirstOrDefault(c => c.Name == positional[1]);
        if (character == null) return Fail($"No character named '{positional[1]}'.");
        if (!SharedAnimBanks.Unlink(model, character)) return Fail($"{character.Name} does not share an Anim Bank.");
        Console.WriteLine($"unlinked {character.Name}");
        return SaveIfAsked(model, args);
    }

    private static int SaveIfAsked(SceneModel model, string[] args)
    {
        string? output = Option(args, "out");
        if (output == null) return 0;
        model.Save(output);
        Console.WriteLine($"saved {output}");
        return 0;
    }
}

using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Core.Plugins;

/// <summary>
/// Plugin data kept in a scene, on its <c>CScene</c> object: per plugin one text (the plugin chooses
/// its format; EZ Exports writes JSON) and a list of references to scene objects. References follow
/// their objects through renames and moves; the text refers to them by their place in the plugin's
/// own list. Rows of other plugins (disabled ones, or ones this build does not have) are kept as they
/// are. A reference whose object was deleted reads back as null and is saved as null.
/// </summary>
/// <remarks>
/// Storage: <see cref="MaterialSchema.PluginIdsMember"/> and <see cref="MaterialSchema.PluginDataMember"/>
/// are parallel (one row per plugin); <see cref="MaterialSchema.PluginRefsMember"/> holds every row's
/// references and <see cref="MaterialSchema.PluginRefOwnersMember"/> the row index of each.
/// </remarks>
public static class PluginSceneData
{
    /// <summary>The plugin's stored text and references; (null, []) when it stored nothing.</summary>
    public static (string? Text, IReadOnlyList<SceneObject?> Refs) Read(SceneModel model, string pluginId)
    {
        var scene = model.Scene.Source;
        var ids = Values(scene[MaterialSchema.PluginIdsMember]);
        int row = ids.FindIndex(v => v as string == pluginId);
        if (row < 0) return (null, []);
        var texts = Values(scene[MaterialSchema.PluginDataMember]);
        string? text = row < texts.Count ? texts[row] as string : null;
        var refs = Values(scene[MaterialSchema.PluginRefsMember]);
        var owners = Values(scene[MaterialSchema.PluginRefOwnersMember]);
        var mine = new List<SceneObject?>();
        for (int i = 0; i < refs.Count && i < owners.Count; i++)
            if (owners[i] is int owner && owner == row)
                mine.Add(refs[i] is AktReference { Target: { } target } && IsAttached(model, target) ? model.Wrap(target) : null);
        return (text ?? "", mine);
    }

    /// <summary>
    /// Replaces the plugin's row: its text and its references (a null entry stays a null reference,
    /// so indexes into the list keep their meaning). A null <paramref name="text"/> removes the row.
    /// </summary>
    public static void Write(SceneModel model, string pluginId, string? text, IReadOnlyList<SceneObject?> refs)
    {
        EnsureMembers(model);
        var scene = model.Scene.Source;
        var ids = Values(scene[MaterialSchema.PluginIdsMember]);
        var texts = Values(scene[MaterialSchema.PluginDataMember]);
        var oldRefs = Values(scene[MaterialSchema.PluginRefsMember]);
        var oldOwners = Values(scene[MaterialSchema.PluginRefOwnersMember]);
        while (texts.Count < ids.Count) texts.Add("");

        int row = ids.FindIndex(v => v as string == pluginId);

        // Every other row's references are kept in order; the removed row's owners above it shift down.
        var newRefs = new List<AktReference>();
        var newOwners = new List<object?>();
        for (int i = 0; i < oldRefs.Count && i < oldOwners.Count; i++)
        {
            if (oldOwners[i] is not int owner || owner == row) continue;
            if (text == null && row >= 0 && owner > row) owner--;
            newRefs.Add(oldRefs[i] as AktReference ?? new AktReference(null));
            newOwners.Add(owner);
        }

        if (text == null)
        {
            if (row >= 0)
            {
                ids.RemoveAt(row);
                texts.RemoveAt(row);
            }
        }
        else
        {
            if (row < 0)
            {
                row = ids.Count;
                ids.Add(pluginId);
                texts.Add(text);
            }
            else texts[row] = text;
            foreach (var r in refs)
            {
                newRefs.Add(new AktReference(r?.Source));
                newOwners.Add(row);
            }
        }

        scene.Set(MaterialSchema.PluginIdsMember, ids);
        scene.Set(MaterialSchema.PluginDataMember, texts);
        scene.Set(MaterialSchema.PluginRefsMember, newRefs);
        scene.Set(MaterialSchema.PluginRefOwnersMember, newOwners);
    }

    /// <summary>The ids of every plugin that has a row in the scene.</summary>
    public static IReadOnlyList<string> PluginIds(SceneModel model) =>
        Values(model.Scene.Source[MaterialSchema.PluginIdsMember]).OfType<string>().ToList();

    /// <summary>Whether <paramref name="obj"/> is still part of the scene (not deleted or detached).</summary>
    public static bool IsAttached(SceneModel model, AktObject obj)
    {
        var top = obj;
        while (top.Parent != null) top = top.Parent;
        return model.Document.Roots.Any(r => ReferenceEquals(r, top));
    }

    private static void EnsureMembers(SceneModel model) =>
        MaterialSchema.EnsureFields(model.Document.Schema, model.Document.Roots);

    // A copy of a stored array as plain values; an absent member (an older scene) reads as empty.
    private static List<object?> Values(object? value) => value switch
    {
        List<AktReference> refs => refs.Cast<object?>().ToList(),
        IEnumerable<object?> list => list.ToList(),
        _ => [],
    };
}

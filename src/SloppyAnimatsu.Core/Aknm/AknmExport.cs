using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Core.Aknm;

/// <summary>
/// Writes an `.aknm`: the character's subtree, the character and
/// its meshes in the classes the app substitutes, and only the selected animations (plus
/// <c>__RigAnim</c>).
/// </summary>
public static class AknmExport
{
    /// <summary>The `.aknm` document for <paramref name="character"/> with <paramref name="animations"/> as the selected set.</summary>
    public static AktDocument Build(SceneModel model, Character character, IEnumerable<Animation> animations) =>
        Build(model.Document, character.Source, animations.Select(a => a.Source));

    public static AktDocument Build(AktDocument scene, AktObject character, IEnumerable<AktObject> animations)
    {
        if (!AknmFormat.IsA(scene, character, AknmFormat.CharacterClass))
            throw new ArgumentException($"{character} is not a CCharacter.", nameof(character));

        var selected = new HashSet<AktObject>(animations, ReferenceEqualityComparer.Instance);
        foreach (var animation in selected)
        {
            if (!AknmFormat.IsA(scene, animation, AknmFormat.AnimationClass))
                throw new ArgumentException($"{animation} is not a CAnimation.", nameof(animations));
            bool inside = false;
            for (var parent = animation.Parent; parent != null && !inside; parent = parent.Parent)
                inside = parent == character;
            if (!inside)
                throw new ArgumentException($"{animation} is not under {character}.", nameof(animations));
        }

        // The export marks unselected animations transient; writing the kept subtrees is equivalent.
        var excluded = new HashSet<AktObject>(ReferenceEqualityComparer.Instance);
        foreach (var obj in character.PreOrder().Skip(1))
            if (AknmFormat.IsA(scene, obj, AknmFormat.AnimationClass)
                && obj.Name != AknmFormat.RigAnimationName
                && !selected.Contains(obj))
                excluded.Add(obj);

        var schema = AknmSchema.Clone(scene.Schema);
        AknmSchema.AddAknmCharacter(schema);
        var builder = new CloneBuilder(scene, schema, excluded);
        var root = builder.Clone(character);
        builder.Remap(root);

        return new AktDocument(schema, new[] { root });
    }

    /// <summary>Writes the `.aknm` to <paramref name="path"/>.</summary>
    public static void Write(SceneModel model, Character character, IEnumerable<Animation> animations, string path,
        IProgress<double>? progress = null)
    {
        var document = Build(model, character, animations);
        progress?.Report(0.3);
        document.Save(path, AktFileFormat.Akeytsu, progress.Slice(0.3, 1));
    }

    private sealed class CloneBuilder
    {
        private readonly AktDocument _scene;
        private readonly AktSchema _schema;
        private readonly HashSet<AktObject> _excluded;
        private readonly HashSet<AktObject> _copies = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<AktObject, AktObject> _map = new(ReferenceEqualityComparer.Instance);

        public CloneBuilder(AktDocument scene, AktSchema schema, HashSet<AktObject> excluded)
        {
            _scene = scene;
            _schema = schema;
            _excluded = excluded;
        }

        /// <summary>A copy of the subtree under <paramref name="source"/>; its references still point at the source objects.</summary>
        public AktObject Clone(AktObject source)
        {
            string type = ClassFor(source);
            var copy = new AktObject(type);
            foreach (var field in FieldsFor(source, type))
                copy.Fields.Add(new AktField(field.Owner, field.Name, field.TypeName, AknmFormat.CopyValue(field.Value)));
            _map[source] = copy;
            _copies.Add(copy);
            foreach (var child in source.Children)
                if (!_excluded.Contains(child))
                    copy.AddChild(Clone(child));
            return copy;
        }

        /// <summary>Points every reference at its copy inside the subtree; references that leave it become null.</summary>
        public void Remap(AktObject root)
        {
            foreach (var copy in root.PreOrder())
                foreach (var field in copy.Fields)
                    AknmFormat.Remap(field.Value, target =>
                        _map.TryGetValue(target, out var inside) ? inside
                        : _copies.Contains(target) ? target
                        : null);
        }

        private string ClassFor(AktObject obj)
        {
            if (AknmFormat.IsA(_scene, obj, AknmFormat.MeshClass)) return AknmFormat.Object3DClass;
            if (AknmFormat.IsA(_scene, obj, AknmFormat.CharacterClass)) return AknmFormat.AknmCharacterClass;
            return obj.TypeName;
        }

        /// <summary>
        /// A plain <c>CObject3D</c> keeps only the members of the CObject3D chain, so a substituted
        /// mesh loses its geometry, UVs, skin weights and material references.
        /// </summary>
        private IEnumerable<AktField> FieldsFor(AktObject source, string targetType)
        {
            if (targetType != AknmFormat.Object3DClass) return source.Fields;
            var definition = _schema.FindClass(targetType)
                             ?? throw new InvalidOperationException($"The schema has no {targetType}.");
            var owners = definition.Chain().Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            return source.Fields.Where(f => owners.Contains(AktNames.Key(f.Owner)));
        }
    }
}

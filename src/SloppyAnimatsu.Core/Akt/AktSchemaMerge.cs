namespace SloppyAnimatsu.Core.Akt;

/// <summary>
/// Extends a document's schema with the classes and structs another document's objects need, and
/// fills members that a loaded object does not carry. Used when objects from another file (a
/// scene, an `.aknm`) are copied into a document whose schema came from a different file.
/// </summary>
internal static class AktSchemaMerge
{
    /// <summary>
    /// Adds every class (with its ancestors) and struct of <paramref name="source"/> that the
    /// <paramref name="objects"/> need. Classes and structs the target already has keep their
    /// order; members the source declares and the target does not are appended. Returns whether
    /// anything was added, i.e. whether objects already in the target may be missing members now.
    /// </summary>
    public static bool Merge(
        AktSchema target, AktSchema source, IEnumerable<AktObject> objects, string sourceDescription)
    {
        bool changed = false;

        foreach (var root in objects)
            foreach (var obj in root.PreOrder())
            {
                var def = source.FindClass(obj.TypeName)
                          ?? throw new InvalidDataException(
                              $"Class '{obj.TypeName}' is missing from the schema of {sourceDescription}.");
                EnsureClass(def);
            }

        foreach (var s in source.Structs) EnsureStruct(s);
        return changed;

        AktClassDef EnsureClass(AktClassDef def)
        {
            if (target.FindClass(def.Name) is { } existing)
            {
                if (Union(existing.Members, def.Members)) changed = true;
                return existing;
            }
            var parent = def.Parent == null ? null : EnsureClass(def.Parent);
            var copy = new AktClassDef(def.Name);
            copy.Members.AddRange(def.Members);
            target.AddClass(copy, parent);
            changed = true;
            return copy;
        }

        void EnsureStruct(AktStructDef def)
        {
            if (target.FindStruct(def.Name) is { } existing)
            {
                if (Union(existing.Members, def.Members)) changed = true;
                return;
            }
            var copy = new AktStructDef(def.Name);
            copy.Members.AddRange(def.Members);
            target.AddStruct(copy);
            changed = true;
        }
    }

    /// <summary>Appends the members whose name the list does not have yet; returns whether it added any.</summary>
    private static bool Union(List<AktMember> into, IEnumerable<AktMember> from)
    {
        bool added = false;
        foreach (var member in from)
        {
            if (into.Any(m => m.Name == member.Name)) continue;
            into.Add(member);
            added = true;
        }
        return added;
    }

    /// <summary>
    /// Adds the schema's members that the object or its struct values do not have, with their
    /// default values (schema evolution: an older file imported into a newer schema).
    /// </summary>
    public static void FillMissingMembers(AktSchema schema, AktObject obj)
    {
        var def = schema.FindClass(obj.TypeName);
        if (def == null) return;
        foreach (var c in def.Chain())
            FillMembers(schema, obj, c.Key, c.Members);
    }

    private static void FillMembers(
        AktSchema schema, AktFieldContainer container, string ownerKey, IEnumerable<AktMember> members)
    {
        foreach (var member in members)
            if (container.FindField(ownerKey, member.Name) == null)
                container.Fields.Add(new AktField(
                    ownerKey, member.Name, member.TypeName, schema.DefaultValue(member.Type)));
        foreach (var member in members)
            if (container.FindField(ownerKey, member.Name) is { } field)
                FillValue(schema, field.Type, field.Value);
    }

    private static void FillValue(AktSchema schema, AktType type, object? value)
    {
        switch (type.Kind)
        {
            case AktTypeKind.Vector:
                if (value is AktPackedList { IsPacked: true, Count: > 0 } packed)
                {
                    // All elements share one layout: filling the first shows whether any member is
                    // missing. A fill that adds members demotes the list; then fill the rest.
                    FillValue(schema, type.Element!, packed[0]);
                    if (!packed.IsPacked)
                        for (int i = 1; i < packed.Count; i++) FillValue(schema, type.Element!, packed[i]);
                }
                else if (value is IList<object?> list)
                    for (int i = 0; i < list.Count; i++) FillValue(schema, type.Element!, list[i]);
                break;
            case AktTypeKind.Tuple:
                if (value is object?[] tuple)
                    foreach (var element in tuple) FillValue(schema, type.Element!, element);
                break;
            case AktTypeKind.Struct:
                if (value is AktStruct s && schema.FindStruct(type.Target!) is { } structDef)
                    FillMembers(schema, s, structDef.Key, structDef.Members);
                break;
            case AktTypeKind.Polymorphic:
                if (value is AktStruct p)
                    foreach (string key in AktNames.PolymorphicChain(AktNames.Key(p.TypeName)))
                        if (schema.FindStruct(key) is { } def)
                            FillMembers(schema, p, def.Key, def.Members);
                break;
        }
    }
}

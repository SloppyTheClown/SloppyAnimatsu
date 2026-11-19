namespace SloppyAnimatsu.Core.Akt;

/// <summary>Operations on the references held by fields, structs and lists.</summary>
public static class AktRefs
{
    /// <summary>
    /// Replaces every reference in <paramref name="value"/> with the mapped object, or with null
    /// when <paramref name="lookup"/> returns none.
    /// </summary>
    public static void Remap(object? value, Func<AktObject, AktObject?> lookup)
    {
        switch (value)
        {
            case AktReference r when r.Target != null:
            {
                var target = lookup(r.Target);
                r.Target = target;
                if (target == null) r.RawIndex = -1;
                break;
            }
            case AktReference r:
            {
                // An unresolved index (a reference the reader could not map) means "no object"
                // in the copied tree; keeping the old index would point at an unrelated object.
                r.RawIndex = -1;
                break;
            }
            case List<AktReference> refs:
                foreach (var r in refs) Remap(r, lookup);
                break;
            case AktStruct s:
                for (int fi = 0; fi < s.FieldCount; fi++) Remap(s.ValueAt(fi), lookup);
                break;
            case List<object?> list:
                foreach (var v in list) Remap(v, lookup);
                break;
            case object?[] tuple:
                foreach (var v in tuple) Remap(v, lookup);
                break;
        }
    }
}
